using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Values;

namespace EArm.Expressions.Functions;

/// <summary>
/// Group 2: string functions with ARM semantics. <c>contains</c>, <c>length</c>, <c>empty</c>, <c>indexOf</c> and
/// <c>lastIndexOf</c> also accept arrays/objects, as in ARM. String comparisons in <c>startsWith</c>, <c>endsWith</c>,
/// <c>indexOf</c> and <c>lastIndexOf</c> are case-insensitive (ARM rule); <c>contains</c> on a string and <c>replace</c> are case-sensitive
/// (ARM: "The string comparison is case-sensitive"), while <c>contains</c> on an object compares keys case-insensitively.
/// </summary>
internal static class StringFunctions
{
    private const string Group = "string";

    public static void Register(FunctionRegistry registry)
    {
        registry.Register(new FunctionDescriptor { Name = "concat", Group = Group, MinArgs = 1, HandlesUnknown = true, Implementation = Concat });
        registry.Register(new FunctionDescriptor { Name = "format", Group = Group, MinArgs = 1, Implementation = StringFormatFunctions.Format });
        registry.Register(new FunctionDescriptor { Name = "toLower", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = call => JsonValue.Create(Charged(call, call.String(0)).ToLowerInvariant()) });
        registry.Register(new FunctionDescriptor { Name = "toUpper", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = call => JsonValue.Create(Charged(call, call.String(0)).ToUpperInvariant()) });
        registry.Register(new FunctionDescriptor { Name = "substring", Group = Group, MinArgs = 1, MaxArgs = 3, Implementation = Substring });
        registry.Register(new FunctionDescriptor { Name = "replace", Group = Group, MinArgs = 3, MaxArgs = 3, Implementation = Replace });
        registry.Register(new FunctionDescriptor { Name = "split", Group = Group, MinArgs = 2, MaxArgs = 2, Implementation = Split });
        registry.Register(new FunctionDescriptor { Name = "join", Group = Group, MinArgs = 2, MaxArgs = 2, Implementation = Join });
        registry.Register(new FunctionDescriptor { Name = "trim", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = call => JsonValue.Create(Charged(call, call.String(0)).Trim()) });
        registry.Register(new FunctionDescriptor { Name = "startsWith", Group = Group, MinArgs = 2, MaxArgs = 2, Implementation = call => JsonValue.Create(Charged(call, call.String(0)).StartsWith(call.String(1), StringComparison.OrdinalIgnoreCase)) });
        registry.Register(new FunctionDescriptor { Name = "endsWith", Group = Group, MinArgs = 2, MaxArgs = 2, Implementation = call => JsonValue.Create(Charged(call, call.String(0)).EndsWith(call.String(1), StringComparison.OrdinalIgnoreCase)) });
        registry.Register(new FunctionDescriptor { Name = "contains", Group = Group, MinArgs = 2, MaxArgs = 2, Implementation = StringSearchFunctions.Contains });
        registry.Register(new FunctionDescriptor { Name = "length", Group = Group, MinArgs = 1, MaxArgs = 1, HandlesUnknown = true, Implementation = Length });
        registry.Register(new FunctionDescriptor { Name = "empty", Group = Group, MinArgs = 1, MaxArgs = 1, HandlesUnknown = true, Implementation = Empty });
        registry.Register(new FunctionDescriptor { Name = "indexOf", Group = Group, MinArgs = 2, MaxArgs = 2, Implementation = call => StringSearchFunctions.IndexOf(call, last: false) });
        registry.Register(new FunctionDescriptor { Name = "lastIndexOf", Group = Group, MinArgs = 2, MaxArgs = 2, Implementation = call => StringSearchFunctions.IndexOf(call, last: true) });
        registry.Register(new FunctionDescriptor { Name = "padLeft", Group = Group, MinArgs = 2, MaxArgs = 3, Implementation = PadLeft });
        registry.Register(new FunctionDescriptor { Name = "string", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = call => JsonValue.Create(Stringify(call, call.Arg(0))) });
        registry.Register(new FunctionDescriptor { Name = "base64", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = Base64 });
        registry.Register(new FunctionDescriptor { Name = "base64ToString", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = Base64ToString });
        registry.Register(new FunctionDescriptor { Name = "uri", Group = Group, MinArgs = 2, MaxArgs = 2, Implementation = Uri });
        registry.Register(new FunctionDescriptor { Name = "uriComponent", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = call => JsonValue.Create(System.Uri.EscapeDataString(Charged(call, call.String(0)))) });
        registry.Register(new FunctionDescriptor { Name = "uriComponentToString", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = call => JsonValue.Create(System.Uri.UnescapeDataString(Charged(call, call.String(0)))) });
        registry.Register(new FunctionDescriptor { Name = "guid", Group = Group, MinArgs = 1, Implementation = Guid });
        registry.Register(new FunctionDescriptor { Name = "uniqueString", Group = Group, MinArgs = 1, Implementation = UniqueString });
        registry.Register(new FunctionDescriptor { Name = "newGuid", Group = Group, MinArgs = 0, MaxArgs = 0, NonDeterministic = true, Implementation = _ => JsonValue.Create(System.Guid.NewGuid().ToString("D")) });
        registry.Register(new FunctionDescriptor { Name = "utcNow", Group = Group, MinArgs = 0, MaxArgs = 1, NonDeterministic = true, Implementation = DateTimeFunctions.UtcNow });
        registry.Register(new FunctionDescriptor { Name = "dateTimeAdd", Group = Group, MinArgs = 2, MaxArgs = 3, Implementation = DateTimeFunctions.DateTimeAdd });
        registry.Register(new FunctionDescriptor { Name = "dateTimeFromEpoch", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = DateTimeFunctions.DateTimeFromEpoch });
        registry.Register(new FunctionDescriptor { Name = "dateTimeToEpoch", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = DateTimeFunctions.DateTimeToEpoch });
        EncodingFunctions.Register(registry, Group);
        WildcardFunctions.Register(registry, Group);
    }

