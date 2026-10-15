using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Engine.Edges;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers.Edges;

/// <summary>
/// App role assignments as an edge kind (part B): the principal-side <c>appRoleAssignments</c>
/// property of users, groups and service principals, <c>[{ resourceId | resourceAppId, appRole | appRoleId }]</c>.
/// Each item resolves to (principal = the declarer, resource service principal, app role id); the edge's
/// <see cref="Edge.TargetId"/> is the composite <c>{resourceId}/{appRoleId}</c> (a principal may hold several roles
/// on one resource, and the edge pass keys principal-side existence and tracked removals by target), the real ids
/// travel in <see cref="Edge.Extra"/> (<c>resourceId</c>, <c>appRoleId</c>, and <c>id</c> for recorded edges).
/// <para>
/// Reads: <c>GET /{users|groups|servicePrincipals}/{principalId}/appRoleAssignments?$top=999</c> (every page) from
/// the principal's side; the resource is answered from the template when it was deployed in this run, else
/// <c>GET /servicePrincipals(appId='…')</c> / <c>GET /servicePrincipals/{id}</c> with the summary select. Writes:
/// <c>POST /servicePrincipals/{resourceId}/appRoleAssignedTo { principalId, resourceId, appRoleId }</c>,
/// <c>DELETE /servicePrincipals/{resourceId}/appRoleAssignedTo/{id}</c>. Validation: an unknown role value or id, a
/// disabled role, or a role whose <c>allowedMemberTypes</c> excludes the declarer's kind (users and groups are
/// <c>User</c>, service principals <c>Application</c>) is an invalid item.
/// </para>
/// <para>
/// De-duplication with the standalone <c>Microsoft.Entra/appRoleAssignments</c> resource: the object pass applies
/// the standalone resource before the edge pass runs, so the principal-side item finds its assignment in place and
/// the edge pass neither writes nor records it; the standalone resource owns the assignment (Complete mode deletes
/// it when the resource is removed, Incremental mode detaches it), and removing only the principal-side item changes
/// nothing. The state records <c>{ principalId, resourceId, appRoleId, id }</c> under
/// <c>relationships.appRoleAssignments</c> for edges the pass created itself.
/// </para>
/// </summary>
[EntraEdgeKind(EdgeKind.AppRoleAssignment)]
public sealed class AppRoleAssignmentEdgeKind : IEdgeKindHandler
{
    private const string Collection = "servicePrincipals";

    public EdgeKind Kind => EdgeKind.AppRoleAssignment;

    public EdgePhase Phase => EdgePhase.B;

    // ---- declared items ----

