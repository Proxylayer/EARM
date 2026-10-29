using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Validation;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using EArm.Graph;

namespace EArm.Providers;

/// <summary>
/// What names an Azure PIM policy: the Azure scope (<see cref="AzureRoleManagementPolicyRules.NormalizeScope"/>) and the qualified role
/// definition id (<see cref="AzureRoleManagementPolicyRules.QualifiedRoleDefinitionId"/>).
/// </summary>
public sealed record AzurePolicyScope(string Scope, string RoleDefinitionId)
{
    /// <summary>The policy as messages name it.</summary>
    public string Describe()
    {
        return $"the policy of Azure role {RoleDefinitionId} at {Scope}";
    }
}

/// <summary>An Azure PIM policy as Azure Resource Manager read it.</summary>
/// <param name="PolicyPath">The policy's resource path, rebuilt from the declared scope and the policy name (never a response's id taken as is).</param>
/// <param name="Policy">The <c>Microsoft.Authorization/roleManagementPolicies</c> resource (<c>properties.rules</c> carries every rule).</param>
public sealed record AzurePolicy(string PolicyPath, JsonObject Policy);

/// <summary>
/// The Azure Resource Manager side of <see cref="AzureRoleManagementPoliciesProvider"/> (api-version <see cref="PolicyApiVersion"/>). The policy of a role at a
/// scope is found through its assignment, <c>GET {scope}/providers/Microsoft.Authorization/roleManagementPolicyAssignments?$filter=roleDefinitionId eq '{id}'</c>
/// (the id subscription-qualified: ARM refuses a bare GUID with 400 InvalidARMRequest), keeping only the assignment whose <c>properties.scope</c> IS the declared
/// scope (an inherited policy is never acted on); its <c>properties.policyId</c> names the policy, read with <c>GET {policyId}</c>. The policy name is not
/// assumed to be the role GUID (it was in the probes, but ARM does not document it). The rules are changed with ONE <c>PATCH {policyId}</c> of
/// <c>properties.rules</c> (the rules sent are changed, the others are untouched; the answer is the whole policy). Role names are resolved with
/// <c>GET {scope}/providers/Microsoft.Authorization/roleDefinitions?$filter=roleName eq '{name}'</c> (the policy filter refuses roleName with 400 InvalidFilter).
/// Never cached.
/// </summary>
public static class AzureRoleManagementPolicyLookup
{
    /// <summary>The api-version of <c>roleManagementPolicies</c> and <c>roleManagementPolicyAssignments</c>.</summary>
    public const string PolicyApiVersion = "2020-10-01";

    /// <summary>The api-version of the <c>roleDefinitions</c> read that resolves a role name.</summary>
    public const string RoleDefinitionApiVersion = "2022-04-01";

    /// <summary><c>{scope}/providers/Microsoft.Authorization/roleManagementPolicyAssignments</c>.</summary>
    public static string AssignmentsPath(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return scope + AzureRoleManagementPolicyRules.AuthorizationProvider + "/roleManagementPolicyAssignments";
    }

    /// <summary><c>{scope}/providers/Microsoft.Authorization/roleManagementPolicies</c>: the only collection a policy of the scope may live in.</summary>
    public static string PoliciesPath(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return scope + AzureRoleManagementPolicyRules.AuthorizationProvider + "/roleManagementPolicies";
    }

    /// <summary><c>{scope}/providers/Microsoft.Authorization/roleDefinitions</c>.</summary>
    public static string RoleDefinitionsPath(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return scope + AzureRoleManagementPolicyRules.AuthorizationProvider + "/roleDefinitions";
    }

    /// <summary>The <c>$filter</c> of the assignment lookup.</summary>
    public static string Filter(AzurePolicyScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return "roleDefinitionId eq '" + PimScheduleRequests.Quote(scope.RoleDefinitionId) + "'";
    }

