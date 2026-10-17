using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Types;

namespace EArm.Core.Validation;

/// <summary>
/// The resource checks of <see cref="SemanticChecker"/> that need only the parsed document and the type catalog: required keys, type and
/// apiVersion, literal names and duplicate ids, the cross-property rules of a few Entra types, and the shape of a resource copy loop.
/// Appends to the diagnostics list it is given.
/// </summary>
internal sealed class ResourceDeclarationChecker
{
    private const string GroupsType = "Microsoft.Entra/groups";
    private const string UsersType = "Microsoft.Entra/users";
    private const string AdministrativeUnitsType = "Microsoft.Entra/administrativeUnits";
    private const string PasswordCredentialsType = "Microsoft.Entra/applications/passwordCredentials";

    private readonly ResourceTypeCatalog _catalog;
    private readonly TemplateDocument _document;
    private readonly List<Diagnostic> _diagnostics;

    public ResourceDeclarationChecker(ResourceTypeCatalog catalog, TemplateDocument document, List<Diagnostic> diagnostics)
    {
        _catalog = catalog;
        _document = document;
        _diagnostics = diagnostics;
    }

    public void Run()
    {
        var seen = new Dictionary<ResourceIdentifier, ResourceDeclaration>();

        foreach (var resource in _document.Resources)
        {
            CheckRequiredResourceKeys(resource);

            ResourceTypeDefinition? definition = null;
            if (resource.Type is not null)
            {
                definition = CheckType(resource);
            }

            if (definition is null)
            {
                CheckCopy(resource);
                continue;
            }

            if (resource.ApiVersion is not null && !definition.ApiVersions.ContainsKey(resource.ApiVersion))
            {
                Error(DiagnosticCodes.UnknownApiVersion, definition.DescribeUnknownApiVersion(resource.ApiVersion), resource.Path.Append("apiVersion"));
            }

            string? leafName = CheckName(resource, definition);
            CheckPasswordCredentialName(resource, definition, leafName);

            if (resource.StaticId is { } id)
            {
                if (seen.TryGetValue(id, out var first))
                {
                    string caseNote = string.Equals(first.StaticId?.Id, id.Id, StringComparison.Ordinal) ? string.Empty : " Names are compared case-insensitively because Graph matches its keys that way: ids that differ only by case address the same object.";
                    Error(DiagnosticCodes.DuplicateResourceId, $"Resource id '{id.Id}' is declared more than once (also at {first.Path}).{caseNote}", resource.Path.Append("name"));
                }
                else
                {
                    seen[id] = resource;
                }
            }

            if (resource.Properties is JsonObject properties)
            {
                CheckProperties(resource, definition, properties, leafName);
            }

            CheckCopy(resource);
        }
    }

    private void CheckRequiredResourceKeys(ResourceDeclaration resource)
    {
        if (resource.DeclaredType is null)
        {
            Error(DiagnosticCodes.MissingResourceType, $"The {resource.Describe()} is missing the required 'type' property.", resource.Path.Append("type"));
        }

        if (resource.ApiVersion is null)
        {
            Error(DiagnosticCodes.MissingApiVersion, $"The {resource.Describe()} is missing the required 'apiVersion' property.", resource.Path.Append("apiVersion"));
        }

        if (resource.DeclaredName is null)
        {
            Error(DiagnosticCodes.MissingResourceName, $"The {resource.Describe()} is missing the required 'name' property.", resource.Path.Append("name"));
        }
    }

    private ResourceTypeDefinition? CheckType(ResourceDeclaration resource)
    {
        var typePath = resource.Path.Append("type");
        if (_catalog.TryGet(resource.Type, out var definition))
        {
            if (resource.IsNested && definition.ParentType is not null && !ResourceIdentifier.TypeEquals(definition.ParentType, resource.NestedParentType))
            {
                Error(DiagnosticCodes.ParentChildTypeMismatch, $"Type '{definition.Type}' cannot be nested under '{resource.NestedParentType}'; its parent type is '{definition.ParentType}'.", typePath);
            }

            return definition;
        }

        string fullType = resource.Type!;
        string leaf = fullType[(fullType.LastIndexOf('/') + 1)..];
        var siblings = _catalog.FindByLeaf(leaf);
        if (resource.IsNested)
        {
            if (siblings.Count > 0)
            {
                Error(DiagnosticCodes.ParentChildTypeMismatch, $"Child type '{resource.DeclaredType}' cannot be nested under '{resource.NestedParentType}'; it belongs under '{siblings[0].ParentType ?? "(top level)"}'.", typePath);
                return null;
            }

            Error(DiagnosticCodes.UnknownResourceType, $"Nested resource type '{resource.DeclaredType}' is unknown under '{resource.NestedParentType}' (resolved to '{resource.Type}').", typePath);
            return null;
        }

        if (siblings.Count > 0 && siblings[0].IsChildType)
        {
            Error(DiagnosticCodes.ParentChildTypeMismatch, $"Resource type '{resource.Type}' is unknown; '{leaf}' is a child type of '{siblings[0].ParentType}' ('{siblings[0].Type}').", typePath);
            return null;
        }

        Error(DiagnosticCodes.UnknownResourceType, $"Resource type '{resource.Type}' is unknown; apiVersion {resource.ApiVersion ?? "(none)"}. Known types: {KnownTypesForMessage()}.", typePath);
        return null;
    }

