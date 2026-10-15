using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Az;
using EArm.Engine.Providers;

namespace EArm.Engine.Edges;

/// <summary>
/// Group membership (plan section 1.4): target side <c>groups.members</c> (authoritative set), principal side
/// <c>memberOf</c> on users, service principals and groups. Live reads: <c>GET /groups/{g}/members?$select=id</c>
/// for the target, <c>GET /{users|servicePrincipals|groups}/{p}/memberOf?$select=id</c> (groups only) for the
/// principal. Writes: <c>POST /groups/{g}/members/$ref</c>, <c>DELETE /groups/{g}/members/{p}/$ref</c>. Dynamic
/// groups reject manual membership (validation error).
/// </summary>
public sealed class GroupMembershipEdgeKind : IEdgeKindHandler
{
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
            edges = await EdgeReads.ReadIdsAsync(context, "/groups/" + GraphPath.Segment(targetId) + "/members", api, cancellationToken).ConfigureAwait(false);
        }

        return new EdgeTargetState(true, dynamic, "groups", edges);
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
