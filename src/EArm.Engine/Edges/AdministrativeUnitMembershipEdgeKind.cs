using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Az;
using EArm.Engine.Providers;

namespace EArm.Engine.Edges;

/// <summary>
/// Administrative unit membership (plan section 1.4): target side <c>administrativeUnits.members</c>
/// (authoritative set), principal side <c>administrativeUnits</c> on users and groups. Live reads:
/// <c>GET /directory/administrativeUnits/{au}/members?$select=id</c> for the target, the principal's <c>memberOf</c>
/// filtered to administrative units for the principal. Writes: <c>POST .../members/$ref</c>,
/// <c>DELETE .../members/{p}/$ref</c>. Dynamic administrative units reject manual members.
/// </summary>
public sealed class AdministrativeUnitMembershipEdgeKind : IEdgeKindHandler
{
    private const string Collection = "directory/administrativeUnits";

    public EdgeKind Kind => EdgeKind.AdministrativeUnitMembership;

    public EdgePhase Phase => EdgePhase.A;

    public bool RejectsDynamicTargets => true;

    public Task<EdgeItem> ResolveItemAsync(JsonNode? item, EdgeSide side, ResourceInstance declarer, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        return Task.FromResult(EdgeReads.StringId(item, side == EdgeSide.Target ? "a member object id" : "an administrative unit object id"));
    }

    public async Task<EdgeTargetState> ReadTargetAsync(string targetId, string? collectionHint, bool includeEdges, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(context);

        JsonObject? unit = null;
        if (context.FindInstanceByGraphId(targetId) is { } instance && string.Equals(EdgeGraphContext.CollectionOf(instance.Type), Collection, StringComparison.Ordinal))
        {
            unit = context.LiveObjectOf(instance);
        }

        unit ??= await context.GetAsync(new GraphRequest("/" + Collection + "/" + GraphPath.Segment(targetId), api).WithSelect("id", "membershipType", "isMemberManagementRestricted"), cancellationToken).ConfigureAwait(false) as JsonObject;
        if (unit is null)
        {
            return EdgeTargetState.Missing;
        }

        bool dynamic = string.Equals(JsonObjects.GetString(unit, "membershipType"), "Dynamic", StringComparison.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, JsonObject?>? edges = null;
        if (includeEdges)
        {
            edges = await EdgeReads.ReadIdsAsync(context, "/" + Collection + "/" + GraphPath.Segment(targetId) + "/members", api, cancellationToken).ConfigureAwait(false);
        }

        return new EdgeTargetState(true, dynamic, Collection, edges);
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
            if (string.Equals(JsonObjects.GetString(item, "@odata.type"), "#microsoft.graph.administrativeUnit", StringComparison.OrdinalIgnoreCase) && JsonObjects.GetId(item) is { } id)
            {
                edges[Edge.NormalizeId(id)] = null;
            }
        }

        return new EdgePrincipalState(true, edges);
    }

    public BatchRequest AddRequest(Edge edge, GraphApi api)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return new BatchRequest("POST", "/" + Collection + "/" + GraphPath.Segment(edge.TargetId) + "/members/$ref", EdgeReads.RefBody(api, "directoryObjects", edge.PrincipalId));
    }

    public BatchRequest RemoveRequest(Edge edge, JsonObject? liveExtra, GraphApi api)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return new BatchRequest("DELETE", "/" + Collection + "/" + GraphPath.Segment(edge.TargetId) + "/members/" + GraphPath.Segment(edge.PrincipalId) + "/$ref");
    }

    public string Describe(Edge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return $"member {edge.PrincipalId} of administrative unit {edge.TargetId}";
    }
}
