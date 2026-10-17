using System;
using System.Globalization;
using System.Text.Json.Nodes;

namespace EArm.Expressions.Functions;

/// <summary>
/// The date/time functions (registered with the string group by <see cref="StringFunctions"/>): <c>utcNow</c>, <c>dateTimeAdd</c>,
/// <c>dateTimeFromEpoch</c> and <c>dateTimeToEpoch</c>, with ISO 8601 durations.
/// </summary>
internal static class DateTimeFunctions
{
    private const string DefaultUtcNowFormat = "yyyyMMddTHHmmssZ";
    private const string Iso8601 = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    internal static JsonNode? UtcNow(FunctionCall call)
    {
        string format = call.Has(0) ? call.String(0) : DefaultUtcNowFormat;
        try
        {
            return JsonValue.Create(DateTime.UtcNow.ToString(format, CultureInfo.InvariantCulture));
        }
        catch (FormatException ex)
        {
            throw call.Failure($"invalid date format '{call.Redact(0, format)}'.", ex);
        }
    }

    internal static JsonNode? DateTimeAdd(FunctionCall call)
    {
        var baseTime = ParseDateTime(call, call.String(0));
        var result = AddIsoDuration(call, baseTime, call.String(1));
        string format = call.Has(2) ? call.String(2) : Iso8601;
        try
        {
            return JsonValue.Create(result.ToString(format, CultureInfo.InvariantCulture));
        }
        catch (FormatException ex)
        {
            throw call.Failure($"invalid date format '{call.Redact(2, format)}'.", ex);
        }
    }

    internal static JsonNode? DateTimeFromEpoch(FunctionCall call)
    {
        long seconds = call.Int(0);
        try
        {
            return JsonValue.Create(DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.ToString(Iso8601, CultureInfo.InvariantCulture));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw call.Failure($"epoch value {call.Redact(0, StringFunctions.Invariant(seconds))} is out of range.", ex);
        }
    }

    internal static JsonNode? DateTimeToEpoch(FunctionCall call)
    {
        var time = ParseDateTime(call, call.String(0));
        return JsonValue.Create(new DateTimeOffset(time, TimeSpan.Zero).ToUnixTimeSeconds());
    }

    private static DateTime ParseDateTime(FunctionCall call, string text)
    {
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed.UtcDateTime;
        }

        throw call.Failure($"'{call.Redact(0, text)}' is not a valid ISO 8601 date/time.");
    }

    /// <summary>
    /// Adds an ISO 8601 duration (<c>[-]P[nY][nM][nW][nD][T[nH][nM][nS]]</c>) to a UTC time. The duration is the
    /// call's second argument (index 1): messages redact it through that argument.
    /// </summary>
    internal static DateTime AddIsoDuration(FunctionCall call, DateTime baseTime, string duration)
    {
        string text = duration.Trim();
        string shown = call.Redact(1, duration);
        int sign = 1;
        int i = 0;
        if (i < text.Length && (text[i] == '-' || text[i] == '+'))
        {
            sign = text[i] == '-' ? -1 : 1;
            i++;
        }

        if (i >= text.Length || char.ToUpperInvariant(text[i]) != 'P')
        {
            throw call.Failure($"'{shown}' is not an ISO 8601 duration (expected P...).");
        }

        i++;
        bool inTime = false;
        bool any = false;
        var result = baseTime;
        while (i < text.Length)
        {
            char c = char.ToUpperInvariant(text[i]);
            if (c == 'T')
            {
                inTime = true;
                i++;
                continue;
            }

            int start = i;
            while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '.'))
            {
                i++;
            }

            if (start == i || i >= text.Length)
            {
                throw call.Failure($"'{shown}' is not an ISO 8601 duration (missing number or unit).");
            }

            if (!double.TryParse(text[start..i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double amount))
            {
                throw call.Failure($"'{shown}' is not an ISO 8601 duration (bad number '{call.Redact(1, text[start..i])}').");
            }

            char unit = char.ToUpperInvariant(text[i]);
            i++;
            any = true;
            try
            {
                switch (unit)
                {
                    case 'Y' when !inTime:
                        result = result.AddYears(checked((int)(sign * amount)));
                        break;
                    case 'M' when !inTime:
                        result = result.AddMonths(checked((int)(sign * amount)));
                        break;
                    case 'W' when !inTime:
                        result = result.AddDays(sign * amount * 7);
                        break;
                    case 'D' when !inTime:
                        result = result.AddDays(sign * amount);
                        break;
                    case 'H' when inTime:
                        result = result.AddHours(sign * amount);
                        break;
                    case 'M' when inTime:
                        result = result.AddMinutes(sign * amount);
                        break;
                    case 'S' when inTime:
                        result = result.AddSeconds(sign * amount);
                        break;
                    default:
                        throw call.Failure($"'{shown}' is not an ISO 8601 duration (unexpected unit '{call.Redact(1, unit.ToString())}').");
                }
            }
            catch (ArgumentOutOfRangeException ex)
            {
                throw call.Failure($"adding '{shown}' moves the date out of range.", ex);
            }
            catch (OverflowException ex)
            {
                throw call.Failure($"'{shown}' is too large.", ex);
            }
        }

        if (!any)
        {
            throw call.Failure($"'{shown}' is not an ISO 8601 duration (no components).");
        }

        return result;
    }
}
