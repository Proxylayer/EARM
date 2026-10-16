using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Validation;
using EArm.Engine.Providers;
using EArm.Graph;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>
/// What names a PIM policy: the scope type (<c>DirectoryRole</c> or <c>Group</c>), the scope (<c>/</c>, or the group's object id) and the role
/// (a directory role template id, or <c>member</c> / <c>owner</c> of the group).
/// </summary>
public sealed record PolicyScope(string ScopeType, string ScopeId, string RoleDefinitionId)
{
    public bool IsGroup => ScopeType == RoleManagementPolicyRules.GroupScope;

    /// <summary>The policy as messages name it.</summary>
    public string Describe()
    {
        return IsGroup ? $"the {RoleDefinitionId} policy of group {ScopeId}" : $"the policy of directory role {RoleDefinitionId}";
    }
}

/// <summary>
/// The Graph side of <see cref="RoleManagementPoliciesProvider"/>: the policy of a scope is found through its assignment,
/// <c>GET /policies/roleManagementPolicyAssignments?$filter=scopeId eq '…' and scopeType eq '…' and roleDefinitionId eq '…'&amp;$expand=policy($expand=rules)</c>
/// (Learn: policyroot-list-rolemanagementpolicyassignments; the scope filter is required), re-matched client-side; a rule is changed with
/// <c>PATCH /policies/roleManagementPolicies/{policyId}/rules/{ruleId}</c> (Learn: unifiedrolemanagementpolicyrule-update; the body must carry the rule's
/// <c>@odata.type</c>). Never cached: PIM re-ids a group's policies when it onboards the group.
/// </summary>
public static class RoleManagementPolicyLookup
{
    public const string AssignmentsPath = "/policies/roleManagementPolicyAssignments";

    public const string PoliciesPath = "/policies/roleManagementPolicies";

    /// <summary>
    /// The policy assignment of <paramref name="scope"/> with its policy and rules expanded, or <c>null</c> when Graph lists none (for a group that PIM for
    /// Groups has not onboarded, Graph answers 404 ResourceNotFound: also <c>null</c>).
    /// </summary>
    public static async Task<JsonObject?> FindAsync(IGraphClient graph, PolicyScope scope, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(scope);
        var request = new GraphRequest(AssignmentsPath, api, hint: hint).WithFilter(Filter(scope)).WithExpand("policy($expand=rules)");
        try
        {
            await foreach (var item in graph.GetAllPagesAsync(request, cancellationToken).ConfigureAwait(false))
            {
                if (item is JsonObject assignment && Matches(assignment, scope) && assignment["policy"] is JsonObject)
                {
                    return assignment;
                }
            }
        }
        catch (GraphException ex) when (ex.IsNotFound && scope.IsGroup)
        {
            // Graph answers the assignment list of a group PIM for Groups has not onboarded with 404 ResourceNotFound, not an empty collection:
            // the group's policy is not listed (yet).
            return null;
        }

        return null;
    }

    /// <summary>The <c>$filter</c> of the assignment lookup.</summary>
    public static string Filter(PolicyScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return "scopeId eq '" + PimScheduleRequests.Quote(scope.ScopeId) + "' and scopeType eq '" + PimScheduleRequests.Quote(scope.ScopeType) + "' and roleDefinitionId eq '" + PimScheduleRequests.Quote(scope.RoleDefinitionId) + "'";
    }

    /// <summary>True when a listed assignment is the one of <paramref name="scope"/> (ids GUID-aware, <c>Directory</c> and <c>DirectoryRole</c> alike).</summary>
    public static bool Matches(JsonObject assignment, PolicyScope scope)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(scope);
        return RoleManagementPolicyRules.NormalizeScopeType(JsonObjects.GetString(assignment, "scopeType")) == scope.ScopeType
            && JsonObjects.KeyEquals(JsonObjects.GetString(assignment, "scopeId"), scope.ScopeId)
            && JsonObjects.KeyEquals(JsonObjects.GetString(assignment, "roleDefinitionId"), scope.RoleDefinitionId);
    }

    /// <summary>The id of the policy an assignment points at.</summary>
    public static string? PolicyId(JsonObject assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        return JsonObjects.GetString(assignment, "policyId") ?? (assignment["policy"] is JsonObject policy ? JsonObjects.GetId(policy) : null);
    }

    /// <summary>The live view of a policy assignment: the scope and every rule of the policy in ARM's shape (<see cref="RoleManagementPolicyShape.ToArm"/>).</summary>
    public static JsonObject LiveView(JsonObject assignment, PolicyScope scope)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(scope);
        var rules = new JsonArray();
        if (assignment["policy"] is JsonObject policy && JsonObjects.TryGetIgnoreCase(policy, "rules", out var node) && node is JsonArray live)
        {
            foreach (var rule in live.OfType<JsonObject>())
            {
                rules.Add(RoleManagementPolicyShape.ToArm(rule));
            }
        }

        return new JsonObject
        {
            ["scopeType"] = scope.ScopeType,
            ["scopeId"] = scope.ScopeId,
            ["roleDefinitionId"] = scope.RoleDefinitionId,
            ["rules"] = rules,
        };
    }

    /// <summary><c>PATCH /policies/roleManagementPolicies/{policyId}/rules/{ruleId}</c> with the Graph body of an ARM-shaped rule.</summary>
    public static Task PatchRuleAsync(IGraphClient graph, string policyId, JsonObject armRule, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyId);
        ArgumentNullException.ThrowIfNull(armRule);
        string ruleId = JsonObjects.GetId(armRule) ?? throw new ArgumentException("The rule has no id.", nameof(armRule));
        string path = PoliciesPath + "/" + GraphPath.Segment(policyId) + "/rules/" + GraphPath.Segment(ruleId);
        return graph.PatchAsync(new GraphRequest(path, api, hint: hint), RoleManagementPolicyShape.ToGraph(armRule), cancellationToken);
    }
}
