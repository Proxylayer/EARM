using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EArm.Graph;

/// <summary>
/// A Microsoft Graph error response (<c>{"error":{"code","message","innerError"}}</c>) with its real HTTP status, the
/// <c>Retry-After</c> header and the <c>request-id</c> / <c>client-request-id</c> headers.
/// </summary>
public sealed partial class GraphException : Exception
{
    public GraphException(
        HttpStatusCode status,
        string code,
        string graphMessage,
        RetryHint hint = RetryHint.Default,
        JsonObject? error = null,
        TimeSpan? retryAfter = null,
        string? requestId = null,
        string? clientRequestId = null)
        : base(FormatMessage(status, code, graphMessage, requestId, clientRequestId, ErrorDate(error)))
    {
        Status = status;
        Code = SecretScrubber.Scrub(code);
        GraphMessage = SecretRedactor.RedactText(graphMessage ?? string.Empty);
        Hint = hint;
        Error = ScrubError(error);
        RetryAfter = retryAfter;
        RequestId = requestId;
        ClientRequestId = clientRequestId;

        IsTransient = IsTransientStatus(status);
        IsAlreadyExists = IsAlreadyExistsError(status, Code, GraphMessage);
        IsReplication = hint == RetryHint.ExpectReplicationLag && IsReplicationError(status, Code, GraphMessage);
    }

    public HttpStatusCode Status { get; }

    /// <summary>Graph error code, e.g. <c>Request_ResourceNotFound</c>.</summary>
    public string Code { get; }

    /// <summary>Graph error message (redacted).</summary>
    public string GraphMessage { get; }

    public RetryHint Hint { get; }

    /// <summary>The parsed <c>error</c> object (including <c>innerError</c>), when available; registered secret values (see <see cref="SecretScrubber"/>) are masked.</summary>
    public JsonObject? Error { get; }

    /// <summary>The <c>Retry-After</c> response header (delta seconds or HTTP-date), when present.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>The <c>request-id</c> response header (or <c>innerError.request-id</c>), for support tickets.</summary>
    public string? RequestId { get; }

    /// <summary>The <c>client-request-id</c> EARM sent with the failed request (echoed by Graph).</summary>
    public string? ClientRequestId { get; }

    /// <summary>429 or 500/502/503/504.</summary>
    public bool IsTransient { get; }

    /// <summary>A not-yet-replicated reference; only ever true under <see cref="RetryHint.ExpectReplicationLag"/>.</summary>
    public bool IsReplication { get; }

    /// <summary>Idempotent success: the object or edge already exists. Never retried; the caller re-reads by key.</summary>
    public bool IsAlreadyExists { get; }

    /// <summary>
    /// A 5xx answer to a WRITE (POST, PATCH, PUT, DELETE): Graph may have committed the change before failing, so the outcome is unknown. The
    /// client does not repeat a non-idempotent write after such an answer; the caller re-reads by key (see <see cref="GraphTransportException.Ambiguous"/>).
    /// Always false for reads, for 429 and for 4xx answers.
    /// </summary>
    public bool IsAmbiguous { get; internal set; }

    public bool IsNotFound => Status == HttpStatusCode.NotFound;

    public bool IsUnauthorized => Status == HttpStatusCode.Unauthorized;

    public bool IsForbidden => Status == HttpStatusCode.Forbidden;

    public static bool IsTransientStatus(HttpStatusCode status)
    {
        return status is HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;
    }

    /// <summary>The full text with registered secret values and secret-looking fragments masked.</summary>
    public override string ToString()
    {
        return SecretRedactor.RedactText(base.ToString());
    }

    private static JsonObject? ScrubError(JsonObject? error)
    {
        if (error is null || !SecretScrubber.HasValues)
        {
            return error;
        }

        var text = error.ToJsonString();
        var scrubbed = SecretScrubber.Scrub(text);
        if (string.Equals(text, scrubbed, StringComparison.Ordinal))
        {
            return error;
        }

        try
        {
            return JsonNode.Parse(scrubbed) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            // A registered value that is not JSON-safe broke the document: keep nothing rather than a leaking object.
            return null;
        }
    }

    /// <summary>The "idempotent-success" retry row: an "already exists" answer that is not retried.</summary>
    public static bool IsAlreadyExistsError(HttpStatusCode status, string code, string message)
    {
        if (status is not (HttpStatusCode.BadRequest or HttpStatusCode.Conflict))
        {
            return false;
        }

        if (string.Equals(code, "Request_MultipleObjectsWithSameKeyValue", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return AlreadyExistsPattern().IsMatch(message ?? string.Empty);
    }

    /// <summary>
    /// The "replication" retry rows (404 not-found, or the listed 400 message fragments, including the Conditional Access API's numbered
    /// "<c>NNNN: … with id … does not exist in the directory</c>" answers for a referenced object that has not replicated to it yet).
    /// </summary>
    public static bool IsReplicationError(HttpStatusCode status, string code, string message)
    {
        if (status == HttpStatusCode.NotFound)
        {
            return string.IsNullOrEmpty(code) || code.Contains("NotFound", StringComparison.OrdinalIgnoreCase);
        }

        if (status == HttpStatusCode.BadRequest)
        {
            return ReplicationPattern().IsMatch(message ?? string.Empty);
        }

        return false;
    }

    /// <summary>
    /// <c>Graph {status} {code}: {message}</c>, followed by the support identifiers Graph returned (<c>request-id</c>, <c>client-request-id</c> and the
    /// <c>innerError.date</c>) when there are any: a support ticket needs them. Registered secret values and secret-looking fragments are masked.
    /// </summary>
    private static string FormatMessage(HttpStatusCode status, string code, string graphMessage, string? requestId, string? clientRequestId, string? date)
    {
        var redacted = SecretRedactor.RedactText(graphMessage ?? string.Empty);
        var text = $"Graph {(int)status} {code}: {redacted}".TrimEnd(':', ' ');
        var ids = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            ids.Add("request-id " + requestId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(clientRequestId))
        {
            ids.Add("client-request-id " + clientRequestId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(date))
        {
            ids.Add("date " + date.Trim());
        }

        if (ids.Count > 0)
        {
            text += " (" + string.Join(", ", ids) + ")";
        }

        return SecretScrubber.Scrub(text);
    }

    /// <summary>The <c>innerError.date</c> of a Graph error object (when Graph answered it), else <c>null</c>.</summary>
    private static string? ErrorDate(JsonObject? error)
    {
        return error?["innerError"] is JsonObject inner && inner["date"] is JsonValue value && value.TryGetValue<string>(out var date) ? date : null;
    }

    [GeneratedRegex(@"already exists?\b|Permission entry already exists|Permission being assigned already exists|conflicting object .*? (?:is|are) present|One or more added object references already exist", RegexOptions.IgnoreCase)]
    private static partial Regex AlreadyExistsPattern();

    // The Conditional Access API answers a reference to an object it cannot see yet (a named location, authentication strength,
    // authentication context, group, user or application created moments earlier) with a numbered 400 such as
    // "1040: NamedLocation with id … does not exist in the directory." — the last two alternatives cover that family.
    [GeneratedRegex(@"does not exist or one of its queried reference-property objects are not present|source resource object|don'?t exist|doesn'?t exist|PrincipalNotFound|does not reference a valid application object|not found in the directory|does not exist in the directory|^\s*\d{3,4}:.*\bdoes not exist\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReplicationPattern();
}
