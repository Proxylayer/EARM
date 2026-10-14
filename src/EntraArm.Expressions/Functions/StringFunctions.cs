using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using EntraArm.Expressions.Values;

namespace EntraArm.Expressions.Functions;

/// <summary>
/// Group 2: string functions with ARM semantics. <c>contains</c>, <c>length</c>, <c>empty</c>, <c>indexOf</c> and
/// <c>lastIndexOf</c> also accept arrays/objects, as in ARM. String comparisons in <c>startsWith</c>, <c>endsWith</c>,
/// <c>contains</c> and <c>indexOf</c> are case-insensitive (ARM rule); <c>replace</c> is case-sensitive.
/// </summary>
internal static class StringFunctions
{
    private const string Group = "string";
    private const string DefaultUtcNowFormat = "yyyyMMddTHHmmssZ";
    private const string Iso8601 = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public static void Register(FunctionRegistry registry)
    {
        registry.Register(new FunctionDescriptor { Name = "concat", Group = Group, MinArgs = 1, HandlesUnknown = true, Implementation = Concat });
        registry.Register(new FunctionDescriptor { Name = "format", Group = Group, MinArgs = 1, Implementation = Format });
        registry.Register(new FunctionDescriptor { Name = "toLower", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = call => JsonValue.Create(call.String(0).ToLowerInvariant()) });
        registry.Register(new FunctionDescriptor { Name = "toUpper", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = call => JsonValue.Create(call.String(0).ToUpperInvariant()) });
        registry.Register(new FunctionDescriptor { Name = "substring", Group = Group, MinArgs = 1, MaxArgs = 3, Implementation = Substring });
        registry.Register(new FunctionDescriptor { Name = "replace", Group = Group, MinArgs = 3, MaxArgs = 3, Implementation = Replace });
        registry.Register(new FunctionDescriptor { Name = "split", Group = Group, MinArgs = 2, MaxArgs = 2, Implementation = Split });
        registry.Register(new FunctionDescriptor { Name = "join", Group = Group, MinArgs = 2, MaxArgs = 2, Implementation = Join });
        registry.Register(new FunctionDescriptor { Name = "trim", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = call => JsonValue.Create(call.String(0).Trim()) });
        registry.Register(new FunctionDescriptor { Name = "startsWith", Group = Group, MinArgs = 2, MaxArgs = 2, Implementation = call => JsonValue.Create(call.String(0).StartsWith(call.String(1), StringComparison.OrdinalIgnoreCase)) });
        registry.Register(new FunctionDescriptor { Name = "endsWith", Group = Group, MinArgs = 2, MaxArgs = 2, Implementation = call => JsonValue.Create(call.String(0).EndsWith(call.String(1), StringComparison.OrdinalIgnoreCase)) });
        registry.Register(new FunctionDescriptor { Name = "contains", Group = Group, MinArgs = 2, MaxArgs = 2, Implementation = Contains });
        registry.Register(new FunctionDescriptor { Name = "length", Group = Group, MinArgs = 1, MaxArgs = 1, HandlesUnknown = true, Implementation = Length });
        registry.Register(new FunctionDescriptor { Name = "empty", Group = Group, MinArgs = 1, MaxArgs = 1, HandlesUnknown = true, Implementation = Empty });
        registry.Register(new FunctionDescriptor { Name = "indexOf", Group = Group, MinArgs = 2, MaxArgs = 2, Implementation = call => IndexOf(call, last: false) });
        registry.Register(new FunctionDescriptor { Name = "lastIndexOf", Group = Group, MinArgs = 2, MaxArgs = 2, Implementation = call => IndexOf(call, last: true) });
        registry.Register(new FunctionDescriptor { Name = "padLeft", Group = Group, MinArgs = 2, MaxArgs = 3, Implementation = PadLeft });
        registry.Register(new FunctionDescriptor { Name = "string", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = call => JsonValue.Create(Stringify(call, call.Arg(0))) });
        registry.Register(new FunctionDescriptor { Name = "base64", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = call => JsonValue.Create(Convert.ToBase64String(Encoding.UTF8.GetBytes(call.String(0)))) });
        registry.Register(new FunctionDescriptor { Name = "base64ToString", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = Base64ToString });
        registry.Register(new FunctionDescriptor { Name = "uri", Group = Group, MinArgs = 2, MaxArgs = 2, Implementation = Uri });
        registry.Register(new FunctionDescriptor { Name = "uriComponent", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = call => JsonValue.Create(System.Uri.EscapeDataString(call.String(0))) });
        registry.Register(new FunctionDescriptor { Name = "uriComponentToString", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = call => JsonValue.Create(System.Uri.UnescapeDataString(call.String(0))) });
        registry.Register(new FunctionDescriptor { Name = "guid", Group = Group, MinArgs = 1, Implementation = Guid });
        registry.Register(new FunctionDescriptor { Name = "uniqueString", Group = Group, MinArgs = 1, Implementation = UniqueString });
        registry.Register(new FunctionDescriptor { Name = "newGuid", Group = Group, MinArgs = 0, MaxArgs = 0, NonDeterministic = true, Implementation = _ => JsonValue.Create(System.Guid.NewGuid().ToString("D")) });
        registry.Register(new FunctionDescriptor { Name = "utcNow", Group = Group, MinArgs = 0, MaxArgs = 1, NonDeterministic = true, Implementation = UtcNow });
        registry.Register(new FunctionDescriptor { Name = "dateTimeAdd", Group = Group, MinArgs = 2, MaxArgs = 3, Implementation = DateTimeAdd });
        registry.Register(new FunctionDescriptor { Name = "dateTimeFromEpoch", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = DateTimeFromEpoch });
        registry.Register(new FunctionDescriptor { Name = "dateTimeToEpoch", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = DateTimeToEpoch });
    }

