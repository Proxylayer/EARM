using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Expressions.Values;

namespace EArm.Expressions.Functions;

/// <summary>
/// <c>union</c> and <c>intersection</c> over arrays or objects (registered with the collection group). Arrays compare elements structurally
/// (linear: hash sets decide "already seen"). Objects match property names case-insensitively, as ARM does: <c>union({ a: 1 }, { A: 2 })</c>
/// is one property, named as the first argument spells it, with the later value.
/// </summary>
internal static class SetFunctions
{
    public static void Register(FunctionRegistry registry, string group)
    {
        registry.Register(new FunctionDescriptor { Name = "union", Group = group, MinArgs = 1, Implementation = Union });
        registry.Register(new FunctionDescriptor { Name = "intersection", Group = group, MinArgs = 1, Implementation = Intersection });
    }

    private static JsonNode? Union(FunctionCall call)
    {
        if (call.Arg(0) is JsonObject)
        {
            var result = new JsonObject();
            for (int i = 0; i < call.Count; i++)
            {
                MergeInto(call, result, call.Object(i));
            }

            return result;
        }

        var merged = new JsonArray();
        var seen = new HashSet<JsonNode?>(JsonValues.Comparer.Instance);
        for (int i = 0; i < call.Count; i++)
        {
            var array = call.Array(i);
            call.Context.Budget.Charge(array.Count, call.Offset);
            foreach (var item in array)
            {
                if (!seen.Add(item))
                {
                    continue;
                }

                call.Context.Budget.CheckCollectionCount(merged.Count + 1L, call.Offset);
                merged.Add(JsonValues.Clone(item));
            }
        }

        return merged;
    }

    /// <summary>
    /// ARM <c>union()</c> on objects: the properties of <paramref name="source"/> are added to <paramref name="target"/>, a later value replacing an
    /// earlier one under the same name (compared case-insensitively; the first spelling is kept), except that two objects under the same name are
    /// merged recursively (arrays are not merged: the later one wins).
    /// </summary>
    private static void MergeInto(FunctionCall call, JsonObject target, JsonObject source)
    {
        call.Context.Budget.Charge(source.Count, call.Offset);
        var names = KeyMap(target);
        foreach (var property in source)
        {
            if (!names.TryGetValue(property.Key, out var existingName))
            {
                names[property.Key] = property.Key;
                target[property.Key] = JsonValues.Clone(property.Value);
            }
            else if (property.Value is JsonObject nested && target[existingName] is JsonObject existing)
            {
                MergeInto(call, existing, nested);
            }
            else
            {
                target[existingName] = JsonValues.Clone(property.Value);
            }
        }

        call.Context.Budget.CheckCollectionCount(target.Count, call.Offset);
    }

    private static JsonNode? Intersection(FunctionCall call)
    {
        if (call.Arg(0) is JsonObject firstObject)
        {
            var result = new JsonObject();
            call.Context.Budget.Charge((long)firstObject.Count * Math.Max(1, call.Count - 1), call.Offset);
            var others = new List<(JsonObject Object, Dictionary<string, string> Names)>();
            for (int i = 1; i < call.Count; i++)
            {
                var other = call.Object(i);
                others.Add((other, KeyMap(other)));
            }

            foreach (var property in firstObject)
            {
                bool common = others.TrueForAll(other => other.Names.TryGetValue(property.Key, out var name) && JsonValues.AreEqual(property.Value, other.Object[name]));
                if (common)
                {
                    result[property.Key] = JsonValues.Clone(property.Value);
                }
            }

            return result;
        }

        var first = call.Array(0);
        call.Context.Budget.Charge(first.Count, call.Offset);
        var sets = new List<HashSet<JsonNode?>>();
        for (int i = 1; i < call.Count; i++)
        {
            var array = call.Array(i);
            call.Context.Budget.Charge(array.Count, call.Offset);
            sets.Add(new HashSet<JsonNode?>(array, JsonValues.Comparer.Instance));
        }

        var intersection = new JsonArray();
        var added = new HashSet<JsonNode?>(JsonValues.Comparer.Instance);
        foreach (var item in first)
        {
            if (added.Contains(item))
            {
                continue;
            }

            if (sets.TrueForAll(set => set.Contains(item)))
            {
                added.Add(item);
                intersection.Add(JsonValues.Clone(item));
            }
        }

        return intersection;
    }

    /// <summary>The property names of <paramref name="obj"/>, keyed case-insensitively to their actual spelling (the first one wins).</summary>
    private static Dictionary<string, string> KeyMap(JsonObject obj)
    {
        var names = new Dictionary<string, string>(obj.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var property in obj)
        {
            names.TryAdd(property.Key, property.Key);
        }

        return names;
    }
}
