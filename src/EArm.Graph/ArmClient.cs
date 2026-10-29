using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
/// Calls the Azure Resource Manager REST API of the cloud: <c>GET</c> and <c>PATCH</c> of one resource and a paged <c>GET</c> of a collection, each
/// <c>{origin}{resourcePath}?api-version={apiVersion}</c> with a bearer token for the cloud's Resource Manager scope (<see cref="GraphCloud.ResourceManagerScopes"/>),
/// a separate token audience from Graph and Key Vault. The resource path is validated BEFORE the URL is built and must survive URL parsing unchanged (nothing is
/// escaped or normalised silently), so a template value cannot steer the request to another host or climb out of the path; the origin is always the fixed
/// <see cref="GraphCloud.ResourceManagerOrigin"/>, and a collection's <c>nextLink</c> is followed only when it stays on that origin. Redirects are never
/// followed. Bodies (request and response) and tokens are never logged; an error message carries ARM's <c>error.code</c> and <c>error.message</c> only.
/// <para>
/// Failures follow <see cref="HttpGraphClient"/>: 429 and 5xx are retried through the shared <see cref="RetryPolicy"/> (honouring <c>Retry-After</c>), a 401 once
/// after the cached token is dropped, and a write is never abandoned once it is on the wire (the caller's cancellation is honoured until the <c>PATCH</c> is
/// sent; after that only the request timeout bounds it), with a failure that may hide a commit marked ambiguous. ARM answers errors in the Graph shape
/// <c>{"error":{"code","message"}}</c> (Learn: Azure REST API reference, Azure Resource Manager error responses), read by <see cref="GraphErrorParser"/>.
/// </para>
/// </summary>
public sealed class ArmClient
{
    /// <summary>The largest response body read, in bytes (counted after decompression): the Graph client's default, so a hostile endpoint cannot stream without end.</summary>
    public const long MaxResponseBytes = 16L * 1024 * 1024;

    /// <summary>The largest request body sent, in bytes; a bigger <c>PATCH</c> is refused before anything is sent.</summary>
    public const long MaxRequestBodyBytes = 4L * 1024 * 1024;

    /// <summary>The most pages a collection read follows before it stops instead of paging without end.</summary>
    public const int MaxPages = 2_000;

    /// <summary>The most items a collection read returns before it stops instead of reading without end.</summary>
    public const int MaxItems = 500_000;

    /// <summary>The longest resource path accepted.</summary>
    public const int MaxResourcePathLength = 2048;

    private static readonly JsonSerializerOptions BodyJsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _http;
    private readonly IGraphTokenProvider _tokens;
    private readonly RetryPolicy _retryPolicy;
    private readonly GraphCloud _cloud;
    private readonly TimeSpan _timeout;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly string _userAgent;
    private readonly TokenScopes _scopes;

    /// <param name="scopes">The Resource Manager token scopes. <c>null</c> means <paramref name="cloud"/>'s <see cref="GraphCloud.ResourceManagerScopes"/> (<c>.default</c>); explicit scopes must belong to the cloud's Resource Manager origin.</param>
    public ArmClient(HttpClient http, IGraphTokenProvider tokens, RetryPolicy retryPolicy, GraphCloud cloud, TimeSpan? timeout = null, ILogger? logger = null, TimeProvider? timeProvider = null, string? userAgentVersion = null, TokenScopes? scopes = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(retryPolicy);
        ArgumentNullException.ThrowIfNull(cloud);
        _http = http;
        _tokens = tokens;
        _retryPolicy = retryPolicy;
        _cloud = cloud;
        _timeout = timeout ?? TimeSpan.FromSeconds(120);
        _logger = logger ?? NullLogger.Instance;
        _time = timeProvider ?? TimeProvider.System;
        _userAgent = "EARM/" + (userAgentVersion ?? GraphVersion.Current);
        _scopes = TokenScopeGuard.Resolve(scopes, cloud.ResourceManagerScopes, cloud, nameof(scopes));
    }

    /// <summary>The scopes of the Resource Manager token this client attaches.</summary>
    public TokenScopes Scopes => _scopes;