    private static JsonNode? Concat(FunctionCall call)
    {
        if (call.Arg(0) is JsonArray)
        {
            var result = new JsonArray();
            for (int i = 0; i < call.Count; i++)
            {
                foreach (var item in call.Array(i))
                {
                    result.Add(JsonValues.Clone(item));
                }
            }

            return result;
        }

        var sb = new StringBuilder();
        for (int i = 0; i < call.Count; i++)
        {
            var node = call.Arg(i);
            if (UnknownJson.IsUnknown(node))
            {
                return call.UnknownFromArguments();
            }

            if (node is JsonArray or JsonObject)
            {
                throw call.Error($"argument {i + 1} is {JsonValues.Describe(node)}; all arguments must be strings (or all arrays).");
            }

            if (node is null)
            {
                throw call.Error($"argument {i + 1} is null.");
            }

            sb.Append(Stringify(call, node));
        }

        return JsonValue.Create(sb.ToString());
    }

    private static JsonNode? Format(FunctionCall call)
    {
        string format = call.String(0);
        var args = new object?[call.Count - 1];
        for (int i = 1; i < call.Count; i++)
        {
            args[i - 1] = FormatArgument(call, call.Arg(i));
        }

        try
        {
            return JsonValue.Create(string.Format(CultureInfo.InvariantCulture, format, args));
        }
        catch (FormatException ex)
        {
            throw call.Failure(call.ArgumentIsTainted(0) ? "invalid format string." : $"invalid format string '{format}': {ex.Message}", ex);
        }
    }

    private static object? FormatArgument(FunctionCall call, JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        if (JsonValues.TryGetString(node, out var text))
        {
            return text;
        }

        if (JsonValues.TryGetInt64(node, out long l))
        {
            return l;
        }

        if (JsonValues.TryGetDouble(node, out double d))
        {
            return d;
        }

        return Stringify(call, node);
    }

