using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EntraArm.Az;

/// <summary>
/// Finds the Graph error document in az's output and turns it into a <see cref="GraphException"/>.
/// <c>az rest</c> prints failures as <c>ERROR: &lt;reason phrase&gt;({"error":{...}})</c> on stderr; the reason
/// phrase gives the HTTP status directly, the error code is the fallback.
/// </summary>
internal static partial class GraphErrorParser
{
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(5);

    private static readonly Dictionary<string, HttpStatusCode> ReasonPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bad Request"] = HttpStatusCode.BadRequest,
        ["Unauthorized"] = HttpStatusCode.Unauthorized,
        ["Payment Required"] = HttpStatusCode.PaymentRequired,
        ["Forbidden"] = HttpStatusCode.Forbidden,
        ["Not Found"] = HttpStatusCode.NotFound,
        ["Method Not Allowed"] = HttpStatusCode.MethodNotAllowed,
        ["Not Acceptable"] = HttpStatusCode.NotAcceptable,
        ["Request Timeout"] = HttpStatusCode.RequestTimeout,
        ["Conflict"] = HttpStatusCode.Conflict,
        ["Gone"] = HttpStatusCode.Gone,
        ["Precondition Failed"] = HttpStatusCode.PreconditionFailed,
        ["Unprocessable Entity"] = HttpStatusCode.UnprocessableEntity,
        ["Locked"] = HttpStatusCode.Locked,
        ["Too Many Requests"] = HttpStatusCode.TooManyRequests,
        ["Internal Server Error"] = HttpStatusCode.InternalServerError,
        ["Not Implemented"] = HttpStatusCode.NotImplemented,
        ["Bad Gateway"] = HttpStatusCode.BadGateway,
        ["Service Unavailable"] = HttpStatusCode.ServiceUnavailable,
        ["Gateway Timeout"] = HttpStatusCode.GatewayTimeout,
        ["Gateway Time-out"] = HttpStatusCode.GatewayTimeout,
        ["Insufficient Storage"] = HttpStatusCode.InsufficientStorage,
    };

    /// <summary>Parses stderr first, then stdout. False when neither carries a Graph error document.</summary>
    public static bool TryParse(string? standardError, string? standardOutput, RetryHint hint, out GraphException? error)
    {
        error = TryParseText(standardError, hint, standardError) ?? TryParseText(standardOutput, hint, standardError);
        return error is not null;
    }

    /// <summary>Builds the exception from a parsed <c>error</c> object; derives the status from the code when none is known.</summary>
    public static GraphException FromErrorObject(HttpStatusCode? status, JsonObject error, RetryHint hint, string? rawStandardError)
    {
        ArgumentNullException.ThrowIfNull(error);
        var code = ReadString(error, "code") ?? string.Empty;
        var message = ReadString(error, "message") ?? string.Empty;
        var innerError = error["innerError"] as JsonObject;
        var requestId = innerError is null ? null : ReadString(innerError, "request-id");
        var resolvedStatus = status ?? StatusFromCode(code, message);
        var retryAfter = ParseRetryAfter(message) ?? ParseRetryAfter(rawStandardError);
        return new GraphException(resolvedStatus, code, message, hint, error, rawStandardError, retryAfter, requestId);
    }

    /// <summary>Builds the exception for a failed <c>$batch</c> sub-response (status is explicit there).</summary>
    public static GraphException FromBatchResponse(int status, JsonNode? body, RetryHint hint)
    {
        var httpStatus = (HttpStatusCode)status;
        if (body is JsonObject obj && obj["error"] is JsonObject error)
        {
            return FromErrorObject(httpStatus, error, hint, null);
        }

        return new GraphException(httpStatus, string.Empty, $"$batch sub-request failed with status {status}", hint);
    }

    /// <summary>Maps a Graph error code (and, as a last resort, the message) to an HTTP status.</summary>
    public static HttpStatusCode StatusFromCode(string code, string message)
    {
        code ??= string.Empty;
        message ??= string.Empty;

        switch (code.ToUpperInvariant())
        {
            case "REQUEST_RESOURCENOTFOUND":
            case "RESOURCENOTFOUND":
            case "ITEMNOTFOUND":
            case "DIRECTORY_OBJECTNOTFOUND":
            case "NOTFOUND":
                return HttpStatusCode.NotFound;
            case "AUTHORIZATION_REQUESTDENIED":
            case "AUTHORIZATION_IDENTITYNOTFOUND":
            case "ACCESSDENIED":
            case "FORBIDDEN":
                return HttpStatusCode.Forbidden;
            case "INVALIDAUTHENTICATIONTOKEN":
            case "INVALIDAUTHENTICATIONTOKENHEADER":
            case "UNAUTHORIZED":
                return HttpStatusCode.Unauthorized;
            case "REQUEST_MULTIPLEOBJECTSWITHSAMEKEYVALUE":
            case "REQUEST_CONFLICTINGOBJECT":
            case "CONFLICT":
                return HttpStatusCode.Conflict;
            case "TOOMANYREQUESTS":
            case "ACTIVITYLIMITREACHED":
            case "REQUEST_THROTTLEDTEMPORARILY":
                return HttpStatusCode.TooManyRequests;
            case "SERVICEUNAVAILABLE":
            case "SERVICE_SERVICEUNAVAILABLE":
                return HttpStatusCode.ServiceUnavailable;
            case "UNKNOWNERROR":
            case "INTERNALSERVERERROR":
            case "SERVICE_INTERNALSERVERERROR":
            case "GENERALEXCEPTION":
                return HttpStatusCode.InternalServerError;
            case "BADGATEWAY":
                return HttpStatusCode.BadGateway;
            case "GATEWAYTIMEOUT":
                return HttpStatusCode.GatewayTimeout;
            case "REQUEST_BADREQUEST":
            case "BADREQUEST":
            case "REQUEST_UNSUPPORTEDQUERY":
            case "INVALIDREQUEST":
                return HttpStatusCode.BadRequest;
        }

        if (code.StartsWith("Authentication_", StringComparison.OrdinalIgnoreCase)
            || code.Contains("AuthenticationToken", StringComparison.OrdinalIgnoreCase)
            || code.Contains("ExpiredToken", StringComparison.OrdinalIgnoreCase))
        {
            return HttpStatusCode.Unauthorized;
        }

        if (code.Contains("throttl", StringComparison.OrdinalIgnoreCase)
            || message.Contains("throttl", StringComparison.OrdinalIgnoreCase)
            || message.Contains("too many requests", StringComparison.OrdinalIgnoreCase))
        {
            return HttpStatusCode.TooManyRequests;
        }

        if (message.Contains("Insufficient privileges", StringComparison.OrdinalIgnoreCase))
        {
            return HttpStatusCode.Forbidden;
        }

        return HttpStatusCode.BadRequest;
    }

    /// <summary>Parses <c>Retry-After: N</c> or "retry after N seconds" fragments (seconds), capped at five minutes.</summary>
    public static TimeSpan? ParseRetryAfter(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var match = RetryAfterPattern().Match(text);
        if (!match.Success || !int.TryParse(match.Groups["seconds"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return null;
        }

        var span = TimeSpan.FromSeconds(seconds);
        return span > MaxRetryAfter ? MaxRetryAfter : span;
    }

    /// <summary>Finds the first <c>{"error": {...}}</c> document in free text, tolerating any prefix or suffix.</summary>
    public static JsonObject? ExtractErrorObject(string text, out int errorStart)
    {
        errorStart = -1;
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        foreach (Match match in ErrorStartPattern().Matches(text))
        {
            var candidate = ExtractBalancedObject(text, match.Index);
            if (candidate is null)
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(candidate, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }) is JsonObject envelope
                    && envelope["error"] is JsonObject error)
                {
                    errorStart = match.Index;
                    return error;
                }
            }
            catch (JsonException)
            {
                // Not well-formed at this position (e.g. truncated); try the next candidate.
            }
        }

        return null;
    }

    private static GraphException? TryParseText(string? text, RetryHint hint, string? rawStandardError)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var error = ExtractErrorObject(text, out var errorStart);
        if (error is null)
        {
            return null;
        }

        return FromErrorObject(FindReasonPhrase(text, errorStart), error, hint, rawStandardError);
    }

    private static HttpStatusCode? FindReasonPhrase(string text, int errorStart)
    {
        Match? best = null;
        foreach (Match match in ReasonPhrasePattern().Matches(text))
        {
            if (match.Index < errorStart)
            {
                best = match;
            }
            else
            {
                break;
            }
        }

        if (best is not null && ReasonPhrases.TryGetValue(best.Groups["reason"].Value, out var fromReason))
        {
            return fromReason;
        }

        var numeric = NumericStatusPattern().Match(text);
        if (numeric.Success && int.TryParse(numeric.Groups["code"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var status))
        {
            return (HttpStatusCode)status;
        }

        return null;
    }

    private static string? ExtractBalancedObject(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return text[start..(i + 1)];
                    }

                    break;
            }
        }

        return null;
    }

    private static string? ReadString(JsonObject obj, string name)
    {
        return obj[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    [GeneratedRegex("\\{\\s*\"error\"\\s*:")]
    private static partial Regex ErrorStartPattern();

    [GeneratedRegex(@"(?<reason>Bad Request|Unauthorized|Payment Required|Forbidden|Not Found|Method Not Allowed|Not Acceptable|Request Timeout|Conflict|Gone|Precondition Failed|Unprocessable Entity|Locked|Too Many Requests|Internal Server Error|Not Implemented|Bad Gateway|Service Unavailable|Gateway Time-?out|Insufficient Storage)\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex ReasonPhrasePattern();

    [GeneratedRegex(@"\b(?:status(?:\s+code)?|HTTP(?:Error)?)\s*:?\s*(?<code>[45]\d{2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex NumericStatusPattern();

    [GeneratedRegex(@"retry[\s_-]*after[\s:""'=]*(?<seconds>\d{1,6})", RegexOptions.IgnoreCase)]
    private static partial Regex RetryAfterPattern();
}
