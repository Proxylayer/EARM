using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Types;

namespace EArm.Core.Loading;

/// <summary>
/// Reads <c>resources</c> into flattened <see cref="ResourceDeclaration"/>s. Language version 1 declares an array; languageVersion 2.0
/// declares an object keyed by symbolic name (https://learn.microsoft.com/azure/azure-resource-manager/templates/resource-declaration#use-symbolic-name),
/// whose entries may be <c>"existing": true</c> lookups. The flattening rules for nested children are described on <see cref="TemplateLoader"/>.
/// </summary>
internal sealed partial class ResourceReader
{
    private const int MaxNestingDepth = 2;

    private readonly ResourceTypeCatalog _catalog;
    private readonly LoadContext _context;

    public ResourceReader(ResourceTypeCatalog catalog, LoadContext context)
    {
        _catalog = catalog;
        _context = context;
    }

    /// <summary>ARM: "The allowed characters for symbolic names are letters, numbers, and _".</summary>
    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex SymbolicNamePattern();

    public List<ResourceDeclaration> Read(JsonObject root, bool symbolicNamesRequired)
    {
        var declarations = new List<ResourceDeclaration>();
        var basePath = JsonPath.Of("resources");
        switch (root["resources"])
        {
            case JsonArray resources:
                if (symbolicNamesRequired)
                {
                    _context.Error(DiagnosticCodes.InvalidLanguageVersion, "With languageVersion 2.0 'resources' must be an object keyed by symbolic name, for example \"resources\": { \"app\": { \"type\": ..., \"apiVersion\": ..., \"name\": ... } }.", basePath);
                }

                for (int i = 0; i < resources.Count; i++)
                {
                    if (resources[i] is JsonObject resource)
                    {
                        Visit(resource, basePath.Append(i), parent: null, depth: 1, symbolicName: null, declarations);
                    }
                }

                break;

            case JsonObject symbolic:
                if (!symbolicNamesRequired)
                {
                    _context.Error(DiagnosticCodes.InvalidLanguageVersion, "'resources' is an object keyed by symbolic name, which needs \"languageVersion\": \"2.0\" at the top of the template.", basePath);
                }

                foreach (var (symbolicName, value) in symbolic)
                {
                    var path = basePath.Append(symbolicName);
                    if (!SymbolicNamePattern().IsMatch(symbolicName))
                    {
                        _context.Error(DiagnosticCodes.InvalidSymbolicName, $"Symbolic name '{SafeText.Escape(symbolicName)}' may only contain letters, digits and '_'.", path);
                    }

                    if (value is JsonObject resource)
                    {
                        Visit(resource, path, parent: null, depth: 1, symbolicName, declarations);
                    }
                }

                break;
        }

        return declarations;
    }

