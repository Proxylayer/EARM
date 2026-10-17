using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace EArm.Core.Json;

/// <summary>
/// ARM property iteration: a <c>"copy": [ { "name", "count", "input" } ]</c> array inside an object of resource properties. Each
/// entry declares the property <c>name</c> as an array of <c>count</c> elements, each built from <c>input</c> with
/// <c>copyIndex('name')</c> bound to its position. The declaration keeps the block as authored (diagnostics point into it);
/// the evaluator expands it. Anything that asks whether a property is declared must ask <see cref="Declares"/>, which also
/// sees the properties a block declares.
/// </summary>
public static class PropertyCopyBlock
{
    public const string Key = "copy";

    /// <summary>One entry of a block. <see cref="Name"/> is <c>null</c> unless the entry's <c>name</c> is a literal string.</summary>
    public sealed record Entry(int Index, string? Name, JsonNode? Count, JsonNode? Input, bool HasInput, JsonNode? Source);

    /// <summary>True for the <c>copy</c> key of an object whose value is an array (a property iteration block).</summary>
    public static bool IsBlock(string key, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        return string.Equals(key, Key, StringComparison.OrdinalIgnoreCase) && value is JsonArray;
    }

    /// <summary>The entries of the block in <paramref name="obj"/>, in order; empty when it has none.</summary>
    public static IReadOnlyList<Entry> Entries(JsonObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var entries = new List<Entry>();
        foreach (var (key, value) in obj)
        {
            if (!IsBlock(key, value))
            {
                continue;
            }

            var array = (JsonArray)value!;
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonObject entry)
                {
                    string? name = TemplateJson.LiteralString(entry["name"]);
                    entries.Add(new Entry(i, string.IsNullOrEmpty(name) ? null : name, entry["count"], entry["input"], entry.ContainsKey("input"), entry));
                }
                else
                {
                    entries.Add(new Entry(i, null, null, null, false, array[i]));
                }
            }
        }

        return entries;
    }

    /// <summary>True when <paramref name="obj"/> has a property iteration block.</summary>
    public static bool HasBlock(JsonObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        foreach (var (key, value) in obj)
        {
            if (IsBlock(key, value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="obj"/> declares <paramref name="property"/> (case-insensitive): as a key of its own, or as the
    /// <c>name</c> of an entry of its property iteration block.
    /// </summary>
    public static bool Declares(JsonObject obj, string property)
    {
        return DeclaredKey(obj, property) is not null;
    }

    /// <summary>
    /// The spelling under which <paramref name="obj"/> declares <paramref name="property"/>: its own key, or the <c>name</c> of the
    /// property iteration entry that builds it; <c>null</c> when neither declares it.
    /// </summary>
    public static string? DeclaredKey(JsonObject obj, string property)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(property);
        foreach (var (key, value) in obj)
        {
            if (string.Equals(key, property, StringComparison.OrdinalIgnoreCase) && !IsBlock(key, value))
            {
                return key;
            }
        }

        foreach (var entry in Entries(obj))
        {
            if (entry.Name is not null && string.Equals(entry.Name, property, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Name;
            }
        }

        return null;
    }
}
