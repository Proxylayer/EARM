using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core.Json;

namespace EArm.Core.Validation;

/// <summary>
/// The pure rules of <c>Microsoft.Entra/azureRoleManagementPolicies</c> (the PIM policy of an Azure RBAC role at an Azure scope, managed through Azure
/// Resource Manager), shared by the offline validator (literal values) and the provider (evaluated values). The declaration is ARM's own
/// <c>Microsoft.Authorization/roleManagementPolicies</c> shape: <c>properties.rules[]</c> exactly as for <c>Microsoft.Entra/roleManagementPolicies</c> (the
/// rule checks are <see cref="RoleManagementPolicyRuleChecks"/>, shared), the policy named by its <c>scope</c> and <c>roleDefinitionId</c> (or the EARM sugar
/// <c>role</c>, a role name resolved at plan time). Rules checked here: the property set, the scope (a management group, a subscription, a resource group
/// or a resource under them; <see cref="AzureScopeRules"/>), and one of <c>role</c> / <c>roleDefinitionId</c> (a GUID or a role definition id).
/// </summary>
public static class AzureRoleManagementPolicyRules
{
    public const string ResourceType = "Microsoft.Entra/azureRoleManagementPolicies";

    /// <summary>The Azure Resource Manager provider path segment every PIM policy, policy assignment and role definition lives under.</summary>
    public const string AuthorizationProvider = AzureScopeRules.AuthorizationProvider;

    /// <summary>The property names a policy declaration accepts (ARM names and EARM sugar).</summary>
    public static IReadOnlyList<string> AllowedProperties { get; } = ["scope", "roleDefinitionId", "role", "rules"];

    /// <summary>The canonical form of a supported scope (<see cref="AzureScopeRules.NormalizeScope"/>): a management group, subscription, resource group or resource.</summary>
    public static string? NormalizeScope(string? scope)
    {
        return AzureScopeRules.NormalizeScope(scope);
    }

    /// <summary>True for a management group scope (<c>/providers/Microsoft.Management/managementGroups/{id}</c>).</summary>
    public static bool IsManagementGroupScope(string? scope)
    {
        return AzureScopeRules.IsManagementGroupScope(scope);
    }

    /// <summary>The subscription id (lower case) of a subscription, resource group or resource scope, or <c>null</c>.</summary>
    public static string? SubscriptionOf(string? scope)
    {
        return AzureScopeRules.SubscriptionOf(scope);
    }

    /// <summary>The role definition GUID of a bare GUID or a role definition id, or <c>null</c>.</summary>
    public static Guid? RoleDefinitionGuid(string? roleDefinitionId)
    {
        return AzureScopeRules.RoleDefinitionGuid(roleDefinitionId);
    }

    /// <summary>
    /// The role definition id ARM names at <paramref name="scope"/> (<see cref="AzureScopeRules.QualifiedRoleDefinitionId"/>): subscription-qualified under a
    /// subscription, tenant-level (<c>/providers/Microsoft.Authorization/roleDefinitions/{guid}</c>) at a management group. <c>null</c> when either is not recognised.
    /// </summary>
    public static string? QualifiedRoleDefinitionId(string? scope, string? roleDefinitionId)
    {
        return AzureScopeRules.QualifiedRoleDefinitionId(scope, roleDefinitionId);
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
                findings.Add(new(JsonPath.Root.Append(property.Key), $"properties.{property.Key} is not a property of an Azure role management policy; expected scope, role or roleDefinitionId, and rules (ARM's read-only displayName, description, policyProperties, effectiveRules and lastModified* are not declared)"));
            }
        }

        AzureScopeRules.CheckScope(properties, isOpaque, findings, "the policy");
        AzureScopeRules.CheckRole(properties, isOpaque, findings);
        RoleManagementPolicyRuleChecks.CheckRules(properties, isOpaque, findings);
        return findings;
    }
}
