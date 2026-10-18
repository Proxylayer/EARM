using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Json;

namespace EArm.Core.Validation;

/// <summary>
/// The languageVersion 2.0 type system, offline (https://learn.microsoft.com/azure/azure-resource-manager/templates/definitions): every type node
/// (a member of <c>definitions</c>, a parameter, an output, a user-defined function parameter or output, and the nested types of
/// <c>properties</c>, <c>additionalProperties</c>, <c>items</c>, <c>prefixItems</c> and <c>discriminator.mapping</c>) has a known <c>type</c>
/// or a resolvable <c>$ref</c>, and only the constraints that apply to its type (<see cref="DiagnosticCodes.InvalidTypeDefinition"/>). The
/// languageVersion 2.0 keywords in a template without <c>"languageVersion": "2.0"</c> are <see cref="DiagnosticCodes.InvalidLanguageVersion"/>.
/// </summary>
internal sealed class TypeDefinitionChecker
{
    private const int MaxDepth = 64;

    private static readonly string[] Version2Keywords = ["$ref", "nullable", "properties", "additionalProperties", "items", "prefixItems", "discriminator", "sealed"];

    private readonly TemplateDocument _document;
    private readonly List<Diagnostic> _diagnostics;

    public TypeDefinitionChecker(TemplateDocument document, List<Diagnostic> diagnostics)
    {
        _document = document;
        _diagnostics = diagnostics;
    }

    public void Run()
    {
        if (_document.Root["definitions"] is not null && !_document.UsesSymbolicNames)
        {
            Report(DiagnosticCodes.InvalidLanguageVersion, "'definitions' (user-defined types) needs \"languageVersion\": \"2.0\".", JsonPath.Of("definitions"));
        }

        foreach (var (name, definition) in _document.Definitions)
        {
            CheckNode(definition, JsonPath.Of("definitions", name), 0);
        }

        foreach (var parameter in _document.Parameters.Values)
        {
            CheckNode(parameter.Source, parameter.Path, 0);
        }

        foreach (var output in _document.Outputs.Values)
        {
            CheckNode(output.Source, output.Path, 0);
        }

        foreach (var ns in _document.Functions)
        {
            foreach (var member in ns.Members.Values)
            {
                if (member.Source["parameters"] is JsonArray parameters)
                {
                    for (int i = 0; i < parameters.Count; i++)
                    {
                        if (parameters[i] is JsonObject parameter && parameter.ContainsKey(TypeReferences.RefKey))
                        {
                            CheckReference(parameter, member.Path.Append("parameters").Append(i));
                        }
                    }
                }

                if (member.Source["output"] is JsonObject output && output.ContainsKey(TypeReferences.RefKey))
                {
                    CheckReference(output, member.Path.Append("output"));
                }
            }
        }
    }

    private void CheckNode(JsonObject node, JsonPath path, int depth)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        if (!_document.UsesSymbolicNames)
        {
            foreach (var keyword in Version2Keywords)
            {
                if (node.ContainsKey(keyword))
                {
                    Report(DiagnosticCodes.InvalidLanguageVersion, $"'{keyword}' is a languageVersion 2.0 type constraint; add \"languageVersion\": \"2.0\" to the template.", path.Append(keyword));
                }
            }
        }

        if (node.ContainsKey(TypeReferences.RefKey) && !CheckReference(node, path))
        {
            return;
        }

        var effective = TypeReferences.Resolve(node, _document.Definitions, out _) ?? node;
        if (!node.ContainsKey(TypeReferences.RefKey) && !node.ContainsKey("type"))
        {
            // A parameter or output without 'type' is reported by the semantic checker; a nested type is reported here.
            if (depth > 0 || path.Segments.Count > 0 && path.Segments[0].Name == "definitions")
            {
                Report(DiagnosticCodes.InvalidTypeDefinition, "A type needs 'type' or '$ref'.", path);
            }

            return;
        }

        string? typeText = TemplateJson.AsString(effective["type"]);
        if (!TemplateParameterTypes.TryParse(typeText, out var type))
        {
            if (depth > 0 || path.Segments[0].Name == "definitions" || node.ContainsKey(TypeReferences.RefKey))
            {
                Report(DiagnosticCodes.InvalidTypeDefinition, $"Unknown type '{typeText}'; expected one of: {string.Join(", ", TemplateParameterTypes.CanonicalNames)}.", path.Append("type"));
            }

            return;
        }

        bool isObject = type is TemplateParameterType.Object or TemplateParameterType.SecureObject;
        bool isArray = type == TemplateParameterType.Array;
        RequireApplicable(node, path, "properties", isObject, "object");
        RequireApplicable(node, path, "additionalProperties", isObject, "object");
        RequireApplicable(node, path, "discriminator", isObject, "object");
        RequireApplicable(node, path, "sealed", isObject, "object");
        RequireApplicable(node, path, "items", isArray, "array");
        RequireApplicable(node, path, "prefixItems", isArray, "array");

        if (node["properties"] is JsonObject properties)
        {
            foreach (var (name, value) in properties)
            {
                CheckNested(value, path.Append("properties").Append(name), depth);
            }
        }

        if (node["additionalProperties"] is JsonObject additional)
        {
            CheckNode(additional, path.Append("additionalProperties"), depth + 1);
        }

        if (node["items"] is JsonObject items)
        {
            CheckNode(items, path.Append("items"), depth + 1);
        }

        if (node["prefixItems"] is JsonArray prefixItems)
        {
            for (int i = 0; i < prefixItems.Count; i++)
            {
                CheckNested(prefixItems[i], path.Append("prefixItems").Append(i), depth);
            }
        }

        if (node["discriminator"] is JsonObject discriminator && discriminator["mapping"] is JsonObject mapping)
        {
            foreach (var (tag, variant) in mapping)
            {
                CheckNested(variant, path.Append("discriminator").Append("mapping").Append(tag), depth);
            }
        }
    }

    private void CheckNested(JsonNode? node, JsonPath path, int depth)
    {
        if (node is JsonObject nested)
        {
            CheckNode(nested, path, depth + 1);
        }
        else
        {
            Report(DiagnosticCodes.InvalidTypeDefinition, "A nested type must be an object with 'type' or '$ref'.", path);
        }
    }

    /// <summary>Reports a <c>$ref</c> that cannot be resolved; true when it resolves.</summary>
    private bool CheckReference(JsonObject node, JsonPath path)
    {
        if (TypeReferences.Resolve(node, _document.Definitions, out var problem) is not null)
        {
            return true;
        }

        Report(DiagnosticCodes.InvalidTypeDefinition, $"{problem}.", path.Append(TypeReferences.RefKey));
        return false;
    }

    private void RequireApplicable(JsonObject node, JsonPath path, string keyword, bool applies, string typeName)
    {
        if (!applies && node.ContainsKey(keyword))
        {
            Report(DiagnosticCodes.InvalidTypeDefinition, $"'{keyword}' only applies to {typeName} types.", path.Append(keyword));
        }
    }

    private void Report(string code, string message, JsonPath path)
    {
        _diagnostics.Add(Diagnostic.Error(code, message, path, _document.LocationOf(path), _document.SourcePath));
    }
}
