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
/// Directory role assignments as an edge kind (part B): the principal-side <c>directoryRoles</c>
/// property of users, role-assignable groups and service principals, <c>[{ role | roleDefinitionId, directoryScopeId
/// | administrativeUnit }]</c>. Each item resolves to (principal = the declarer, role definition id, directory scope);
/// the edge's <see cref="Edge.TargetId"/> is the composite <c>{roleDefinitionId}@{directoryScopeId}</c> (a principal
/// may hold one role at several scopes, and the edge pass keys principal-side existence and tracked removals by
/// target), the real ids travel in <see cref="Edge.Extra"/> (<c>roleDefinitionId</c>, <c>directoryScopeId</c>, and
/// <c>id</c> for recorded edges).
/// <para>
/// Resolution: <c>role</c> is a GUID (template or custom role id, no request), a built-in display name
/// (<see cref="WellKnownIds.RoleTemplates"/>, no request), the name of a <c>Microsoft.Entra/roleDefinitions</c>
/// resource of this template (answered from the resolver, no request) or a custom role name looked up with
/// <c>GET /roleManagement/directory/roleDefinitions?$filter=displayName eq '…'</c>; <c>administrativeUnit</c> is a
/// unit's id, the name of a <c>Microsoft.Entra/administrativeUnits</c> resource of this template, or a name looked up
/// with <c>GET /directory/administrativeUnits?$filter=displayName eq '…'</c>. A group declarer must be role-assignable
/// (its live or declared <c>isAssignableToRole</c>, else <c>GET /groups/{id}?$select=id,isAssignableToRole</c>);
/// otherwise the item is invalid and the row fails validation.
/// </para>
/// <para>
/// Reads: <c>GET /roleManagement/directory/roleAssignments?$filter=principalId eq '{principalId}'</c> (every page) from
/// the principal's side; the role definition's existence is answered from the template, the built-in table or
/// <c>GET /roleManagement/directory/roleDefinitions/{id}?$select=id</c> (plus the unit for a scoped assignment).
/// Writes: <c>POST /roleManagement/directory/roleAssignments { "@odata.type": "#microsoft.graph.unifiedRoleAssignment",
/// roleDefinitionId, principalId, directoryScopeId }</c>, <c>DELETE /roleManagement/directory/roleAssignments/{id}</c>.
/// </para>
/// <para>
/// De-duplication with the standalone <c>Microsoft.Entra/roleAssignments</c> resource: the object pass applies the
/// standalone resource before the edge pass runs, so the principal-side item finds its assignment in place and the
/// edge pass does not write it again; removing either declaration never removes the assignment (principal-side
/// edges are ensure-only; a retained assignment of a declared principal is reported, never deleted).
/// </para>
/// </summary>
[EntraEdgeKind(EdgeKind.DirectoryRole)]
public sealed class DirectoryRoleEdgeKind : IEdgeKindHandler
{
    /// <summary>The Graph collection the target (the role definition) lives in.</summary>
    private const string Collection = "roleManagement/directory/roleDefinitions";

    public EdgeKind Kind => EdgeKind.DirectoryRole;

    public EdgePhase Phase => EdgePhase.B;

    // ---- declared items ----

