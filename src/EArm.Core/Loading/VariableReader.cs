using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Json;

namespace EArm.Core.Loading;

/// <summary>
/// Reads the <c>variables</c> object: one definition per name (case-insensitive), and the ARM variable iteration of a top-level
/// <c>"copy": [ { "name", "count", "input" } ]</c> array, whose every entry declares the variable <c>name</c>
/// (https://learn.microsoft.com/azure/azure-resource-manager/templates/copy-variables). A <c>copy</c> array inside a variable's value
/// (an object property built by iteration) is left in the definition; the evaluator expands it.
/// </summary>
internal static class VariableReader
{
    public static (Dictionary<string, JsonNode?> Variables, Dictionary<string, VariableIteration> Iterations) Read(JsonObject root, LoadContext context)
    {
        var variables = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        var iterations = new Dictionary<string, VariableIteration>(StringComparer.OrdinalIgnoreCase);
        if (root["variables"] is not JsonObject source)
        {
            return (variables, iterations);
        }

        var basePath = JsonPath.Of("variables");
        foreach (var entry in source)
        {
            if (!entry.Key.Equals(PropertyCopyBlock.Key, StringComparison.OrdinalIgnoreCase))
            {
                Add(variables, entry.Key, entry.Value, basePath.Append(entry.Key), context);
            }
        }

        // The iterations are read after the plain variables so a clash names the iteration entry, wherever the two are written.
        foreach (var entry in source)
        {
            if (entry.Key.Equals(PropertyCopyBlock.Key, StringComparison.OrdinalIgnoreCase))
            {
                ReadIterations(entry.Value, basePath.Append(entry.Key), variables, iterations, context);
            }
        }

        return (variables, iterations);
    }

    private static void ReadIterations(JsonNode? block, JsonPath blockPath, Dictionary<string, JsonNode?> variables, Dictionary<string, VariableIteration> iterations, LoadContext context)
    {
        if (block is not JsonArray entries)
        {
            context.Error(DiagnosticCodes.InvalidCopy, "'copy' in variables must be an array of { \"name\", \"count\", \"input\" } entries (variable iteration).", blockPath);
            return;
        }

        for (int i = 0; i < entries.Count; i++)
        {
            var path = blockPath.Append(i);
            if (entries[i] is not JsonObject entry)
            {
                context.Error(DiagnosticCodes.InvalidCopy, $"Variable iteration entry {i} must be an object with 'name', 'count' and 'input'.", path);
                continue;
            }

            string? name = TemplateJson.LiteralString(entry["name"]);
            if (string.IsNullOrEmpty(name))
            {
                context.Error(DiagnosticCodes.InvalidCopy, $"Variable iteration entry {i} needs a non-empty literal 'name' (the variable it declares).", path.Append("name"));
                continue;
            }

            if (!entry.ContainsKey("count"))
            {
                context.Error(DiagnosticCodes.InvalidCopy, $"Variable iteration '{name}' is missing the required 'count'.", path.Append("count"));
                continue;
            }

            if (!entry.ContainsKey("input"))
            {
                context.Error(DiagnosticCodes.InvalidCopy, $"Variable iteration '{name}' is missing the required 'input'.", path.Append("input"));
                continue;
            }

            if (Add(variables, name, entry, path.Append("name"), context))
            {
                iterations[name] = new VariableIteration { Name = name, Count = entry["count"], Input = entry["input"], Source = entry, Path = path };
            }
        }
    }

    private static bool Add(Dictionary<string, JsonNode?> variables, string name, JsonNode? value, JsonPath path, LoadContext context)
    {
        if (variables.TryAdd(name, value))
        {
            return true;
        }

        context.Error(DiagnosticCodes.DuplicateDefinition, $"Variable '{name}' is defined more than once (variable names are case-insensitive; a variable iteration entry declares the variable it names).", path);
        return false;
    }
}
