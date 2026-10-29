using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EArm.Core.Json;
using static EArm.Core.Validation.PimScheduleRules;

namespace EArm.Core.Validation;

/// <summary>
/// The Azure scopes and role definition ids the Azure Resource Manager types share (<c>azureRoleManagementPolicies</c>,
/// <c>azureRoleEligibilityScheduleRequests</c>, <c>azureRoleAssignments</c>): a scope is a management group
/// (<c>/providers/Microsoft.Management/managementGroups/{id}</c>), a subscription, a resource group, or a resource under a resource group; a role definition
/// id is a GUID or any qualified form of it. Pure functions, plus the scope and role checks every such declaration runs.
/// </summary>
public static class AzureScopeRules
{
    /// <summary>The Azure Resource Manager provider path segment every PIM policy, eligibility, role assignment and role definition lives under.</summary>
    public const string AuthorizationProvider = "/providers/Microsoft.Authorization";

    /// <summary>The prefix of a management group scope.</summary>
    public const string ManagementGroupPrefix = "/providers/Microsoft.Management/managementGroups/";

    private const string GuidPattern = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";

    /// <summary>A subscription, a resource group, or a resource under a resource group (<c>/providers/{namespace}/{type}/{name}</c>, nested type / name pairs allowed).</summary>
    private static readonly Regex ScopePattern = new(
        "^/subscriptions/(?<subscription>" + GuidPattern + ")(?:/resourceGroups/(?<group>[^/]+)(?<resource>/providers/[^/]+/[^/]+/[^/]+(?:/[^/]+/[^/]+)*)?)?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    private static readonly Regex ManagementGroupPattern = new(
        "^/providers/Microsoft\\.Management/managementGroups/(?<group>[^/]+)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    /// <summary>A role definition id: tenant-, subscription- or resource-group-qualified, or a bare GUID.</summary>
    private static readonly Regex RoleDefinitionIdPattern = new(
        "^(?:(?:/subscriptions/" + GuidPattern + "(?:/resourceGroups/[^/]+)?)?/providers/Microsoft\\.Authorization/roleDefinitions/)?(?<id>" + GuidPattern + ")$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    /// <summary>What a scope declaration message lists as the accepted forms.</summary>
    public const string ScopeForms = "/providers/Microsoft.Management/managementGroups/{managementGroupId}, /subscriptions/{subscriptionId}, /subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName} or a resource id under a resource group (the subscription id is a GUID)";

    /// <summary>
    /// The canonical form of a supported scope (trailing <c>/</c> removed; <c>subscriptions</c>, <c>resourceGroups</c> and the management group prefix in ARM's
    /// casing, the subscription id lower case; the group, resource and management group segments as declared, compared case-insensitively), or <c>null</c>
    /// when it is not a management group, subscription, resource group or resource scope.
    /// </summary>
    public static string? NormalizeScope(string? scope)
    {
        string? text = scope?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (ManagementGroupPattern.Match(text) is { Success: true } group)
        {
            return ManagementGroupPrefix + group.Groups["group"].Value;
        }

        if (ScopePattern.Match(text) is not { Success: true } match)
        {
            return null;
        }

        string normalized = "/subscriptions/" + match.Groups["subscription"].Value.ToLowerInvariant();
        if (match.Groups["group"].Success)
        {
            normalized += "/resourceGroups/" + match.Groups["group"].Value + match.Groups["resource"].Value;
        }

        return normalized;
    }

    /// <summary>True for a management group scope (<c>/providers/Microsoft.Management/managementGroups/{id}</c>).</summary>
    public static bool IsManagementGroupScope(string? scope)
    {
        string? text = scope?.Trim().TrimEnd('/');
        return !string.IsNullOrEmpty(text) && ManagementGroupPattern.IsMatch(text);
    }

    /// <summary>The subscription id (lower case) of a subscription, resource group or resource scope, or <c>null</c> (also for a management group).</summary>
    public static string? SubscriptionOf(string? scope)
    {
        string? text = scope?.Trim().TrimEnd('/');
        return !string.IsNullOrEmpty(text) && ScopePattern.Match(text) is { Success: true } match ? match.Groups["subscription"].Value.ToLowerInvariant() : null;
    }

    /// <summary>The role definition GUID of a bare GUID or a role definition id, or <c>null</c>.</summary>
    public static Guid? RoleDefinitionGuid(string? roleDefinitionId)
    {
        string? text = roleDefinitionId?.Trim();
        return !string.IsNullOrEmpty(text) && RoleDefinitionIdPattern.Match(text) is { Success: true } match && Guid.TryParse(match.Groups["id"].Value, out var id) ? id : null;
    }

    /// <summary>
    /// The role definition id of <paramref name="roleDefinitionId"/> (a GUID or any qualified form) as ARM names it at <paramref name="scope"/>: under a
    /// subscription, resource group or resource the subscription-qualified id <c>/subscriptions/{subscription}/providers/Microsoft.Authorization/roleDefinitions/{guid}</c>
    /// (the form the policy assignment filter accepts; a bare GUID is refused there); at a management group the tenant-level id
    /// <c>/providers/Microsoft.Authorization/roleDefinitions/{guid}</c> (a management group has no subscription). <c>null</c> when either is not recognised.
    /// </summary>
    public static string? QualifiedRoleDefinitionId(string? scope, string? roleDefinitionId)
    {
        if (RoleDefinitionGuid(roleDefinitionId) is not { } id)
        {
            return null;
        }

        string tail = AuthorizationProvider + "/roleDefinitions/" + id.ToString("D");
        if (SubscriptionOf(scope) is { } subscription)
        {
            return "/subscriptions/" + subscription + tail;
        }

        return IsManagementGroupScope(scope) ? tail : null;
    }

    /// <summary>The object id of a principal in canonical form (a lower-case GUID), or <c>null</c> when it is not a GUID.</summary>
    public static string? NormalizePrincipalId(string? principalId)
    {
        return Guid.TryParse(principalId?.Trim(), out var id) ? id.ToString("D") : null;
    }

    /// <summary>Two scopes compared as ARM does: case-insensitively, without a trailing <c>/</c>.</summary>
    public static bool ScopeEquals(string? left, string? right)
    {
        return left is not null && right is not null
            && string.Equals(left.Trim().TrimEnd('/'), right.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The scope rule: <c>scope</c> is required and, when it is known, a management group, subscription, resource group or resource scope.
    /// </summary>
    /// <param name="properties">The declared or evaluated properties.</param>
    /// <param name="isOpaque">True for a value that cannot be judged yet.</param>
    /// <param name="findings">Where the problems are added.</param>
    /// <param name="subject">What the scope belongs to in messages ("the policy", "the eligibility").</param>
    public static void CheckScope(JsonObject properties, Func<JsonNode?, bool> isOpaque, List<PimRequestFinding> findings, string subject)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(isOpaque);
        ArgumentNullException.ThrowIfNull(findings);
        if (Property(properties, "scope") is not (string key, var node) || IsAbsent(node))
        {
            findings.Add(new(JsonPath.Root.Append("scope"), $"properties.scope is required: the Azure scope of {subject}, {ScopeForms}"));
            return;
        }

        if (isOpaque(node))
        {
            return;
        }

        if (!TryString(node, out string? scope))
        {
            findings.Add(new(JsonPath.Root.Append(key), $"properties.{key} must be a string: an Azure scope id", SchemaCovered: true));
            return;
        }

        if (NormalizeScope(scope) is null)
        {
            findings.Add(new(JsonPath.Root.Append(key), $"properties.{key} '{scope}' is not an Azure scope; use {ScopeForms}"));
        }
    }

    /// <summary>The role rule: exactly one of <c>role</c> (a non-empty name) and <c>roleDefinitionId</c> (a GUID or a role definition id).</summary>
    public static void CheckRole(JsonObject properties, Func<JsonNode?, bool> isOpaque, List<PimRequestFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(isOpaque);
        ArgumentNullException.ThrowIfNull(findings);
        bool role = RoleManagementPolicyRules.Declared(properties, "role");
        bool roleDefinitionId = RoleManagementPolicyRules.Declared(properties, "roleDefinitionId");
        if (role && roleDefinitionId)
        {
            findings.Add(new(JsonPath.Root.Append(RoleManagementPolicyRules.KeyOf(properties, "role")), "declare one of properties.role and properties.roleDefinitionId, not both"));
            return;
        }

        if (!role && !roleDefinitionId)
        {
            findings.Add(new(JsonPath.Root.Append("roleDefinitionId"), "properties.roleDefinitionId (the role definition GUID or id) or properties.role (the role's name, for example Reader) is required"));
            return;
        }

        if (role)
        {
            CheckNonEmptyString(properties, RoleManagementPolicyRules.KeyOf(properties, "role"), isOpaque, findings);
            return;
        }

        if (Judged(properties, "roleDefinitionId", isOpaque) is (string key, var value) && !(TryString(value, out string? text) && RoleDefinitionGuid(text) is not null))
        {
            string shown = TryString(value, out string? raw) ? raw : value?.ToJsonString() ?? "null";
            findings.Add(new(JsonPath.Root.Append(key), $"properties.{key} '{shown}' is not a role definition: use its GUID or its id (/subscriptions/{{subscriptionId}}/providers/Microsoft.Authorization/roleDefinitions/{{guid}}), or name the role with properties.role"));
        }
    }

    /// <summary>The principal rule: <c>principalId</c> is required and, when it is known, a GUID (the object id of a user, group or service principal).</summary>
    public static void CheckPrincipal(JsonObject properties, Func<JsonNode?, bool> isOpaque, List<PimRequestFinding> findings)
    {
        CheckRequiredGuid(properties, "principalId", "properties.principalId is required: the object id of the user, group or service principal, typically [reference(resourceId('Microsoft.Entra/groups', '...')).id]", isOpaque, findings);
    }
}