    /// <summary>
    /// A resource path: starts with <c>/</c>, at most <see cref="MaxResourcePathLength"/> characters, no empty, <c>.</c> or <c>..</c> segment, and only characters a
    /// URL path carries as they are (ASCII letters and digits, <c>- . _ ~ ! $ &amp; ' ( ) * + , ; = : @</c> and <c>/</c>). So no scheme or host, no query or
    /// fragment, no backslash, no percent-encoding and nothing <see cref="Uri"/> would escape.
    /// </summary>
    public static bool IsValidResourcePath(string? resourcePath)
    {
        if (string.IsNullOrEmpty(resourcePath) || resourcePath.Length > MaxResourcePathLength || resourcePath[0] != '/')
        {
            return false;
        }

        foreach (var c in resourcePath)
        {
            if (!char.IsAsciiLetterOrDigit(c) && "-._~!$&'()*+,;=:@/".IndexOf(c, StringComparison.Ordinal) < 0)
            {
                return false;
            }
        }

        foreach (var segment in resourcePath[1..].Split('/'))
        {
            if (segment is "" or "." or "..")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>An <c>api-version</c>: 1-64 ASCII letters, digits, hyphens and periods (<c>2020-10-01</c>, <c>2024-02-01-preview</c>).</summary>
    public static bool IsValidApiVersion(string? apiVersion)
    {
        if (apiVersion is null || apiVersion.Length is < 1 or > 64)
        {
            return false;
        }

        foreach (var c in apiVersion)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '.')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The validated request URL: <c>{ResourceManagerOrigin}{resourcePath}?api-version={apiVersion}</c>, plus <c>&amp;$filter=</c> with
    /// <paramref name="filter"/> percent-encoded as a query value when one is given.
    /// </summary>
    /// <exception cref="ArgumentException">The resource path or api-version is malformed, or the URL did not keep the expected origin and path (nothing is sent).</exception>
    public Uri BuildUri(string resourcePath, string apiVersion, string? filter = null)
    {
        if (!IsValidResourcePath(resourcePath))
        {
            throw new ArgumentException($"'{Shorten(resourcePath)}' is not an Azure Resource Manager resource path (it must start with '/', name no host, query or fragment, have no empty, '.' or '..' segment, and use only characters a URL path carries unescaped).", nameof(resourcePath));
        }

        if (!IsValidApiVersion(apiVersion))
        {
            throw new ArgumentException($"'{Shorten(apiVersion)}' is not a valid api-version (1-64 letters, digits, hyphens and periods).", nameof(apiVersion));
        }

        var text = new StringBuilder(_cloud.ResourceManagerOrigin).Append(resourcePath).Append("?api-version=").Append(apiVersion);
        if (filter is not null)
        {
            text.Append("&$filter=").Append(GraphUrl.EncodeValue(filter));
        }

        var uri = new Uri(text.ToString(), UriKind.Absolute);
        if (CheckOrigin(uri) is not null || !string.Equals(uri.AbsolutePath, resourcePath, StringComparison.Ordinal))
        {
            throw new ArgumentException("The Azure Resource Manager URL did not keep the expected origin and path.", nameof(resourcePath));
        }

        return uri;
    }

    /// <summary>Reads one resource. <c>null</c> when ARM answered 2xx with an empty body.</summary>
    /// <exception cref="ArgumentException">An invalid resource path or api-version (nothing is sent).</exception>
    /// <exception cref="GraphException">ARM answered with an error status (403 no access, 404 no such resource, ...).</exception>
    /// <exception cref="GraphTransportException">No token, ARM could not be reached, the call timed out, or the answer was not a JSON object.</exception>
    public async Task<JsonObject?> GetAsync(string resourcePath, string apiVersion, CancellationToken cancellationToken)
    {
        var uri = BuildUri(resourcePath, apiVersion);
        var body = await SendAsync(HttpMethod.Get, uri, null, cancellationToken).ConfigureAwait(false);
        return AsObject(body, "GET", uri);
    }

    /// <summary>
    /// Reads a collection: every object of each page's <c>value</c> array, following <c>nextLink</c> (an absolute URL, followed only when it stays on the
    /// cloud's Resource Manager origin) until a page has none.
    /// </summary>
    /// <param name="filter">An optional OData <c>$filter</c> (for example <c>atScope()</c>), percent-encoded into the query.</param>
    /// <exception cref="ArgumentException">An invalid resource path or api-version (nothing is sent).</exception>
    /// <exception cref="GraphException">ARM answered with an error status.</exception>
    /// <exception cref="GraphTransportException">
    /// As <see cref="GetAsync"/>; also a page that is not a collection, a <c>nextLink</c> to another origin or one already followed (<see cref="GraphTransportFailure.UnsafeUrl"/> /
    /// <see cref="GraphTransportFailure.InvalidResponse"/>), or more than <see cref="MaxPages"/> pages or <see cref="MaxItems"/> items.
    /// </exception>
    public async IAsyncEnumerable<JsonObject> ListAsync(string resourcePath, string apiVersion, string? filter = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var uri = BuildUri(resourcePath, apiVersion, filter);
        var visited = new HashSet<string>(StringComparer.Ordinal) { uri.AbsoluteUri };
        var pages = 0;
        var yielded = 0;
        while (true)
        {
            if (pages >= MaxPages)
            {
                throw new GraphTransportException(
                    GraphTransportFailure.PagingLimitExceeded,
                    $"GET {uri.AbsolutePath}: the collection has more than {MaxPages.ToString(CultureInfo.InvariantCulture)} pages ({yielded.ToString(CultureInfo.InvariantCulture)} items so far); stopping instead of paging without end.");
            }

            pages++;
            var page = AsObject(await SendAsync(HttpMethod.Get, uri, null, cancellationToken).ConfigureAwait(false), "GET", uri);
            if (page?["value"] is not JsonArray items)
            {
                throw new GraphTransportException(GraphTransportFailure.InvalidResponse, $"GET {uri.AbsolutePath}: Azure Resource Manager answered without a 'value' array; the path does not address a collection.");
            }

            foreach (var item in items)
            {
                if (item is null)
                {
                    continue;
                }

                if (item is not JsonObject resource)
                {
                    throw new GraphTransportException(GraphTransportFailure.InvalidResponse, $"GET {uri.AbsolutePath}: an item of the collection is not a JSON object.");
                }

                if (yielded >= MaxItems)
                {
                    throw new GraphTransportException(
                        GraphTransportFailure.PagingLimitExceeded,
                        $"GET {uri.AbsolutePath}: the collection has more than {MaxItems.ToString(CultureInfo.InvariantCulture)} items; stopping instead of reading without end.");
                }

                yielded++;
                yield return resource;
            }

            var nextLink = page["nextLink"] is JsonValue link && link.TryGetValue<string>(out var text) ? text : null;
            if (string.IsNullOrEmpty(nextLink))
            {
                yield break;
            }

            uri = ParseNextLink(nextLink);
            if (!visited.Add(uri.AbsoluteUri))
            {
                throw new GraphTransportException(GraphTransportFailure.InvalidResponse, $"Azure Resource Manager returned a nextLink that was already followed ({uri.AbsolutePath}); stopping instead of looping.");
            }
        }
    }

    /// <summary>
    /// Updates one resource with <paramref name="body"/> and returns ARM's answer (<c>null</c> for an empty 2xx body). Once the request is sent it is not
    /// abandoned on cancellation; a timeout, dropped connection or 5xx after sending is marked ambiguous (the write may have been applied: re-read it).
    /// </summary>
    /// <exception cref="ArgumentException">An invalid resource path or api-version (nothing is sent).</exception>
    /// <exception cref="GraphException">ARM answered with an error status.</exception>
    /// <exception cref="GraphTransportException">As <see cref="GetAsync"/>; also a body over <see cref="MaxRequestBodyBytes"/> (nothing is sent).</exception>
    public async Task<JsonObject?> PatchAsync(string resourcePath, string apiVersion, JsonObject body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var uri = BuildUri(resourcePath, apiVersion);
        var payload = body.ToJsonString(BodyJsonOptions);
        var bytes = Encoding.UTF8.GetByteCount(payload);
        if (bytes > MaxRequestBodyBytes)
        {
            throw new GraphTransportException(
                GraphTransportFailure.RequestTooLarge,
                $"PATCH {uri.AbsolutePath}: the request body is {bytes.ToString(CultureInfo.InvariantCulture)} bytes, over the {MaxRequestBodyBytes.ToString(CultureInfo.InvariantCulture)}-byte limit; nothing was sent.");
        }

        var answer = await SendAsync(HttpMethod.Patch, uri, payload, cancellationToken).ConfigureAwait(false);
        return AsObject(answer, "PATCH", uri);
    }

    /// <summary>Parses and validates an absolute <c>nextLink</c>.</summary>
    /// <exception cref="GraphTransportException">The link is not an absolute URL on the cloud's Resource Manager origin, or its path is not intact.</exception>
    internal Uri ParseNextLink(string nextLink)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nextLink);
        if (!GraphUrl.TryParseAbsolute(nextLink, out var uri))
        {
            throw new GraphTransportException(GraphTransportFailure.UnsafeUrl, "Refusing to follow the Azure Resource Manager nextLink: it is not an absolute URL.");
        }

        if (CheckOrigin(uri) is { } problem)
        {
            throw new GraphTransportException(GraphTransportFailure.UnsafeUrl, $"Refusing to follow the Azure Resource Manager nextLink to '{GraphUrl.DescribeForError(uri)}': {problem}. Credentials are only ever sent to {_cloud.ResourceManagerOrigin}/.");
        }

        GraphUrl.EnsurePathIntact(uri, "nextLink");
        return uri;
    }

