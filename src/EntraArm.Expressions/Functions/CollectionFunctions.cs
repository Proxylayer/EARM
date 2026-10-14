using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using EntraArm.Core.Secrets;
using EntraArm.Expressions.Values;

namespace EntraArm.Expressions.Functions;

/// <summary>
/// Group 4b: <c>array createArray createObject first last take skip union intersection range items json null
/// objectKeys flatten</c>. Constructors and slicing functions accept unknown elements (they do not inspect
/// values); <c>union</c>/<c>intersection</c> yield an unknown value when any input leaf is unknown.
/// </summary>
internal static class CollectionFunctions
{
    private const string Group = "collection";
    private const int MaxRangeCount = 10000;

    public static void Register(FunctionRegistry registry)
    {
        registry.Register(new FunctionDescriptor { Name = "array", Group = Group, MinArgs = 1, MaxArgs = 1, HandlesUnknown = true, Implementation = call => new JsonArray(JsonValues.Clone(call.Arg(0))) });
        registry.Register(new FunctionDescriptor { Name = "createArray", Group = Group, MinArgs = 0, HandlesUnknown = true, Implementation = CreateArray });
        registry.Register(new FunctionDescriptor { Name = "createObject", Group = Group, MinArgs = 0, HandlesUnknown = true, Implementation = CreateObject });
        registry.Register(new FunctionDescriptor { Name = "first", Group = Group, MinArgs = 1, MaxArgs = 1, HandlesUnknown = true, Implementation = call => FirstOrLast(call, first: true) });
        registry.Register(new FunctionDescriptor { Name = "last", Group = Group, MinArgs = 1, MaxArgs = 1, HandlesUnknown = true, Implementation = call => FirstOrLast(call, first: false) });
        registry.Register(new FunctionDescriptor { Name = "take", Group = Group, MinArgs = 2, MaxArgs = 2, HandlesUnknown = true, Implementation = call => TakeOrSkip(call, take: true) });
        registry.Register(new FunctionDescriptor { Name = "skip", Group = Group, MinArgs = 2, MaxArgs = 2, HandlesUnknown = true, Implementation = call => TakeOrSkip(call, take: false) });
        registry.Register(new FunctionDescriptor { Name = "union", Group = Group, MinArgs = 1, Implementation = Union });
        registry.Register(new FunctionDescriptor { Name = "intersection", Group = Group, MinArgs = 1, Implementation = Intersection });
        registry.Register(new FunctionDescriptor { Name = "range", Group = Group, MinArgs = 2, MaxArgs = 2, Implementation = Range });
        registry.Register(new FunctionDescriptor { Name = "items", Group = Group, MinArgs = 1, MaxArgs = 1, HandlesUnknown = true, Implementation = Items });
        registry.Register(new FunctionDescriptor { Name = "json", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = Json });
        registry.Register(new FunctionDescriptor { Name = "null", Group = Group, MinArgs = 0, MaxArgs = 0, Implementation = _ => null });
        registry.Register(new FunctionDescriptor { Name = "objectKeys", Group = Group, MinArgs = 1, MaxArgs = 1, HandlesUnknown = true, Implementation = ObjectKeys });
        registry.Register(new FunctionDescriptor { Name = "flatten", Group = Group, MinArgs = 1, MaxArgs = 1, HandlesUnknown = true, Implementation = Flatten });
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
        int n = (int)Math.Clamp(count, 0, text.Length);
        return JsonValue.Create(take ? text[..n] : text[n..]);
    }

    private static JsonNode? Union(FunctionCall call)
    {
        if (call.Arg(0) is JsonObject)
        {
            var result = new JsonObject();
            for (int i = 0; i < call.Count; i++)
            {
                foreach (var property in call.Object(i))
                {
                    result[property.Key] = JsonValues.Clone(property.Value);
                }
            }

            return result;
        }

        var merged = new JsonArray();
        var seen = new List<JsonNode?>();
        for (int i = 0; i < call.Count; i++)
        {
            foreach (var item in call.Array(i))
            {
                if (seen.Any(s => JsonValues.AreEqual(s, item)))
                {
                    continue;
                }

                seen.Add(item);
                merged.Add(JsonValues.Clone(item));
            }
        }

        return merged;
    }

    private static JsonNode? Intersection(FunctionCall call)
    {
        if (call.Arg(0) is JsonObject firstObject)
        {
            var result = new JsonObject();
            foreach (var property in firstObject)
            {
                bool common = true;
                for (int i = 1; i < call.Count && common; i++)
                {
                    common = call.Object(i).TryGetPropertyValue(property.Key, out var other) && JsonValues.AreEqual(property.Value, other);
                }

                if (common)
                {
                    result[property.Key] = JsonValues.Clone(property.Value);
                }
            }

            return result;
        }

        var intersection = new JsonArray();
        var added = new List<JsonNode?>();
        foreach (var item in call.Array(0))
        {
            if (added.Any(a => JsonValues.AreEqual(a, item)))
            {
                continue;
            }

            bool common = true;
            for (int i = 1; i < call.Count && common; i++)
            {
                common = call.Array(i).Any(other => JsonValues.AreEqual(other, item));
            }

            if (common)
            {
                added.Add(item);
                intersection.Add(JsonValues.Clone(item));
            }
        }

        return intersection;
    }

    private static JsonNode? Range(FunctionCall call)
    {
        long start = call.Int(0);
        long count = call.Int(1);
        if (count < 0 || count > MaxRangeCount)
        {
            throw call.Error($"count must be between 0 and {MaxRangeCount}, got {call.Render(1)}.");
        }

        var result = new JsonArray();
        try
        {
            for (long i = 0; i < count; i++)
            {
                result.Add(JsonValue.Create(checked(start + i)));
            }
        }
        catch (OverflowException ex)
        {
            throw call.Failure($"the range starting at {call.Render(0)} with {call.Render(1)} element(s) exceeds the 64-bit integer range.", ex);
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
        string text = call.String(0);
        try
        {
            var parsed = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

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
        foreach (var item in call.Array(0))
        {
            if (item is JsonArray inner)
            {
                foreach (var element in inner)
                {
                    result.Add(JsonValues.Clone(element));
                }
            }
            else
            {
                result.Add(JsonValues.Clone(item));
            }
        }

        return result;
    }
}