    /// <summary>
    /// The policy of <paramref name="scope"/>, or <c>null</c> when ARM lists no policy assignment of the role AT the scope (also when the scope does not exist:
    /// ARM answers the list with 404). A listed assignment whose policy is not in the scope's own policy collection, or a policy whose <c>properties.scope</c>
    /// is another scope, is refused (<see cref="EngineDiagnosticCodes.ReadFailed"/>) rather than acted on.
    /// </summary>
    public static async Task<AzurePolicy?> FindAsync(ArmClient arm, AzurePolicyScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arm);
        ArgumentNullException.ThrowIfNull(scope);
        JsonObject? assignment = null;
        try
        {
            await foreach (var item in arm.ListAsync(AssignmentsPath(scope.Scope), PolicyApiVersion, Filter(scope), cancellationToken).ConfigureAwait(false))
            {
                if (Matches(item, scope))
                {
                    assignment = item;
                    break;
                }
            }
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            // ARM answers a scope that does not exist (a resource group not created yet) with 404: no policy is listed.
            return null;
        }

        if (assignment is null)
        {
            return null;
        }

        string policyId = JsonObjects.GetString(Properties(assignment), "policyId")
            ?? throw new ProviderException(EngineDiagnosticCodes.ReadFailed, $"The PIM policy assignment of {scope.Describe()} names no policy (properties.policyId).");
        string policyPath = PolicyPath(scope, policyId)
            ?? throw new ProviderException(EngineDiagnosticCodes.ReadFailed, $"The PIM policy assignment of {scope.Describe()} names the policy '{policyId}', which is not in {PoliciesPath(scope.Scope)}; EARM never changes a policy of another scope.");
        var policy = await arm.GetAsync(policyPath, PolicyApiVersion, cancellationToken).ConfigureAwait(false)
            ?? throw new ProviderException(EngineDiagnosticCodes.ReadFailed, $"GET {policyPath} answered with an empty body.");
        if (JsonObjects.GetString(Properties(policy), "scope") is { } policyScope && !ScopeEquals(policyScope, scope.Scope))
        {
            throw new ProviderException(EngineDiagnosticCodes.ReadFailed, $"The PIM policy {policyPath} of {scope.Describe()} belongs to scope '{policyScope}'; EARM never changes a policy of another scope.");
        }

