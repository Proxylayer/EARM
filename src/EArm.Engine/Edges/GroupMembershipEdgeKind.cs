using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Engine.Providers;

namespace EArm.Engine.Edges;

/// <summary>
/// Group membership: target side <c>groups.members</c> (authoritative set), principal side
/// <c>memberOf</c> on users, service principals and groups. Live reads: <c>GET /groups/{g}/members?$select=id</c>
/// plus <c>GET /groups/{g}/members/microsoft.graph.servicePrincipal?$select=id</c> for the target,<c>GET /{users|servicePrincipals|groups}/{p}/memberOf?$select=id</c> (groups only) for the
/// principal. Writes: <c>POST /groups/{g}/members/$ref</c>, <c>DELETE /groups/{g}/members/{p}/$ref</c>. Dynamic
/// groups reject manual membership (validation error).
/// </summary>
public sealed class GroupMembershipEdgeKind : IEdgeKindHandler
{
    private const string ServicePrincipalCast = "microsoft.graph.servicePrincipal";

    public EdgeKind Kind => EdgeKind.GroupMembership;

    public EdgePhase Phase => EdgePhase.A;

    public bool RejectsDynamicTargets => true;

    public Task<EdgeItem> ResolveItemAsync(JsonNode? item, EdgeSide side, ResourceInstance declarer, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        return Task.FromResult(EdgeReads.StringId(item, side == EdgeSide.Target ? "a member object id" : "a group object id"));
    }

    public async Task<EdgeTargetState> ReadTargetAsync(string targetId, string? collectionHint, bool includeEdges, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(context);

        JsonObject? group = null;
        if (context.FindInstanceByGraphId(targetId) is { } instance && string.Equals(EdgeGraphContext.CollectionOf(instance.Type), "groups", StringComparison.Ordinal))
        {
            group = context.LiveObjectOf(instance);
        }

        group ??= await context.GetAsync(new GraphRequest("/groups/" + GraphPath.Segment(targetId), api).WithSelect("id", "groupTypes", "membershipRuleProcessingState"), cancellationToken).ConfigureAwait(false) as JsonObject;
        if (group is null)
        {
            return EdgeTargetState.Missing;
        }

        bool dynamic = EdgeReads.ArrayContains(group, "groupTypes", "DynamicMembership");
        IReadOnlyDictionary<string, JsonObject?>? edges = null;
        if (includeEdges)
        {
            edges = await ReadMembersAsync(targetId, api, context, cancellationToken).ConfigureAwait(false);
        }

        return new EdgeTargetState(true, dynamic, "groups", edges);
    }

    /// <summary>
    /// The live members of a group. v1.0 <c>GET /groups/{g}/members</c> omits service principal members (confirmed against a real
    /// tenant: beta, the type cast and <c>$expand=members</c> list them, and re-adding one answers "already exist"), so the service
    /// principals are read through the type cast <c>/groups/{g}/members/microsoft.graph.servicePrincipal</c> and merged in.
    /// </summary>
    private static async Task<Dictionary<string, JsonObject?>> ReadMembersAsync(string groupId, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        string path = "/groups/" + GraphPath.Segment(groupId) + "/members";
        var members = await EdgeReads.ReadIdsAsync(context, path, api, cancellationToken).ConfigureAwait(false);
        var servicePrincipals = await EdgeReads.ReadIdsAsync(context, path + "/" + ServicePrincipalCast, api, cancellationToken).ConfigureAwait(false);
        foreach (var (id, extra) in servicePrincipals)
        {
            if (members.ContainsKey(id))
            {
                continue;
            }

            var typed = extra ?? new JsonObject();
            if (JsonObjects.GetString(typed, "@odata.type") is null)
            {
                typed["@odata.type"] = "#" + ServicePrincipalCast;
            }

            members[id] = typed;
        }

        return members;
    }


    public async Task<EdgePrincipalState> ReadPrincipalAsync(string principalId, string principalType, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principalId);
        ArgumentNullException.ThrowIfNull(principalType);
        ArgumentNullException.ThrowIfNull(context);

        string collection = EdgeGraphContext.CollectionOf(principalType) ?? "directoryObjects";
        var items = await context.ReadMemberOfAsync(collection, principalId, api, cancellationToken).ConfigureAwait(false);
        var edges = new Dictionary<string, JsonObject?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (string.Equals(JsonObjects.GetString(item, "@odata.type"), "#microsoft.graph.group", StringComparison.OrdinalIgnoreCase) && JsonObjects.GetId(item) is { } id)
            {
                edges[Edge.NormalizeId(id)] = null;
            }
        }

        return new EdgePrincipalState(true, edges);
    }

    public BatchRequest AddRequest(Edge edge, GraphApi api)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return new BatchRequest("POST", "/groups/" + GraphPath.Segment(edge.TargetId) + "/members/$ref", EdgeReads.RefBody(api, "directoryObjects", edge.PrincipalId));
    }

    public BatchRequest RemoveRequest(Edge edge, JsonObject? liveExtra, GraphApi api)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return new BatchRequest("DELETE", "/groups/" + GraphPath.Segment(edge.TargetId) + "/members/" + GraphPath.Segment(edge.PrincipalId) + "/$ref");
    }

    public string Describe(Edge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return $"member {edge.PrincipalId} of group {edge.TargetId}";
    }
}