    private static JsonNode? Substring(FunctionCall call)
    {
        string text = call.String(0);
        long start = call.Has(1) ? call.Int(1) : 0;
        long length = call.Has(2) ? call.Int(2) : text.Length - start;
        // Every number here derives from more than one argument (a defaulted length comes from the text itself), so
        // a tainted call shows none of them.
        if (start < 0 || start > text.Length)
        {
            throw call.Failure($"start index {call.Redact(Invariant(start))} is outside the string (length {call.Redact(Invariant(text.Length))}).");
        }

        if (length < 0 || length > text.Length - start)
        {
            throw call.Failure($"length {call.Redact(Invariant(length))} from index {call.Redact(Invariant(start))} exceeds the string (length {call.Redact(Invariant(text.Length))}).");
        }

        return JsonValue.Create(text.Substring((int)start, (int)length));
    }

    private static string Invariant(long value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static JsonNode? Replace(FunctionCall call)
    {
        string text = call.String(0);
        string oldValue = call.String(1);
        string newValue = call.String(2);
        if (oldValue.Length == 0)
        {
            throw call.Error("the string to replace must not be empty.");
        }

        return JsonValue.Create(text.Replace(oldValue, newValue, StringComparison.Ordinal));
    }

    private static JsonNode? Split(FunctionCall call)
    {
        string text = call.String(0);
        string[] parts;
        if (call.Arg(1) is JsonArray delimiters)
        {
            var list = new List<string>();
            for (int i = 0; i < delimiters.Count; i++)
            {
                if (!JsonValues.TryGetString(delimiters[i], out var delimiter))
                {
                    throw call.Error($"delimiter array element {i} must be a string.");
                }

                list.Add(delimiter);
            }

            parts = text.Split([.. list], StringSplitOptions.None);
        }
        else
        {
            parts = text.Split(call.String(1));
        }

        var result = new JsonArray();
        foreach (var part in parts)
        {
            result.Add(JsonValue.Create(part));
        }

        return result;
    }

    private static JsonNode? Join(FunctionCall call)
    {
        var array = call.Array(0);
        string delimiter = call.String(1);
        var items = new List<string>();
        for (int i = 0; i < array.Count; i++)
        {
            if (!JsonValues.TryGetString(array[i], out var item))
            {
                throw call.Error($"array element {i} must be a string, got {JsonValues.Describe(array[i])}.");
            }

            items.Add(item);
        }

        return JsonValue.Create(string.Join(delimiter, items));
    }

    private static JsonNode? Contains(FunctionCall call)
    {
        var container = call.Arg(0);
        var item = call.Arg(1);
        switch (container)
        {
            case JsonArray array:
                return JsonValue.Create(array.Any(element => JsonValues.AreEqual(element, item)));
            case JsonObject obj:
                {
                    string key = call.String(1);
                    return JsonValue.Create(obj.Any(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)));
                }

            default:
                {
                    string text = call.String(0);
                    string needle = Stringify(call, item);
                    return JsonValue.Create(text.Contains(needle, StringComparison.OrdinalIgnoreCase));
                }
        }
    }

    private static JsonNode? Length(FunctionCall call)
    {
        var node = call.Arg(0);
        if (UnknownJson.IsUnknown(node))
        {
            return call.UnknownFromArguments();
        }

        return node switch
        {
            JsonArray array => JsonValue.Create((long)array.Count),
            JsonObject obj => JsonValue.Create((long)obj.Count),
            _ => JsonValue.Create((long)call.String(0).Length),
        };
    }

    private static JsonNode? Empty(FunctionCall call)
    {
        var node = call.Arg(0);
        if (UnknownJson.IsUnknown(node))
        {
            return call.UnknownFromArguments();
        }

        return node switch
        {
            null => JsonValue.Create(true),
            JsonArray array => JsonValue.Create(array.Count == 0),
            JsonObject obj => JsonValue.Create(obj.Count == 0),
            _ => JsonValue.Create(call.String(0).Length == 0),
        };
    }

    private static JsonNode? IndexOf(FunctionCall call, bool last)
    {
        if (call.Arg(0) is JsonArray array)
        {
            var item = call.Arg(1);
            int found = -1;
            for (int i = 0; i < array.Count; i++)
            {
                if (JsonValues.AreEqual(array[i], item))
                {
                    found = i;
                    if (!last)
                    {
                        break;
                    }
                }
            }

            return JsonValue.Create((long)found);
        }

        string text = call.String(0);
        string needle = call.String(1);
        int index = last ? text.LastIndexOf(needle, StringComparison.OrdinalIgnoreCase) : text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        return JsonValue.Create((long)index);
    }

