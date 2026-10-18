using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine.Edges;
using EArm.Engine.Providers;
using EArm.Graph;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers.Edges;

/// <summary>
/// A policy assigned to an application or service principal through a <c>$ref</c> navigation (token lifetime, claims mapping and app management
/// policies): the principal-side property (<c>tokenLifetimePolicies: ["[reference('Web sessions').id]"]</c>) is ensure-only like every principal-side
/// edge, so a policy that is no longer declared stays assigned.
/// <para>
/// Reads: <c>GET /{applications|servicePrincipals}/{id}/{navigation}?$select=id</c> from the principal's side; the policy itself is answered from the
/// template when it was deployed in this run, else <c>GET /policies/{collection}/{id}</c>. Writes: <c>POST /{applications|servicePrincipals}/{id}/{navigation}/$ref</c>
/// with <c>{ "@odata.id": "…/policies/{collection}/{policyId}" }</c>; the remove (never planned from the principal side) is
/// <c>DELETE …/{navigation}/{policyId}/$ref</c>. The principal's collection travels in <see cref="Edge.Extra"/> (<c>principalCollection</c>).
/// </para>
/// <para>
/// When Graph allows one policy of the kind per object (<see cref="OnePerPrincipal"/>), a principal that already holds another policy of the kind, or that
/// declares two, is an invalid item: Graph would refuse the add, and EARM never removes the other assignment for you.
/// </para>
/// </summary>
public abstract class PolicyAssignmentEdgeKind : IEdgeKindHandler
{
    /// <summary>The <see cref="Edge.Extra"/> member that carries the principal's Graph collection.</summary>
    public const string PrincipalCollectionProperty = "principalCollection";

    private readonly IReadOnlyList<string> _principalCollections;