    private string KnownTypesForMessage()
    {
        var names = new List<string>();
        foreach (var type in _catalog.Types)
        {
            names.Add(type.Type);
        }

        return string.Join(", ", names);
    }

    /// <summary>Validates a literal name's shape for the type; returns the final segment when it is literal and well-formed.</summary>
    private string? CheckName(ResourceDeclaration resource, ResourceTypeDefinition definition)
    {
        if (resource.Name is null || !resource.HasLiteralName)
        {
            return null;
        }

        var namePath = resource.Path.Append("name");
        string literal = ExpressionSyntax.LiteralValue(resource.Name);
        var segments = literal.Split('/');

        if (segments.Length != definition.Depth)
        {
            string expected = definition.Depth == 1 ? "a single segment" : $"{definition.Depth} segments ('parent/child')";
            Error(DiagnosticCodes.InvalidResourceName, $"Name '{literal}' has {segments.Length} segment(s) but type '{definition.Type}' expects {expected}.", namePath);
            return null;
        }

        foreach (var segment in segments)
        {
            if (ResourceNameRules.SegmentProblem(literal, segment) is { } segmentProblem)
            {
                Error(DiagnosticCodes.InvalidResourceName, segmentProblem, namePath);
                return null;
            }
        }

        string leaf = segments[^1];
        if (ResourceNameRules.LeafProblem(definition, leaf) is { } leafProblem)
        {
            Error(DiagnosticCodes.InvalidResourceName, leafProblem, namePath);
            return null;
        }

        return leaf;
    }

    private void CheckProperties(ResourceDeclaration resource, ResourceTypeDefinition definition, JsonObject properties, string? leafName)
    {
        var propertiesPath = resource.Path.Append("properties");

        if (definition.NameMapsTo is { } mapped && leafName is not null
            && properties.TryGetPropertyValue(mapped, out var mappedNode)
            && TemplateJson.LiteralString(mappedNode) is { } mappedLiteral
            && !string.Equals(mappedLiteral, leafName, StringComparison.Ordinal))
        {
            Error(DiagnosticCodes.NameMappedPropertyConflict, $"'properties.{mapped}' ('{mappedLiteral}') conflicts with the resource name ('{leafName}'); the name is injected into '{mapped}', so either omit the property or make it equal.", propertiesPath.Append(mapped));
        }

        if (ResourceIdentifier.TypeEquals(definition.Type, GroupsType))
        {
            if (IsDynamicGroup(properties) && properties.ContainsKey("members"))
            {
                Error(DiagnosticCodes.DynamicMembershipConflict, "'members' cannot be declared on a dynamic group ('groupTypes' contains 'DynamicMembership'); membership is computed from 'membershipRule'.", propertiesPath.Append("members"));
            }

            if (properties.ContainsKey("directoryRoles"))
            {
                var assignable = properties["isAssignableToRole"];
                if (!TemplateJson.IsExpression(assignable) && TemplateJson.AsBool(assignable) != true)
                {
                    Error(DiagnosticCodes.DirectoryRolesRequireRoleAssignableGroup, "'directoryRoles' requires 'isAssignableToRole: true'; only role-assignable groups can hold directory roles.", propertiesPath.Append("directoryRoles"));
                }
            }
        }
        else if (ResourceIdentifier.TypeEquals(definition.Type, AdministrativeUnitsType))
        {
            if (string.Equals(TemplateJson.LiteralString(properties["membershipType"]), "Dynamic", StringComparison.OrdinalIgnoreCase) && properties.ContainsKey("members"))
            {
                Error(DiagnosticCodes.DynamicMembershipConflict, "'members' cannot be declared on a dynamic administrative unit ('membershipType' is 'Dynamic').", propertiesPath.Append("members"));
            }
        }
        else if (ResourceIdentifier.TypeEquals(definition.Type, UsersType))
        {
            if (properties.ContainsKey("assignedLicenses") && !properties.ContainsKey("usageLocation"))
            {
                Error(DiagnosticCodes.AssignedLicensesRequireUsageLocation, "'assignedLicenses' requires 'usageLocation' on the user; Microsoft Graph refuses license assignment without it.", propertiesPath.Append("assignedLicenses"));
            }
        }
    }

