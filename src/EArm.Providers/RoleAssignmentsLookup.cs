using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Engine.Edges;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>The outcome of resolving a name to an id: the id, or the reason it could not be resolved.</summary>
public sealed record NameResolution(string? Id, string? Error)
{
    public static NameResolution Found(string id)
    {
        return new NameResolution(id, null);
    }

    public static NameResolution Failed(string error)
    {
        return new NameResolution(null, error);
    }
}

/// <summary>
/// The Graph reads directory role assignments need, shared by the standalone provider and the <c>directoryRoles</c>
/// edge kind: role definitions and administrative units by display name, a principal's assignments
/// (<c>GET /roleManagement/directory/roleAssignments?$filter=principalId eq '…'</c>, every page, matched client-side),
/// and the principal checks (kind, role-assignable group). Never cached.
/// </summary>
public static class RoleAssignmentsLookup
{
    /// <summary>The properties selected on role definition reads.</summary>
    public static readonly string[] DefinitionProperties = ["id", "displayName", "isBuiltIn", "templateId", "isEnabled"];

    /// <summary><c>/roleManagement/directory/roleAssignments/{id}</c>.</summary>
    public static string AssignmentPath(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return RoleAssignmentsRules.AssignmentsPath + "/" + GraphPath.Segment(id);
    }

    /// <summary><c>/roleManagement/directory/roleDefinitions/{id}</c>.</summary>
    public static string DefinitionPath(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return RoleAssignmentsRules.DefinitionsPath + "/" + GraphPath.Segment(id);
    }

    /// <summary><c>GET /roleManagement/directory/roleDefinitions?$filter=displayName eq '…'</c> (every page), matched client-side on the name.</summary>
    public static Task<IReadOnlyList<JsonObject>> FindRoleDefinitionsByNameAsync(IGraphClient graph, string displayName, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        return FindByDisplayNameAsync(graph, RoleAssignmentsRules.DefinitionsPath, displayName, DefinitionProperties, api, hint, cancellationToken);
    }

    /// <summary><c>GET /directory/administrativeUnits?$filter=displayName eq '…'</c> (every page), matched client-side on the name.</summary>
    public static Task<IReadOnlyList<JsonObject>> FindAdministrativeUnitsByNameAsync(IGraphClient graph, string displayName, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        return FindByDisplayNameAsync(graph, "/directory/administrativeUnits", displayName, ["id", "displayName"], api, hint, cancellationToken);
    }