        return new AzurePolicy(policyPath, policy);
    }

    /// <summary>True when a listed assignment is the one of <paramref name="scope"/>: its <c>properties.scope</c> is the scope itself (not a parent) and its role is the role.</summary>
    public static bool Matches(JsonObject assignment, AzurePolicyScope scope)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(scope);
        var properties = Properties(assignment);
        return JsonObjects.GetString(properties, "scope") is { } assignmentScope && ScopeEquals(assignmentScope, scope.Scope)
            && AzureRoleManagementPolicyRules.RoleDefinitionGuid(JsonObjects.GetString(properties, "roleDefinitionId")) is { } role
            && role == AzureRoleManagementPolicyRules.RoleDefinitionGuid(scope.RoleDefinitionId);
    }

    /// <summary>
    /// The resource path of the policy an assignment names, rebuilt as <c>{PoliciesPath(scope)}/{name}</c> when <paramref name="policyId"/> is a policy of
    /// that collection (case-insensitive); <c>null</c> otherwise.
    /// </summary>
    public static string? PolicyPath(AzurePolicyScope scope, string policyId)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(policyId);
        string prefix = PoliciesPath(scope.Scope) + "/";
        string id = policyId.Trim();
        if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string name = id[prefix.Length..];
        string path = prefix + name;
        return name.Length > 0 && !name.Contains('/', StringComparison.Ordinal) && ArmClient.IsValidResourcePath(path) ? path : null;
    }

    /// <summary>
    /// The live view of a policy: the scope, the role and every rule of <c>properties.rules</c>, normalised (<see cref="RoleManagementPolicyRules.NormalizeRules"/>)
    /// and comparable (<see cref="RoleManagementPolicyShape.Comparable"/>), as the declared rules are.
    /// </summary>
    public static JsonObject LiveView(AzurePolicy policy, AzurePolicyScope scope)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(scope);
        var rules = new JsonArray();
        if (JsonObjects.TryGetIgnoreCase(Properties(policy.Policy), "rules", out var node) && node is JsonArray live)
        {
            var copies = new JsonArray(live.OfType<JsonObject>().Select(r => JsonValues.Clone(r)).ToArray());
            RoleManagementPolicyRules.NormalizeRules(copies, static _ => false);
            foreach (var rule in copies.OfType<JsonObject>())
            {
                rules.Add(RoleManagementPolicyShape.Comparable(rule));
            }
        }

        return new JsonObject
        {
            ["scope"] = scope.Scope,
            ["roleDefinitionId"] = scope.RoleDefinitionId,
            ["rules"] = rules,
        };
    }

    /// <summary>
    /// <c>PATCH {policyPath}</c> with <c>{ "properties": { "rules": [...] } }</c>: ARM changes only the rules sent, each replaced whole (a member left out is
    /// reset, so every rule must be complete). Returns ARM's answer, the whole policy (<c>null</c> for an empty body).
    /// </summary>
    public static Task<JsonObject?> PatchRulesAsync(ArmClient arm, string policyPath, IReadOnlyList<JsonObject> rules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arm);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyPath);
        ArgumentNullException.ThrowIfNull(rules);
        var body = new JsonObject
        {
            ["properties"] = new JsonObject { ["rules"] = new JsonArray(rules.Select(r => (JsonNode?)JsonValues.Clone(r)).ToArray()) },
        };
        return arm.PatchAsync(policyPath, PolicyApiVersion, body, cancellationToken);
    }

    /// <summary>
    /// The role definition GUID of the role named <paramref name="roleName"/> at <paramref name="scope"/> (a built-in or an assignable custom role), or the
    /// reason there is none or more than one. ARM filters by <c>roleName</c>; the names are matched again here, ignoring case.
    /// </summary>
    public static async Task<(string? Id, string? Error)> ResolveRoleAsync(ArmClient arm, string scope, string roleName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arm);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(roleName);
        string name = roleName.Trim();
        string filter = "roleName eq '" + PimScheduleRequests.Quote(name) + "'";
        var found = new List<(string Id, string Name)>();
        await foreach (var definition in arm.ListAsync(RoleDefinitionsPath(scope), RoleDefinitionApiVersion, filter, cancellationToken).ConfigureAwait(false))
        {
            string? listed = JsonObjects.GetString(Properties(definition), "roleName");
            var id = AzureRoleManagementPolicyRules.RoleDefinitionGuid(JsonObjects.GetString(definition, "name"))
                ?? AzureRoleManagementPolicyRules.RoleDefinitionGuid(JsonObjects.GetId(definition));
            if (listed is not null && id is { } guid && string.Equals(listed.Trim(), name, StringComparison.OrdinalIgnoreCase) && found.All(f => f.Id != guid.ToString("D")))
            {
                found.Add((guid.ToString("D"), listed));
            }
        }

        return found.Count switch
        {
            1 => (found[0].Id, null),
            0 => (null, $"no Azure role named '{name}' is assignable at {scope} (GET {RoleDefinitionsPath(scope)}?$filter={filter}); check the role name, or declare roleDefinitionId"),
            _ => (null, $"{found.Count} Azure roles named '{name}' are assignable at {scope} ({string.Join(", ", found.Select(f => f.Id))}); declare roleDefinitionId"),
        };
    }

    /// <summary>Two scopes compared as ARM does: case-insensitively, without a trailing <c>/</c>.</summary>
    public static bool ScopeEquals(string? left, string? right)
    {
        return left is not null && right is not null
            && string.Equals(left.Trim().TrimEnd('/'), right.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private static JsonObject Properties(JsonObject resource)
    {
        return JsonObjects.TryGetIgnoreCase(resource, "properties", out var node) && node is JsonObject properties ? properties : new JsonObject();
    }
}
