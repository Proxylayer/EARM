using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EArm.Graph;

/// <summary>
/// <see cref="IGraphClient"/> over <see cref="HttpClient"/>: real HTTP statuses, bearer tokens from an <see cref="IGraphTokenProvider"/>,
/// error mapping, retries per <see cref="RetryPolicy"/>, <c>@odata.nextLink</c> pagination and <c>$batch</c> in chunks of 20 with
/// individual retry of failed sub-requests.
/// <para>
/// Security: every URL the token is sent to (the request URL, each <c>@odata.nextLink</c>) must be <c>https</c> on exactly the cloud's Graph host
/// (<see cref="GraphUrl.Check"/>); redirects are never followed (a 3xx is an error), so the Authorization header cannot reach another host;
/// tokens, Authorization headers and request bodies are never logged.
/// </para>
/// </summary>
public sealed partial class HttpGraphClient : IGraphClient
{
    public const int MaxBatchSize = 20;

    private readonly HttpClient _http;
    private readonly IGraphTokenProvider _tokens;
    private readonly RetryPolicy _retryPolicy;
    private readonly GraphOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate;
    private readonly string _userAgent;
    private readonly TokenScopes _scopes;
    private long _responsesStarted;

    public HttpGraphClient(HttpClient http, IGraphTokenProvider tokens, RetryPolicy retryPolicy, GraphOptions? options = null, ILogger? logger = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(retryPolicy);
        _http = http;
        _tokens = tokens;
        _retryPolicy = retryPolicy;
        _options = options ?? new GraphOptions();
        _logger = logger ?? NullLogger.Instance;
        _time = timeProvider ?? TimeProvider.System;
        _gate = new SemaphoreSlim(Math.Max(1, _options.MaxParallel));
        _userAgent = "EARM/" + _options.UserAgentVersion;
        _scopes = TokenScopeGuard.Resolve(_options.Scopes, _options.Cloud.GraphScopes, _options.Cloud, nameof(options));
    }

    /// <summary>The cloud (Graph host) this client talks to.</summary>
    public GraphCloud Cloud => _options.Cloud;

    /// <summary>The scopes of the Graph token this client attaches (<see cref="GraphOptions.Scopes"/>, or the cloud's <c>.default</c>).</summary>
    public TokenScopes Scopes => _scopes;

    /// <summary>The state of one logical call across its 401 refresh: which token was sent and what the 401 asked for.</summary>
    private sealed class AuthAttempt
    {
        public string? TokenValue { get; set; }

        /// <summary>The decoded <c>claims</c> parameter of a <c>WWW-Authenticate: Bearer</c> challenge (continuous access evaluation), when the 401 carried one.</summary>
        public string? Claims { get; set; }

        /// <summary>The 401 answer that asked for the refresh (its code and request id go into the terminal message).</summary>
        public GraphException? Rejected { get; set; }
    }

