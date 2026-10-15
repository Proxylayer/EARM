using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Core.Json;

namespace EArm.Core.Validation;

/// <summary>One property of an evaluated resource whose value has the wrong JSON type for the resource type's schema.</summary>
public sealed record PropertyTypeFinding(JsonPath Path, string Message);

/// <summary>
/// Checks the evaluated <c>properties</c> of a resource against the JSON type of each property in the per-type schema. The template schema
/// can only judge what is written in the file: a property given as an expression (<c>"[createArray(...)]"</c>) matches the "expression" branch of
/// its schema and says nothing about the value it produces. Once the expression is evaluated offline the value is known, and a value of the
/// wrong type (an array for a string property) is a certain failure at deployment time, so it is reported by <c>validate</c>.
/// <para>
/// Only <c>type</c> findings are reported, and only at values that are fully known: a leaf the caller marks opaque (a deploy-time unknown or a
/// secure value) is replaced by a placeholder and any finding at or below it is dropped.
/// </para>
/// </summary>
public static class PropertyTypeChecker
{
    /// <summary>
    /// The type mismatches of <paramref name="evaluatedProperties"/> for <paramref name="resourceType"/>; empty when the type has no schema, the
    /// value is not an object or the schema cannot be evaluated. Paths are relative to the properties object.
    /// </summary>
    /// <param name="isOpaque">True for a leaf whose value is not known (unknown or secure); it is not checked.</param>
    public static IReadOnlyList<PropertyTypeFinding> Check(string resourceType, JsonNode? evaluatedProperties, Func<JsonNode?, bool> isOpaque)
    {
        ArgumentNullException.ThrowIfNull(resourceType);
        ArgumentNullException.ThrowIfNull(isOpaque);

        if (evaluatedProperties is not JsonObject || !TemplateSchemas.Default.TypesByName.TryGetValue(resourceType, out var schema))
        {
            return [];
        }

        var opaquePaths = new HashSet<JsonPath>();
        int counter = 0;
        var placeholderTree = Placeholders(evaluatedProperties, JsonPath.Root, isOpaque, opaquePaths, ref counter);

        IReadOnlyList<SchemaFinding> findings;
        try
        {
            using var document = JsonDocument.Parse(placeholderTree!.ToJsonString());
            findings = TemplateSchemas.Evaluate(schema, document.RootElement.Clone(), placeholderTree);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException or NotSupportedException)
        {
            return [];
        }

        var result = new List<PropertyTypeFinding>();
        foreach (var finding in findings)
        {
            if (finding.Keyword != "type" || IsOpaque(finding.Path, opaquePaths))
            {
                continue;
            }

            result.Add(new PropertyTypeFinding(finding.Path, finding.Message));
        }

        return result;
    }

    private static bool IsOpaque(JsonPath path, HashSet<JsonPath> opaquePaths)
    {
        for (JsonPath? current = path; current is not null; current = current.Parent)
        {
            if (opaquePaths.Contains(current))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A copy of the tree in which every opaque leaf is a distinct string (so uniqueness checks do not see them as equal) and its path is recorded.</summary>
    private static JsonNode? Placeholders(JsonNode? node, JsonPath path, Func<JsonNode?, bool> isOpaque, HashSet<JsonPath> opaquePaths, ref int counter)
    {
        ValueGuard.EnsureStack();
        switch (node)
        {
            case JsonObject obj:
                {
                    var copy = new JsonObject();
                    foreach (var property in obj)
                    {
                        copy[property.Key] = Placeholders(property.Value, path.Append(property.Key), isOpaque, opaquePaths, ref counter);
                    }

                    return copy;
                }

            case JsonArray array:
                {
                    var copy = new JsonArray();
                    for (int i = 0; i < array.Count; i++)
                    {
                        copy.Add(Placeholders(array[i], path.Append(i), isOpaque, opaquePaths, ref counter));
                    }

                    return copy;
                }

            case null:
                return null;

            default:
                if (isOpaque(node))
                {
                    opaquePaths.Add(path);
                    return JsonValue.Create("opaque-" + (counter++).ToString(CultureInfo.InvariantCulture));
                }

                return node.DeepClone();
        }
    }
}