    public async Task<EdgeItem> ResolveItemAsync(JsonNode? item, EdgeSide side, ResourceInstance declarer, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(declarer);
        ArgumentNullException.ThrowIfNull(context);

        if (side == EdgeSide.Target)
        {
            return EdgeItem.Invalid("app role assignments are declared on the principal (appRoleAssignments on users, groups and service principals) or as Microsoft.Entra/appRoleAssignments resources; a resource service principal has no target-side property.");
        }

        if (UnknownJson.IsUnknown(item, out var unknownItem))
        {
            return EdgeItem.Unknown(unknownItem.Symbol);
        }

        if (item is not JsonObject obj)
        {
            return EdgeItem.Invalid($"expected an object {{ resourceId | resourceAppId, appRole | appRoleId }}, got {JsonValues.Describe(item)}.");
        }

        var declared = AppRoleAssignmentsRules.Parse(obj, principalAllowed: false, out var error);
        if (declared is null)
        {
            return EdgeItem.Invalid(error!);
        }

        if (declared.HasUnknown)
        {
            return EdgeItem.Unknown(declared.UnknownSymbol!);
        }

        ServicePrincipalSummary? resource;
        if (declared.ResourceAppId is { } resourceAppId)
        {
            resource = TemplateServicePrincipalByAppId(context, resourceAppId)
                ?? await context.WithRetryAsync(ct => ServicePrincipalLookup.FindByAppIdAsync(context.Graph, resourceAppId, api, RetryHint.Default, ct), RetryHint.Default, cancellationToken).ConfigureAwait(false);
            if (resource is null)
            {
                return EdgeItem.Invalid($"no service principal exists for application id '{resourceAppId}' (resourceAppId); deploy a Microsoft.Entra/servicePrincipals resource for it, or declare resourceId.");
            }

            if (declared.ResourceId is { } declaredResourceId && !JsonObjects.KeyEquals(declaredResourceId, resource.Id))
            {
                return EdgeItem.Invalid($"resourceAppId '{resourceAppId}' resolves to service principal {resource.Id}, which differs from the declared resourceId {declaredResourceId}; declare one of them.");
            }
        }
        else
        {
            string resourceId = declared.ResourceId!;
            resource = TemplateServicePrincipalById(context, resourceId)
                ?? (await context.GetAsync(new GraphRequest("/" + Collection + "/" + GraphPath.Segment(resourceId), api).WithSelect(ServicePrincipalLookup.SelectedProperties), cancellationToken).ConfigureAwait(false) is JsonObject live ? ServicePrincipalLookup.Summarize(live) : null);
            if (resource is null)
            {
                return EdgeItem.Invalid($"'{resourceId}' is not an existing service principal (resourceId).");
            }
        }

        string? roleError = AppRoleAssignmentsRules.ResolveRole(resource, declared.AppRoleId, declared.AppRole, out var appRoleId, out var role);
        if (roleError is not null)
        {
            return EdgeItem.Invalid(roleError);
        }

        if (role is not null && AppRoleAssignmentsRules.CheckAssignable(role, AppRoleAssignmentsRules.MemberTypeOfResourceType(declarer.Type)) is { } reason)
        {
            return EdgeItem.Invalid(reason);
        }

        var extra = new JsonObject
        {
            ["resourceId"] = AppRoleAssignmentsRules.NormalizeId(resource.Id),
            ["appRoleId"] = appRoleId,
        };
        return new EdgeItem(AppRoleAssignmentsRules.TargetKey(resource.Id, appRoleId), Collection, extra);
    }

    /// <summary>A service principal deployed by this template, by application id (its resource name), without a Graph call.</summary>
    private static ServicePrincipalSummary? TemplateServicePrincipalByAppId(EdgeGraphContext context, string appId)
    {
        if (!ResourceIdentifier.TryCreate(ServicePrincipalsProvider.ResourceType, appId, out var id) || id is null)
        {
            return null;
        }

        return context.Resolver.TryGetLive(id, out var live, out _) ? TrySummarize(live) : null;
    }

    /// <summary>A service principal deployed by this template, by Graph id, without a Graph call.</summary>
    private static ServicePrincipalSummary? TemplateServicePrincipalById(EdgeGraphContext context, string resourceId)
    {
        if (context.FindInstanceByGraphId(resourceId) is { } instance
            && string.Equals(EdgeGraphContext.CollectionOf(instance.Type), Collection, StringComparison.Ordinal)
            && context.LiveObjectOf(instance) is { } live)
        {
            return TrySummarize(live);
        }

        return null;
    }

    private static ServicePrincipalSummary? TrySummarize(JsonObject live)
    {
        try
        {
            return ServicePrincipalLookup.Summarize(live);
        }
        catch (ArgumentException)
        {
            // A published object without id/appId (not a service principal after all): fall back to Graph.
            return null;
        }
    }

    // ---- live reads ----

    public async Task<EdgeTargetState> ReadTargetAsync(string targetId, string? collectionHint, bool includeEdges, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(context);

        if (!AppRoleAssignmentsRules.TryParseTargetKey(targetId, out var resourceId, out var appRoleId))
        {
            return EdgeTargetState.Missing;
        }

        bool exists = context.FindInstanceByGraphId(resourceId) is not null
            || await context.GetAsync(new GraphRequest("/" + Collection + "/" + GraphPath.Segment(resourceId), api).WithSelect("id"), cancellationToken).ConfigureAwait(false) is JsonObject;
        if (!exists)
        {
            return EdgeTargetState.Missing;
        }

        IReadOnlyDictionary<string, JsonObject?>? edges = null;
        if (includeEdges)
        {
            var assignments = await context.GetAllAsync(new GraphRequest(AppRoleAssignmentsLookup.AssignedToPath(resourceId), api).WithTop(999), cancellationToken).ConfigureAwait(false);
            var byPrincipal = new Dictionary<string, JsonObject?>(StringComparer.OrdinalIgnoreCase);
            foreach (var assignment in assignments)
            {
                if (JsonObjects.KeyEquals(JsonObjects.GetString(assignment, "appRoleId"), appRoleId) && JsonObjects.GetString(assignment, "principalId") is { } principalId)
                {
                    byPrincipal[Edge.NormalizeId(principalId)] = LiveExtra(assignment);
                }
            }

            edges = byPrincipal;
        }

        return new EdgeTargetState(true, false, Collection, edges);
    }