    public async Task<EdgeItem> ResolveItemAsync(JsonNode? item, EdgeSide side, ResourceInstance declarer, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(declarer);
        ArgumentNullException.ThrowIfNull(context);

        if (side == EdgeSide.Target)
        {
            return EdgeItem.Invalid("directory roles are declared on the principal (directoryRoles on users, role-assignable groups and service principals) or as Microsoft.Entra/roleAssignments resources; a role definition has no target-side property.");
        }

        if (UnknownJson.IsUnknown(item, out var unknownItem))
        {
            return EdgeItem.Unknown(unknownItem.Symbol);
        }

        if (item is not JsonObject obj)
        {
            return EdgeItem.Invalid($"expected an object {{ role | roleDefinitionId, directoryScopeId | administrativeUnit }}, got {JsonValues.Describe(item)}.");
        }

        var declared = RoleAssignmentsRules.Parse(obj, principalAllowed: false, out var error);
        if (declared is null)
        {
            return EdgeItem.Invalid(error!);
        }

        if (declared.HasUnknown)
        {
            return EdgeItem.Unknown(declared.UnknownSymbol!);
        }

        // The declarer's kind: users, service principals, and groups that are role-assignable.
        string? collection = EdgeGraphContext.CollectionOf(declarer.Type);
        if (collection == "groups" && !await IsRoleAssignableAsync(declarer, api, context, cancellationToken).ConfigureAwait(false))
        {
            return EdgeItem.Invalid($"directoryRoles can only be declared on a role-assignable group (isAssignableToRole: true, immutable after create); {declarer.Describe()} is not role-assignable.");
        }

        if (collection is not ("users" or "groups" or "servicePrincipals"))
        {
            return EdgeItem.Invalid("only users, role-assignable groups and service principals can hold a directory role.");
        }

        // Role.
        string? roleDefinitionId = declared.RoleDefinitionId is { } declaredRole ? RoleAssignmentsRules.NormalizeId(declaredRole) : null;
        if (declared.Role is { } role)
        {
            var resolved = RoleAssignmentsRules.ResolveRoleOffline(role) is { } offline
                ? NameResolution.Found(offline)
                : TemplateRoleDefinition(context, role) is { } templateId
                    ? NameResolution.Found(templateId)
                    : await context.WithRetryAsync(ct => RoleAssignmentsLookup.ResolveRoleAsync(context.Graph, role, api, RetryHint.Default, ct), RetryHint.Default, cancellationToken).ConfigureAwait(false);
            if (resolved.Id is null)
            {
                return EdgeItem.Invalid(resolved.Error!);
            }

            if (roleDefinitionId is not null && !JsonObjects.KeyEquals(roleDefinitionId, resolved.Id))
            {
                return EdgeItem.Invalid($"role '{role}' resolves to role definition {resolved.Id}, which differs from the declared roleDefinitionId {roleDefinitionId}; declare one of them.");
            }

            roleDefinitionId = resolved.Id;
        }

        // Scope.
        string scope = RoleAssignmentsRules.NormalizeScope(declared.DirectoryScopeId);
        if (declared.AdministrativeUnit is { } unit)
        {
            var resolved = RoleAssignmentsRules.IsGuid(unit)
                ? NameResolution.Found(RoleAssignmentsRules.AdministrativeUnitScope(unit))
                : TemplateAdministrativeUnit(context, unit) is { } unitId
                    ? NameResolution.Found(RoleAssignmentsRules.AdministrativeUnitScope(unitId))
                    : await context.WithRetryAsync(ct => RoleAssignmentsLookup.ResolveAdministrativeUnitScopeAsync(context.Graph, unit, api, RetryHint.Default, ct), RetryHint.Default, cancellationToken).ConfigureAwait(false);
            if (resolved.Id is null)
            {
                return EdgeItem.Invalid(resolved.Error!);
            }

            if (declared.DirectoryScopeId is not null && !RoleAssignmentsRules.ScopeEquals(scope, resolved.Id))
            {
                return EdgeItem.Invalid($"administrativeUnit '{unit}' resolves to scope {resolved.Id}, which differs from the declared directoryScopeId '{declared.DirectoryScopeId}'; declare one of them.");
            }

            scope = resolved.Id;
        }

        var extra = new JsonObject
        {
            ["roleDefinitionId"] = roleDefinitionId,
            ["directoryScopeId"] = scope,
        };
        return new EdgeItem(RoleAssignmentsRules.TargetKey(roleDefinitionId!, scope), Collection, extra);
    }

    /// <summary>A custom role deployed by this template, by display name (its resource name), without a Graph call.</summary>
    private static string? TemplateRoleDefinition(EdgeGraphContext context, string displayName)
    {
        if (!ResourceIdentifier.TryCreate(RoleDefinitionsProvider.ResourceType, displayName, out var id) || id is null)
        {
            return null;
        }

        return context.Resolver.TryGetLive(id, out var live, out var graphId) && JsonObjects.GetId(live) is not null ? RoleAssignmentsRules.NormalizeId(graphId) : null;
    }

