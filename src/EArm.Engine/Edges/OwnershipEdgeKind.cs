using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Engine.Providers;

namespace EArm.Engine.Edges;

/// <summary>
/// Ownership: target side <c>owners</c> on applications, service principals and groups
/// (authoritative set), principal side <c>ownerOf</c> on users and service principals. The target's Graph
/// collection comes from the template resource when the target is declared in it, else from
/// <c>GET /directoryObjects/{id}?$select=id</c> (<c>@odata.type</c>). Live reads: <c>GET /{collection}/{t}/owners</c> (plus <c>/owners/microsoft.graph.servicePrincipal</c> for a group)
/// for the target, <c>GET /{users|servicePrincipals}/{p}/ownedObjects</c> for the principal. Writes:
/// <c>POST /{collection}/{t}/owners/$ref</c>, <c>DELETE /{collection}/{t}/owners/{p}/$ref</c>.
/// </summary>
public sealed class OwnershipEdgeKind : IEdgeKindHandler
{
    private const string ServicePrincipalCast = "microsoft.graph.servicePrincipal";

    private static readonly HashSet<string> OwnableCollections = new(StringComparer.Ordinal) { "applications", "servicePrincipals", "groups" };

    public EdgeKind Kind => EdgeKind.Ownership;

    public EdgePhase Phase => EdgePhase.A;

    public bool WarnOnSignedInPrincipalRemoval => true;

    public string? UnsupportedWithAppOnlyToken(string principalType)
    {
        ArgumentNullException.ThrowIfNull(principalType);

        // Learn: user-list-ownedobjects lists no application permission that works for GET /users/{id}/ownedObjects; service principals can be read.
        return string.Equals(EdgeGraphContext.CollectionOf(principalType), "users", StringComparison.Ordinal)
            ? "properties.ownerOf on a user needs GET /users/{id}/ownedObjects, which Microsoft Graph does not support with an application (app-only) token; the property was not applied. Declare the user in properties.owners of the application, service principal or group instead, or sign in as a user."
            : null;
    }

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
            edges = await ReadOwnersAsync(collection, targetId, api, context, cancellationToken).ConfigureAwait(false);
        }

        return new EdgeTargetState(true, false, collection, edges);
    }

    /// <summary>
    /// The live owners of a target. v1.0 <c>GET /groups/{g}/owners</c> leaves service principal owners out (Learn, group-list-owners: "service principals are
    /// not listed as group owners due to the staged rollout of service principals to the Microsoft Graph v1.0 endpoint"), so for a group they are read through
    /// the type cast <c>/groups/{g}/owners/microsoft.graph.servicePrincipal</c> and merged in, as for group members. Application and service principal owners
    /// carry no such note and are read whole.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, JsonObject?>> ReadOwnersAsync(string collection, string targetId, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        string path = "/" + collection + "/" + GraphPath.Segment(targetId) + "/owners";
        var owners = await EdgeReads.ReadIdsAsync(context, path, api, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(collection, "groups", StringComparison.Ordinal))
        {
            return owners;
        }

        foreach (var (id, extra) in await EdgeReads.ReadIdsAsync(context, path + "/" + ServicePrincipalCast, api, cancellationToken).ConfigureAwait(false))
        {
            if (owners.ContainsKey(id))
            {
                continue;
            }

            // A type-cast listing may leave the element type implicit; the cast says what it is.
            var typed = extra ?? new JsonObject();
            if (JsonObjects.GetString(typed, "@odata.type") is null)
            {
                typed["@odata.type"] = "#" + ServicePrincipalCast;
            }

            owners[id] = typed;
        }

        return owners;
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
