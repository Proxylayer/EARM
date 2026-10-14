using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EntraArm.Core.Diagnostics;
using EntraArm.Core.Json;
using EntraArm.Core.Types;

namespace EntraArm.Core.Validation;

/// <summary>
/// The semantic checks that need only the parsed document and the type catalog (no expression evaluation).
/// Appends to the diagnostics list it is given.
/// </summary>
internal sealed partial class SemanticChecker
{
    private const string GroupsType = "Microsoft.Entra/groups";
    private const string UsersType = "Microsoft.Entra/users";
    private const string AdministrativeUnitsType = "Microsoft.Entra/administrativeUnits";

    private static readonly string[] KnownTopLevelKeys = ["$schema", "contentVersion", "metadata", "parameters", "variables", "functions", "resources", "outputs"];

    private readonly ResourceTypeCatalog _catalog;
    private readonly TemplateDocument _document;
    private readonly List<Diagnostic> _diagnostics;

    public SemanticChecker(ResourceTypeCatalog catalog, TemplateDocument document, List<Diagnostic> diagnostics)
    {
        _catalog = catalog;
        _document = document;
        _diagnostics = diagnostics;
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+\.\d+$")]
    private static partial Regex ContentVersionPattern();

    public void Run()
    {
        CheckEnvelope();
        CheckParameters();
        CheckParameterValues();
        CheckFunctions();
        CheckResources();
        CheckOutputs();
    }

    // ---- envelope ----

    private void CheckEnvelope()
    {
        var root = _document.Root;

        if (_document.Schema is null)
        {
            Error(DiagnosticCodes.UnrecognizedSchema, $"The template is missing '$schema'; expected '{ResourceTypeCatalog.TemplateSchemaUri}#'.", JsonPath.Of("$schema"));
        }
        else if (!ResourceTypeCatalog.IsTemplateSchemaRecognized(_document.Schema))
        {
            Error(DiagnosticCodes.UnrecognizedSchema, $"'$schema' value '{_document.Schema}' is not recognised; expected '{ResourceTypeCatalog.TemplateSchemaUri}#'.", JsonPath.Of("$schema"));
        }

        if (!root.ContainsKey("contentVersion"))
        {
            Error(DiagnosticCodes.MissingRequiredProperty, "The template is missing the required 'contentVersion' property.", JsonPath.Of("contentVersion"));
        }
        else if (_document.ContentVersion is null || !ContentVersionPattern().IsMatch(_document.ContentVersion))
        {
            Error(DiagnosticCodes.InvalidContentVersion, "'contentVersion' must be a literal of the form 'major.minor.build.revision', for example '1.0.0.0'.", JsonPath.Of("contentVersion"));
        }

        if (!root.ContainsKey("resources"))
        {
            Error(DiagnosticCodes.MissingRequiredProperty, "The template is missing the required 'resources' array.", JsonPath.Of("resources"));
        }

        foreach (var key in _document.TopLevelKeys)
        {
            if (Array.IndexOf(KnownTopLevelKeys, key) < 0)
            {
                Warning(DiagnosticCodes.UnknownTopLevelProperty, $"Top-level property '{key}' is not part of the template format and will be ignored.", JsonPath.Of(key));
            }
        }
    }

    // ---- parameters ----

    private void CheckParameters()
    {
        foreach (var definition in _document.Parameters.Values)
        {
            var typePath = definition.Path.Append("type");
            if (definition.Type is null)
            {
                Error(DiagnosticCodes.InvalidParameterType, $"Parameter '{definition.Name}' is missing the required 'type' property.", typePath);
                continue;
            }

            if (definition.ParsedType is not { } type)
            {
                Error(DiagnosticCodes.InvalidParameterType, $"Parameter '{definition.Name}' has unknown type '{definition.Type}'; expected one of: {string.Join(", ", TemplateParameterTypes.CanonicalNames)}.", typePath);
                continue;
            }

            if (definition.AllowedValues is { } allowed)
            {
                for (int i = 0; i < allowed.Count; i++)
                {
                    if (!TemplateParameterTypes.Matches(allowed[i], type))
                    {
                        Error(DiagnosticCodes.AllowedValueTypeMismatch, $"Parameter '{definition.Name}': allowed value at index {i} does not match the declared type '{definition.Type}'.", definition.Path.Append("allowedValues").Append(i));
                    }
                }
            }

            CheckRange(definition, type, "minValue", "maxValue", definition.MinValue, definition.MaxValue, TemplateParameterType.Int);
            CheckRange(definition, type, "minLength", "maxLength", definition.MinLength, definition.MaxLength, TemplateParameterType.String, TemplateParameterType.SecureString, TemplateParameterType.Array);

            if (!definition.HasDefaultValue)
            {
                continue;
            }

            var defaultPath = definition.Path.Append("defaultValue");
            if (definition.IsSecure)
            {
                Warning(DiagnosticCodes.SecureParameterHasDefault, $"Parameter '{definition.Name}' is secure but has a default value; secrets should not be stored in the template.", defaultPath);
            }

            if (!TemplateParameterTypes.Matches(definition.DefaultValue, type))
            {
                Error(DiagnosticCodes.DefaultValueTypeMismatch, $"Parameter '{definition.Name}': the default value does not match the declared type '{definition.Type}'.", defaultPath);
            }
            else if (definition.AllowedValues is { } allowedForDefault && !TemplateJson.IsExpression(definition.DefaultValue) && !IsAllowed(definition.DefaultValue, allowedForDefault))
            {
                Error(DiagnosticCodes.DefaultValueNotAllowed, $"Parameter '{definition.Name}': the default value is not one of the allowed values.", defaultPath);
            }
        }
    }

    private void CheckRange(ParameterDefinition definition, TemplateParameterType type, string minKey, string maxKey, JsonNode? min, JsonNode? max, params TemplateParameterType[] applicableTypes)
    {
        if (min is null && max is null)
        {
            return;
        }

        if (Array.IndexOf(applicableTypes, type) < 0)
        {
            string names = string.Join("/", Array.ConvertAll(applicableTypes, TemplateParameterTypes.CanonicalName));
            Error(DiagnosticCodes.InvalidParameterConstraint, $"Parameter '{definition.Name}': '{minKey}'/'{maxKey}' only apply to {names} parameters, not '{definition.Type}'.", definition.Path.Append(min is not null ? minKey : maxKey));
            return;
        }

        long? minValue = LiteralInt(definition, minKey, min);
        long? maxValue = LiteralInt(definition, maxKey, max);
        if (minKey == "minLength")
        {
            if (minValue is < 0)
            {
                Error(DiagnosticCodes.InvalidParameterConstraint, $"Parameter '{definition.Name}': '{minKey}' must not be negative.", definition.Path.Append(minKey));
            }

            if (maxValue is < 0)
            {
                Error(DiagnosticCodes.InvalidParameterConstraint, $"Parameter '{definition.Name}': '{maxKey}' must not be negative.", definition.Path.Append(maxKey));
            }
        }

        if (minValue is { } lo && maxValue is { } hi && lo > hi)
        {
            Error(DiagnosticCodes.InvalidParameterConstraint, $"Parameter '{definition.Name}': '{minKey}' ({lo}) is greater than '{maxKey}' ({hi}).", definition.Path.Append(minKey));
        }
    }

    private long? LiteralInt(ParameterDefinition definition, string key, JsonNode? node)
    {
        if (node is null || TemplateJson.IsExpression(node))
        {
            return null;
        }

        long? value = TemplateJson.AsInt64(node);
        if (value is null)
        {
            Error(DiagnosticCodes.InvalidParameterConstraint, $"Parameter '{definition.Name}': '{key}' must be an integer.", definition.Path.Append(key));
        }

        return value;
    }

    private void CheckParameterValues()
    {
        string? parameterSource = _document.SuppliedParameters.SourcePath;
        foreach (var bound in _document.ParameterValues.Values)
        {
            var definition = bound.Definition;
            if (bound.IsMissing)
            {
                // A warning offline: deployment (WI-05) turns a missing value into an error.
                Warning(DiagnosticCodes.MissingParameterValue, $"Parameter '{definition.Name}' has no value and no default value; a value is required to deploy.", definition.Path);
                continue;
            }

            if (bound.Source == ParameterValueSource.Default || bound.PendingReference is not null || definition.ParsedType is not { } type)
            {
                continue;
            }

            if (bound.ConversionFailed)
            {
                // The loader already reported ParameterValueTypeMismatch for this inline value (with its location).
                continue;
            }

            JsonPath? valuePath = bound.Source == ParameterValueSource.File ? JsonPath.Of("parameters", definition.Name, "value") : null;
            string? valueSource = bound.Source == ParameterValueSource.File ? parameterSource : null;

            if (!TemplateParameterTypes.Matches(bound.Value, type))
            {
                _diagnostics.Add(Diagnostic.Error(DiagnosticCodes.ParameterValueTypeMismatch, $"The value supplied for parameter '{definition.Name}' does not match the declared type '{definition.Type}'.", valuePath, null, valueSource));
            }
            else if (definition.AllowedValues is { } allowed && !definition.IsSecure && !TemplateJson.IsExpression(bound.Value) && !IsAllowed(bound.Value, allowed))
            {
                _diagnostics.Add(Diagnostic.Error(DiagnosticCodes.ParameterValueNotAllowed, $"The value supplied for parameter '{definition.Name}' is not one of its allowed values.", valuePath, null, valueSource));
            }
        }
    }

    private static bool IsAllowed(JsonNode? value, JsonArray allowed)
    {
        foreach (var candidate in allowed)
        {
            if (JsonNode.DeepEquals(candidate, value))
            {
                return true;
            }
        }

        return false;
    }

    // ---- functions ----

    private void CheckFunctions()
    {
        foreach (var ns in _document.Functions)
        {
            if (ns.Namespace is null)
            {
                Error(DiagnosticCodes.MissingRequiredProperty, "A function namespace entry is missing the required 'namespace' property.", ns.Path.Append("namespace"));
            }

            if (ns.Source["members"] is not JsonObject)
            {
                Error(DiagnosticCodes.MissingRequiredProperty, $"Function namespace '{ns.Namespace}' is missing the required 'members' object.", ns.Path.Append("members"));
            }

            foreach (var member in ns.Members.Values)
            {
                if (member.Source["output"] is not JsonObject)
                {
                    Error(DiagnosticCodes.MissingRequiredProperty, $"Function '{ns.Namespace}.{member.Name}' is missing the required 'output' object.", member.Path.Append("output"));
                    continue;
                }

                if (member.OutputType is null)
                {
                    Error(DiagnosticCodes.MissingRequiredProperty, $"Function '{ns.Namespace}.{member.Name}' output is missing the required 'type' property.", member.Path.Append("output").Append("type"));
                }
                else if (!TemplateParameterTypes.TryParse(member.OutputType, out _))
                {
                    Error(DiagnosticCodes.InvalidOutputType, $"Function '{ns.Namespace}.{member.Name}' output has unknown type '{member.OutputType}'.", member.Path.Append("output").Append("type"));
                }

                if (!((JsonObject)member.Source["output"]!).ContainsKey("value"))
                {
                    Error(DiagnosticCodes.MissingRequiredProperty, $"Function '{ns.Namespace}.{member.Name}' output is missing the required 'value' property.", member.Path.Append("output").Append("value"));
                }

                for (int i = 0; i < member.Parameters.Count; i++)
                {
                    var parameter = member.Parameters[i];
                    if (parameter.Name is null)
                    {
                        Error(DiagnosticCodes.MissingRequiredProperty, $"Function '{ns.Namespace}.{member.Name}' parameter {i} is missing 'name'.", member.Path.Append("parameters").Append(i));
                    }

                    if (parameter.Type is null)
                    {
                        Error(DiagnosticCodes.MissingRequiredProperty, $"Function '{ns.Namespace}.{member.Name}' parameter '{parameter.Name}' is missing 'type'.", member.Path.Append("parameters").Append(i));
                    }
                    else if (!TemplateParameterTypes.TryParse(parameter.Type, out _))
                    {
                        Error(DiagnosticCodes.InvalidParameterType, $"Function '{ns.Namespace}.{member.Name}' parameter '{parameter.Name}' has unknown type '{parameter.Type}'.", member.Path.Append("parameters").Append(i).Append("type"));
                    }
                }
            }
        }
    }

    // ---- resources ----

    private void CheckResources()
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

            if (resource.StaticId is { } id)
            {
                if (seen.TryGetValue(id, out var first))
                {
                    Error(DiagnosticCodes.DuplicateResourceId, $"Resource id '{id.Id}' is declared more than once (also at {first.Path}).", resource.Path.Append("name"));
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
            if (string.IsNullOrWhiteSpace(segment))
            {
                Error(DiagnosticCodes.InvalidResourceName, $"Name '{literal}' contains an empty segment.", namePath);
                return null;
            }
        }

        string leaf = segments[^1];
        switch (definition.NameKind)
        {
            case ResourceNameKind.Guid:
                if (!Guid.TryParse(leaf, out _))
                {
                    Error(DiagnosticCodes.InvalidResourceName, $"Name '{leaf}' must be a GUID for type '{definition.Type}' (use the guid() function for a deterministic name).", namePath);
                    return null;
                }

                break;

            case ResourceNameKind.AppId:
                if (!Guid.TryParse(leaf, out _))
                {
                    Error(DiagnosticCodes.InvalidResourceName, $"Name '{leaf}' must be the application (client) id GUID for type '{definition.Type}'.", namePath);
                    return null;
                }

                break;

            case ResourceNameKind.UserPrincipalName:
                if (!leaf.Contains('@'))
                {
                    Error(DiagnosticCodes.InvalidResourceName, $"Name '{leaf}' must be a user principal name (user@domain) for type '{definition.Type}'.", namePath);
                    return null;
                }

                break;
        }

        if (definition.NamePattern is { } pattern && !Regex.IsMatch(leaf, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            Error(DiagnosticCodes.InvalidResourceName, $"Name '{leaf}' is not valid for type '{definition.Type}'; it must match '{pattern}'.", namePath);
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

    // ---- outputs ----

    private void CheckOutputs()
    {
        foreach (var output in _document.Outputs.Values)
        {
            if (output.Type is null)
            {
                Error(DiagnosticCodes.MissingRequiredProperty, $"Output '{output.Name}' is missing the required 'type' property.", output.Path.Append("type"));
            }
            else if (output.ParsedType is null)
            {
                Error(DiagnosticCodes.InvalidOutputType, $"Output '{output.Name}' has unknown type '{output.Type}'; expected one of: {string.Join(", ", TemplateParameterTypes.CanonicalNames)}.", output.Path.Append("type"));
            }

            if (!output.Source.ContainsKey("value"))
            {
                Error(DiagnosticCodes.MissingRequiredProperty, $"Output '{output.Name}' is missing the required 'value' property.", output.Path.Append("value"));
            }
        }
    }

    // ---- helpers ----

    private void Error(string code, string message, JsonPath path)
    {
        _diagnostics.Add(Diagnostic.Error(code, message, path, _document.LocationOf(path), _document.SourcePath));
    }

    private void Warning(string code, string message, JsonPath path)
    {
        _diagnostics.Add(Diagnostic.Warning(code, message, path, _document.LocationOf(path), _document.SourcePath));
    }
}
