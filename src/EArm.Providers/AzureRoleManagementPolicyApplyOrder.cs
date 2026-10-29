using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Validation;
using EArm.Engine.Providers;

namespace EArm.Providers;

/// <summary>
/// The Azure eligibility requests an <c>azureRoleManagementPolicies</c> declaration governs, found on the declarations before any Azure Resource Manager
/// call (for <see cref="IApplyOrderSource"/>): the <c>azureRoleEligibilityScheduleRequests</c> of the same scope and the same role. Values are matched the
/// way <see cref="DeclarationConflicts"/> matches them: the same declared resource referenced, the same literal (a scope in its canonical form), the same
/// expression text outside a copy loop; a role by its GUID or by its name, so declare the policy and the eligibility the same way (both <c>role</c>, or both
/// <c>roleDefinitionId</c>).
/// </summary>
internal static class AzureRoleManagementPolicyApplyOrder
{
    /// <summary>The declared eligibility requests <paramref name="policy"/> governs (applied after it).</summary>
    public static IReadOnlyList<ResourceInstance> GovernedRequests(ResourceInstance policy, IReadOnlyList<ResourceInstance> declared)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(declared);
        if (policy.IsIgnored || policy.Declaration.Properties is not JsonObject properties
            || ScopeSpelling(policy, properties) is not { } scope
            || RoleSpelling(policy, properties) is not { } role)
        {
            return [];
        }

        return declared
            .Where(r => !ReferenceEquals(r, policy) && !r.IsIgnored
                && ResourceIdentifier.TypeEquals(r.Type, AzureRoleEligibilityScheduleRules.ResourceType)
                && r.Declaration.Properties is JsonObject request
                && ScopeSpelling(r, request) == scope
                && RoleSpelling(r, request) == role)
            .ToList();
    }

    /// <summary>A literal scope in its canonical form (lower case), else the spelling of its reference or expression.</summary>
    private static string? ScopeSpelling(ResourceInstance instance, JsonObject properties)
    {
        if (DeclarationConflicts.Literal(properties, "scope") is { } literal)
        {
            return "lit:" + (AzureScopeRules.NormalizeScope(literal) ?? literal).ToLowerInvariant();
        }

        return DeclarationConflicts.Spelling(instance, properties, DeclarationConflicts.PropertiesPath(instance), "scope");
    }

    /// <summary>A literal role definition id by its GUID, a literal role name lower case, else the spelling of its reference or expression.</summary>
    private static string? RoleSpelling(ResourceInstance instance, JsonObject properties)
    {
        if (DeclarationConflicts.Literal(properties, "roleDefinitionId") is { } literal && AzureScopeRules.RoleDefinitionGuid(literal) is { } id)
        {
            return "id:" + id.ToString("D");
        }

        if (DeclarationConflicts.Literal(properties, "role") is { } role)
        {
            return AzureScopeRules.RoleDefinitionGuid(role) is { } roleId ? "id:" + roleId.ToString("D") : "name:" + role.ToLowerInvariant();
        }

        return DeclarationConflicts.RoleSpelling(instance, properties, DeclarationConflicts.PropertiesPath(instance));
    }
}
