using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Types;

namespace EArm.Core.Validation;

/// <summary>
/// The literal shape of every <c>Microsoft.Resources/deployments</c> declaration (https://learn.microsoft.com/azure/azure-resource-manager/templates/linked-templates,
/// https://learn.microsoft.com/azure/templates/microsoft.resources/deployments): <c>mode</c> Incremental only, exactly one of <c>template</c> /
/// <c>templateLink</c>, at most one of <c>parameters</c> / <c>parametersLink</c>, a literal evaluation scope and the rules of the outer scope.
/// Values computed by expressions are checked when the engine expands the deployment; the nested template itself is validated then too.
/// </summary>
internal sealed class NestedDeploymentChecker
{
    private static readonly string[] ScopeValues = ["inner", "outer", "NotSpecified"];
    private static readonly string[] CrossScopeKeys = ["subscriptionId", "resourceGroup", "scope", "managementGroup"];
    private static readonly string[] IgnoredResourceKeys = ["location", "tags", "identity"];

    private readonly TemplateDocument _document;
    private readonly List<Diagnostic> _diagnostics;

    public NestedDeploymentChecker(TemplateDocument document, List<Diagnostic> diagnostics)
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
            if (DeploymentResourceType.Is(resource.Type))
            {
                Check(resource);
            }
        }
    }

    private void Check(ResourceDeclaration resource)
    {
        foreach (var key in CrossScopeKeys)
        {
            if (resource.Source.ContainsKey(key))
            {
                Error(DiagnosticCodes.NestedDeploymentFeatureNotSupported, $"'{key}' targets another Azure scope; an EARM nested deployment always deploys to the tenant of the parent deployment. Remove it.", resource.Path.Append(key));
            }
        }

        foreach (var key in IgnoredResourceKeys)
        {
            if (resource.Source.ContainsKey(key))
            {
                Warning(DiagnosticCodes.NestedDeploymentPropertyIgnored, $"'{key}' of a nested deployment is ignored: EARM keeps no deployment record.", resource.Path.Append(key));
            }
        }

        var path = resource.Path.Append("properties");
        if (resource.Source["properties"] is not JsonObject properties)
        {
            Error(DiagnosticCodes.InvalidNestedDeployment, $"{resource.Describe()} needs a 'properties' object with 'mode' and a 'template' or 'templateLink'.", path);
            return;
        }

        CheckMode(properties, path);
        CheckUnknownKeys(properties, path);
        bool linked = CheckTemplateSource(properties, path);
        CheckParameters(properties, path);
        var scope = CheckScope(properties, path, linked);
        if (scope == NestedTemplateScope.Outer && !linked && properties["template"] is JsonObject template)
        {
            CheckOuterScope(properties, template, path);
        }
    }

    private void CheckMode(JsonObject properties, JsonPath path)
    {
        var modePath = path.Append("mode");
        if (!properties.TryGetPropertyValue("mode", out var mode))
        {
            Error(DiagnosticCodes.InvalidNestedDeployment, "A nested deployment needs 'mode': \"Incremental\" (ARM requires it).", modePath);
            return;
        }

        if (TemplateJson.IsExpression(mode))
        {
            return;
        }

        string? text = TemplateJson.AsString(mode);
        if (string.Equals(text, "Incremental", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string message = string.Equals(text, "Complete", StringComparison.OrdinalIgnoreCase)
            ? "A nested or linked deployment can only use 'mode': \"Incremental\" (ARM rule). Deploy the parent with --mode Complete instead: the resources of the nested templates are part of the parent's Complete-mode comparison."
            : "'mode' must be \"Incremental\".";
        Error(DiagnosticCodes.InvalidNestedDeployment, message, modePath);
    }

    private void CheckUnknownKeys(JsonObject properties, JsonPath path)
    {
        foreach (var (key, _) in properties)
        {
            if (!DeploymentResourceType.KnownProperties.Contains(key, StringComparer.Ordinal))
            {
                Warning(DiagnosticCodes.NestedDeploymentPropertyIgnored, $"'properties.{key}' of a nested deployment is not supported by EARM and is ignored.", path.Append(key));
            }
        }
    }

    /// <summary>Checks <c>template</c> / <c>templateLink</c>; returns true for a linked template.</summary>
    private bool CheckTemplateSource(JsonObject properties, JsonPath path)
    {
        bool hasTemplate = properties.TryGetPropertyValue("template", out var template);
        bool hasLink = properties.TryGetPropertyValue("templateLink", out var link);
        if (hasTemplate && hasLink)
        {
            Error(DiagnosticCodes.InvalidNestedDeployment, "Use either 'template' or 'templateLink', not both.", path.Append("templateLink"));
            return true;
        }

        if (!hasTemplate && !hasLink)
        {
            Error(DiagnosticCodes.InvalidNestedDeployment, "A nested deployment needs an inline 'template' or a 'templateLink'.", path);
            return false;
        }

        if (hasTemplate)
        {
            if (template is not JsonObject)
            {
                Error(DiagnosticCodes.NestedDeploymentFeatureNotSupported, "'template' must be an inline template object; a template given as a JSON string or built by an expression is not supported.", path.Append("template"));
            }

            return false;
        }

        var linkPath = path.Append("templateLink");
        if (link is not JsonObject linkObject)
        {
            Error(DiagnosticCodes.InvalidNestedDeployment, "'templateLink' must be an object with 'relativePath' or 'uri'.", linkPath);
            return true;
        }

        if (linkObject.ContainsKey("id"))
        {
            Error(DiagnosticCodes.NestedDeploymentFeatureNotSupported, "'templateLink.id' names a template spec, an Azure resource EARM cannot read; use 'relativePath' (a local file next to the template) or an https 'uri'.", linkPath.Append("id"));
            return true;
        }

        bool hasUri = linkObject.ContainsKey("uri");
        bool hasRelative = linkObject.ContainsKey("relativePath");
        if (hasUri == hasRelative)
        {
            Error(DiagnosticCodes.InvalidNestedDeployment, "'templateLink' needs exactly one of 'relativePath' and 'uri'.", linkPath);
        }

        CheckLinkText(linkObject, "uri", linkPath);
        CheckLinkText(linkObject, "relativePath", linkPath);
        CheckLinkText(linkObject, "contentVersion", linkPath);
        CheckLinkText(linkObject, "queryString", linkPath);
        foreach (var (key, _) in linkObject)
        {
            if (key is not ("uri" or "relativePath" or "contentVersion" or "queryString"))
            {
                Warning(DiagnosticCodes.NestedDeploymentPropertyIgnored, $"'templateLink.{key}' is not part of ARM's templateLink and is ignored.", linkPath.Append(key));
            }
        }

        return true;
    }

    private void CheckLinkText(JsonObject link, string key, JsonPath linkPath)
    {
        if (link.TryGetPropertyValue(key, out var value) && TemplateJson.AsString(value) is null)
        {
            Error(DiagnosticCodes.InvalidNestedDeployment, $"'{key}' must be a string.", linkPath.Append(key));
        }
    }

    private void CheckParameters(JsonObject properties, JsonPath path)
    {
        bool hasParameters = properties.TryGetPropertyValue("parameters", out var parameters);
        bool hasLink = properties.TryGetPropertyValue("parametersLink", out var link);
        if (hasParameters && hasLink)
        {
            Error(DiagnosticCodes.InvalidNestedDeployment, "Use either 'parameters' or 'parametersLink', not both (ARM fails a deployment that specifies both).", path.Append("parametersLink"));
        }

        if (hasParameters)
        {
            var parametersPath = path.Append("parameters");
            if (parameters is not JsonObject map)
            {
                Error(DiagnosticCodes.InvalidNestedDeployment, "'parameters' must be an object: { \"name\": { \"value\": ... } }.", parametersPath);
            }
            else
            {
                foreach (var (name, entry) in map)
                {
                    var entryPath = parametersPath.Append(name);
                    if (entry is not JsonObject entryObject)
                    {
                        Error(DiagnosticCodes.InvalidNestedDeployment, $"Parameter '{name}' must be an object with a 'value'.", entryPath);
                    }
                    else if (entryObject.ContainsKey("reference"))
                    {
                        CheckKeyVaultReference(name, entryObject, entryPath);
                    }
                    else if (entryObject.ContainsKey("expression"))
                    {
                        Error(DiagnosticCodes.NestedDeploymentFeatureNotSupported, $"Parameter '{name}': 'expression' is not supported; pass the value as \"value\": \"[...]\".", entryPath.Append("expression"));
                    }
                    else if (!entryObject.ContainsKey("value"))
                    {
                        Error(DiagnosticCodes.InvalidNestedDeployment, $"Parameter '{name}' needs a 'value'.", entryPath);
                    }
                }
            }
        }

        if (hasLink)
        {
            var linkPath = path.Append("parametersLink");
            if (link is not JsonObject linkObject || TemplateJson.AsString(linkObject["uri"]) is null && !TemplateJson.IsExpression(linkObject["uri"]))
            {
                Error(DiagnosticCodes.InvalidNestedDeployment, "'parametersLink' must be an object with a 'uri' (an https URL, or a path relative to this template).", linkPath);
            }
        }
    }

    /// <summary>
    /// A Key Vault reference entry, <c>{ "reference": { "keyVault": { "id" }, "secretName"[, "secretVersion"] } }</c>: ARM allows it in the parameters
    /// of a nested deployment, with expressions of the parent for the coordinates (a dynamic id,
    /// https://learn.microsoft.com/azure/azure-resource-manager/templates/key-vault-parameter#reference-secrets-with-dynamic-id).
    /// </summary>
    private void CheckKeyVaultReference(string name, JsonObject entry, JsonPath entryPath)
    {
        var referencePath = entryPath.Append("reference");
        if (entry.ContainsKey("value"))
        {
            Error(DiagnosticCodes.InvalidNestedDeployment, $"Parameter '{name}' has both 'value' and 'reference'; use one.", entryPath);
        }

        if (entry["reference"] is not JsonObject reference)
        {
            Error(DiagnosticCodes.InvalidNestedDeployment, $"Parameter '{name}': 'reference' must be an object: {{ \"keyVault\": {{ \"id\": \"...\" }}, \"secretName\": \"...\" }}.", referencePath);
            return;
        }

        if (reference["keyVault"] is not JsonObject keyVault || !IsText(keyVault["id"]))
        {
            Error(DiagnosticCodes.InvalidNestedDeployment, $"Parameter '{name}': a Key Vault reference needs 'keyVault': {{ \"id\": \"<vault resource id>\" }}.", referencePath.Append("keyVault"));
        }

        if (!IsText(reference["secretName"]))
        {
            Error(DiagnosticCodes.InvalidNestedDeployment, $"Parameter '{name}': a Key Vault reference needs a 'secretName' string.", referencePath.Append("secretName"));
        }

        if (reference.TryGetPropertyValue("secretVersion", out var version) && version is not null && !IsText(version))
        {
            Error(DiagnosticCodes.InvalidNestedDeployment, $"Parameter '{name}': 'secretVersion' must be a string.", referencePath.Append("secretVersion"));
        }
    }

    private static bool IsText(JsonNode? node)
    {
        return TemplateJson.AsString(node) is not null;
    }

    private NestedTemplateScope CheckScope(JsonObject properties, JsonPath path, bool linked)
    {
        var scope = DeploymentResourceType.ScopeOf(properties, _document.UsesSymbolicNames);
        if (!properties.TryGetPropertyValue("expressionEvaluationOptions", out var options))
        {
            return scope;
        }

        var optionsPath = path.Append("expressionEvaluationOptions");
        if (options is not JsonObject optionsObject)
        {
            Error(DiagnosticCodes.InvalidNestedDeployment, "'expressionEvaluationOptions' must be an object: { \"scope\": \"inner\" }.", optionsPath);
            return scope;
        }

        var scopePath = optionsPath.Append("scope");
        if (!optionsObject.TryGetPropertyValue("scope", out var value))
        {
            return scope;
        }

        string? text = TemplateJson.IsExpression(value) ? null : TemplateJson.AsString(value);
        if (text is null || !ScopeValues.Contains(text, StringComparer.OrdinalIgnoreCase))
        {
            Error(DiagnosticCodes.InvalidNestedDeployment, "'expressionEvaluationOptions.scope' must be the literal \"inner\" or \"outer\".", scopePath);
            return scope;
        }

        bool outer = string.Equals(text, "outer", StringComparison.OrdinalIgnoreCase);
        if (outer && _document.UsesSymbolicNames)
        {
            Error(DiagnosticCodes.InvalidNestedDeployment, "With languageVersion 2.0 a nested template is always evaluated in its own scope: ARM blocks 'outer'. Use \"inner\" and pass values as parameters.", scopePath);
        }
        else if (outer && linked)
        {
            Warning(DiagnosticCodes.NestedDeploymentPropertyIgnored, "A linked template is always evaluated in its own (inner) scope; 'outer' is ignored.", scopePath);
        }

        return scope;
    }

    private void CheckOuterScope(JsonObject properties, JsonObject template, JsonPath path)
    {
        foreach (var key in new[] { "parameters", "parametersLink" })
        {
            if (properties.ContainsKey(key))
            {
                Error(DiagnosticCodes.InvalidNestedDeployment, $"'{key}' passes values to a nested template evaluated in its own scope; this template uses the outer scope (the default), where it sees the parent's parameters. Set \"expressionEvaluationOptions\": {{ \"scope\": \"inner\" }} to pass parameters.", path.Append(key));
            }
        }

        var templatePath = path.Append("template");
        foreach (var key in new[] { "parameters", "variables", "functions" })
        {
            if (template[key] is JsonObject { Count: > 0 } or JsonArray { Count: > 0 })
            {
                Warning(DiagnosticCodes.NestedDeploymentPropertyIgnored, $"The nested template declares '{key}', but it is evaluated in the outer scope (the default): the parent's {key} are used and these are ignored. Set \"expressionEvaluationOptions\": {{ \"scope\": \"inner\" }} to use them.", templatePath.Append(key));
            }
        }

        // ARM: a copy loop inside an outer-scope nested template is an error (put the copy on the deployment, or use the inner scope).
        foreach (var (resource, resourcePath) in Resources(template, templatePath))
        {
            if (resource.ContainsKey("copy"))
            {
                Error(DiagnosticCodes.InvalidNestedDeployment, "A copy loop inside a nested template needs the inner scope (ARM rule); put the copy on the deployment, or set \"expressionEvaluationOptions\": { \"scope\": \"inner\" }.", resourcePath.Append("copy"));
            }
        }
    }

    private static IEnumerable<(JsonObject Resource, JsonPath Path)> Resources(JsonObject template, JsonPath templatePath)
    {
        var resourcesPath = templatePath.Append("resources");
        switch (template["resources"])
        {
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonObject resource)
                    {
                        yield return (resource, resourcesPath.Append(i));
                    }
                }

                break;
            case JsonObject symbolic:
                foreach (var (name, value) in symbolic)
                {
                    if (value is JsonObject resource)
                    {
                        yield return (resource, resourcesPath.Append(name));
                    }
                }

                break;
        }
    }

    private void Error(string code, string message, JsonPath path)
    {
        _diagnostics.Add(Diagnostic.Error(code, message, path, _document.LocationOf(path), _document.SourcePath));
    }

    private void Warning(string code, string message, JsonPath path)
    {
        _diagnostics.Add(Diagnostic.Warning(code, message, path, _document.LocationOf(path), _document.SourcePath));
    }
}
