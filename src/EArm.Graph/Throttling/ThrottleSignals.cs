using System;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace EArm.Graph.Throttling;

/// <summary>
/// What one Graph response says about throttling: its status, <c>Retry-After</c>, and the directory headers <c>x-ms-resource-unit</c> (the resource
/// units actually charged), <c>x-ms-throttle-limit-percentage</c> (sent only above 80 % of the limit) and <c>x-ms-throttle-scope</c>.
/// </summary>
internal readonly record struct ThrottleSignals(int Status, TimeSpan? RetryAfter, int? ResourceUnits, double? LimitPercentage, string? Scope)
{
    public bool IsThrottled => Status == 429;

    /// <summary>The signals of an HTTP response.</summary>
    public static ThrottleSignals From(int status, HttpResponseHeaders headers, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(headers);
        return new ThrottleSignals(
            status,
            GraphErrorParser.ParseRetryAfter(headers.RetryAfter, time),
            ParseInt(GraphResponseReader.FirstHeader(headers, "x-ms-resource-unit")),
            ParseDouble(GraphResponseReader.FirstHeader(headers, "x-ms-throttle-limit-percentage")),
            GraphResponseReader.FirstHeader(headers, "x-ms-throttle-scope"));
    }

    /// <summary>The signals of a <c>$batch</c> sub-response (its <c>headers</c> object may be absent).</summary>
    public static ThrottleSignals From(int status, JsonObject? headers, TimeProvider time)
    {
        TimeSpan? retryAfter = null;
        int? units = null;
        double? percentage = null;
        string? scope = null;
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
                    retryAfter = GraphErrorParser.ParseRetryAfterText(value, time);
                }
                else if (string.Equals(pair.Key, "x-ms-resource-unit", StringComparison.OrdinalIgnoreCase))
                {
                    units = ParseInt(value);
                }
                else if (string.Equals(pair.Key, "x-ms-throttle-limit-percentage", StringComparison.OrdinalIgnoreCase))
                {
                    percentage = ParseDouble(value);
                }
                else if (string.Equals(pair.Key, "x-ms-throttle-scope", StringComparison.OrdinalIgnoreCase))
                {
                    scope = value;
                }
            }
        }

        return new ThrottleSignals(status, retryAfter, units, percentage, scope);
    }

    private static int? ParseInt(string? text)
    {
        return int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static double? ParseDouble(string? text)
    {
        return double.TryParse(text?.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
