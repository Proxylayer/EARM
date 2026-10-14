using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Az;
using EntraArm.Engine.Providers;
using EntraArm.Expressions.Values;

namespace EntraArm.Engine.Edges;

/// <summary>
/// Ownership (plan section 1.4): target side <c>owners</c> on applications, service principals and groups
/// (authoritative set), principal side <c>ownerOf</c> on users and service principals. The target's Graph
/// collection comes from the template resource when the target is declared in it, else from
/// <c>GET /directoryObjects/{id}?$select=id</c> (<c>@odata.type</c>). Live reads: <c>GET /{collection}/{t}/owners</c>
/// for the target, <c>GET /{users|servicePrincipals}/{p}/ownedObjects</c> for the principal. Writes:
/// <c>POST /{collection}/{t}/owners/$ref</c>, <c>DELETE /{collection}/{t}/owners/{p}/$ref</c>. The state records
/// <c>{ id, collection }</c> per principal-side edge so removal needs no type lookup.
/// </summary>
public sealed class OwnershipEdgeKind : IEdgeKindHandler
{
    private static readonly HashSet<string> OwnableCollections = new(StringComparer.Ordinal) { "applications", "servicePrincipals", "groups" };

    public EdgeKind Kind => EdgeKind.Ownership;

    public EdgePhase Phase => EdgePhase.A;

    public bool WarnOnSignedInPrincipalRemoval => true;

    public async Task<EdgeItem> ResolveItemAsync(JsonNode? item, EdgeSide side, ResourceInstance declarer, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(declarer);
        ArgumentNullException.ThrowIfNull(context);

        if (side == EdgeSide.Target)
        {
            string? collection = EdgeGraphContext.CollectionOf(declarer.Type);
            if (collection is null || !OwnableCollections.Contains(collection))
            {
                return EdgeItem.Invalid($"'owners' cannot be declared on {declarer.Type}; only applications, service principals and groups have owners.");
            }

            var owner = EdgeReads.StringId(item, "an owner object id");
            return owner with { TargetCollection = collection };
        }

        var resolved = EdgeReads.StringId(item, "the object id of an application, service principal or group");
        if (resolved.OtherId is null)
        {
            return resolved;
        }

        string? targetCollection = await context.ResolveCollectionAsync(resolved.OtherId, api, cancellationToken).ConfigureAwait(false);
        if (targetCollection is null)
        {
            return EdgeItem.Invalid($"'{resolved.OtherId}' does not exist or is not an object that can be owned (application, service principal or group).");
        }

        if (!OwnableCollections.Contains(targetCollection))
        {
            return EdgeItem.Invalid($"'{resolved.OtherId}' is a {targetCollection} object; only applications, service principals and groups can be owned.");
        }

        return resolved with { TargetCollection = targetCollection };
    }

    public async Task<EdgeTargetState> ReadTargetAsync(string targetId, string? collectionHint, bool includeEdges, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(context);

        string? collection = collectionHint ?? await context.ResolveCollectionAsync(targetId, api, cancellationToken).ConfigureAwait(false);
        if (collection is null || !OwnableCollections.Contains(collection))
        {
            return EdgeTargetState.Missing;
        }

        bool exists = context.FindInstanceByGraphId(targetId) is not null
            || await context.GetAsync(new GraphRequest("/" + collection + "/" + GraphPath.Segment(targetId), api).WithSelect("id"), cancellationToken).ConfigureAwait(false) is JsonObject;
        if (!exists)
        {
            return EdgeTargetState.Missing;
        }

        IReadOnlyDictionary<string, JsonObject?>? edges = null;
        if (includeEdges)
        {
            edges = await EdgeReads.ReadIdsAsync(context, "/" + collection + "/" + GraphPath.Segment(targetId) + "/owners", api, cancellationToken).ConfigureAwait(false);
        }

        return new EdgeTargetState(true, false, collection, edges);
    }

    public async Task<EdgePrincipalState> ReadPrincipalAsync(string principalId, string principalType, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principalId);
        ArgumentNullException.ThrowIfNull(principalType);
        ArgumentNullException.ThrowIfNull(context);

        string? collection = EdgeGraphContext.CollectionOf(principalType);
        if (collection is not ("users" or "servicePrincipals"))
        {
            // Only users and service principals own objects.
            return new EdgePrincipalState(true, new Dictionary<string, JsonObject?>(StringComparer.OrdinalIgnoreCase));
        }

        var request = new GraphRequest("/" + collection + "/" + GraphPath.Segment(principalId) + "/ownedObjects", api).WithSelect("id").WithTop(999);
        var items = await context.GetAllAsync(request, cancellationToken).ConfigureAwait(false);
        var edges = new Dictionary<string, JsonObject?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (JsonObjects.GetId(item) is { } id)
            {
                var extra = new JsonObject();
                if (EdgeGraphContext.CollectionOfODataType(JsonObjects.GetString(item, "@odata.type")) is { } owned)
                {
                    extra["collection"] = owned;
                }

                edges[Edge.NormalizeId(id)] = extra;
            }
        }

        return new EdgePrincipalState(true, edges);
    }

    public BatchRequest AddRequest(Edge edge, GraphApi api)
    {
        ArgumentNullException.ThrowIfNull(edge);
        string collection = RequireCollection(edge, null);
        return new BatchRequest("POST", "/" + collection + "/" + GraphPath.Segment(edge.TargetId) + "/owners/$ref", EdgeReads.RefBody(api, "directoryObjects", edge.PrincipalId));
    }

    public BatchRequest RemoveRequest(Edge edge, JsonObject? liveExtra, GraphApi api)
    {
        ArgumentNullException.ThrowIfNull(edge);
        string collection = RequireCollection(edge, liveExtra);
        return new BatchRequest("DELETE", "/" + collection + "/" + GraphPath.Segment(edge.TargetId) + "/owners/" + GraphPath.Segment(edge.PrincipalId) + "/$ref");
    }

    public JsonNode RecordEntry(Edge edge, JsonObject? applied)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return new JsonObject { ["id"] = edge.TargetId, ["collection"] = edge.TargetCollection };
    }

    public async Task<Edge?> FromRecordAsync(JsonNode? record, string principalId, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principalId);
        ArgumentNullException.ThrowIfNull(context);

        string? id;
        string? collection = null;
        if (record is JsonObject obj)
        {
            id = JsonObjects.GetId(obj);
            collection = JsonObjects.GetString(obj, "collection");
        }
        else
        {
            id = JsonValues.TryGetString(record, out var text) ? text : null;
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        collection ??= await context.ResolveCollectionAsync(id, api, cancellationToken).ConfigureAwait(false);
        return collection is null ? null : new Edge(Kind, id, principalId, collection);
    }

    public string Describe(Edge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return $"owner {edge.PrincipalId} of {edge.TargetCollection ?? "object"} {edge.TargetId}";
    }

    private static string RequireCollection(Edge edge, JsonObject? liveExtra)
    {
        return edge.TargetCollection
            ?? JsonObjects.GetString(liveExtra, "collection")
            ?? throw new InvalidOperationException($"The ownership edge {edge} has no target collection; it must be resolved before building a request.");
    }
}
