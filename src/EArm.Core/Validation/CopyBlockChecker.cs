using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Json;

namespace EArm.Core.Validation;

/// <summary>
/// The shape of the two ARM iterations that are not resource loops (<see cref="DiagnosticCodes.InvalidCopy"/>):
/// <list type="bullet">
/// <item>property iteration, <c>"copy": [ { "name", "count", "input" } ]</c> in any object of a resource's <c>properties</c>: every entry is an
/// object with a non-empty literal <c>name</c>, a <c>count</c> and an <c>input</c>; no two entries, and no entry and a sibling key, declare the
/// same property;</item>
/// <item>output iteration, <c>"copy": { "count", "input" }</c> on an output, which replaces <c>value</c>.</item>
/// </list>
/// A literal <c>count</c> must be a non-negative integer; an expression is checked when it is evaluated.
/// </summary>
internal sealed class CopyBlockChecker
{
    private readonly TemplateDocument _document;
    private readonly List<Diagnostic> _diagnostics;

    public CopyBlockChecker(TemplateDocument document, List<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(diagnostics);
        _document = document;
        _diagnostics = diagnostics;
    }

    public void Run()
    {
        foreach (var resource in _document.Resources)
        {
            CheckProperties(resource.Properties, resource.Path.Append("properties"));
        }

        foreach (var output in _document.Outputs.Values)
        {
            CheckOutput(output);
        }
    }

    private void CheckProperties(JsonNode? node, JsonPath path)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (PropertyCopyBlock.IsBlock(key, value))
                    {
                        CheckBlock(obj, key, (JsonArray)value!, path.Append(key));
                    }
                    else
                    {
                        CheckProperties(value, path.Append(key));
                    }
                }

                break;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    CheckProperties(array[i], path.Append(i));
                }

                break;
        }
    }

    private void CheckBlock(JsonObject owner, string blockKey, JsonArray block, JsonPath blockPath)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < block.Count; i++)
        {
            var entryPath = blockPath.Append(i);
            if (block[i] is not JsonObject entry)
            {
                Error("A property copy entry must be an object with 'name', 'count' and 'input'.", entryPath);
                continue;
            }

            string? name = TemplateJson.LiteralString(entry["name"]);
            if (string.IsNullOrEmpty(name) || ExpressionSyntax.IsExpression(name))
            {
                Error("A property copy entry needs a non-empty literal 'name': the property it builds, also used by copyIndex('name').", entryPath.Append("name"));
            }
            else if (!names.Add(name))
            {
                Error($"Two property copy entries build the property '{name}'.", entryPath.Append("name"));
            }
            else if (SiblingKey(owner, blockKey, name) is { } sibling)
            {
                Error($"The property copy entry '{name}' builds a property that is also declared directly ('{sibling}'); declare it one way.", entryPath.Append("name"));
            }

            CheckCount(entry, entryPath, "A property copy entry");
            if (!entry.ContainsKey("input"))
            {
                Error("A property copy entry is missing the required 'input' property (the value of each element).", entryPath.Append("input"));
            }
            else
            {
                CheckProperties(entry["input"], entryPath.Append("input"));
            }
        }
    }

    private void CheckOutput(OutputDefinition output)
    {
        if (output.Source["copy"] is { } node && node is not JsonObject)
        {
            Error($"Output '{output.Name}': 'copy' must be an object with 'count' and 'input'.", output.Path.Append("copy"));
            return;
        }

        if (output.Copy is not { } copy)
        {
            return;
        }

        if (output.Source.ContainsKey("value"))
        {
            Error($"Output '{output.Name}' declares both 'value' and 'copy'; an output loop builds the value from 'copy.input', so remove 'value'.", output.Path.Append("value"));
        }

        CheckCount(copy.Source, copy.Path, $"Output '{output.Name}': 'copy'");
        if (!copy.Source.ContainsKey("input"))
        {
            Error($"Output '{output.Name}': 'copy' is missing the required 'input' property (the value of each element).", copy.Path.Append("input"));
        }

        foreach (var (key, _) in copy.Source)
        {
            if (key is not ("count" or "input"))
            {
                Error($"Output '{output.Name}': 'copy' takes only 'count' and 'input', not '{key}' (an output loop has no name; use copyIndex() without one).", copy.Path.Append(key));
            }
        }
    }

    private void CheckCount(JsonObject copy, JsonPath path, string what)
    {
        if (!copy.TryGetPropertyValue("count", out var count) || count is null)
        {
            Error($"{what} is missing the required 'count' property.", path.Append("count"));
            return;
        }

        if (TemplateJson.IsExpression(count))
        {
            return;
        }

        long? literal = TemplateJson.AsInt64(count);
        if (literal is null)
        {
            Error($"{what}: 'count' must be an integer or an expression.", path.Append("count"));
        }
        else if (literal < 0)
        {
            Error($"{what}: 'count' must not be negative (was {literal.Value.ToString(CultureInfo.InvariantCulture)}).", path.Append("count"));
        }
    }

    private static string? SiblingKey(JsonObject owner, string blockKey, string name)
    {
        foreach (var (key, _) in owner)
        {
            if (!string.Equals(key, blockKey, StringComparison.Ordinal) && string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }

        return null;
    }

    private void Error(string message, JsonPath path)
    {
        _diagnostics.Add(Diagnostic.Error(DiagnosticCodes.InvalidCopy, message, path, _document.LocationOf(path), _document.SourcePath));
    }
}
