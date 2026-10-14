using System;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EntraArm.Az;

/// <summary>
/// A Microsoft Graph error (<c>{"error":{"code","message","innerError"}}</c>) surfaced by <c>az rest</c>.
/// The HTTP status is taken from az's reason phrase when present, otherwise derived from the error code.
/// </summary>
public sealed partial class GraphException : Exception
{
    public GraphException(
        HttpStatusCode status,
        string code,
        string graphMessage,
        RetryHint hint = RetryHint.Default,
        JsonObject? error = null,
        string? rawStandardError = null,
        TimeSpan? retryAfter = null,
        string? requestId = null)
        : base(FormatMessage(status, code, graphMessage))
    {
        Status = status;
        Code = code ?? string.Empty;
        GraphMessage = SecretRedactor.RedactText(graphMessage ?? string.Empty);
        Hint = hint;
        Error = error;
        RawStandardError = SecretRedactor.RedactText(rawStandardError ?? string.Empty);
        RetryAfter = retryAfter;
        RequestId = requestId;

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

    /// <summary>The parsed <c>error</c> object (including <c>innerError</c>), when available.</summary>
    public JsonObject? Error { get; }

    /// <summary>The captured stderr, redacted.</summary>
    public string RawStandardError { get; }

    /// <summary>Parsed from a <c>Retry-After</c> / "retry after N seconds" fragment when present.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary><c>innerError.request-id</c>, for support tickets.</summary>
    public string? RequestId { get; }

    /// <summary>429 or 500/502/503/504.</summary>
    public bool IsTransient { get; }

    /// <summary>A not-yet-replicated reference; only ever true under <see cref="RetryHint.ExpectReplicationLag"/>.</summary>
    public bool IsReplication { get; }

    /// <summary>Idempotent success: the object or edge already exists. Never retried; the caller re-reads by key.</summary>
    public bool IsAlreadyExists { get; }

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

    /// <summary>Table 3.6 "idempotent-success" row.</summary>
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

    /// <summary>Table 3.6 "replication" rows (404 not-found, or the listed 400 message fragments).</summary>
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

    private static string FormatMessage(HttpStatusCode status, string code, string graphMessage)
    {
        var redacted = SecretRedactor.RedactText(graphMessage ?? string.Empty);
        return $"Graph {(int)status} {code}: {redacted}".TrimEnd(':', ' ');
    }

    [GeneratedRegex(@"already exists?\b|Permission entry already exists|Permission being assigned already exists|conflicting object .*? (?:is|are) present|One or more added object references already exist", RegexOptions.IgnoreCase)]
    private static partial Regex AlreadyExistsPattern();

    [GeneratedRegex(@"does not exist or one of its queried reference-property objects are not present|source resource object|don'?t exist|doesn'?t exist|PrincipalNotFound|does not reference a valid application object|not found in the directory", RegexOptions.IgnoreCase)]
    private static partial Regex ReplicationPattern();
}