    /// <summary>
    /// A password credential's live <c>displayName</c> is <c>&lt;name&gt;#&lt;rotationTag&gt;</c> (see <see cref="PasswordCredentialName"/>): a literal
    /// name or tag containing <c>#</c>, or a live name over the cap, is rejected here, offline. Names and tags computed by expressions are
    /// checked by the provider once they are evaluated.
    /// </summary>
    private void CheckPasswordCredentialName(ResourceDeclaration resource, ResourceTypeDefinition definition, string? leafName)
    {
        if (!ResourceIdentifier.TypeEquals(definition.Type, PasswordCredentialsType))
        {
            return;
        }

        string? tag = null;
        if (resource.Properties is JsonObject properties)
        {
            foreach (var (key, value) in properties)
            {
                if (string.Equals(key, "rotationTag", StringComparison.OrdinalIgnoreCase))
                {
                    tag = TemplateJson.LiteralString(value);
                    break;
                }
            }
        }

        if (leafName is null && tag is null)
        {
            return;
        }

        if (PasswordCredentialName.Problem(leafName ?? string.Empty, tag) is { } problem)
        {
            bool tagAtFault = tag is not null && tag.Contains(PasswordCredentialName.TagSeparator, StringComparison.Ordinal)
                && (leafName is null || !leafName.Contains(PasswordCredentialName.TagSeparator, StringComparison.Ordinal));
            var path = tagAtFault ? resource.Path.Append("properties").Append("rotationTag") : resource.Path.Append("name");
            Error(DiagnosticCodes.InvalidResourceName, $"'{definition.Type}': {problem}", path);
        }
    }

    private static bool IsDynamicGroup(JsonObject properties)
    {
        if (properties["groupTypes"] is not JsonArray groupTypes)
        {
            return false;
        }

        foreach (var item in groupTypes)
        {
            if (string.Equals(TemplateJson.LiteralString(item), "DynamicMembership", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void CheckCopy(ResourceDeclaration resource)
    {
        if (resource.Copy is not { } copy)
        {
            return;
        }

        if (copy.Name is null)
        {
            Error(DiagnosticCodes.InvalidCopy, "'copy' is missing the required 'name' property.", copy.Path.Append("name"));
        }
        else if (TemplateJson.LiteralString(copy.Name) is not { Length: > 0 })
        {
            Error(DiagnosticCodes.InvalidCopy, "'copy.name' must be a non-empty literal string.", copy.Path.Append("name"));
        }

        if (copy.Count is null)
        {
            Error(DiagnosticCodes.InvalidCopy, "'copy' is missing the required 'count' property.", copy.Path.Append("count"));
        }
        else if (!TemplateJson.IsExpression(copy.Count))
        {
            long? count = TemplateJson.AsInt64(copy.Count);
            if (count is null)
            {
                Error(DiagnosticCodes.InvalidCopy, "'copy.count' must be an integer or an expression.", copy.Path.Append("count"));
            }
            else if (count < 0)
            {
                Error(DiagnosticCodes.InvalidCopy, $"'copy.count' must not be negative (was {count.Value.ToString(CultureInfo.InvariantCulture)}).", copy.Path.Append("count"));
            }
        }

        if (copy.Mode is not null && !TemplateJson.IsExpression(copy.Mode))
        {
            string? mode = TemplateJson.LiteralString(copy.Mode);
            if (!string.Equals(mode, "serial", StringComparison.OrdinalIgnoreCase) && !string.Equals(mode, "parallel", StringComparison.OrdinalIgnoreCase))
            {
                Error(DiagnosticCodes.InvalidCopy, "'copy.mode' must be 'serial' or 'parallel'.", copy.Path.Append("mode"));
            }
        }

        if (copy.BatchSize is not null && !TemplateJson.IsExpression(copy.BatchSize))
        {
            long? batchSize = TemplateJson.AsInt64(copy.BatchSize);
            if (batchSize is null or < 1)
            {
                Error(DiagnosticCodes.InvalidCopy, "'copy.batchSize' must be a positive integer.", copy.Path.Append("batchSize"));
            }
        }
    }

    // ---- helpers ----

    private void Error(string code, string message, JsonPath path)
    {
        _diagnostics.Add(Diagnostic.Error(code, message, path, _document.LocationOf(path), _document.SourcePath));
    }
}
