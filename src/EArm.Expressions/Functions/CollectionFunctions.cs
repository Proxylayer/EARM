using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Core.Json;
using EArm.Core.Secrets;
using EArm.Expressions.Values;

namespace EArm.Expressions.Functions;

/// <summary>
/// Group 4b: <c>array createArray createObject first last take skip union intersection range items json null
/// objectKeys flatten distinct shallowMerge tryGet indexFromEnd tryIndexFromEnd</c>. Constructors and slicing functions accept unknown elements (they do not inspect
/// values); <c>union</c>/<c>intersection</c> yield an unknown value when any input leaf is unknown.
/// </summary>
internal static class CollectionFunctions
{
    private const string Group = "collection";
    private const int MaxRangeCount = 10000;

    public static void Register(FunctionRegistry registry)
    {
        registry.Register(new FunctionDescriptor { Name = "array", Group = Group, MinArgs = 1, MaxArgs = 1, HandlesUnknown = true, Implementation = Array });
        registry.Register(new FunctionDescriptor { Name = "createArray", Group = Group, MinArgs = 0, HandlesUnknown = true, Implementation = CreateArray });
        registry.Register(new FunctionDescriptor { Name = "createObject", Group = Group, MinArgs = 0, HandlesUnknown = true, Implementation = CreateObject });
        registry.Register(new FunctionDescriptor { Name = "first", Group = Group, MinArgs = 1, MaxArgs = 1, HandlesUnknown = true, Implementation = call => FirstOrLast(call, first: true) });
        registry.Register(new FunctionDescriptor { Name = "last", Group = Group, MinArgs = 1, MaxArgs = 1, HandlesUnknown = true, Implementation = call => FirstOrLast(call, first: false) });
        registry.Register(new FunctionDescriptor { Name = "take", Group = Group, MinArgs = 2, MaxArgs = 2, HandlesUnknown = true, Implementation = call => TakeOrSkip(call, take: true) });
        registry.Register(new FunctionDescriptor { Name = "skip", Group = Group, MinArgs = 2, MaxArgs = 2, HandlesUnknown = true, Implementation = call => TakeOrSkip(call, take: false) });
        SetFunctions.Register(registry, Group);
        registry.Register(new FunctionDescriptor { Name = "range", Group = Group, MinArgs = 2, MaxArgs = 2, Implementation = Range });
        registry.Register(new FunctionDescriptor { Name = "items", Group = Group, MinArgs = 1, MaxArgs = 1, HandlesUnknown = true, Implementation = Items });
        registry.Register(new FunctionDescriptor { Name = "json", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = Json });
        registry.Register(new FunctionDescriptor { Name = "null", Group = Group, MinArgs = 0, MaxArgs = 0, Implementation = _ => null });
        registry.Register(new FunctionDescriptor { Name = "objectKeys", Group = Group, MinArgs = 1, MaxArgs = 1, HandlesUnknown = true, Implementation = ObjectKeys });
        registry.Register(new FunctionDescriptor { Name = "flatten", Group = Group, MinArgs = 1, MaxArgs = 1, HandlesUnknown = true, Implementation = Flatten });
        registry.Register(new FunctionDescriptor { Name = "distinct", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = Distinct });
        registry.Register(new FunctionDescriptor { Name = "shallowMerge", Group = Group, MinArgs = 1, MaxArgs = 1, HandlesUnknown = true, Implementation = ShallowMerge });
        registry.Register(new FunctionDescriptor { Name = "tryGet", Group = Group, MinArgs = 2, MaxArgs = 2, HandlesUnknown = true, Implementation = TryGet });
        registry.Register(new FunctionDescriptor { Name = "indexFromEnd", Group = Group, MinArgs = 2, MaxArgs = 2, HandlesUnknown = true, Implementation = call => IndexFromEnd(call, safe: false) });
        registry.Register(new FunctionDescriptor { Name = "tryIndexFromEnd", Group = Group, MinArgs = 2, MaxArgs = 2, HandlesUnknown = true, Implementation = call => IndexFromEnd(call, safe: true) });
    }