    public async Task<JsonNode?> GetAsync(GraphRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var response = await SendAsync("GET", request, null, cancellationToken).ConfigureAwait(false);
            return response.Body;
        }
        catch (GraphException ex) when (ex.IsNotFound && !ex.IsReplication)
        {
            return null;
        }
    }

    public Task<GraphResponse> PostAsync(GraphRequest request, JsonNode? body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync("POST", request, body, cancellationToken);
    }

    public Task<GraphResponse> PatchAsync(GraphRequest request, JsonNode body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(body);
        return SendAsync("PATCH", request, body, cancellationToken);
    }

    public Task<GraphResponse> PutAsync(GraphRequest request, JsonNode body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(body);
        return SendAsync("PUT", request, body, cancellationToken);
    }

    public async Task DeleteAsync(GraphRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await SendAsync("DELETE", request, null, cancellationToken).ConfigureAwait(false);
    }

    private Task<GraphResponse> SendAsync(string method, GraphRequest request, JsonNode? body, CancellationToken cancellationToken)
    {
        var uri = GraphUrl.Build(_options.Cloud, request);
        return SendAsync(method, uri, request, body, cancellationToken);
    }

    /// <summary>Runs one call under the retry policy. <paramref name="request"/> supplies the headers and the retry hint; <paramref name="uri"/> is already validated.</summary>
    private async Task<GraphResponse> SendAsync(string method, Uri uri, GraphRequest request, JsonNode? body, CancellationToken cancellationToken)
    {
        method = method.ToUpperInvariant();
        if (body is not null && method is "GET" or "DELETE" or "HEAD")
        {
            throw new ArgumentException($"A {method} request never carries a body.", nameof(body));
        }

        // Defence in depth: the URL was checked when it was built or parsed, and is checked again where the token is attached.
        GraphUrl.EnsureAllowed(_options.Cloud, uri, "request");
        var headers = request.EffectiveHeaders();
        GraphRequestBuilder.ValidateHeaders(headers);
        var payload = body is null ? null : GraphRequestBuilder.SerializeBody(body, _options.Cloud);
        if (payload is not null)
        {
            var bytes = Encoding.UTF8.GetByteCount(payload);
            if (bytes > _options.MaxRequestBodyBytes)
            {
                throw new GraphTransportException(
                    GraphTransportFailure.RequestTooLarge,
                    $"{method} {GraphUrl.RedactForLog(uri)}: the request body is {bytes.ToString(CultureInfo.InvariantCulture)} bytes, over the {_options.MaxRequestBodyBytes.ToString(CultureInfo.InvariantCulture)}-byte limit; nothing was sent. Split the change into smaller requests.");
            }
        }

        var scope = _scopes;
        var write = IsWrite(method);
        var attempt = new AuthAttempt();

        try
        {
            return await _retryPolicy.ExecuteAsync(
                token => SendOnceAsync(method, uri, headers, payload, request.Hint, scope, write, attempt, token),
                request.Hint,
                token => RefreshTokenAsync(scope, attempt, token),
                cancellationToken,
                request.IsIdempotentFor(method)).ConfigureAwait(false);
        }
        catch (GraphException ex) when (write && (int)ex.Status >= 500)
        {
            // A 5xx to a write may follow a commit: tell the caller the outcome is unknown (it re-reads by key rather than repeating the write).
            ex.IsAmbiguous = true;
            throw;
        }
    }

    /// <summary>
    /// The one refresh after a 401: a new token (for a continuous-access-evaluation challenge, one requested WITH the challenge's claims). When the
    /// credential hands back the very token that was just rejected, repeating the call cannot help: the failure is terminal and says so, instead of
    /// the same request being sent again.
    /// </summary>
    private async Task RefreshTokenAsync(TokenScopes scope, AuthAttempt attempt, CancellationToken cancellationToken)
    {
        var fresh = await _tokens.RefreshTokenAsync(scope, attempt.Claims, cancellationToken).ConfigureAwait(false);
        if (attempt.TokenValue is not null && string.Equals(fresh.Value, attempt.TokenValue, StringComparison.Ordinal))
        {
            throw new GraphTransportException(
                GraphTransportFailure.NotAuthenticated,
                $"Graph rejected the access token (HTTP 401 {attempt.Rejected?.Code}, request id {attempt.Rejected?.RequestId ?? "n/a"}) and the credential issued the same token again, so repeating the call cannot succeed"
                + (attempt.Claims is null ? string.Empty : " (the token was also requested with the claims challenge Graph sent)")
                + ". The token may have been revoked, or the identity lost access: sign in again ('earm auth login') or check the credential.",
                attempt.Rejected);
        }
    }

    internal static bool IsWrite(string method)
    {
        return !(method.Equals("GET", StringComparison.OrdinalIgnoreCase) || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase) || method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<GraphResponse> SendOnceAsync(string method, Uri uri, IReadOnlyDictionary<string, string> headers, string? payload, RetryHint hint, TokenScopes scope, bool write, AuthAttempt attempt, CancellationToken cancellationToken)
    {
        var clientRequestId = Guid.NewGuid().ToString();
        var logTarget = GraphUrl.RedactForLog(uri);
        var graphToken = await _tokens.GetTokenAsync(scope, cancellationToken).ConfigureAwait(false);
        attempt.TokenValue = graphToken.Value;
        attempt.Claims = null;
        var probe = new ConnectProbe();
        var responsesBefore = Volatile.Read(ref _responsesStarted);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            // A write is never abandoned once it is on the wire: the caller's cancellation (Ctrl+C) is honoured up to here and then only between
            // requests, so a POST that Graph commits is answered and recorded instead of orphaned (with any secret only its response carries). The
            // request timeout still bounds it. A read stays cancellable throughout.
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = write ? new CancellationTokenSource() : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.Timeout);
            using var message = GraphRequestBuilder.BuildMessage(method, uri, headers, payload, graphToken, clientRequestId, _userAgent);
            message.Options.Set(ConnectProbeKey, probe);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _responsesStarted);
            }
            catch (OperationCanceledException) when (write || !cancellationToken.IsCancellationRequested)
            {
                // Still making the connection, and no other call finished meanwhile (so none could have lent this one an idle connection): nothing was sent.
                var nothingSent = probe.StillConnecting && Volatile.Read(ref _responsesStarted) == responsesBefore;
                throw TimeoutFailure(method, logTarget, stopwatch, clientRequestId, null, write && !nothingSent);
            }
            catch (HttpRequestException ex)
            {
                // A failure before the request was written (DNS, refused connection, TLS) cannot hide a commit; anything later can.
                throw NetworkFailure(method, logTarget, stopwatch, clientRequestId, ex, write && !FailedBeforeSending(ex));
            }

            using (response)
            {
                string text;
                try
                {
                    var bytes = await BoundedContent.ReadAsync(
                        response.Content,
                        _options.MaxResponseBytes,
                        () => new GraphTransportException(
                            GraphTransportFailure.ResponseTooLarge,
                            $"{method} {logTarget}: the response is larger than {_options.MaxResponseBytes.ToString(CultureInfo.InvariantCulture)} bytes (after decompression); it was discarded.",
                            null,
                            write),
                        timeout.Token).ConfigureAwait(false);
                    text = Encoding.UTF8.GetString(bytes);
                }
                catch (OperationCanceledException) when (write || !cancellationToken.IsCancellationRequested)
                {
                    throw TimeoutFailure(method, logTarget, stopwatch, clientRequestId, null, write);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
                {
                    // The server answered but the body did not arrive intact: a write was (probably) processed.
                    throw NetworkFailure(method, logTarget, stopwatch, clientRequestId, ex, write);
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    attempt.Claims = ParseClaimsChallenge(response.Headers);
                }

                var requestId = GraphResponseReader.FirstHeader(response.Headers, "request-id");
                GraphLog.GraphResponse(_logger, method, logTarget, (int)response.StatusCode, stopwatch.ElapsedMilliseconds, requestId, clientRequestId);
                try
                {
                    return GraphResponseReader.Map(response, text, hint, clientRequestId, requestId, _time);
                }
                catch (GraphException ex) when (ex.IsUnauthorized)
                {
                    attempt.Rejected = ex;
                    throw;
                }
                catch (GraphException ex) when (hint == RetryHint.ExpectReplicationLag && GraphException.IsPimGroupReplicationForbidden(ex.Status, ex.Code, uri.AbsolutePath))
                {
                    // PIM for Groups refuses the first requests about a just-created group with a 403: the replication schedule repeats it.
                    ex.IsReplication = true;
                    GraphLog.PimGroupForbiddenAsReplication(_logger, method, logTarget);
                    throw;
                }
                catch (GraphException ex) when (GraphException.IsPimGroupReplicationForbidden(ex.Status, ex.Code, uri.AbsolutePath))
                {
                    // Without the replication hint the 403 is not repeated; a schedule list read treats it as "PIM does not know the group yet".
                    GraphLog.PimGroupForbidden(_logger, method, logTarget);
                    throw;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private GraphTransportException TimeoutFailure(string method, string target, Stopwatch stopwatch, string clientRequestId, Exception? inner, bool ambiguous)
    {
        GraphLog.GraphTransportFailed(_logger, method, target, stopwatch.ElapsedMilliseconds, "timeout", clientRequestId);
        return new GraphTransportException(GraphTransportFailure.Timeout, $"{method} {target}: Graph did not answer within {_options.Timeout.TotalSeconds:F0} s.", inner, ambiguous);
    }

    private GraphTransportException NetworkFailure(string method, string target, Stopwatch stopwatch, string clientRequestId, Exception inner, bool ambiguous)
    {
        GraphLog.GraphTransportFailed(_logger, method, target, stopwatch.ElapsedMilliseconds, inner.GetType().Name, clientRequestId);
        var detail = inner.InnerException?.Message ?? inner.Message;
        return new GraphTransportException(GraphTransportFailure.Network, $"{method} {target}: the request to {_options.Cloud.GraphHost} failed ({detail}).", inner, ambiguous);
    }

    /// <summary>True for the <see cref="HttpRequestError"/> kinds that occur before any byte of the request was written.</summary>
    private static bool FailedBeforeSending(HttpRequestException ex)
    {
        return ex.HttpRequestError is HttpRequestError.NameResolutionError
            or HttpRequestError.ConnectionError
            or HttpRequestError.SecureConnectionError
            or HttpRequestError.ProxyTunnelError
            or HttpRequestError.VersionNegotiationError
            or HttpRequestError.ExtendedConnectNotSupported;
    }

    /// <summary>
    /// The <c>claims</c> parameter of a <c>WWW-Authenticate: Bearer ... claims="..."</c> challenge (continuous access evaluation), base64-decoded to the JSON
    /// that <see cref="Azure.Core.TokenRequestContext.Claims"/> expects; null when there is none or it does not decode.
    /// </summary>
    internal static string? ParseClaimsChallenge(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("WWW-Authenticate", out var values))
        {
            return null;
        }

        foreach (var value in values)
        {
            var match = ClaimsParameter().Match(value);
            if (!match.Success)
            {
                continue;
            }

            var encoded = match.Groups["claims"].Value.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - (encoded.Length % 4)) % 4), '=');
            try
            {
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                return json.TrimStart().StartsWith('{') ? json : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        return null;
    }

    [System.Text.RegularExpressions.GeneratedRegex("""(?:^|[\s,])claims\s*=\s*"(?<claims>[^"]*)"|(?:^|[\s,])claims\s*=\s*(?<claims>[A-Za-z0-9_\-+/=]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex ClaimsParameter();
}