    private static async Task<IReadOnlyList<JsonObject>> FindByDisplayNameAsync(IGraphClient graph, string collection, string displayName, string[] select, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        string filter = "displayName eq '" + displayName.Replace("'", "''", StringComparison.Ordinal) + "'";
        var request = new GraphRequest(collection, api, hint: hint).WithFilter(filter).WithSelect(select);
        var matches = new List<JsonObject>();
        await foreach (var item in graph.GetAllPagesAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (item is JsonObject obj && string.Equals(JsonObjects.GetString(obj, "displayName"), displayName, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(obj);
            }
        }

        return matches;
    }

    /// <summary>
    /// Resolves a <c>role</c> value to a role definition id: a GUID or a built-in display name needs no request
    /// (<see cref="RoleAssignmentsRules.ResolveRoleOffline"/>); any other text is a custom role's display name,
    /// looked up in the tenant (0 or several hits are errors).
    /// </summary>
    public static async Task<NameResolution> ResolveRoleAsync(IGraphClient graph, string role, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        if (RoleAssignmentsRules.ResolveRoleOffline(role) is { } offline)
        {
            return NameResolution.Found(offline);
        }

        var matches = await FindRoleDefinitionsByNameAsync(graph, role, api, hint, cancellationToken).ConfigureAwait(false);
        return matches.Count switch
        {
            0 => NameResolution.Failed($"no directory role named '{role}' exists in the tenant (neither built-in nor custom); declare a Microsoft.Entra/roleDefinitions resource for it, or use roleTemplate('<built-in name>') / the role definition id."),
            1 => NameResolution.Found(RoleAssignmentsRules.NormalizeId(JsonObjects.GetId(matches[0]) ?? string.Empty)),
            _ => NameResolution.Failed($"{matches.Count} directory roles are named '{role}' ({string.Join(", ", matches.Select(m => JsonObjects.GetId(m) ?? "?"))}); declare roleDefinitionId with the intended id instead."),
        };
    }

    /// <summary>
    /// Resolves an <c>administrativeUnit</c> value (a unit's object id, or its displayName looked up in the tenant)
    /// to the scope <c>/administrativeUnits/{id}</c>.
    /// </summary>
    public static async Task<NameResolution> ResolveAdministrativeUnitScopeAsync(IGraphClient graph, string administrativeUnit, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(administrativeUnit);
        if (RoleAssignmentsRules.IsGuid(administrativeUnit))
        {
            return NameResolution.Found(RoleAssignmentsRules.AdministrativeUnitScope(administrativeUnit));
        }

        var matches = await FindAdministrativeUnitsByNameAsync(graph, administrativeUnit, api, hint, cancellationToken).ConfigureAwait(false);
        return matches.Count switch
        {
            0 => NameResolution.Failed($"no administrative unit named '{administrativeUnit}' exists in the tenant; deploy a Microsoft.Entra/administrativeUnits resource for it (and depend on it), or declare directoryScopeId with the unit's id."),
            1 => NameResolution.Found(RoleAssignmentsRules.AdministrativeUnitScope(JsonObjects.GetId(matches[0]) ?? string.Empty)),
            _ => NameResolution.Failed($"{matches.Count} administrative units are named '{administrativeUnit}' ({string.Join(", ", matches.Select(m => JsonObjects.GetId(m) ?? "?"))}); declare directoryScopeId with the intended unit's id instead."),
        };
    }

    /// <summary>
    /// Lists <c>GET /roleManagement/directory/roleAssignments?$filter=principalId eq '{principalId}'</c> (every page).
    /// </summary>
    public static async Task<IReadOnlyList<JsonObject>> ReadPrincipalAssignmentsAsync(IGraphClient graph, string principalId, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        var request = new GraphRequest(RoleAssignmentsRules.AssignmentsPath, api, hint: hint).WithFilter("principalId eq '" + RoleAssignmentsRules.NormalizeId(principalId) + "'");
        var items = new List<JsonObject>();
        await foreach (var item in graph.GetAllPagesAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (item is JsonObject assignment && JsonObjects.KeyEquals(JsonObjects.GetString(assignment, "principalId"), principalId))
            {
                items.Add(assignment);
            }
        }

        return items;
    }

    /// <summary>The assignment of <paramref name="principalId"/> to <paramref name="roleDefinitionId"/> at <paramref name="directoryScopeId"/>, or <c>null</c>.</summary>
    public static async Task<JsonObject?> FindAssignmentAsync(IGraphClient graph, string principalId, string roleDefinitionId, string? directoryScopeId, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        var assignments = await ReadPrincipalAssignmentsAsync(graph, principalId, api, hint, cancellationToken).ConfigureAwait(false);
        return assignments.FirstOrDefault(a => RoleAssignmentsRules.Matches(a, roleDefinitionId, directoryScopeId));
    }

    /// <summary>
    /// The Graph collection a principal lives in (<c>users</c>, <c>groups</c>, <c>servicePrincipals</c>, …):
    /// <c>GET /directoryObjects/{id}?$select=id</c> and its <c>@odata.type</c>. <c>null</c> when the object does not
    /// exist (404 under the default hint) or has an unsupported type.
    /// </summary>
    public static async Task<string?> PrincipalCollectionAsync(IGraphClient graph, string principalId, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        var request = new GraphRequest("/directoryObjects/" + GraphPath.Segment(principalId), api, hint: hint).WithSelect("id");
        var node = await graph.GetAsync(request, cancellationToken).ConfigureAwait(false) as JsonObject;
        return node is null ? null : EdgeGraphContext.CollectionOfODataType(JsonObjects.GetString(node, "@odata.type")) ?? "directoryObjects";
    }

    /// <summary><c>GET /groups/{id}?$select=id,isAssignableToRole</c>: <c>true</c> / <c>false</c>, or <c>null</c> when the group does not exist.</summary>
    public static async Task<bool?> IsRoleAssignableGroupAsync(IGraphClient graph, string groupId, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        var request = new GraphRequest("/groups/" + GraphPath.Segment(groupId), api, hint: hint).WithSelect("id", "isAssignableToRole");
        var group = await graph.GetAsync(request, cancellationToken).ConfigureAwait(false) as JsonObject;
        return group is null ? null : IsRoleAssignable(group);
    }

    /// <summary>True when a group object carries <c>isAssignableToRole: true</c>.</summary>
    public static bool IsRoleAssignable(JsonObject? group)
    {
        return JsonObjects.TryGetIgnoreCase(group, "isAssignableToRole", out var node) && JsonValues.TryGetBool(node, out bool assignable) && assignable;
    }

    /// <summary>The reason a principal cannot hold a directory role, or <c>null</c> when it can: only users, role-assignable groups and service principals may.</summary>
    public static string? CheckPrincipalKind(string? collection, bool? roleAssignable, string principalId)
    {
        ArgumentNullException.ThrowIfNull(principalId);
        switch (collection?.ToLowerInvariant())
        {
            case "users":
            case "serviceprincipals":
                return null;
            case "groups":
                return roleAssignable == true
                    ? null
                    : $"group {principalId} is not role-assignable; a directory role can only be assigned to a group created with isAssignableToRole: true (the property is immutable, so recreate the group).";
            case null:
                return $"principal {principalId} does not exist.";
            default:
                return $"principal {principalId} is not a user, a role-assignable group or a service principal; nothing else can hold a directory role.";
        }
    }
}