    /// <summary>The reason a URI is not on the cloud's Resource Manager origin, or null when it is.</summary>
    private string? CheckOrigin(Uri uri)
    {
        var loopback = _cloud.LoopbackPort != 0;
        if (!string.Equals(uri.Scheme, loopback ? Uri.UriSchemeHttp : Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return loopback ? "the scheme is not http (loopback test cloud)" : "the scheme is not https";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return "the URL carries user information";
        }

        if (uri.HostNameType != (loopback ? UriHostNameType.IPv4 : UriHostNameType.Dns) || !string.Equals(uri.Host, _cloud.ResourceManagerHost, StringComparison.OrdinalIgnoreCase))
        {
            return "the host is not " + _cloud.ResourceManagerHost;
        }

        if (loopback ? uri.Port != _cloud.LoopbackPort : !uri.IsDefaultPort)
        {
            return "the port is not the default https port";
        }

        return uri.Fragment.Length > 0 ? "the URL carries a fragment" : null;
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, Uri uri, string? payload, CancellationToken cancellationToken)
    {
        var scope = _scopes;
        var write = HttpGraphClient.IsWrite(method.Method);
        try
        {
            return await _retryPolicy.ExecuteAsync(
                token => SendOnceAsync(method, uri, payload, scope, write, token),
                RetryHint.Default,
                _ =>
                {
                    _tokens.Invalidate(scope);
                    return Task.CompletedTask;
                },
                cancellationToken,
                GraphRequest.IsIdempotentMethod(method.Method)).ConfigureAwait(false);
        }
        catch (GraphException ex) when (write && (int)ex.Status >= 500)
        {
            // A 5xx to a write may follow a commit: tell the caller the outcome is unknown (it re-reads rather than repeating the write).
            ex.IsAmbiguous = true;
            throw;
        }
    }

    private async Task<JsonNode?> SendOnceAsync(HttpMethod method, Uri uri, string? payload, TokenScopes scope, bool write, CancellationToken cancellationToken)
    {
        var clientRequestId = Guid.NewGuid().ToString();
        var verb = method.Method;
        var target = uri.AbsolutePath;
        var armToken = await _tokens.GetTokenAsync(scope, cancellationToken).ConfigureAwait(false);

        // A write is never abandoned once it is on the wire: the caller's cancellation (Ctrl+C) is honoured up to here and then only between requests,
        // so a PATCH that ARM applies is answered instead of left unknown. The request timeout still bounds it. A read stays cancellable throughout.
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        using var timeout = write ? new CancellationTokenSource() : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using var message = new HttpRequestMessage(method, uri);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken.Value);
        message.Headers.TryAddWithoutValidation("Accept", "application/json");
        message.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        message.Headers.TryAddWithoutValidation("x-ms-client-request-id", clientRequestId);
        if (payload is not null)
        {
            message.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (write || !cancellationToken.IsCancellationRequested)
        {
            // Without the Graph client's connect probe it cannot be told whether the PATCH left: a write's timeout is always ambiguous.
            throw TimeoutFailure(verb, target, stopwatch, clientRequestId, write);
        }
        catch (HttpRequestException ex)
        {
            // A failure before the request was written (DNS, refused connection, TLS) cannot hide a commit; anything later can.
            GraphLog.GraphTransportFailed(_logger, verb, target, stopwatch.ElapsedMilliseconds, ex.GetType().Name, clientRequestId);
            throw new GraphTransportException(
                GraphTransportFailure.Network,
                $"{verb} {target}: the request to {_cloud.ResourceManagerHost} failed ({ex.InnerException?.Message ?? ex.Message}).",
                ex,
                write && !HttpGraphClient.FailedBeforeSending(ex));
        }

        using (response)
        {
            byte[] bytes;
            try
            {
                bytes = await BoundedContent.ReadAsync(
                    response.Content,
                    MaxResponseBytes,
                    () => new GraphTransportException(
                        GraphTransportFailure.ResponseTooLarge,
                        $"{verb} {target}: the response is larger than {MaxResponseBytes.ToString(CultureInfo.InvariantCulture)} bytes (after decompression); it was discarded.",
                        null,
                        write),
                    timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (write || !cancellationToken.IsCancellationRequested)
            {
                throw TimeoutFailure(verb, target, stopwatch, clientRequestId, write);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                // The server answered but the body did not arrive intact: a write was (probably) applied.
                GraphLog.GraphTransportFailed(_logger, verb, target, stopwatch.ElapsedMilliseconds, ex.GetType().Name, clientRequestId);
                throw new GraphTransportException(GraphTransportFailure.Network, $"{verb} {target}: Azure Resource Manager dropped the connection ({ex.Message}).", ex, write);
            }

            var status = response.StatusCode;
            var code = (int)status;
            var requestId = GraphResponseReader.FirstHeader(response.Headers, "x-ms-request-id");
            GraphLog.GraphResponse(_logger, verb, target, code, stopwatch.ElapsedMilliseconds, requestId, clientRequestId);
            var text = Encoding.UTF8.GetString(bytes);
            if (code is >= 200 and < 300)
            {
                return Parse(text, verb, target, code, requestId);
            }

            if (code is >= 300 and < 400)
            {
                throw new GraphException(status, "UnexpectedRedirect", $"Azure Resource Manager answered with a redirect ({code}); redirects are never followed because the credentials must not leave {_cloud.ResourceManagerHost}.", RetryHint.Default, null, null, requestId, clientRequestId);
            }

            throw GraphErrorParser.FromResponse(status, text, response.Headers, clientRequestId, RetryHint.Default, _time);
        }
    }

    private GraphTransportException TimeoutFailure(string verb, string target, Stopwatch stopwatch, string clientRequestId, bool ambiguous)
    {
        GraphLog.GraphTransportFailed(_logger, verb, target, stopwatch.ElapsedMilliseconds, "timeout", clientRequestId);
        return new GraphTransportException(GraphTransportFailure.Timeout, $"{verb} {target}: Azure Resource Manager did not answer within {_timeout.TotalSeconds:F0} s.", null, ambiguous);
    }

    private static JsonNode? Parse(string text, string verb, string target, int code, string? requestId)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(JsonSurrogateSanitizer.Replace(text.TrimStart('﻿')));
        }
        catch (JsonException)
        {
            // The message must not echo the body.
            throw new GraphTransportException(GraphTransportFailure.InvalidResponse, $"{verb} {target}: Azure Resource Manager answered {code.ToString(CultureInfo.InvariantCulture)} with a body that is not JSON. x-ms-request-id {requestId ?? "n/a"}.");
        }
    }

    private static JsonObject? AsObject(JsonNode? body, string verb, Uri uri)
    {
        return body switch
        {
            null => null,
            JsonObject obj => obj,
            _ => throw new GraphTransportException(GraphTransportFailure.InvalidResponse, $"{verb} {uri.AbsolutePath}: Azure Resource Manager answered with JSON that is not an object."),
        };
    }

    private static string Shorten(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var cleaned = new StringBuilder();
        foreach (var c in text.Length > 40 ? text[..40] : text)
        {
            cleaned.Append(char.IsControl(c) ? '?' : c);
        }

        return text.Length > 40 ? cleaned + "..." : cleaned.ToString();
    }
}
