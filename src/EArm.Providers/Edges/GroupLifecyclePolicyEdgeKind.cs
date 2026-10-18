using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine.Edges;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using EArm.Graph;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers.Edges;

/// <summary>
/// The groups of a group lifecycle (expiration) policy whose <c>managedGroupTypes</c> is <c>Selected</c>: the principal-side
/// <c>groupLifecyclePolicies</c> property of a group, <c>["[reference('default').id]"]</c> (ensure-only like every principal-side edge: a group
/// that is no longer declared stays in the policy).
/// <para>
/// Graph (https://learn.microsoft.com/en-us/graph/api/grouplifecyclepolicy-addgroup?view=graph-rest-1.0, -removegroup, group-list-grouplifecyclepolicies):
/// the live state is read from the group, <c>GET /groups/{id}/groupLifecyclePolicies</c> (there is no documented listing of a policy's groups);
/// the add is <c>POST /groupLifecyclePolicies/{policyId}/addGroup { groupId }</c> (one group per request, up to 500 groups), the remove
/// <c>POST …/removeGroup { groupId }</c>. Only Microsoft 365 groups expire, and a group can only be added while the policy's
/// <c>managedGroupTypes</c> is <c>Selected</c>: a policy of <c>All</c> already governs every Microsoft 365 group and <c>None</c> governs none,
/// so either is an invalid item (the template's declared value is used when the policy is part of the same deployment).
/// </para>
/// </summary>
[EntraEdgeKind(EdgeKind.GroupLifecyclePolicy)]
public sealed class GroupLifecyclePolicyEdgeKind : IEdgeKindHandler
{
    private const string Collection = "groupLifecyclePolicies";

    public EdgeKind Kind => EdgeKind.GroupLifecyclePolicy;

    public EdgePhase Phase => EdgePhase.B;

    public async Task<EdgeItem> ResolveItemAsync(JsonNode? item, EdgeSide side, ResourceInstance declarer, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(declarer);
        ArgumentNullException.ThrowIfNull(context);
        if (side == EdgeSide.Target)
        {
            return EdgeItem.Invalid("groups join a lifecycle policy from the group's side (groupLifecyclePolicies on Microsoft.Entra/groups); the policy has no member list.");
        }

        var resolved = EdgeReads.StringId(item, "a group lifecycle policy object id");
        if (resolved.IsUnknown || resolved.Error is not null || resolved.OtherId is null)
        {
            return resolved;
        }

        if (DeclaresNonUnifiedGroup(declarer))
        {
            return EdgeItem.Invalid("only Microsoft 365 groups (groupTypes containing Unified) expire; a security group cannot join a group lifecycle policy.");
        }

        string? managed = ManagedGroupTypesOf(context, resolved.OtherId);
        if (managed is null)
        {
            var live = await context.GetAsync(PolicyRequest(resolved.OtherId, api), cancellationToken).ConfigureAwait(false) as JsonObject;
            if (live is null)
            {
                return EdgeItem.Invalid($"'{resolved.OtherId}' is not an existing group lifecycle policy.");
            }

            managed = JsonObjects.GetString(live, GroupLifecyclePoliciesRules.ManagedGroupTypesProperty);
        }

        if (managed is not null && !managed.Equals(GroupLifecyclePoliciesRules.Selected, StringComparison.OrdinalIgnoreCase))
        {
            return EdgeItem.Invalid($"the group lifecycle policy '{resolved.OtherId}' has managedGroupTypes '{managed}'; groups can only be added to a policy whose managedGroupTypes is Selected (All already governs every Microsoft 365 group, None governs none).");
        }

        return new EdgeItem(resolved.OtherId, Collection);
    }

    public async Task<EdgeTargetState> ReadTargetAsync(string targetId, string? collectionHint, bool includeEdges, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(context);
        bool exists = context.FindInstanceByGraphId(targetId) is { } instance && context.LiveObjectOf(instance) is not null
            || await context.GetAsync(PolicyRequest(targetId, api), cancellationToken).ConfigureAwait(false) is JsonObject;
        return exists ? new EdgeTargetState(true, false, Collection, null) : EdgeTargetState.Missing;
    }

    public async Task<EdgePrincipalState> ReadPrincipalAsync(string principalId, string principalType, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principalId);
        ArgumentNullException.ThrowIfNull(context);
        var items = await context.GetAllAsync(new GraphRequest("/groups/" + GraphPath.Segment(principalId) + "/" + Collection, api).WithSelect("id"), cancellationToken).ConfigureAwait(false);
        var edges = new Dictionary<string, JsonObject?>(StringComparer.OrdinalIgnoreCase);
        foreach (var policy in items)
        {
            if (JsonObjects.GetId(policy) is { } id)
            {
                edges[Edge.NormalizeId(id)] = null;
            }
        }

        return new EdgePrincipalState(true, edges);
    }

    public BatchRequest AddRequest(Edge edge, GraphApi api)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return new BatchRequest("POST", ActionPath(edge, "addGroup"), new JsonObject { ["groupId"] = edge.PrincipalId }, Idempotent: true);
    }

    public BatchRequest RemoveRequest(Edge edge, JsonObject? liveExtra, GraphApi api)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return new BatchRequest("POST", ActionPath(edge, "removeGroup"), new JsonObject { ["groupId"] = edge.PrincipalId }, Idempotent: true);
    }

    public string Describe(Edge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return $"group {edge.PrincipalId} in group lifecycle policy {edge.TargetId}";
    }

    private static string ActionPath(Edge edge, string action)
    {
        return "/" + Collection + "/" + GraphPath.Segment(edge.TargetId) + "/" + action;
    }

    private static GraphRequest PolicyRequest(string policyId, GraphApi api)
    {
        return new GraphRequest("/" + Collection + "/" + GraphPath.Segment(policyId), api);
    }

    /// <summary>The policy's <c>managedGroupTypes</c> as the template declares it (a literal), else as the run applied it; <c>null</c> when the policy is not part of the deployment.</summary>
    private static string? ManagedGroupTypesOf(EdgeGraphContext context, string policyId)
    {
        if (context.FindInstanceByGraphId(policyId) is not { } instance)
        {
            return null;
        }

        if (instance.Properties is { } declared
            && TenantPolicyValues.TryGetDeclared(declared, GroupLifecyclePoliciesRules.ManagedGroupTypesProperty, out _, out var value)
            && JsonValues.TryGetString(value, out var text))
        {
            return text;
        }

        return context.LiveObjectOf(instance) is { } live ? JsonObjects.GetString(live, GroupLifecyclePoliciesRules.ManagedGroupTypesProperty) : null;
    }

    /// <summary>The declaring group states a literal <c>groupTypes</c> without <c>Unified</c>.</summary>
    private static bool DeclaresNonUnifiedGroup(ResourceInstance declarer)
    {
        return declarer.Properties is { } properties
            && TenantPolicyValues.TryGetDeclared(properties, "groupTypes", out _, out var groupTypes)
            && groupTypes is JsonArray types
            && !types.Any(t => UnknownJson.IsUnknown(t))
            && !EdgeReads.ArrayContains(properties, "groupTypes", "Unified");
    }
}