    /// <summary>Counts the work of looking at <paramref name="text"/> against the run's budget and returns it (see <see cref="EvaluationBudget.ChargeText"/>).</summary>
    private static string Charged(FunctionCall call, string text)
    {
        call.Context.Budget.ChargeText(text.Length, call.Offset);
        return text;
    }

    private static JsonNode? Base64(FunctionCall call)
    {
        string text = Charged(call, call.String(0));
        long bytes = Encoding.UTF8.GetByteCount(text);
        call.Context.Budget.CheckStringLength((bytes + 2) / 3 * 4, call.Offset);
        return JsonValue.Create(Convert.ToBase64String(Encoding.UTF8.GetBytes(text)));
    }

    private static JsonNode? Concat(FunctionCall call)
    {
        if (call.Arg(0) is JsonArray)
        {
            var result = new JsonArray();
            for (int i = 0; i < call.Count; i++)
            {
                var array = call.Array(i);
                call.Context.Budget.CheckCollectionCount(result.Count + (long)array.Count, call.Offset);
                foreach (var item in array)
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

            string part = Stringify(call, node);
            call.Context.Budget.CheckStringLength(sb.Length + (long)part.Length, call.Offset);
            call.Context.Budget.ChargeText(part.Length, call.Offset);
            sb.Append(part);
        }

        return JsonValue.Create(sb.ToString());
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

    internal static string Invariant(long value)
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

        call.Context.Budget.ChargeText((long)text.Length + oldValue.Length + newValue.Length, call.Offset);

        // The result length is known before the result is built: text length plus the growth of every match.
        if (newValue.Length > oldValue.Length)
        {
            long matches = 0;
            for (int at = text.IndexOf(oldValue, StringComparison.Ordinal); at >= 0; at = text.IndexOf(oldValue, at + oldValue.Length, StringComparison.Ordinal))
            {
                matches++;
            }

            call.Context.Budget.CheckStringLength(text.Length + (matches * (newValue.Length - oldValue.Length)), call.Offset);
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

            // Every position of the text is compared with every delimiter.
            call.Context.Budget.ChargeText((long)text.Length * Math.Max(1, list.Count), call.Offset);
            parts = text.Split([.. list], StringSplitOptions.None);
        }
        else
        {
            call.Context.Budget.ChargeText(text.Length, call.Offset);
            parts = text.Split(call.String(1));
        }

        call.Context.Budget.CheckCollectionCount(parts.Length, call.Offset);
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
        call.Context.Budget.Charge(array.Count, call.Offset);
        var items = new List<string>();
        long total = 0;
        for (int i = 0; i < array.Count; i++)
        {
            if (!JsonValues.TryGetString(array[i], out var item))
            {
                throw call.Error($"array element {i} must be a string, got {JsonValues.Describe(array[i])}.");
            }

            total += item.Length + (long)delimiter.Length;
            call.Context.Budget.CheckStringLength(total, call.Offset);
            items.Add(item);
        }

        return JsonValue.Create(string.Join(delimiter, items));
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


    private static JsonNode? PadLeft(FunctionCall call)
    {
        var node = call.Arg(0);
        string text = JsonValues.TryGetInt64(node, out long number) ? number.ToString(CultureInfo.InvariantCulture) : call.String(0);
        int total = call.Int32(1);
        if (total < 0)
        {
            throw call.Error($"total length must not be negative, got {call.Redact(1, Invariant(total))}.");
        }

        // ARM documents no upper bound; the result is held to EARM's string length limit like any other built string.
        call.Context.Budget.CheckStringLength(total, call.Offset);
        call.Context.Budget.ChargeText(Math.Max(total, text.Length), call.Offset);

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
            CheckJsonLength(call, node);
            return node.ToJsonString();
        }

        throw call.Error($"{JsonValues.Describe(node)} cannot be converted to a string.");
    }

    /// <summary>Refuses to serialise a value whose JSON text (estimated from the lengths of its strings and keys, without escapes) would pass the string limit.</summary>
    private static void CheckJsonLength(FunctionCall call, JsonNode node)
    {
        var budget = call.Context.Budget;
        long estimate = 0;
        var stack = new Stack<JsonNode>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            estimate += 4;
            switch (current)
            {
                case JsonObject obj:
                    foreach (var property in obj)
                    {
                        estimate += property.Key.Length + 4;
                        if (property.Value is not null)
                        {
                            stack.Push(property.Value);
                        }
                    }

                    break;
                case JsonArray array:
                    foreach (var item in array)
                    {
                        if (item is not null)
                        {
                            stack.Push(item);
                        }
                    }

                    break;
                case JsonValue leaf when leaf.TryGetValue<string>(out var text):
                    estimate += text.Length;
                    break;
                default:
                    estimate += 16;
                    break;
            }

            budget.CheckStringLength(estimate, call.Offset);
        }

        budget.ChargeText(estimate, call.Offset);
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
}