    /// <param name="kind">The edge kind.</param>
    /// <param name="navigation">The navigation property on the principal (<c>tokenLifetimePolicies</c>), also the template property name.</param>
    /// <param name="policyCollection">The policy collection without leading slash (<c>policies/tokenLifetimePolicies</c>).</param>
    /// <param name="noun">One policy in messages (<c>token lifetime policy</c>).</param>
    /// <param name="principalCollections">The principal collections Graph documents the assignment for (<c>applications</c>, <c>servicePrincipals</c>).</param>
    /// <param name="onePerPrincipal">Graph allows one policy of the kind per principal.</param>
    protected PolicyAssignmentEdgeKind(EdgeKind kind, string navigation, string policyCollection, string noun, IReadOnlyList<string> principalCollections, bool onePerPrincipal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(navigation);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyCollection);
        ArgumentException.ThrowIfNullOrWhiteSpace(noun);
        ArgumentNullException.ThrowIfNull(principalCollections);
        Kind = kind;
        Navigation = navigation;
        PolicyCollection = policyCollection.Trim('/');
        Noun = noun;
        _principalCollections = principalCollections;
        OnePerPrincipal = onePerPrincipal;
    }

    public EdgeKind Kind { get; }

    public EdgePhase Phase => EdgePhase.B;

    /// <summary>The navigation property on the principal.</summary>
    public string Navigation { get; }

    /// <summary>The policy collection, without leading slash.</summary>
    public string PolicyCollection { get; }

    /// <summary>One policy in messages.</summary>
    public string Noun { get; }

    /// <summary>Graph allows one policy of the kind per principal.</summary>
    public bool OnePerPrincipal { get; }

    public async Task<EdgeItem> ResolveItemAsync(JsonNode? item, EdgeSide side, ResourceInstance declarer, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(declarer);
        ArgumentNullException.ThrowIfNull(context);
        if (side == EdgeSide.Target)
        {
            return EdgeItem.Invalid($"a {Noun} is assigned from the application or service principal ({Navigation}); the policy has no target-side property.");
        }

        var resolved = EdgeReads.StringId(item, $"a {Noun} object id");
        if (resolved.IsUnknown || resolved.Error is not null || resolved.OtherId is null)
        {
            return resolved;
        }

        string? collection = EdgeGraphContext.CollectionOf(declarer.Type);
        if (collection is null || !_principalCollections.Contains(collection, StringComparer.Ordinal))
        {
            return EdgeItem.Invalid($"a {Noun} can only be assigned to {string.Join(" or ", _principalCollections)}.");
        }

        if (OnePerPrincipal)
        {
            if (DeclaredCount(declarer) > 1)
            {
                return EdgeItem.Invalid($"Graph allows one {Noun} per {Singular(collection)}; declare one.");
            }

            if (context.LiveObjectOf(declarer) is { } live && JsonObjects.GetId(live) is { } selfId)
            {
                var held = await ReadAssignedAsync(collection, selfId, api, context, cancellationToken).ConfigureAwait(false);
                string other = held.Keys.FirstOrDefault(id => !Edge.IdEquals(id, resolved.OtherId)) ?? string.Empty;
                if (other.Length > 0)
                {
                    return EdgeItem.Invalid($"the {Singular(collection)} already has {Noun} {other} assigned and Graph allows one; remove that assignment first (EARM never removes it, principal-side edges are ensure-only).");
                }
            }
        }

        return new EdgeItem(resolved.OtherId, PolicyCollection, new JsonObject { [PrincipalCollectionProperty] = collection });
    }

    public async Task<EdgeTargetState> ReadTargetAsync(string targetId, string? collectionHint, bool includeEdges, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(context);
        bool exists = (context.FindInstanceByGraphId(targetId) is { } instance && context.LiveObjectOf(instance) is not null)
            || await context.GetAsync(new GraphRequest("/" + PolicyCollection + "/" + GraphPath.Segment(targetId), api).WithSelect("id"), cancellationToken).ConfigureAwait(false) is JsonObject;
        if (!exists)
        {
            return EdgeTargetState.Missing;
        }

        IReadOnlyDictionary<string, JsonObject?>? edges = null;
        if (includeEdges)
        {
            edges = await EdgeReads.ReadIdsAsync(context, "/" + PolicyCollection + "/" + GraphPath.Segment(targetId) + "/appliesTo", api, cancellationToken).ConfigureAwait(false);
        }

        return new EdgeTargetState(true, false, PolicyCollection, edges);
    }

    public async Task<EdgePrincipalState> ReadPrincipalAsync(string principalId, string principalType, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principalId);
        ArgumentNullException.ThrowIfNull(principalType);
        ArgumentNullException.ThrowIfNull(context);
        string collection = EdgeGraphContext.CollectionOf(principalType) ?? _principalCollections[0];
        return new EdgePrincipalState(true, await ReadAssignedAsync(collection, principalId, api, context, cancellationToken).ConfigureAwait(false));
    }

    public BatchRequest AddRequest(Edge edge, GraphApi api)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return new BatchRequest("POST", PrincipalPath(edge) + "/" + Navigation + "/$ref", EdgeReads.RefBody(api, PolicyCollection, edge.TargetId));
    }

    public BatchRequest RemoveRequest(Edge edge, JsonObject? liveExtra, GraphApi api)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return new BatchRequest("DELETE", PrincipalPath(edge) + "/" + Navigation + "/" + GraphPath.Segment(edge.TargetId) + "/$ref");
    }

    public string Describe(Edge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return $"{Noun} {edge.TargetId} assigned to {Singular(PrincipalCollectionOf(edge))} {edge.PrincipalId}";
    }

    private string PrincipalPath(Edge edge)
    {
        return "/" + PrincipalCollectionOf(edge) + "/" + GraphPath.Segment(edge.PrincipalId);
    }

    private string PrincipalCollectionOf(Edge edge)
    {
        return JsonObjects.GetString(edge.Extra, PrincipalCollectionProperty) ?? _principalCollections[0];
    }

    private async Task<Dictionary<string, JsonObject?>> ReadAssignedAsync(string collection, string principalId, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        var items = await context.GetAllAsync(new GraphRequest("/" + collection + "/" + GraphPath.Segment(principalId) + "/" + Navigation, api).WithSelect("id"), cancellationToken).ConfigureAwait(false);
        var edges = new Dictionary<string, JsonObject?>(StringComparer.OrdinalIgnoreCase);
        foreach (var policy in items)
        {
            if (JsonObjects.GetId(policy) is { } id)
            {
                edges[Edge.NormalizeId(id)] = null;
            }
        }

        return edges;
    }

    private int DeclaredCount(ResourceInstance declarer)
    {
        return declarer.Declaration.Properties is JsonObject properties
            && JsonObjects.TryGetIgnoreCase(properties, Navigation, out var node)
            && node is JsonArray array
                ? array.Count
                : 1;
    }

    private static string Singular(string collection)
    {
        return collection switch
        {
            "applications" => "application",
            "servicePrincipals" => "service principal",
            _ => collection,
        };
    }
}