    private static JsonNode? PadLeft(FunctionCall call)
    {
        var node = call.Arg(0);
        string text = JsonValues.TryGetInt64(node, out long number) ? number.ToString(CultureInfo.InvariantCulture) : call.String(0);
        int total = call.Int32(1);
        if (total < 0 || total > 16)
        {
            throw call.Error($"total length must be between 0 and 16 (ARM limit), got {call.Redact(1, Invariant(total))}.");
        }

        char pad = ' ';
        if (call.Has(2))
        {
            string padText = call.String(2);
            if (padText.Length != 1)
            {
                throw call.Error("the padding character must be a single character.");
            }

            pad = padText[0];
        }

        return JsonValue.Create(text.PadLeft(total, pad));
    }

    /// <summary>ARM <c>string()</c>: strings as they are, numbers invariant, booleans lowercase, objects/arrays as compact JSON.</summary>
    internal static string Stringify(FunctionCall call, JsonNode? node)
    {
        if (node is null)
        {
            throw call.Error("null cannot be converted to a string.");
        }

        if (JsonValues.TryGetString(node, out var text))
        {
            return text;
        }

        if (JsonValues.TryGetBool(node, out bool b))
        {
            return b ? "true" : "false";
        }

        if (JsonValues.TryGetInt64(node, out long l))
        {
            return l.ToString(CultureInfo.InvariantCulture);
        }

        if (JsonValues.TryGetDouble(node, out double d))
        {
            return d.ToString("R", CultureInfo.InvariantCulture);
        }

        if (node is JsonObject or JsonArray)
        {
            return node.ToJsonString();
        }

        throw call.Error($"{JsonValues.Describe(node)} cannot be converted to a string.");
    }

    private static JsonNode? Base64ToString(FunctionCall call)
    {
        try
        {
            return JsonValue.Create(Encoding.UTF8.GetString(Convert.FromBase64String(call.String(0))));
        }
        catch (FormatException ex)
        {
            throw call.Failure("the argument is not valid base64.", ex);
        }
    }

    private static JsonNode? Uri(FunctionCall call)
    {
        string baseUri = call.String(0);
        string relative = call.String(1);
        try
        {
            var combined = new Uri(new Uri(baseUri, UriKind.Absolute), relative);
            return JsonValue.Create(combined.AbsoluteUri);
        }
        catch (UriFormatException ex)
        {
            throw call.Failure(call.IsTainted ? "cannot combine the base and relative URIs." : $"cannot combine '{baseUri}' and '{relative}': {ex.Message}", ex);
        }
    }

    private static JsonNode? Guid(FunctionCall call)
    {
        var parts = new List<string>();
        for (int i = 0; i < call.Count; i++)
        {
            parts.Add(Stringify(call, call.Arg(i)));
        }

        return JsonValue.Create(Hashing.GuidV5(Hashing.GuidNamespace, string.Join("-", parts)).ToString("D"));
    }

    private static JsonNode? UniqueString(FunctionCall call)
    {
        var parts = new List<string>();
        for (int i = 0; i < call.Count; i++)
        {
            parts.Add(Stringify(call, call.Arg(i)));
        }

        return JsonValue.Create(Hashing.UniqueString(string.Join("-", parts)));
    }

    private static JsonNode? UtcNow(FunctionCall call)
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

    private static JsonNode? DateTimeAdd(FunctionCall call)
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

    private static JsonNode? DateTimeFromEpoch(FunctionCall call)
    {
        long seconds = call.Int(0);
        try
        {
            return JsonValue.Create(DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.ToString(Iso8601, CultureInfo.InvariantCulture));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw call.Failure($"epoch value {call.Redact(0, Invariant(seconds))} is out of range.", ex);
        }
    }

    private static JsonNode? DateTimeToEpoch(FunctionCall call)
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
