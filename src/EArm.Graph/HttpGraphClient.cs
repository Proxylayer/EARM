using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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

    private static readonly JsonSerializerOptions BodyJsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly HashSet<string> ForbiddenRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Proxy-Authorization",
        "Cookie",
        "Cookie2",
    };

    private static readonly HashSet<string> IgnoredRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host",
        "Content-Length",
        "Transfer-Encoding",
        "User-Agent",
        "Content-Type",
        "Connection",
        "Expect",
        "client-request-id",
    };

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

    /// <summary>
    /// A <see cref="HttpClient"/> configured for Graph: no redirects, no cookies, gzip/deflate, platform TLS defaults, a connection lifetime
    /// that follows DNS changes, and an infinite client timeout (the per-call timeout is applied by <see cref="HttpGraphClient"/>).
    /// </summary>
    public static HttpClient CreateHttpClient()
    {
        return new HttpClient(CreateHandler(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>The production handler settings of <see cref="CreateHttpClient"/>, separate so tests can inspect every property.</summary>
    internal static SocketsHttpHandler CreateHandler()
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30),
            ConnectCallback = ConnectAsync,
        };
    }

    /// <summary>
    /// The plain TCP connect (what <see cref="SocketsHttpHandler"/> does by default; TLS is layered on top by the handler) with a probe on the request that
    /// asked for the connection, so a per-call timeout that fires while the connection is still being made can be told apart from one that fires after
    /// bytes were sent (see <see cref="ConnectProbe"/>).
    /// </summary>
    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        ConnectProbe? probe = null;
        context.InitialRequestMessage?.Options.TryGetValue(ConnectProbeKey, out probe);
        probe?.MarkStarted();
        var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
            probe?.MarkConnected();
            return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Tracks, per request, whether the connection it asked for was still being established. A per-call timeout that fires in exactly that state, with
    /// no other call of the client having completed meanwhile (so the request cannot have been handed an idle connection), proves that nothing was
    /// sent: the failure is not ambiguous and a write may be repeated. In every other case (including a handler without the callback) the timeout is
    /// reported as ambiguous.
    /// </summary>
    internal sealed class ConnectProbe
    {
        private volatile bool _started;
        private volatile bool _connected;

        public bool StillConnecting => _started && !_connected;

        internal void MarkStarted()
        {
            _started = true;
        }

        internal void MarkConnected()
        {
            _connected = true;
        }
    }

    /// <summary>The probe travels on the request's options to <see cref="ConnectAsync"/>.</summary>
    internal static readonly HttpRequestOptionsKey<ConnectProbe> ConnectProbeKey = new("EArm.ConnectProbe");

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

    public async IAsyncEnumerable<JsonNode> GetAllPagesAsync(GraphRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = GraphUrl.Build(_options.Cloud, request);
        var visited = new HashSet<string>(StringComparer.Ordinal) { uri.AbsoluteUri };
        var maxPages = Math.Max(1, _options.MaxPages);
        var maxItems = Math.Max(1, _options.MaxItems);
        var pages = 0;
        var yielded = 0;
        while (true)
        {
            if (pages >= maxPages)
            {
                throw new GraphTransportException(
                    GraphTransportFailure.PagingLimitExceeded,
                    $"GET {GraphUrl.RedactForLog(uri)}: the collection has more than {maxPages.ToString(CultureInfo.InvariantCulture)} pages ({yielded.ToString(CultureInfo.InvariantCulture)} items so far); stopping instead of paging without end. Narrow the query with $filter or $top.");
            }

            pages++;
            var response = await SendAsync("GET", uri, request, null, cancellationToken).ConfigureAwait(false);
            if (response.Body is not JsonObject page)
            {
                yield break;
            }

            if (page["value"] is JsonArray items)
            {
                foreach (var item in items)
                {
                    if (item is null)
                    {
                        continue;
                    }

                    if (yielded >= maxItems)
                    {
                        throw new GraphTransportException(
                            GraphTransportFailure.PagingLimitExceeded,
                            $"GET {GraphUrl.RedactForLog(uri)}: the collection has more than {maxItems.ToString(CultureInfo.InvariantCulture)} items; stopping instead of reading without end. Narrow the query with $filter.");
                    }

                    yielded++;
                    yield return item;
                }
            }
            else
            {
                // Not a collection: a single entity was addressed.
                yield return page;
                yield break;
            }

            var nextLink = page["@odata.nextLink"] is JsonValue link && link.TryGetValue<string>(out var text) ? text : null;
            if (string.IsNullOrEmpty(nextLink))
            {
                yield break;
            }

            uri = GraphUrl.ParseNextLink(_options.Cloud, nextLink);
            if (!visited.Add(uri.AbsoluteUri))
            {
                throw new GraphTransportException(GraphTransportFailure.InvalidResponse, $"Graph returned an @odata.nextLink that was already followed ({GraphUrl.RedactForLog(uri)}); stopping instead of looping.");
            }
        }
    }

    public async Task<IReadOnlyList<BatchResponse>> BatchAsync(IReadOnlyList<BatchRequest> requests, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            return [];
        }

        var urls = new string[requests.Count];
        for (var i = 0; i < requests.Count; i++)
        {
            if (requests[i].Path.Contains('?', StringComparison.Ordinal))
            {
                throw new ArgumentException($"Batch request {i} embeds a query string in its path; use UrlParameters.", nameof(requests));
            }

            urls[i] = BuildBatchUrl(requests[i]);
            GraphUrl.ValidateBatchUrl(urls[i]);
        }

        var results = new BatchResponse?[requests.Count];
        var chunks = 0;
        var failedAt = requests.Count;
        Exception? envelopeFailure = null;
        for (var offset = 0; offset < requests.Count; offset += MaxBatchSize)
        {
            chunks++;
            var count = Math.Min(MaxBatchSize, requests.Count - offset);
            var body = new JsonObject();
            var array = new JsonArray();
            var chunkIdempotent = true;
            for (var i = 0; i < count; i++)
            {
                array.Add(BuildBatchItem(requests[offset + i], urls[offset + i], WireId(offset + i)));
                chunkIdempotent &= requests[offset + i].IsIdempotent;
            }

            body["requests"] = array;

            // The envelope is a POST, but it only repeats what its sub-requests are allowed to repeat: one non-idempotent sub-request makes the whole POST non-idempotent.
            var batchRequest = new GraphRequest("/$batch", api, hint: hint) { Idempotent = chunkIdempotent };
            JsonArray responses;
            try
            {
                var response = await SendAsync("POST", batchRequest, body, cancellationToken).ConfigureAwait(false);
                if (response.Body is not JsonObject envelope || envelope["responses"] is not JsonArray parsed)
                {
                    throw new GraphTransportException(GraphTransportFailure.InvalidResponse, "$batch returned no 'responses' array.");
                }

                responses = parsed;
            }
            catch (Exception ex) when (offset > 0 && ex is GraphException or GraphTransportException)
            {
                // An earlier chunk already ran: its sub-requests may have committed, so its results must not be thrown away. Report this
                // chunk (and, because the later chunks may depend on it, every chunk after it) per item instead of throwing. A failure
                // of the very first chunk still throws: nothing has been reported yet, and the exception carries the precise outcome.
                failedAt = offset;
                envelopeFailure = ex;
                break;
            }

            foreach (var item in responses)
            {
                if (item is not JsonObject sub)
                {
                    continue;
                }

                var id = sub["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var idText) ? idText : null;
                if (id is null || !TryIndexFromWireId(id, requests.Count, out var index) || index < offset || index >= offset + count)
                {
                    continue;
                }

                var status = sub["status"] is JsonValue statusValue && statusValue.TryGetValue<int>(out var statusInt) ? statusInt : 0;
                var subBody = sub["body"]?.DeepClone();
                var request = requests[index];
                var reportedId = request.Id ?? WireId(index);
                if (status is >= 200 and < 400)
                {
                    results[index] = new BatchResponse(reportedId, request, (HttpStatusCode)status, subBody, null);
                    continue;
                }

                var error = GraphErrorParser.FromBatchResponse(status, subBody, sub["headers"] as JsonObject, hint, _time);
                error.IsAmbiguous = status >= 500 && IsWrite(request.Method);
                results[index] = new BatchResponse(reportedId, request, (HttpStatusCode)status, subBody, error);
            }
        }

        if (envelopeFailure is not null)
        {
            for (var i = failedAt; i < results.Length; i++)
            {
                results[i] = EnvelopeFailureResponse(requests[i], WireId(i), envelopeFailure, i < Math.Min(failedAt + MaxBatchSize, requests.Count), hint);
            }
        }

        var failed = 0;
        for (var i = 0; i < results.Length; i++)
        {
            if (results[i] is null)
            {
                // Never "OK by default": a sub-request without an answer is a failure, and for a write its outcome is unknown.
                var request = requests[i];
                results[i] = new BatchResponse(
                    request.Id ?? WireId(i),
                    request,
                    HttpStatusCode.InternalServerError,
                    null,
                    new GraphException(HttpStatusCode.InternalServerError, string.Empty, "$batch returned no response for this sub-request.", hint) { IsAmbiguous = IsWrite(request.Method) });
            }

            if (results[i]!.Error is { IsAlreadyExists: false })
            {
                failed++;
            }
        }

        GraphLog.BatchSummary(_logger, requests.Count, chunks, failed);

        for (var i = 0; i < results.Length; i++)
        {
            var current = results[i]!;
            if (current.Error is null || current.Error.IsAlreadyExists)
            {
                continue;
            }

            if (i >= failedAt)
            {
                // The envelope itself failed under the retry policy (or was never sent): nothing to re-send one by one.
                continue;
            }

            var request = current.Request;

            // A non-idempotent sub-request is only repeated when the failure proves it was not processed: a 429, or a replication-lag
            // answer (the referenced object was not there yet). A 5xx or a missing answer may hide a commit; those stay failed (IsAmbiguous).
            if (!request.IsIdempotent && !(current.Error.Status == HttpStatusCode.TooManyRequests || current.Error.IsReplication))
            {
                continue;
            }

            var single = new GraphRequest(request.Path, api, request.Headers, advanced: false, hint) { UrlParameters = request.UrlParameters, Idempotent = request.Idempotent };
            try
            {
                var response = await SendAsync(request.Method, single, request.Body, cancellationToken).ConfigureAwait(false);
                results[i] = new BatchResponse(current.Id, request, response.Status, response.Body, null);
            }
            catch (GraphException ex)
            {
                results[i] = new BatchResponse(current.Id, request, ex.Status, current.Body, ex);
            }
            catch (GraphTransportException ex) when (ex.Failure is GraphTransportFailure.Network or GraphTransportFailure.Timeout or GraphTransportFailure.ResponseTooLarge)
            {
                // One sub-request that cannot be re-sent must not discard the results of the others: report it as that item's failure.
                var status = ex.Failure == GraphTransportFailure.Timeout ? HttpStatusCode.GatewayTimeout : HttpStatusCode.BadGateway;
                var error = new GraphException(status, string.Empty, ex.Message, hint) { IsAmbiguous = ex.Ambiguous };
                results[i] = new BatchResponse(current.Id, request, status, current.Body, error);
            }
        }

        var final = new BatchResponse[results.Length];
        for (var i = 0; i < results.Length; i++)
        {
            final[i] = results[i]!;
        }

        return final;
    }

    /// <summary>
    /// The per-item outcome of a <c>$batch</c> envelope that failed after earlier chunks had already been reported. <paramref name="sent"/> is true for the
    /// items of the failed envelope (a write's outcome is then ambiguous when the failure was) and false for items of later chunks that were never sent
    /// (never ambiguous: safe to run again).
    /// </summary>
    private static BatchResponse EnvelopeFailureResponse(BatchRequest request, string wireId, Exception failure, bool sent, RetryHint hint)
    {
        HttpStatusCode status;
        string code;
        string message;
        bool envelopeAmbiguous;
        JsonObject? error = null;
        TimeSpan? retryAfter = null;
        string? requestId = null;
        string? clientRequestId = null;
        if (failure is GraphException graph)
        {
            status = graph.Status;
            code = graph.Code;
            message = graph.GraphMessage;
            envelopeAmbiguous = graph.IsAmbiguous;
            error = graph.Error;
            retryAfter = graph.RetryAfter;
            requestId = graph.RequestId;
            clientRequestId = graph.ClientRequestId;
        }
        else
        {
            var transport = (GraphTransportException)failure;
            status = transport.Failure switch
            {
                GraphTransportFailure.NotAuthenticated => HttpStatusCode.Unauthorized,
                GraphTransportFailure.Timeout => HttpStatusCode.GatewayTimeout,
                GraphTransportFailure.RequestTooLarge or GraphTransportFailure.UnsafeUrl => HttpStatusCode.BadRequest,
                _ => HttpStatusCode.BadGateway,
            };
            code = "BatchEnvelopeFailed";
            message = transport.Message;
            envelopeAmbiguous = transport.Ambiguous;
        }

        var reportedId = request.Id ?? wireId;
        if (!sent)
        {
            var notSent = new GraphException(status, "BatchNotSent", "Not sent: an earlier $batch envelope failed (" + message + "); nothing was written for this sub-request.", hint);
            return new BatchResponse(reportedId, request, status, null, notSent);
        }

        var text = "The $batch envelope failed: " + message;
        var item = new GraphException(status, code, text, hint, error, retryAfter, requestId, clientRequestId)
        {
            IsAmbiguous = envelopeAmbiguous && IsWrite(request.Method),
        };
        return new BatchResponse(reportedId, request, status, null, item);
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
        ValidateHeaders(headers);
        var payload = body is null ? null : SerializeBody(body);
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

    private static bool IsWrite(string method)
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
            using var message = BuildMessage(method, uri, headers, payload, graphToken, clientRequestId);
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

                var requestId = FirstHeader(response.Headers, "request-id");
                GraphLog.GraphResponse(_logger, method, logTarget, (int)response.StatusCode, stopwatch.ElapsedMilliseconds, requestId, clientRequestId);
                try
                {
                    return Map(response, text, hint, clientRequestId, requestId);
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

    private GraphResponse Map(HttpResponseMessage response, string text, RetryHint hint, string clientRequestId, string? requestId)
    {
        var status = response.StatusCode;
        var code = (int)status;
        if (code is >= 200 and < 300)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new GraphResponse(status, null);
            }

            try
            {
                return new GraphResponse(status, JsonNode.Parse(JsonSurrogateSanitizer.Replace(text.TrimStart('﻿'))));
            }
            catch (JsonException ex)
            {
                throw new GraphTransportException(GraphTransportFailure.InvalidResponse, $"Graph answered {code} with a body that is not JSON ({ex.Message}). request-id {requestId ?? "n/a"}.", ex);
            }
        }

        if (code is >= 300 and < 400)
        {
            throw new GraphException(status, "UnexpectedRedirect", $"Graph answered with a redirect ({code}); redirects are never followed because the credentials must not leave the Graph host.", hint, null, null, requestId, clientRequestId);
        }

        throw GraphErrorParser.FromResponse(status, text, response.Headers, clientRequestId, hint, _time);
    }

    private HttpRequestMessage BuildMessage(string method, Uri uri, IReadOnlyDictionary<string, string> headers, string? payload, GraphToken token, string clientRequestId)
    {
        var message = new HttpRequestMessage(new HttpMethod(method), uri);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        message.Headers.TryAddWithoutValidation("Accept", "application/json");
        message.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        message.Headers.TryAddWithoutValidation("client-request-id", clientRequestId);
        message.Headers.TryAddWithoutValidation("return-client-request-id", "true");

        string? contentType = null;
        foreach (var pair in headers)
        {
            if (string.Equals(pair.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = pair.Value;
                continue;
            }

            if (IgnoredRequestHeaders.Contains(pair.Key))
            {
                continue;
            }

            if (string.Equals(pair.Key, "Accept", StringComparison.OrdinalIgnoreCase))
            {
                message.Headers.Remove("Accept");
            }

            message.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        }

        if (payload is not null)
        {
            message.Content = new StringContent(payload, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), "application/json");
            if (contentType is not null && MediaTypeHeaderValue.TryParse(contentType, out var parsed))
            {
                message.Content.Headers.ContentType = parsed;
            }
        }

        return message;
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

    private string SerializeBody(JsonNode body)
    {
        if (_options.Cloud != GraphCloud.Public)
        {
            body = body.DeepClone();
            RewriteODataIds(body, _options.Cloud);
        }

        return body.ToJsonString(BodyJsonOptions);
    }

    /// <summary>
    /// The Edge pass and the providers write public-cloud references (<c>@odata.id</c>, <c>owners@odata.bind</c>, <c>members@odata.bind</c>, ...); in a
    /// sovereign cloud they must name that cloud's Graph host. Every string value, and every string element of an array value, of a key ending in
    /// <c>@odata.id</c> or <c>@odata.bind</c> that starts with the public Graph origin is rewritten.
    /// </summary>
    private static void RewriteODataIds(JsonNode? node, GraphCloud cloud)
    {
        var publicOrigin = GraphCloud.Public.GraphOrigin + "/";
        switch (node)
        {
            case JsonObject obj:
                foreach (var pair in obj.ToArray())
                {
                    if (IsReferenceKey(pair.Key))
                    {
                        if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text))
                        {
                            obj[pair.Key] = RewriteOrigin(text, publicOrigin, cloud);
                            continue;
                        }

                        if (pair.Value is JsonArray references)
                        {
                            for (var i = 0; i < references.Count; i++)
                            {
                                if (references[i] is JsonValue element && element.TryGetValue<string>(out var elementText))
                                {
                                    references[i] = RewriteOrigin(elementText, publicOrigin, cloud);
                                }
                            }

                            continue;
                        }
                    }

                    RewriteODataIds(pair.Value, cloud);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    RewriteODataIds(item, cloud);
                }

                break;
        }
    }

    private static bool IsReferenceKey(string key)
    {
        return key.EndsWith("@odata.id", StringComparison.OrdinalIgnoreCase) || key.EndsWith("@odata.bind", StringComparison.OrdinalIgnoreCase);
    }

    private static string RewriteOrigin(string text, string publicOrigin, GraphCloud cloud)
    {
        return text.StartsWith(publicOrigin, StringComparison.OrdinalIgnoreCase) ? cloud.GraphOrigin + "/" + text[publicOrigin.Length..] : text;
    }
    private static void ValidateHeaders(IReadOnlyDictionary<string, string> headers)
    {
        foreach (var pair in headers)
        {
            if (ForbiddenRequestHeaders.Contains(pair.Key))
            {
                throw new ArgumentException($"The '{pair.Key}' header is managed by the client and cannot be supplied by a request.", nameof(headers));
            }

            if (pair.Key.Length == 0 || pair.Key.AsSpan().IndexOfAny("\r\n:") >= 0 || pair.Value.AsSpan().IndexOfAny('\r', '\n') >= 0)
            {
                throw new ArgumentException($"The header '{pair.Key}' is not a valid HTTP header.", nameof(headers));
            }
        }
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

    private static string? FirstHeader(HttpResponseHeaders headers, string name)
    {
        if (!headers.TryGetValues(name, out var values))
        {
            return null;
        }

        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static JsonObject BuildBatchItem(BatchRequest request, string url, string wireId)
    {
        var item = new JsonObject
        {
            ["id"] = wireId,
            ["method"] = request.Method.ToUpperInvariant(),
            ["url"] = url,
        };

        // JsonObject keys are case-sensitive but HTTP header names are not: track a caller-supplied Content-Type of any case so
        // the default is added only when none exists, and the caller's value and spelling win.
        var headers = new JsonObject();
        var hasContentType = false;
        if (request.Headers is not null)
        {
            foreach (var pair in request.Headers)
            {
                headers[pair.Key] = pair.Value;
                hasContentType |= string.Equals(pair.Key, "Content-Type", StringComparison.OrdinalIgnoreCase);
            }
        }

        if (request.Body is not null && !hasContentType)
        {
            headers["Content-Type"] = "application/json";
        }

        if (headers.Count > 0)
        {
            item["headers"] = headers;
        }

        if (request.Body is not null)
        {
            item["body"] = request.Body.DeepClone();
        }

        return item;
    }

    private static string BuildBatchUrl(BatchRequest request)
    {
        var path = request.Path.StartsWith('/') ? request.Path : "/" + request.Path;
        var query = GraphUrl.BuildQuery(request.UrlParameters);
        return query.Length == 0 ? path : path + "?" + query;
    }

    private static string WireId(int index)
    {
        return (index + 1).ToString(CultureInfo.InvariantCulture);
    }

    private static bool TryIndexFromWireId(string id, int count, out int index)
    {
        index = -1;
        if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1 || number > count)
        {
            return false;
        }

        index = number - 1;
        return true;
    }
}