    /// <summary>ARM <c>distinct(array)</c>: the elements without duplicates, in order of first appearance (linear: a hash set decides "already seen").</summary>
    private static JsonNode? Distinct(FunctionCall call)
    {
        var array = call.Array(0);
        call.Context.Budget.Charge(array.Count, call.Offset);
        var result = new JsonArray();
        var seen = new HashSet<JsonNode?>(JsonValues.Comparer.Instance);
        foreach (var item in array)
        {
            if (seen.Add(item))
            {
                result.Add(JsonValues.Clone(item));
            }
        }

        return result;
    }

    /// <summary>
    /// ARM <c>shallowMerge(arrayOfObjects)</c>: the top-level properties of the objects combined, a later one replacing an earlier one entirely
    /// (nested objects are not merged, unlike <c>union</c>). Unknown property values flow through; an unknown array or element makes the whole
    /// result unknown, since its properties cannot be known.
    /// </summary>
    private static JsonNode? ShallowMerge(FunctionCall call)
    {
        if (call.IsUnknown(0))
        {
            return call.UnknownFromArguments();
        }

        var array = call.Array(0);
        call.Context.Budget.Charge(array.Count, call.Offset);
        var result = new JsonObject();
        for (int i = 0; i < array.Count; i++)
        {
            if (UnknownJson.IsUnknown(array[i]))
            {
                return call.UnknownFromArguments();
            }

            if (array[i] is not JsonObject source)
            {
                throw call.Error($"element {i} of the array must be an object, got {JsonValues.Describe(array[i])}.");
            }

            call.Context.Budget.Charge(source.Count, call.Offset);
            foreach (var property in source)
            {
                result[property.Key] = JsonValues.Clone(property.Value);
            }

            call.Context.Budget.CheckCollectionCount(result.Count, call.Offset);
        }

        return result;
    }