    private void Visit(JsonObject resource, JsonPath path, ResourceDeclaration? parent, int depth, string? symbolicName, List<ResourceDeclaration> declarations)
    {
        string? declaredType = TemplateJson.AsString(resource["type"]);
        string? declaredName = TemplateJson.AsString(resource["name"]);

        string? fullType = declaredType is null ? null : ResourceIdentifier.NormalizeType(declaredType);
        string? name = declaredName;
        var inferred = new List<string>();

        if (parent is not null)
        {
            if (fullType is not null && parent.Type is not null)
            {
                if (fullType.StartsWith(ResourceIdentifier.ProviderNamespace + "/", StringComparison.OrdinalIgnoreCase))
                {
                    // Full type spelled inside a nested declaration: it must sit under the parent's type.
                    if (!fullType.StartsWith(parent.Type + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        _context.Error(
                            DiagnosticCodes.ParentChildTypeMismatch,
                            $"Nested resource type '{declaredType}' is not a child type of its parent '{parent.Type}'.",
                            path.Append("type"));
                    }
                }
                else
                {
                    fullType = parent.Type + "/" + fullType.Trim('/');
                }
            }

            name = parent.Name is not null && declaredName is not null ? ExpressionSyntax.ComposeName(parent.Name, declaredName) : null;

            if (parent.Type is not null && parent.Name is not null)
            {
                inferred.Add(ExpressionSyntax.ResourceIdExpression(parent.Type, parent.Name));
            }
        }

        if (fullType is not null)
        {
            fullType = _catalog.CanonicalType(fullType);
        }

        var dependsOn = new List<string>();
        if (resource["dependsOn"] is JsonArray dependsOnArray)
        {
            foreach (var item in dependsOnArray)
            {
                if (TemplateJson.AsString(item) is { } text)
                {
                    dependsOn.Add(text);
                }
            }
        }

        CopyDeclaration? copy = null;
        if (resource["copy"] is JsonObject copyObject)
        {
            copy = new CopyDeclaration
            {
                Name = copyObject["name"],
                Count = copyObject["count"],
                Mode = copyObject["mode"],
                BatchSize = copyObject["batchSize"],
                Source = copyObject,
                Path = path.Append("copy"),
            };
        }

        var declaration = new ResourceDeclaration
        {
            Type = fullType,
            DeclaredType = declaredType,
            ApiVersion = TemplateJson.AsString(resource["apiVersion"]),
            Name = name,
            DeclaredName = declaredName,
            SymbolicName = symbolicName,
            IsExisting = ReadExisting(resource, path, symbolicName),
            StaticId = ComputeStaticId(fullType, name),
            Properties = resource["properties"],
            DependsOn = dependsOn,
            InferredDependsOn = inferred,
            Condition = resource["condition"],
            Copy = copy,
            Comments = TemplateJson.AsString(resource["comments"]),
            Metadata = resource["metadata"] as JsonObject,
            Source = resource,
            Path = path,
            Location = _context.SourceMap.FindNearest(path),
            NestingDepth = depth,
            NestedParentType = parent?.Type,
            NestedParentPath = parent?.Path,
        };
        declarations.Add(declaration);

        if (resource["resources"] is not JsonArray children || children.Count == 0)
        {
            return;
        }

        var childrenPath = path.Append("resources");
        if (depth >= MaxNestingDepth)
        {
            _context.Error(
                DiagnosticCodes.NestingTooDeep,
                $"Resources nested more than {MaxNestingDepth} levels deep are not supported; declare '{declaration.Describe()}' children at the top level with a 'parent/child' name.",
                childrenPath);
            return;
        }

        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is JsonObject child)
            {
                Visit(child, childrenPath.Append(i), declaration, depth + 1, symbolicName: null, declarations);
            }
        }
    }

    /// <summary>
    /// <c>"existing": true</c> is a languageVersion 2.0 symbolic declaration that ARM reads rather than deploys; it needs only <c>type</c>,
    /// <c>apiVersion</c> and <c>name</c> (https://learn.microsoft.com/azure/azure-resource-manager/templates/resource-declaration#declare-existing-resources).
    /// Properties on it would be silently ignored, so they are refused.
    /// </summary>
    private bool ReadExisting(JsonObject resource, JsonPath path, string? symbolicName)
    {
        if (!resource.TryGetPropertyValue("existing", out var node))
        {
            return false;
        }

        var existingPath = path.Append("existing");
        if (TemplateJson.AsBool(node) is not { } existing)
        {
            _context.Error(DiagnosticCodes.InvalidExistingResource, "'existing' must be the literal true or false.", existingPath);
            return false;
        }

        if (!existing)
        {
            return false;
        }

        if (symbolicName is null)
        {
            _context.Error(DiagnosticCodes.InvalidExistingResource, "'existing' is only allowed on a languageVersion 2.0 resource declared under its symbolic name (\"resources\": { \"name\": { ... } }).", existingPath);
            return false;
        }

        if (resource.ContainsKey("properties"))
        {
            _context.Error(DiagnosticCodes.InvalidExistingResource, $"Existing resource '{symbolicName}' is only read, so it cannot declare 'properties'; remove them, or remove \"existing\": true to deploy it.", path.Append("properties"));
        }

        return true;
    }

    private ResourceIdentifier? ComputeStaticId(string? type, string? name)
    {
        if (type is null || name is null || ExpressionSyntax.IsExpression(name))
        {
            return null;
        }

        if (!_catalog.TryGet(type, out var definition))
        {
            return null;
        }

        var segments = ExpressionSyntax.LiteralValue(name).Split('/');
        if (segments.Length != definition.Depth)
        {
            return null;
        }

        return ResourceIdentifier.TryCreate(definition.Type, string.Join('/', segments), out var identifier) ? identifier : null;
    }
}
