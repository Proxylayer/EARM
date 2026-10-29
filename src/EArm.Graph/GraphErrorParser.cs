using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EArm.Graph;

/// <summary>
/// Turns a Graph error response (real status, headers, <c>{"error":{...}}</c> body) into a <see cref="GraphException"/>; an Azure Resource Manager error
/// response has the same body shape (<c>{"error":{"code","message","details"}}</c>) and is read the same way.
/// The status always comes from the HTTP response; the error code is informational.
/// </summary>
internal static class GraphErrorParser
{
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(5);

    /// <summary>The exception for a non-success HTTP response: parses the body, the <c>Retry-After</c> header and the request ids.</summary>
    public static GraphException FromResponse(HttpStatusCode status, string? body, HttpResponseHeaders headers, string? clientRequestId, RetryHint hint, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var retryAfter = ParseRetryAfter(headers.RetryAfter, timeProvider);
        // Graph names its request id request-id; Azure Resource Manager (whose error body has the same {"error":{"code","message"}} shape) x-ms-request-id.
        var requestId = FirstHeader(headers, "request-id") ?? FirstHeader(headers, "x-ms-request-id");
        var echoedClientId = FirstHeader(headers, "client-request-id") ?? FirstHeader(headers, "x-ms-client-request-id") ?? clientRequestId;
        return FromBody(status, body, hint, retryAfter, requestId, echoedClientId);
    }

    /// <summary>The exception for a failed <c>$batch</c> sub-response (explicit status, optional sub-response headers).</summary>
    public static GraphException FromBatchResponse(int status, JsonNode? body, JsonObject? headers, RetryHint hint, TimeProvider timeProvider)
    {
        var httpStatus = (HttpStatusCode)status;
        TimeSpan? retryAfter = null;
        string? requestId = null;
        string? clientRequestId = null;
        if (headers is not null)
        {
            foreach (var pair in headers)
            {
                var value = pair.Value is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
                if (value is null)
                {
                    continue;
                }

                if (string.Equals(pair.Key, "Retry-After", StringComparison.OrdinalIgnoreCase))
                {
                    retryAfter = ParseRetryAfterText(value, timeProvider);
                }
                else if (string.Equals(pair.Key, "request-id", StringComparison.OrdinalIgnoreCase))
                {
                    requestId = value;
                }
                else if (string.Equals(pair.Key, "client-request-id", StringComparison.OrdinalIgnoreCase))
                {
                    clientRequestId = value;
                }
            }
        }

        if (body is JsonObject obj && obj["error"] is JsonObject error)
        {
            return FromErrorObject(httpStatus, error, hint, retryAfter, requestId, clientRequestId);
        }

        return new GraphException(httpStatus, string.Empty, $"$batch sub-request failed with status {status}", hint, null, retryAfter, requestId, clientRequestId);
    }

    /// <summary>Builds the exception from a parsed <c>error</c> object.</summary>
    public static GraphException FromErrorObject(HttpStatusCode status, JsonObject error, RetryHint hint, TimeSpan? retryAfter, string? requestId, string? clientRequestId)
    {
        ArgumentNullException.ThrowIfNull(error);
        var code = ReadString(error, "code") ?? string.Empty;
        var message = ReadString(error, "message") ?? string.Empty;
        if (requestId is null && error["innerError"] is JsonObject innerError)
        {
            requestId = ReadString(innerError, "request-id");
        }

        return new GraphException(status, code, message, hint, error, retryAfter, requestId, clientRequestId);
    }

    /// <summary>Parses the <c>Retry-After</c> header (delta seconds or HTTP-date), clamped to [0, five minutes].</summary>
    public static TimeSpan? ParseRetryAfter(RetryConditionHeaderValue? header, TimeProvider timeProvider)
    {
        if (header is null)
        {
            return null;
        }

        TimeSpan? span = null;
        if (header.Delta is { } delta)
        {
            span = delta;
        }
        else if (header.Date is { } date)
        {
            span = date - timeProvider.GetUtcNow();
        }

        return Clamp(span);
    }

    /// <summary>Parses a raw <c>Retry-After</c> value (used for <c>$batch</c> sub-response headers): seconds or an HTTP-date.</summary>
    public static TimeSpan? ParseRetryAfterText(string? text, TimeProvider timeProvider)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return Clamp(TimeSpan.FromSeconds(seconds));
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            return Clamp(date - timeProvider.GetUtcNow());
        }

        return null;
    }

    private static TimeSpan? Clamp(TimeSpan? span)
    {
        if (span is null)
        {
            return null;
        }

        if (span.Value < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return span.Value > MaxRetryAfter ? MaxRetryAfter : span;
    }

    private static GraphException FromBody(HttpStatusCode status, string? body, RetryHint hint, TimeSpan? retryAfter, string? requestId, string? clientRequestId)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                if (JsonNode.Parse(JsonSurrogateSanitizer.Replace(body)) is JsonObject envelope && envelope["error"] is JsonObject error)
                {
                    return FromErrorObject(status, error, hint, retryAfter, requestId, clientRequestId);
                }
            }
            catch (JsonException)
            {
                // Not a Graph error document (an HTML gateway page, a truncated body): fall through to the generic message.
            }
        }

        var reason = ((int)status).ToString(CultureInfo.InvariantCulture) + " " + status;
        return new GraphException(status, string.Empty, $"Microsoft Graph answered {reason} without an error document.", hint, null, retryAfter, requestId, clientRequestId);
    }

    private static string? FirstHeader(HttpResponseHeaders headers, string name)
    {
        return headers.TryGetValues(name, out var values) ? values.FirstOrDefault(static v => !string.IsNullOrWhiteSpace(v)) : null;
    }

    private static string? ReadString(JsonObject obj, string name)
    {
        // Parser output: a registered secret value that Graph echoed back in the code or message never leaves this method.
        return obj[name] is JsonValue value && value.TryGetValue<string>(out var text) ? SecretScrubber.Scrub(text) : null;
    }
}
