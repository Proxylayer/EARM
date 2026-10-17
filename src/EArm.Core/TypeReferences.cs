using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core.Json;

namespace EArm.Core;

/// <summary>
/// languageVersion 2.0 user-defined types: a type node (a parameter, an output, a definition, or a nested <c>properties</c> / <c>items</c> /
/// <c>prefixItems</c> / <c>discriminator</c> entry) may name a member of <c>definitions</c> with <c>"$ref": "#/definitions/name"</c> instead of
/// spelling its constraints (https://learn.microsoft.com/azure/azure-resource-manager/templates/definitions). The keys written next to the
/// <c>$ref</c> (Bicep writes <c>nullable</c> and <c>metadata</c> there) take precedence over the referenced definition's.
/// </summary>
public static class TypeReferences
{
    public const string RefKey = "$ref";

    public const string DefinitionsPrefix = "#/definitions/";

    /// <summary>The most <c>$ref</c> hops followed for one node; a longer chain is reported as a cycle.</summary>
    public const int MaxChain = 32;

    /// <summary>
    /// The effective type node of <paramref name="node"/>: the node itself when it has no <c>$ref</c>; otherwise a detached copy of the referenced
    /// definition (followed transitively) with the node's own keys laid over it. Returns <c>null</c> with <paramref name="problem"/> set when a
    /// reference is not of the form <c>#/definitions/name</c>, names no definition, or the chain loops.
    /// </summary>
    public static JsonObject? Resolve(JsonObject node, IReadOnlyDictionary<string, JsonObject> definitions, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(definitions);
        problem = null;
        if (!node.ContainsKey(RefKey))
        {
            return node;
        }

        var layers = new List<JsonObject> { node };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = node;
        while (current.ContainsKey(RefKey))
        {
            string? reference = TemplateJson.AsString(current[RefKey]);
            if (reference is null || !reference.StartsWith(DefinitionsPrefix, StringComparison.Ordinal))
            {
                problem = $"'$ref' must be a string of the form '{DefinitionsPrefix}<name>'";
                return null;
            }

            string name = reference[DefinitionsPrefix.Length..];
            if (!definitions.TryGetValue(name, out var target))
            {
                problem = $"'$ref' names '{name}', which is not a member of 'definitions'";
                return null;
            }

            if (!seen.Add(name) || seen.Count > MaxChain)
            {
                problem = $"'$ref' to '{name}' refers back to itself";
                return null;
            }

            layers.Add(target);
            current = target;
        }

        var merged = new JsonObject();
        for (int i = layers.Count - 1; i >= 0; i--)
        {
            foreach (var (key, value) in layers[i])
            {
                if (key != RefKey)
                {
                    merged[key] = value?.DeepClone();
                }
            }
        }

        return merged;
    }

    /// <summary>True when the effective node says <c>"nullable": true</c>.</summary>
    public static bool IsNullable(JsonObject? effective)
    {
        return effective is not null && TemplateJson.AsBool(effective["nullable"]) == true;
    }
}
