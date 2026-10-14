using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Az;
using EntraArm.Engine.Providers;

namespace EntraArm.Engine.Edges;

/// <summary>
/// The strategy for one <see cref="EdgeKind"/>: how a declared item becomes an edge, how the live state is read
/// from the target's side (authoritative sets) and from the principal's side (existence checks for principal-side
/// declarations), the Graph requests that add and remove an edge, and what the state records for a principal-side
/// edge. Part A kinds ship with the engine (<see cref="EdgeKindRegistry.Default"/>); WI-12 (app role assignments)
/// and WI-15 (directory roles) register their own handlers for the part B kinds.
/// </summary>
public interface IEdgeKindHandler
{
    EdgeKind Kind { get; }

    EdgePhase Phase { get; }

    /// <summary>A single-valued relationship (<c>manager</c>): the property holds one id or <c>null</c>, not an array.</summary>
    bool IsSingleValued => false;

    /// <summary>True when the target may be dynamic (groups, administrative units), in which case manual edges are a validation error.</summary>
    bool RejectsDynamicTargets => false;

    /// <summary>True when removing an edge may strip the signed-in principal of access (ownership): the planner warns.</summary>
    bool WarnOnSignedInPrincipalRemoval => false;

    /// <summary>
    /// Normalises one declared item (a string id for part A kinds, an object for part B kinds) into the id on the
    /// other side of the edge plus extras. A deploy-time placeholder yields <see cref="EdgeItem.IsUnknown"/>; an
    /// invalid item yields <see cref="EdgeItem.Error"/>. Part B handlers resolve names (app role values, role names)
    /// here.
    /// </summary>
    Task<EdgeItem> ResolveItemAsync(JsonNode? item, EdgeSide side, ResourceInstance declarer, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a target: existence, dynamic flag, collection and, when <paramref name="includeEdges"/>, the
    /// principals attached to it (the authoritative-set read). Template objects published to the resolver are
    /// answered without a Graph call where possible.
    /// </summary>
    Task<EdgeTargetState> ReadTargetAsync(string targetId, string? collectionHint, bool includeEdges, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken);

    /// <summary>Reads the targets a principal is attached to from the principal's side (<c>memberOf</c>, <c>ownedObjects</c>, <c>manager</c>).</summary>
    Task<EdgePrincipalState> ReadPrincipalAsync(string principalId, string principalType, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken);

    /// <summary>The request that creates the edge (<c>POST .../$ref</c>, <c>PUT .../manager/$ref</c>, assignment <c>POST</c>).</summary>
    BatchRequest AddRequest(Edge edge, GraphApi api);

    /// <summary>The request that removes the edge; <paramref name="liveExtra"/> is what the live read returned for it (assignment ids).</summary>
    BatchRequest RemoveRequest(Edge edge, JsonObject? liveExtra, GraphApi api);

    /// <summary>What the state records for a principal-side edge (<paramref name="applied"/> is the add response body, for assignment ids).</summary>
    JsonNode RecordEntry(Edge edge, JsonObject? applied)
    {
        return JsonValue.Create(edge.TargetId);
    }

    /// <summary>Rebuilds an edge from a recorded state entry, for removal; <c>null</c> when the record cannot be interpreted.</summary>
    Task<Edge?> FromRecordAsync(JsonNode? record, string principalId, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        if (record is JsonValue value && value.TryGetValue<string>(out var id) && !string.IsNullOrWhiteSpace(id))
        {
            return Task.FromResult<Edge?>(new Edge(Kind, id, principalId));
        }

        return Task.FromResult<Edge?>(null);
    }

    /// <summary>The identity used for de-duplication; part B kinds include the assignment-defining extras.</summary>
    string IdentityOf(Edge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return $"{Kind}|{Edge.NormalizeId(edge.TargetId)}|{Edge.NormalizeId(edge.PrincipalId)}";
    }

    /// <summary>
    /// The Graph object ids on the two sides of the edge, for the replication-lag hint (an edge touching an object
    /// created in this run is written under <see cref="RetryHint.ExpectReplicationLag"/>). Part B kinds whose
    /// <see cref="Edge.TargetId"/> encodes an assignment (resource + role, role + scope) return the real object ids.
    /// </summary>
    IEnumerable<string> GraphIdsOf(Edge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return [edge.TargetId, edge.PrincipalId];
    }

    /// <summary>A short rendering for logs and messages.</summary>
    string Describe(Edge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return edge.ToString();
    }
}

/// <summary>Handlers keyed by edge kind. <see cref="Default"/> holds the part A kinds; later work items add part B.</summary>
public sealed class EdgeKindRegistry
{
    private readonly Dictionary<EdgeKind, IEdgeKindHandler> _handlers = [];

    /// <summary>A registry with the part A handlers (group membership, ownership, AU membership, manager).</summary>
    public static EdgeKindRegistry Default
    {
        get
        {
            var registry = new EdgeKindRegistry();
            registry.Register(new GroupMembershipEdgeKind());
            registry.Register(new OwnershipEdgeKind());
            registry.Register(new AdministrativeUnitMembershipEdgeKind());
            registry.Register(new ManagerEdgeKind());
            return registry;
        }
    }

    public static EdgeKindRegistry Empty => new();

    public IReadOnlyCollection<IEdgeKindHandler> Handlers => _handlers.Values;

    public void Register(IEdgeKindHandler handler, bool replace = false)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!replace && _handlers.ContainsKey(handler.Kind))
        {
            throw new InvalidOperationException($"An edge handler for {handler.Kind} is already registered.");
        }

        _handlers[handler.Kind] = handler;
    }

    public bool TryGet(EdgeKind kind, out IEdgeKindHandler handler)
    {
        return _handlers.TryGetValue(kind, out handler!);
    }

    public bool Contains(EdgeKind kind)
    {
        return _handlers.ContainsKey(kind);
    }

    /// <summary>The registered kinds of a phase, in enum order.</summary>
    public IReadOnlyList<IEdgeKindHandler> OfPhase(EdgePhase phase)
    {
        return _handlers.Values.Where(h => h.Phase == phase).OrderBy(h => h.Kind).ToList();
    }
}
