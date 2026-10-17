using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace EArm.Core.Types;

/// <summary>
/// ARM's <c>Microsoft.Resources/deployments</c>: a nested (inline <c>template</c>) or linked (<c>templateLink</c>) deployment
/// (https://learn.microsoft.com/azure/azure-resource-manager/templates/linked-templates). It is not an Entra object: EARM is stateless, so a
/// nested deployment is template composition. The engine expands it into the resources of its template, which are planned and applied with
/// the others; the deployment itself writes nothing to Graph. The type keeps ARM's name and apiVersions so ARM snippets paste in
/// (https://learn.microsoft.com/azure/templates/microsoft.resources/deployments).
/// </summary>
/// <remarks>
/// The catalog resolves the type (<see cref="ResourceTypeCatalog.TryGet"/>) so names, ids, <c>resourceId()</c>, <c>dependsOn</c> and
/// <c>reference()</c> treat it like any declared resource, but it is not listed in <see cref="ResourceTypeCatalog.Types"/>: it has no provider,
/// no Graph permissions and no property schema file.
/// </remarks>
public static class DeploymentResourceType
{
    /// <summary>The properties a deployment may declare that EARM reads; <c>debugSetting</c> is accepted and ignored.</summary>
    public static IReadOnlyList<string> KnownProperties { get; } =
        ["mode", "template", "templateLink", "parameters", "parametersLink", "expressionEvaluationOptions", "debugSetting"];

    /// <summary>The ARM resource type.</summary>
    public const string Type = "Microsoft.Resources/deployments";

    /// <summary>ARM: a deployment name has 1 to 64 characters.</summary>
    public const int MaxNameLength = 64;

    /// <summary>ARM: <c>^[-\w\._\(\)]+$</c>.</summary>
    public const string NamePattern = @"^[-\w\._\(\)]+$";

    /// <summary>
    /// The deepest chain of nested deployments EARM expands (a deployment inside a deployment inside ... counts one per level). ARM documents no depth
    /// limit; this guard stops runaway recursion (a template that links itself is reported as a cycle before it is reached).
    /// </summary>
    public const int MaxDepth = 10;

    /// <summary>The most nested deployments (all levels, all copy iterations) one run expands: ARM's what-if limit of 500 nested templates.</summary>
    public const int MaxDeployments = 500;

    /// <summary>The apiVersions ARM publishes for the type; any of them is accepted (the value changes nothing in EARM).</summary>
    public static IReadOnlyList<string> ApiVersions { get; } =
    [
        "2015-01-01", "2015-11-01", "2016-02-01", "2016-07-01", "2016-09-01", "2017-05-10", "2018-02-01", "2018-05-01", "2019-03-01",
        "2019-05-01", "2019-05-10", "2019-07-01", "2019-08-01", "2019-10-01", "2020-06-01", "2020-08-01", "2020-10-01", "2021-01-01",
        "2021-04-01", "2022-09-01", "2023-07-01", "2024-03-01", "2024-07-01", "2024-11-01", "2025-03-01", "2025-04-01", "2026-06-01",
    ];

    /// <summary>The catalog entry of the type (see the remarks on the class).</summary>
    public static ResourceTypeDefinition Definition { get; } = Build();

    /// <summary>True when <paramref name="type"/> is <c>Microsoft.Resources/deployments</c> (case-insensitive).</summary>
    public static bool Is(string? type)
    {
        return type is not null && ResourceIdentifier.TypeEquals(type, Type);
    }

    /// <summary>
    /// The expression evaluation scope of a deployment (<c>properties.expressionEvaluationOptions.scope</c>): a linked template is always
    /// <see cref="NestedTemplateScope.Inner"/>; an inline template defaults to <see cref="NestedTemplateScope.Outer"/>, or to inner when the parent
    /// template uses languageVersion 2.0 (where ARM blocks outer). A value that is not a literal <c>inner</c>/<c>outer</c>/<c>NotSpecified</c>
    /// yields the default (the semantic checks report it).
    /// </summary>
    public static NestedTemplateScope ScopeOf(JsonNode? properties, bool parentUsesSymbolicNames)
    {
        if (properties is not JsonObject obj)
        {
            return parentUsesSymbolicNames ? NestedTemplateScope.Inner : NestedTemplateScope.Outer;
        }

        if (obj.ContainsKey("templateLink"))
        {
            return NestedTemplateScope.Inner;
        }

        string? text = obj["expressionEvaluationOptions"] is JsonObject options ? Json.TemplateJson.LiteralString(options["scope"]) : null;
        if (string.Equals(text, "inner", StringComparison.OrdinalIgnoreCase))
        {
            return NestedTemplateScope.Inner;
        }

        if (string.Equals(text, "outer", StringComparison.OrdinalIgnoreCase) && !parentUsesSymbolicNames)
        {
            return NestedTemplateScope.Outer;
        }

        return parentUsesSymbolicNames ? NestedTemplateScope.Inner : NestedTemplateScope.Outer;
    }

    private static ResourceTypeDefinition Build()
    {
        var versions = new Dictionary<string, GraphApi>(StringComparer.OrdinalIgnoreCase);
        foreach (var version in ApiVersions)
        {
            versions[version] = GraphApi.V1;
        }

        return new ResourceTypeDefinition
        {
            Type = Type,
            Description = "A nested or linked deployment: its template's resources are deployed with the rest of the template.",
            GraphPath = string.Empty,
            ApiVersions = versions,
            NameKind = ResourceNameKind.DisplayName,
            NamePattern = NamePattern,
            NameMaxLength = MaxNameLength,
            IsTemplateLevel = true,
            Schema = new JsonObject
            {
                ["title"] = Type,
                ["description"] = "Nested or linked deployment (see docs/format.md, Nested and linked deployments).",
                ["type"] = "object",
            },
            SchemaResourceName = "(built in)",
        };
    }
}

/// <summary>Where the expressions of a nested template are evaluated (ARM <c>expressionEvaluationOptions.scope</c>).</summary>
public enum NestedTemplateScope
{
    /// <summary>The parent template's parameters, variables and functions; the nested template declares none of its own.</summary>
    Outer,

    /// <summary>An isolated template: its own parameters (passed in <c>properties.parameters</c>), variables and functions.</summary>
    Inner,
}