    public async Task<EdgePrincipalState> ReadPrincipalAsync(string principalId, string principalType, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principalId);
        ArgumentNullException.ThrowIfNull(principalType);
        ArgumentNullException.ThrowIfNull(context);

        string? collection = EdgeGraphContext.CollectionOf(principalType);
        if (collection is not ("users" or "groups" or "servicePrincipals"))
        {
            // Only users, groups and service principals hold app role assignments.
            return new EdgePrincipalState(true, new Dictionary<string, JsonObject?>(StringComparer.OrdinalIgnoreCase));
        }

        var edges = await ReadPrincipalAssignmentsAsync(collection, principalId, api, context, cancellationToken).ConfigureAwait(false);
        return new EdgePrincipalState(true, edges);
    }

    /// <summary><c>GET /{collection}/{principalId}/appRoleAssignments?$top=999</c> keyed by target key (<c>{resourceId}/{appRoleId}</c>).</summary>
    private static async Task<Dictionary<string, JsonObject?>> ReadPrincipalAssignmentsAsync(string collection, string principalId, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        var request = new GraphRequest("/" + collection + "/" + GraphPath.Segment(principalId) + "/appRoleAssignments", api).WithTop(999);
        var items = await context.GetAllAsync(request, cancellationToken).ConfigureAwait(false);
        var edges = new Dictionary<string, JsonObject?>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in items)
        {
            if (JsonObjects.GetString(assignment, "resourceId") is { } resourceId && JsonObjects.GetString(assignment, "appRoleId") is { } appRoleId)
            {
                edges[Edge.NormalizeId(AppRoleAssignmentsRules.TargetKey(resourceId, appRoleId))] = LiveExtra(assignment);
            }
        }

        return edges;
    }

    private static JsonObject LiveExtra(JsonObject assignment)
    {
        var extra = new JsonObject();
        if (JsonObjects.GetId(assignment) is { } id)
        {
            extra["id"] = id;
        }

        if (JsonObjects.GetString(assignment, "resourceId") is { } resourceId)
        {
            extra["resourceId"] = resourceId;
        }

        if (JsonObjects.GetString(assignment, "appRoleId") is { } appRoleId)
        {
            extra["appRoleId"] = appRoleId;
        }

        return extra;
    }

    // ---- writes ----

    public BatchRequest AddRequest(Edge edge, GraphApi api)
    {
        ArgumentNullException.ThrowIfNull(edge);
        var (resourceId, appRoleId) = Composite(edge);
        var body = new JsonObject
        {
            ["principalId"] = edge.PrincipalId,
            ["resourceId"] = resourceId,
            ["appRoleId"] = appRoleId,
        };
        return new BatchRequest("POST", AppRoleAssignmentsLookup.AssignedToPath(resourceId), body);
    }

    public BatchRequest RemoveRequest(Edge edge, JsonObject? liveExtra, GraphApi api)
    {
        ArgumentNullException.ThrowIfNull(edge);
        var (resourceId, _) = Composite(edge);
        string id = JsonObjects.GetId(liveExtra)
            ?? JsonObjects.GetId(edge.Extra)
            ?? throw new InvalidOperationException($"The app role assignment edge {Describe(edge)} has no assignment id; it must be read before it can be removed.");
        return new BatchRequest("DELETE", AppRoleAssignmentsLookup.AssignedToPath(resourceId) + "/" + GraphPath.Segment(id));
    }

    // ---- state ----

    public JsonNode RecordEntry(Edge edge, JsonObject? applied)
    {
        ArgumentNullException.ThrowIfNull(edge);
        var (resourceId, appRoleId) = Composite(edge);
        var record = new JsonObject
        {
            ["principalId"] = edge.PrincipalId,
            ["resourceId"] = resourceId,
            ["appRoleId"] = appRoleId,
        };

        string? id = JsonObjects.GetId(applied) ?? JsonObjects.GetId(edge.Extra);
        if (id is not null)
        {
            record["id"] = id;
        }

        return record;
    }

    public async Task<Edge?> FromRecordAsync(JsonNode? record, string principalId, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principalId);
        ArgumentNullException.ThrowIfNull(context);

        if (record is not JsonObject obj || JsonObjects.GetString(obj, "resourceId") is not { } resourceId || JsonObjects.GetString(obj, "appRoleId") is not { } appRoleId
            || !AppRoleAssignmentsRules.IsGuid(resourceId) || !AppRoleAssignmentsRules.IsGuid(appRoleId))
        {
            return null;
        }

        string? id = JsonObjects.GetId(obj);
        if (id is null)
        {
            // Recorded without the assignment id (a rerun that found the edge in place): look it up from the principal.
            string? collection = await context.ResolveCollectionAsync(principalId, api, cancellationToken).ConfigureAwait(false);
            if (collection is "users" or "groups" or "servicePrincipals")
            {
                var live = await ReadPrincipalAssignmentsAsync(collection, principalId, api, context, cancellationToken).ConfigureAwait(false);
                if (live.TryGetValue(Edge.NormalizeId(AppRoleAssignmentsRules.TargetKey(resourceId, appRoleId)), out var extra))
                {
                    id = JsonObjects.GetId(extra);
                }
            }

            if (id is null)
            {
                // Not in place any more: nothing to remove, and nothing to interpret.
                return null;
            }
        }

        var recorded = new JsonObject
        {
            ["resourceId"] = AppRoleAssignmentsRules.NormalizeId(resourceId),
            ["appRoleId"] = AppRoleAssignmentsRules.NormalizeId(appRoleId),
            ["id"] = id,
        };
        return new Edge(Kind, AppRoleAssignmentsRules.TargetKey(resourceId, appRoleId), principalId, Collection, recorded);
    }

    /// <summary>GET the recorded assignment by id: it must be the assignment of this principal to the recorded resource and app role (a tampered state could name anybody's assignment).</summary>
    public async Task<bool> VerifyRecordedAsync(Edge edge, string principalId, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentNullException.ThrowIfNull(context);
        string? id = JsonObjects.GetId(edge.Extra);
        if (id is null)
        {
            return false;
        }

        var (resourceId, appRoleId) = Composite(edge);
        JsonNode? live;
        try
        {
            live = await context.GetAsync(new GraphRequest(AppRoleAssignmentsLookup.AssignedToPath(resourceId) + "/" + GraphPath.Segment(id), api), cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            return false;
        }

        return live is JsonObject assignment
            && Edge.IdEquals(JsonObjects.GetString(assignment, "principalId"), principalId)
            && Edge.IdEquals(JsonObjects.GetString(assignment, "appRoleId"), appRoleId)
            && (JsonObjects.GetString(assignment, "resourceId") is not { } liveResource || Edge.IdEquals(liveResource, resourceId));
    }

    // ---- identity ----

    public string IdentityOf(Edge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        var (resourceId, appRoleId) = Composite(edge);
        return AppRoleAssignmentsRules.Identity(edge.PrincipalId, resourceId, appRoleId);
    }

    public IEnumerable<string> GraphIdsOf(Edge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        var (resourceId, _) = Composite(edge);
        return [resourceId, edge.PrincipalId];
    }

    public string Describe(Edge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        var (resourceId, appRoleId) = Composite(edge);
        return $"app role {appRoleId} on service principal {resourceId} assigned to {edge.PrincipalId}";
    }

    /// <summary>The (resourceId, appRoleId) of an edge: from the composite target key, else from the extras.</summary>
    private static (string ResourceId, string AppRoleId) Composite(Edge edge)
    {
        if (AppRoleAssignmentsRules.TryParseTargetKey(edge.TargetId, out var resourceId, out var appRoleId))
        {
            return (resourceId, appRoleId);
        }

        string? extraResource = JsonObjects.GetString(edge.Extra, "resourceId") ?? edge.TargetId;
        string? extraRole = JsonObjects.GetString(edge.Extra, "appRoleId");
        if (!AppRoleAssignmentsRules.IsGuid(extraResource) || !AppRoleAssignmentsRules.IsGuid(extraRole))
        {
            throw new InvalidOperationException($"The app role assignment edge {edge} carries no (resourceId, appRoleId) composite.");
        }

        return (AppRoleAssignmentsRules.NormalizeId(extraResource!), AppRoleAssignmentsRules.NormalizeId(extraRole!));
    }
}
