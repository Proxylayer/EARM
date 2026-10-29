using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core.Json;
using static EArm.Core.Validation.PimScheduleRules;

namespace EArm.Core.Validation;

/// <summary>
/// The pure rules of <c>Microsoft.Entra/azureRoleAssignments</c> (a permanent, active Azure RBAC role assignment at an Azure scope, made through Azure
/// Resource Manager as <c>Microsoft.Authorization/roleAssignments</c>), shared by the offline validator (literal values) and the provider (evaluated values).
/// Rules checked here: the property set, the scope (<see cref="AzureScopeRules.CheckScope"/>), one of <c>role</c> / <c>roleDefinitionId</c>
/// (<see cref="AzureScopeRules.CheckRole"/>), a GUID <c>principalId</c>, a <c>principalType</c> of User, Group or ServicePrincipal, and a string
/// <c>description</c>.
/// </summary>
public static class AzureRoleAssignmentRules
{
    public const string ResourceType = "Microsoft.Entra/azureRoleAssignments";

    /// <summary>The property names an assignment declaration accepts (ARM names and EARM sugar).</summary>
    public static IReadOnlyList<string> AllowedProperties { get; } = ["scope", "roleDefinitionId", "role", "principalId", "principalType", "description"];

    /// <summary>The principal types ARM accepts on a role assignment that EARM sends (ARM's own casing).</summary>
    public static IReadOnlyList<string> PrincipalTypes { get; } = ["User", "Group", "ServicePrincipal"];

    /// <summary>ARM's casing of a principal type in any casing, or <c>null</c>.</summary>
    public static string? NormalizePrincipalType(string? principalType)
    {
        return PrincipalTypes.FirstOrDefault(t => t.Equals(principalType?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The problems of a declaration. Nothing is changed.</summary>
    /// <param name="properties">The declared (offline) or evaluated (plan time) properties.</param>
    /// <param name="isOpaque">True for a value that cannot be judged yet (a template expression, a deploy-time value, a secret).</param>
    public static IReadOnlyList<PimRequestFinding> Check(JsonObject properties, Func<JsonNode?, bool> isOpaque)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(isOpaque);
        var findings = new List<PimRequestFinding>();
        foreach (var property in properties)
        {
            if (!AllowedProperties.Contains(property.Key, StringComparer.OrdinalIgnoreCase))
            {
                findings.Add(new(JsonPath.Root.Append(property.Key), $"properties.{property.Key} is not a property of an Azure role assignment; expected scope, role or roleDefinitionId, principalId, principalType and description (ARM's condition, conditionVersion and delegatedManagedIdentityResourceId are not supported)"));
            }
        }

        AzureScopeRules.CheckScope(properties, isOpaque, findings, "the role assignment");
        AzureScopeRules.CheckRole(properties, isOpaque, findings);
        AzureScopeRules.CheckPrincipal(properties, isOpaque, findings);
        if (Judged(properties, "principalType", isOpaque) is (string typeKey, var type) && !(TryString(type, out string? text) && NormalizePrincipalType(text) is not null))
        {
            string shown = TryString(type, out string? raw) ? raw : type?.ToJsonString() ?? "null";
            findings.Add(new(JsonPath.Root.Append(typeKey), $"properties.{typeKey} '{shown}' must be User, Group or ServicePrincipal"));
        }

        if (Judged(properties, "description", isOpaque) is (string descriptionKey, var description) && !TryString(description, out _))
        {
            findings.Add(new(JsonPath.Root.Append(descriptionKey), $"properties.{descriptionKey} must be a string", SchemaCovered: true));
        }

        return findings;
    }
}
