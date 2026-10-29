using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EArm.Core.Json;
using static EArm.Core.Validation.PimScheduleRules;

namespace EArm.Core.Validation;

/// <summary>
/// The pure rules of <c>Microsoft.Entra/azureRoleManagementPolicies</c> (the PIM policy of an Azure RBAC role at an Azure scope, managed through Azure
/// Resource Manager), shared by the offline validator (literal values) and the provider (evaluated values). The declaration is ARM's own
/// <c>Microsoft.Authorization/roleManagementPolicies</c> shape: <c>properties.rules[]</c> exactly as for <c>Microsoft.Entra/roleManagementPolicies</c> (the
/// rule checks are <see cref="RoleManagementPolicyRuleChecks"/>, shared), the policy named by its <c>scope</c> and <c>roleDefinitionId</c> (or the EARM sugar
/// <c>role</c>, a role name resolved at plan time). Rules checked here: the property set, the scope (a subscription, a resource group or a resource under
/// them; management group scopes are refused until they are verified), and one of <c>role</c> / <c>roleDefinitionId</c> (a GUID or a role definition id).
/// </summary>
public static class AzureRoleManagementPolicyRules
{
    public const string ResourceType = "Microsoft.Entra/azureRoleManagementPolicies";

    /// <summary>The Azure Resource Manager provider path segment every PIM policy, policy assignment and role definition lives under.</summary>
    public const string AuthorizationProvider = "/providers/Microsoft.Authorization";

    /// <summary>The property names a policy declaration accepts (ARM names and EARM sugar).</summary>
    public static IReadOnlyList<string> AllowedProperties { get; } = ["scope", "roleDefinitionId", "role", "rules"];

    private const string GuidPattern = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";

    /// <summary>A subscription, a resource group, or a resource under a resource group (<c>/providers/{namespace}/{type}/{name}</c>, nested type / name pairs allowed).</summary>
    private static readonly Regex ScopePattern = new(
        "^/subscriptions/(?<subscription>" + GuidPattern + ")(?:/resourceGroups/(?<group>[^/]+)(?<resource>/providers/[^/]+/[^/]+/[^/]+(?:/[^/]+/[^/]+)*)?)?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    private static readonly Regex ManagementGroupPattern = new(
        "^/providers/Microsoft\\.Management/managementGroups/[^/]+$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    /// <summary>A role definition id: tenant-, subscription- or resource-group-qualified, or a bare GUID.</summary>
    private static readonly Regex RoleDefinitionIdPattern = new(
        "^(?:(?:/subscriptions/" + GuidPattern + "(?:/resourceGroups/[^/]+)?)?/providers/Microsoft\\.Authorization/roleDefinitions/)?(?<id>" + GuidPattern + ")$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// The canonical form of a supported scope (trailing <c>/</c> removed, <c>subscriptions</c> / <c>resourceGroups</c> in ARM's casing, the subscription id lower
    /// case; the group and resource segments as declared, compared case-insensitively), or <c>null</c> when it is not a subscription, resource group or
    /// resource scope.
    /// </summary>
    public static string? NormalizeScope(string? scope)
    {
        string? text = scope?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(text) || ScopePattern.Match(text) is not { Success: true } match)
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

    /// <summary>True for a management group scope (<c>/providers/Microsoft.Management/managementGroups/{id}</c>), which is refused until it is verified.</summary>
    public static bool IsManagementGroupScope(string? scope)
    {
        string? text = scope?.Trim().TrimEnd('/');
        return !string.IsNullOrEmpty(text) && ManagementGroupPattern.IsMatch(text);
    }

    /// <summary>The subscription id (lower case) of a scope <see cref="NormalizeScope"/> accepts, or <c>null</c>.</summary>
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
    /// The subscription-qualified role definition id of <paramref name="roleDefinitionId"/> (a GUID or any qualified form) for the subscription of
    /// <paramref name="scope"/>: <c>/subscriptions/{subscription}/providers/Microsoft.Authorization/roleDefinitions/{guid}</c>, the form the policy assignment
    /// filter accepts (a bare GUID is refused there). <c>null</c> when either is not recognised.
    /// </summary>
    public static string? QualifiedRoleDefinitionId(string? scope, string? roleDefinitionId)
    {
        return SubscriptionOf(scope) is { } subscription && RoleDefinitionGuid(roleDefinitionId) is { } id
            ? "/subscriptions/" + subscription + AuthorizationProvider + "/roleDefinitions/" + id.ToString("D")
            : null;
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

        CheckScope(properties, isOpaque, findings);
        CheckRole(properties, isOpaque, findings);
        RoleManagementPolicyRuleChecks.CheckRules(properties, isOpaque, findings);
        return findings;
    }

    private static void CheckScope(JsonObject properties, Func<JsonNode?, bool> isOpaque, List<PimRequestFinding> findings)
    {
        if (Property(properties, "scope") is not (string key, var node) || IsAbsent(node))
        {
            findings.Add(new(JsonPath.Root.Append("scope"), "properties.scope is required: the Azure scope of the policy, /subscriptions/{subscriptionId} or /subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName} (or a resource id under it)"));
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

        if (IsManagementGroupScope(scope))
        {
            findings.Add(new(JsonPath.Root.Append(key), $"properties.{key} '{scope}' is a management group scope, which is not supported yet; declare the policy at a subscription (/subscriptions/{{subscriptionId}}), a resource group or a resource"));
        }
        else if (NormalizeScope(scope) is null)
        {
            findings.Add(new(JsonPath.Root.Append(key), $"properties.{key} '{scope}' is not an Azure scope; use /subscriptions/{{subscriptionId}}, /subscriptions/{{subscriptionId}}/resourceGroups/{{resourceGroupName}} or a resource id under a resource group (the subscription id is a GUID)"));
        }
    }

    private static void CheckRole(JsonObject properties, Func<JsonNode?, bool> isOpaque, List<PimRequestFinding> findings)
    {
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
}
