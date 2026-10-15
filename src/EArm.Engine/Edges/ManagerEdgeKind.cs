using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Engine.Providers;

namespace EArm.Engine.Edges;

/// <summary>
/// The single-valued <c>manager</c> edge on users: the target is the manager, the principal
/// the user. <c>null</c> removes the manager, an absent property leaves it untouched. Live read:
/// <c>GET /users/{u}/manager?$select=id</c> (404 = none). Writes: <c>PUT /users/{u}/manager/$ref</c>,
/// <c>DELETE /users/{u}/manager/$ref</c>.
/// </summary>
public sealed class ManagerEdgeKind : IEdgeKindHandler
{
    public EdgeKind Kind => EdgeKind.Manager;

    public EdgePhase Phase => EdgePhase.A;

    public bool IsSingleValued => true;

    public Task<EdgeItem> ResolveItemAsync(JsonNode? item, EdgeSide side, ResourceInstance declarer, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(declarer);
        if (side != EdgeSide.Principal || !string.Equals(EdgeGraphContext.CollectionOf(declarer.Type), "users", StringComparison.Ordinal))
        {
            return Task.FromResult(EdgeItem.Invalid($"'manager' can only be declared on Microsoft.Entra/users, not on {declarer.Type}."));
        }

        if (EdgeReads.IsNull(item))
        {
            return Task.FromResult(EdgeItem.Clear);
        }

        return Task.FromResult(EdgeReads.StringId(item, "the manager's user object id (or null to remove the manager)"));
    }

    public async Task<EdgeTargetState> ReadTargetAsync(string targetId, string? collectionHint, bool includeEdges, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(context);

        bool exists = context.FindInstanceByGraphId(targetId) is { } instance && string.Equals(EdgeGraphContext.CollectionOf(instance.Type), "users", StringComparison.Ordinal);
        if (!exists)
        {
            exists = await context.GetAsync(new GraphRequest("/users/" + GraphPath.Segment(targetId), api).WithSelect("id"), cancellationToken).ConfigureAwait(false) is JsonObject;
        }

        return exists ? new EdgeTargetState(true, false, "users", null) : EdgeTargetState.Missing;
    }

    public async Task<EdgePrincipalState> ReadPrincipalAsync(string principalId, string principalType, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principalId);
        ArgumentNullException.ThrowIfNull(context);

        JsonObject? manager;
        try
        {
            manager = await context.GetAsync(new GraphRequest("/users/" + GraphPath.Segment(principalId) + "/manager", api).WithSelect("id"), cancellationToken).ConfigureAwait(false) as JsonObject;
        }
        catch (GraphException ex) when (ex.IsForbidden)
        {
            return EdgePrincipalState.Skipped($"reading the manager of user {principalId} (GET /users/{{id}}/manager) was refused ({(int)ex.Status} {ex.Code}); the manager relationship was not applied. Grant User.Read.All (or Directory.Read.All) to read it.");
        }

        var edges = new Dictionary<string, JsonObject?>(StringComparer.OrdinalIgnoreCase);
        if (JsonObjects.GetId(manager) is { } id)
        {
            edges[Edge.NormalizeId(id)] = null;
        }

        return new EdgePrincipalState(true, edges);
    }

    public BatchRequest AddRequest(Edge edge, GraphApi api)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return new BatchRequest("PUT", "/users/" + GraphPath.Segment(edge.PrincipalId) + "/manager/$ref", EdgeReads.RefBody(api, "users", edge.TargetId));
    }

    public BatchRequest RemoveRequest(Edge edge, JsonObject? liveExtra, GraphApi api)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return new BatchRequest("DELETE", "/users/" + GraphPath.Segment(edge.PrincipalId) + "/manager/$ref");
    }

    public string Describe(Edge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return $"manager {edge.TargetId} of user {edge.PrincipalId}";
    }
}