    /// <summary>
    /// ARM <c>tryGet(objectOrArray, keyOrIndex)</c>: the property (looked up case-insensitively, as by <c>obj[key]</c>) or the element, or null when
    /// it does not exist or the index is out of bounds. A null container gives null (Bicep's <c>.?</c> compiles to this function). An unknown
    /// container or key makes the result unknown; a known container passes an unknown value it holds through.
    /// </summary>
    private static JsonNode? TryGet(FunctionCall call)
    {
        if (call.IsUnknown(0) || call.IsUnknown(1))
        {
            return call.UnknownFromArguments();
        }

        switch (call.Arg(0))
        {
            case null:
                return null;
            case JsonArray array:
                {
                    long index = call.Int(1);
                    return index >= 0 && index < array.Count ? JsonValues.Clone(array[(int)index]) : null;
                }

            case JsonObject obj:
                {
                    string key = call.String(1);
                    if (obj.TryGetPropertyValue(key, out var exact))
                    {
                        return JsonValues.Clone(exact);
                    }

                    foreach (var property in obj)
                    {
                        if (property.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                        {
                            return JsonValues.Clone(property.Value);
                        }
                    }

                    return null;
                }

            default:
                throw call.Error($"argument 1 must be an object or an array, got {JsonValues.Describe(call.Arg(0))}.");
        }
    }

    /// <summary>
    /// ARM <c>indexFromEnd(array, reverseIndex)</c> / <c>tryIndexFromEnd</c>: the element counted from the end with a one-based index
    /// (<c>1</c> is the last element). Out of range is an error for <c>indexFromEnd</c> and null for <c>tryIndexFromEnd</c>.
    /// </summary>
    private static JsonNode? IndexFromEnd(FunctionCall call, bool safe)
    {
        if (call.IsUnknown(0) || call.IsUnknown(1))
        {
            return call.UnknownFromArguments();
        }

        var array = call.Array(0);
        long reverseIndex = call.Int(1);
        if (reverseIndex >= 1 && reverseIndex <= array.Count)
        {
            return JsonValues.Clone(array[array.Count - (int)reverseIndex]);
        }

        if (safe)
        {
            return null;
        }

        throw call.Failure($"reverse index {call.Render(1)} is outside the array (length {call.Redact(0, array.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))}); it counts from 1 (the last element).");
    }

    private static JsonNode? CreateArray(FunctionCall call)
    {
        var result = new JsonArray();
        for (int i = 0; i < call.Count; i++)
        {
            result.Add(JsonValues.Clone(call.Arg(i)));
        }

        return result;
    }

    private static JsonNode? CreateObject(FunctionCall call)
    {
        if (call.Count % 2 != 0)
        {
            throw call.Error($"expected an even number of arguments (key, value, ...), got {call.Count}.");
        }

        var result = new MaskedKeyObject();
        for (int i = 0; i < call.Count; i += 2)
        {
            if (call.IsUnknown(i))
            {
                return call.UnknownFromArguments();
            }

            // Property names carry no taint of their own, so a key derived from a secure value is masked, never written.
            string key = call.String(i);
            result.Set(key, call.ArgumentIsTainted(i), JsonValues.Clone(call.Arg(i + 1)));
        }

        return result.Build();
    }

    private static JsonNode? FirstOrLast(FunctionCall call, bool first)
    {
        var node = call.Arg(0);
        if (UnknownJson.IsUnknown(node))
        {
            return call.UnknownFromArguments();
        }

        if (node is JsonArray array)
        {
            if (array.Count == 0)
            {
                return null;
            }

            return JsonValues.Clone(array[first ? 0 : array.Count - 1]);
        }

        string text = call.String(0);
        if (text.Length == 0)
        {
            return JsonValue.Create(string.Empty);
        }

        return JsonValue.Create(first ? text[..1] : text[^1..]);
    }

    private static JsonNode? TakeOrSkip(FunctionCall call, bool take)
    {
        var node = call.Arg(0);
        if (UnknownJson.IsUnknown(node) || call.IsUnknown(1))
        {
            return call.UnknownFromArguments();
        }

        long count = call.Int(1);
        if (node is JsonArray array)
        {
            call.Context.Budget.Charge(array.Count, call.Offset);
            var result = new JsonArray();
            int from = take ? 0 : (int)Math.Clamp(count, 0, array.Count);
            int to = take ? (int)Math.Clamp(count, 0, array.Count) : array.Count;
            for (int i = from; i < to; i++)
            {
                result.Add(JsonValues.Clone(array[i]));
            }

            return result;
        }

        string text = call.String(0);
        call.Context.Budget.ChargeText(text.Length, call.Offset);
        int n = (int)Math.Clamp(count, 0, text.Length);
        return JsonValue.Create(take ? text[..n] : text[n..]);
    }

    /// <summary>ARM <c>array(value)</c>: an array is returned as it is; any other value (including null) becomes a one-element array.</summary>
    private static JsonNode? Array(FunctionCall call)
    {
        var value = call.Arg(0);
        if (UnknownJson.IsUnknown(value))
        {
            // An unknown might itself be an array, which array() returns unchanged: the result is not known either.
            return call.UnknownFromArguments();
        }

        return value is JsonArray ? JsonValues.Clone(value) : new JsonArray(JsonValues.Clone(value));
    }

    private static JsonNode? Range(FunctionCall call)
    {
        long start = call.Int(0);
        long count = call.Int(1);
        if (count < 0 || count > MaxRangeCount)
        {
            throw call.Error($"count must be between 0 and {MaxRangeCount}, got {call.Render(1)}.");
        }

        // ARM: startIndex is an int, and startIndex + count may not exceed 2,147,483,647.
        if (start is < int.MinValue or > int.MaxValue || start + count > int.MaxValue)
        {
            throw call.Error($"startIndex must be a 32-bit integer and startIndex + count may not exceed {int.MaxValue} (ARM limit), got {call.Render(0)} and {call.Render(1)}.");
        }

        var result = new JsonArray();
        for (long i = 0; i < count; i++)
        {
            result.Add(JsonValue.Create(start + i));
        }

        return result;
    }

    private static JsonNode? Items(FunctionCall call)
    {
        if (call.IsUnknown(0))
        {
            return call.UnknownFromArguments();
        }

        var obj = call.Object(0);
        call.Context.Budget.Charge(obj.Count, call.Offset);
        var result = new JsonArray();
        foreach (var property in obj.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            result.Add(new JsonObject
            {
                ["key"] = property.Key,
                ["value"] = JsonValues.Clone(property.Value),
            });
        }

        return result;
    }

    private static JsonNode? Json(FunctionCall call)
    {
        return ParseJsonText(call, call.String(0));
    }

    /// <summary>
    /// Parses <paramref name="text"/>, the first argument of <paramref name="call"/> or a string decoded from it (<c>json</c>, <c>base64ToJson</c>):
    /// repeated keys are refused, and the result of a secure argument has masked keys and tainted leaves.
    /// </summary>
    internal static JsonNode? ParseJsonText(FunctionCall call, string text)
    {
        call.Context.Budget.ChargeText(text.Length, call.Offset);
        try
        {
            string jsonText = LoneSurrogates.Replace(text);

            // A repeated key would make the parsed object throw on first use; reject it here, where the text is known.
            if (DuplicateKeys.Find(System.Text.Encoding.UTF8.GetBytes(jsonText), limit: 1) is { Count: > 0 } duplicates)
            {
                throw call.Failure(call.ArgumentIsTainted(0) ? "the secure value repeats a property name." : $"the text is not accepted: {duplicates[0].Describe()}");
            }

            var parsed = JsonNode.Parse(jsonText, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

            // The structure of a secure string (its object keys) is part of the secret, and keys carry no taint of their
            // own: mask them (the same rule as toObject/groupBy/createObject) and taint every leaf.
            return call.ArgumentIsTainted(0) ? SecretJson.Taint(MaskKeys(parsed)) : parsed;
        }
        catch (JsonException ex)
        {
            // The parser's message quotes the offending character, so nothing of it is shown for a secure value.
            throw call.Failure(call.ArgumentIsTainted(0) ? "the secure value is not valid JSON." : $"'{text}' is not valid JSON: {ex.Message}", ex);
        }
    }

    /// <summary>Returns a copy of <paramref name="node"/> in which every object key is masked (<c>***</c>, <c>***#2</c>, ... per object, first-appearance order).</summary>
    private static JsonNode? MaskKeys(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var masked = new MaskedKeyObject();
                foreach (var property in obj)
                {
                    masked.Set(property.Key, true, MaskKeys(property.Value));
                }

                return masked.Build();
            case JsonArray array:
                var copy = new JsonArray();
                foreach (var item in array)
                {
                    copy.Add(MaskKeys(item));
                }

                return copy;
            default:
                return node?.DeepClone();
        }
    }

    private static JsonNode? ObjectKeys(FunctionCall call)
    {
        if (call.IsUnknown(0))
        {
            return call.UnknownFromArguments();
        }

        var result = new JsonArray();
        foreach (var property in call.Object(0))
        {
            result.Add(JsonValue.Create(property.Key));
        }

        return result;
    }

    private static JsonNode? Flatten(FunctionCall call)
    {
        if (call.IsUnknown(0))
        {
            return call.UnknownFromArguments();
        }

        var result = new JsonArray();
        var budget = call.Context.Budget;
        foreach (var item in call.Array(0))
        {
            if (item is JsonArray inner)
            {
                // Checked per inner array, before it is copied: an array of 100,000 arrays of 100,000 elements must not get as far as copying.
                budget.CheckCollectionCount(result.Count + (long)inner.Count, call.Offset);
                budget.Charge(inner.Count, call.Offset);
                foreach (var element in inner)
                {
                    result.Add(JsonValues.Clone(element));
                }
            }
            else
            {
                budget.CheckCollectionCount(result.Count + 1L, call.Offset);
                result.Add(JsonValues.Clone(item));
            }
        }

        return result;
    }
}