    /// <summary>An administrative unit deployed by this template, by display name (its resource name), without a Graph call.</summary>
    private static string? TemplateAdministrativeUnit(EdgeGraphContext context, string displayName)
    {
        if (!ResourceIdentifier.TryCreate(AdministrativeUnitsProvider.ResourceType, displayName, out var id) || id is null)
        {
            return null;
        }

        return context.Resolver.TryGetLive(id, out _, out var graphId) ? RoleAssignmentsRules.NormalizeId(graphId) : null;
    }

    /// <summary>
    /// Whether a group declarer is role-assignable: from its published live object, else from its declared
    /// properties (what-if before the group exists), else <c>GET /groups/{id}?$select=id,isAssignableToRole</c>.
    /// </summary>
    private static async Task<bool> IsRoleAssignableAsync(ResourceInstance declarer, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        if (context.LiveObjectOf(declarer) is { } live && JsonObjects.TryGetIgnoreCase(live, "isAssignableToRole", out var liveNode) && !JsonObjects.IsAbsent(liveNode))
        {
            return RoleAssignmentsLookup.IsRoleAssignable(live);
        }

        if (JsonObjects.TryGetIgnoreCase(declarer.Properties, "isAssignableToRole", out var declaredNode) && !JsonObjects.IsAbsent(declaredNode) && !UnknownJson.IsUnknown(declaredNode))
        {
            return RoleAssignmentsLookup.IsRoleAssignable(declarer.Properties);
        }

        if (context.Resolver.TryGetGraphId(declarer.Id, out var graphId) || context.Resolver.TryGetGraphId(declarer.PlannedId, out graphId))
        {
            return await context.GetAsync(new GraphRequest("/groups/" + GraphPath.Segment(graphId), api).WithSelect("id", "isAssignableToRole"), cancellationToken).ConfigureAwait(false) is JsonObject group
                && RoleAssignmentsLookup.IsRoleAssignable(group);
        }

        return false;
    }

    // ---- live reads ----

    public async Task<EdgeTargetState> ReadTargetAsync(string targetId, string? collectionHint, bool includeEdges, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(context);

        if (!RoleAssignmentsRules.TryParseTargetKey(targetId, out var roleDefinitionId, out var scope))
        {
            return EdgeTargetState.Missing;
        }

        bool roleExists = context.FindInstanceByGraphId(roleDefinitionId) is not null
            || IsBuiltInTemplate(roleDefinitionId)
            || await context.GetAsync(new GraphRequest(RoleAssignmentsLookup.DefinitionPath(roleDefinitionId), api).WithSelect("id"), cancellationToken).ConfigureAwait(false) is JsonObject;
        if (!roleExists)
        {
            return EdgeTargetState.Missing;
        }

        if (RoleAssignmentsRules.TryGetAdministrativeUnitId(scope, out var unitId))
        {
            bool unitExists = context.FindInstanceByGraphId(unitId) is not null
                || await context.GetAsync(new GraphRequest("/directory/administrativeUnits/" + GraphPath.Segment(unitId), api).WithSelect("id"), cancellationToken).ConfigureAwait(false) is JsonObject;
            if (!unitExists)
            {
                return EdgeTargetState.Missing;
            }
        }

        // No target-side property exists for this kind, so the principals attached to a role are never read.
        return new EdgeTargetState(true, false, Collection, null);
    }

    public async Task<EdgePrincipalState> ReadPrincipalAsync(string principalId, string principalType, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principalId);
        ArgumentNullException.ThrowIfNull(principalType);
        ArgumentNullException.ThrowIfNull(context);

        string? collection = EdgeGraphContext.CollectionOf(principalType);
        if (collection is not ("users" or "groups" or "servicePrincipals"))
        {
            // Only users, groups and service principals hold directory roles.
            return new EdgePrincipalState(true, new Dictionary<string, JsonObject?>(StringComparer.OrdinalIgnoreCase));
        }

