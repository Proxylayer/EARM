using System;
using System.Text.RegularExpressions;

namespace EArm.Integration.Tests;

/// <summary>
/// The one Azure role management policy the Azure PIM policy probes may read and change: the policy of one built-in role
/// (<c>EARM_IT_AZURE_ROLE</c>, Reader by default) at one resource group (<c>EARM_IT_AZURE_SCOPE</c>). Both values are validated here, before any request:
/// the scope must be exactly <c>/subscriptions/{guid}/resourceGroups/{name}</c> (a subscription or management group scope is refused, it would change the
/// policy every resource below it inherits), and the role must be a role definition GUID that is not one of the roles that grant access management
/// (<see cref="RefusedRoles"/>).
/// </summary>
internal sealed record AzurePimPolicyTarget
{
    /// <summary>The api-version of the PIM role management policy and policy assignment resources (<c>Microsoft.Authorization/roleManagementPolicies</c>).</summary>
    public const string PimApiVersion = "2020-10-01";

    /// <summary>The api-version of the resource group read the fixture's preflight sends.</summary>
    public const string ResourceGroupApiVersion = "2021-04-01";

    /// <summary>The api-version of the role definition reads (by id and by <c>roleName</c>).</summary>
    public const string RoleDefinitionApiVersion = "2022-04-01";

    /// <summary>The built-in Reader role: the default, and the lowest-risk role whose policy a probe can change.</summary>
    public const string ReaderRoleId = "acdd72a7-3385-48ef-bd42-f606fba81ae7";

    /// <summary>Roles whose policy the probes never touch, whatever the configuration says: each grants (or can grant) access management at the scope.</summary>
    internal static readonly (string Id, string Name)[] RefusedRoles =
    [
        ("8e3af657-a8ff-443c-a75c-2fe8c4bcb635", "Owner"),
        ("18d7d88d-d35e-4fb5-a5c3-7773c20a72d9", "User Access Administrator"),
        ("f58310d9-a9f6-439a-9e8d-f62e7b41a168", "Role Based Access Control Administrator"),
        ("b24988ac-6180-42dd-ab88-20f7c3ec20f2", "Contributor"),
    ];

    private const string AuthorizationProvider = "/providers/Microsoft.Authorization";

    // A resource group name is 1-90 characters of letters, digits, '-', '_', '.', '(' and ')' and does not end with '.'; only the ASCII subset is accepted,
    // so the scope survives ArmClient's resource path check unchanged.
    private static readonly Regex ResourceGroupScope = new(
        "^/subscriptions/(?<subscription>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})/resourceGroups/(?<group>[A-Za-z0-9_().-]{0,89}[A-Za-z0-9_()-])$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    private AzurePimPolicyTarget(string subscriptionId, string resourceGroupName, string roleDefinitionId)
    {
        SubscriptionId = subscriptionId;
        ResourceGroupName = resourceGroupName;
        RoleDefinitionId = roleDefinitionId;
        Scope = $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}";
    }

    /// <summary>The subscription id (lower case).</summary>
    public string SubscriptionId { get; }

    /// <summary>The resource group name, as configured.</summary>
    public string ResourceGroupName { get; }

    /// <summary>The resource group scope <c>/subscriptions/{id}/resourceGroups/{name}</c>.</summary>
    public string Scope { get; }

    /// <summary>The role definition GUID (lower case).</summary>
    public string RoleDefinitionId { get; }

    /// <summary>The subscription-qualified role definition id, the form the Azure portal and Azure PowerShell filter policy assignments with.</summary>
    public string SubscriptionRoleDefinitionId => $"/subscriptions/{SubscriptionId}{AuthorizationProvider}/roleDefinitions/{RoleDefinitionId}";

    /// <summary>The role definition id qualified with the resource group scope.</summary>
    public string ScopedRoleDefinitionId => $"{Scope}{AuthorizationProvider}/roleDefinitions/{RoleDefinitionId}";

    /// <summary>The policy assignments collection at the scope.</summary>
    public string PolicyAssignmentsPath => Scope + AuthorizationProvider + "/roleManagementPolicyAssignments";

    /// <summary>The policies collection at the scope.</summary>
    public string PoliciesPath => Scope + AuthorizationProvider + "/roleManagementPolicies";

    /// <summary>The role definitions collection at the scope.</summary>
    public string RoleDefinitionsPath => Scope + AuthorizationProvider + "/roleDefinitions";

    /// <summary>One line naming the policy, for logs and messages.</summary>
    public string Describe()
    {
        return $"the Azure PIM policy of role {RoleDefinitionId} at {Scope}";
    }

    /// <summary>
    /// Validates the configured scope and role (<paramref name="roleText"/> null means Reader). Returns the target, or null with
    /// <paramref name="problem"/> saying why the values are refused.
    /// </summary>
    public static AzurePimPolicyTarget? Parse(string scopeText, string? roleText, out string? problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeText);
        string scope = scopeText.Trim().TrimEnd('/');
        var match = ResourceGroupScope.Match(scope);
        if (!match.Success)
        {
            problem = DescribeRefusedScope(scope);
            return null;
        }

        string role = (roleText ?? ReaderRoleId).Trim();
        if (!Guid.TryParseExact(role, "D", out var roleGuid))
        {
            problem = $"{IntegrationSettings.AzureRoleVariable}='{role}' is not a role definition GUID (for example Reader, {ReaderRoleId}); a role name or a full role definition id is not accepted.";
            return null;
        }

        string roleId = roleGuid.ToString("D");
        foreach (var (id, name) in RefusedRoles)
        {
            if (string.Equals(id, roleId, StringComparison.OrdinalIgnoreCase))
            {
                problem = $"{IntegrationSettings.AzureRoleVariable}={roleId} is {name}: the probes never change the policy of Owner, User Access Administrator, Role Based Access Control Administrator or Contributor. Use a low-risk role such as Reader ({ReaderRoleId}).";
                return null;
            }
        }

        problem = null;
        return new AzurePimPolicyTarget(match.Groups["subscription"].Value.ToLowerInvariant(), match.Groups["group"].Value, roleId);
    }

    private static string DescribeRefusedScope(string scope)
    {
        string head = $"{IntegrationSettings.AzureScopeVariable}='{scope}' is refused: ";
        if (scope.Contains("/providers/Microsoft.Management/managementGroups/", StringComparison.OrdinalIgnoreCase))
        {
            return head + "it is a management group scope. The probes change the PIM policy at the scope they are given, and every subscription and resource below a management group inherits its policy; use a throw-away resource group: /subscriptions/{subscription-id}/resourceGroups/{name}.";
        }

        if (Regex.IsMatch(scope, "^/subscriptions/[^/]+$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
        {
            return head + "it is a subscription scope, and every resource group and resource in the subscription inherits its policy; use a throw-away resource group: /subscriptions/{subscription-id}/resourceGroups/{name}.";
        }

        return head + "it must be exactly a resource group id, /subscriptions/{subscription-id}/resourceGroups/{name} (a GUID subscription id, a name of ASCII letters, digits, '-', '_', '.', '(' and ')' not ending in '.', nothing below the resource group).";
    }
}