        var edges = await ReadPrincipalAssignmentsAsync(principalId, api, context, cancellationToken).ConfigureAwait(false);
        return new EdgePrincipalState(true, edges);
    }

    /// <summary><c>GET /roleManagement/directory/roleAssignments?$filter=principalId eq '…'</c> keyed by target key (<c>{roleDefinitionId}@{directoryScopeId}</c>).</summary>
    private static async Task<Dictionary<string, JsonObject?>> ReadPrincipalAssignmentsAsync(string principalId, GraphApi api, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        var items = await context.WithRetryAsync(ct => RoleAssignmentsLookup.ReadPrincipalAssignmentsAsync(context.Graph, principalId, api, RetryHint.Default, ct), RetryHint.Default, cancellationToken).ConfigureAwait(false);
        var edges = new Dictionary<string, JsonObject?>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in items)
        {
            if (JsonObjects.GetString(assignment, "roleDefinitionId") is { } roleDefinitionId)
            {
                edges[Edge.NormalizeId(RoleAssignmentsRules.TargetKey(roleDefinitionId, JsonObjects.GetString(assignment, "directoryScopeId")))] = RoleAssignmentsRules.LiveExtra(assignment);
            }
        }

        return edges;
    }

    // ---- writes ----

    public BatchRequest AddRequest(Edge edge, GraphApi api)
    {
        ArgumentNullException.ThrowIfNull(edge);
        var (roleDefinitionId, scope) = Composite(edge);
        return new BatchRequest("POST", RoleAssignmentsRules.AssignmentsPath, RoleAssignmentsRules.AssignmentBody(roleDefinitionId, edge.PrincipalId, scope));
    }

    public BatchRequest RemoveRequest(Edge edge, JsonObject? liveExtra, GraphApi api)
    {
        ArgumentNullException.ThrowIfNull(edge);
        string id = JsonObjects.GetId(liveExtra)
            ?? JsonObjects.GetId(edge.Extra)
            ?? throw new InvalidOperationException($"The directory role edge {Describe(edge)} has no assignment id; it must be read before it can be removed.");
        return new BatchRequest("DELETE", RoleAssignmentsLookup.AssignmentPath(id));
    }

    // ---- identity ----

    public string IdentityOf(Edge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        var (roleDefinitionId, scope) = Composite(edge);
        return RoleAssignmentsRules.Identity(edge.PrincipalId, roleDefinitionId, scope);
    }

    public IEnumerable<string> GraphIdsOf(Edge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        var (roleDefinitionId, scope) = Composite(edge);
        var ids = new List<string> { edge.PrincipalId, roleDefinitionId };
        if (RoleAssignmentsRules.TryGetAdministrativeUnitId(scope, out var unitId))
        {
            ids.Add(unitId);
        }

        return ids;
    }

    public string Describe(Edge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        var (roleDefinitionId, scope) = Composite(edge);
        return $"directory role {roleDefinitionId} at scope {scope} assigned to {edge.PrincipalId}";
    }

    private static bool IsBuiltInTemplate(string roleDefinitionId)
    {
        foreach (var template in WellKnownIds.RoleTemplates)
        {
            if (JsonObjects.KeyEquals(template.Id, roleDefinitionId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The (roleDefinitionId, directoryScopeId) of an edge: from the composite target key, else from the extras.</summary>
    private static (string RoleDefinitionId, string DirectoryScopeId) Composite(Edge edge)
    {
        if (RoleAssignmentsRules.TryParseTargetKey(edge.TargetId, out var roleDefinitionId, out var scope))
        {
            return (roleDefinitionId, scope);
        }

        string? extraRole = JsonObjects.GetString(edge.Extra, "roleDefinitionId") ?? edge.TargetId;
        if (!RoleAssignmentsRules.IsGuid(extraRole))
        {
            throw new InvalidOperationException($"The directory role edge {edge} carries no (roleDefinitionId, directoryScopeId) composite.");
        }

        return (RoleAssignmentsRules.NormalizeId(extraRole!), RoleAssignmentsRules.NormalizeScope(JsonObjects.GetString(edge.Extra, "directoryScopeId")));
    }
}
