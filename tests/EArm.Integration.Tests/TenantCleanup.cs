using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;

namespace EArm.Integration.Tests;

/// <summary>
/// The prefix sweep of the cleanup guard. It finds every object whose name (<c>displayName</c>, <c>userPrincipalName</c>
/// or <c>uniqueName</c>) starts with the run prefix and deletes it in dependency order, then purges the soft-deleted
/// copies. Objects without the prefix are never listed for deletion, and every delete is re-checked against the prefix
/// right before it is sent. Failures never throw: they are logged and returned as leftovers.
/// </summary>
internal sealed class TenantCleanup
{
    private static readonly string[] NameProperties = ["displayName", "userPrincipalName", "uniqueName"];

    private readonly IGraphClient _graph;
    private readonly string _prefix;
    private readonly List<string> _log = [];
    private readonly List<string> _leftovers = [];

    public TenantCleanup(IGraphClient graph, string prefix)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (!IntegrationSettings.IsValidPrefix(prefix))
        {
            throw new ArgumentException($"Prefix '{prefix}' is too short or malformed for a tenant sweep.", nameof(prefix));
        }

        _graph = graph;
        _prefix = prefix;
    }

    /// <summary>What the sweep did, one line per action.</summary>
    public IReadOnlyList<string> Entries => _log;

    /// <summary>Live objects that could not be removed (soft-deleted copies that could not be purged are only logged).</summary>
    public IReadOnlyList<string> Leftovers => _leftovers;

    /// <summary>
    /// Runs the whole sweep. <paramref name="trackedPaths"/> are entity paths (<c>/applications/{id}</c>) of objects a
    /// test created directly; they are removed first, but only when their name still starts with the prefix.
    /// </summary>
    public async Task SweepAsync(IReadOnlyCollection<string> trackedPaths, CancellationToken cancellationToken)
    {
        _leftovers.Clear();
        foreach (string path in trackedPaths)
        {
            await DeleteTrackedAsync(path, cancellationToken);
        }

        // Conditional Access first: policies reference groups, locations, strengths and contexts.
        foreach (var policy in await ListOwnedAsync("CA policy", "/identity/conditionalAccess/policies", null, cancellationToken))
        {
            if (string.Equals(policy.Text("state"), "enabled", StringComparison.OrdinalIgnoreCase))
            {
                Leftover($"CA policy '{policy.Text("displayName")}' ({policy.Text("id")}) is enabled (enforced); the guard never deletes enforced policies - disable or delete it in the portal");
                continue;
            }

            await DeleteOwnedAsync("CA policy", "/identity/conditionalAccess/policies", policy, cancellationToken);
        }

        foreach (var strength in await ListOwnedAsync("authentication strength", "/policies/authenticationStrengthPolicies", null, cancellationToken))
        {
            if (string.Equals(strength.Text("policyType"), "builtIn", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await DeleteOwnedAsync("authentication strength", "/policies/authenticationStrengthPolicies", strength, cancellationToken);
        }

        foreach (var context in await ListOwnedAsync("authentication context", "/identity/conditionalAccess/authenticationContextClassReferences", null, cancellationToken))
        {
            string path = "/identity/conditionalAccess/authenticationContextClassReferences";
            try
            {
                // Graph refuses to delete a context that is still available.
                await _graph.PatchAsync(new GraphRequest(path + "/" + context.RequireId()), new JsonObject { ["isAvailable"] = false }, cancellationToken);
            }
            catch (GraphException ex)
            {
                Log($"could not mark context {context.Text("id")} unavailable: {ex.Code}");
            }

            await DeleteOwnedAsync("authentication context", path, context, cancellationToken);
        }

        foreach (var location in await ListOwnedAsync("named location", "/identity/conditionalAccess/namedLocations", null, cancellationToken))
        {
            await DeleteOwnedAsync("named location", "/identity/conditionalAccess/namedLocations", location, cancellationToken);
        }

        // Directory roles: assignments of owned principals and of owned custom roles go before the roles and principals.
        var roleDefinitions = (await ListOwnedAsync("role definition", "/roleManagement/directory/roleDefinitions", null, cancellationToken))
            .Where(r => r["isBuiltIn"] is not JsonValue builtIn || !builtIn.TryGetValue<bool>(out bool isBuiltIn) || !isBuiltIn)
            .ToList();
        var administrativeUnits = await ListOwnedAsync("administrative unit", "/directory/administrativeUnits", null, cancellationToken);
        var groups = await ListOwnedAsync("group", "/groups", "displayName", cancellationToken);
        var users = await ListOwnedByNameOrUpnAsync(cancellationToken);
        var applications = await ListOwnedAsync("application", "/applications", "displayName", cancellationToken);
        var servicePrincipals = await ListOwnedServicePrincipalsAsync(applications, cancellationToken);

        var assignmentFilters = new List<string>();
        assignmentFilters.AddRange(roleDefinitions.Select(r => "roleDefinitionId eq '" + r.RequireId() + "'"));
        assignmentFilters.AddRange(groups.Concat(users).Concat(servicePrincipals).Select(p => "principalId eq '" + p.RequireId() + "'"));
        foreach (string filter in assignmentFilters)
        {
            await DeleteRoleAssignmentsAsync(filter, cancellationToken);
        }

        foreach (var role in roleDefinitions)
        {
            await DeleteOwnedAsync("role definition", "/roleManagement/directory/roleDefinitions", role, cancellationToken);
        }

        foreach (var unit in administrativeUnits)
        {
            await DeleteOwnedAsync("administrative unit", "/directory/administrativeUnits", unit, cancellationToken);
        }

        foreach (var group in groups)
        {
            await DeleteOwnedAsync("group", "/groups", group, cancellationToken);
        }

        foreach (var user in users)
        {
            await DeleteOwnedAsync("user", "/users", user, cancellationToken);
        }

        foreach (var principal in servicePrincipals)
        {
            await DeleteOwnedAsync("service principal", "/servicePrincipals", principal, cancellationToken);
        }

        foreach (var application in applications)
        {
            await DeleteOwnedAsync("application", "/applications", application, cancellationToken);
        }

        await PurgeSoftDeletedAsync(cancellationToken);
        await VerifyAsync(cancellationToken);
    }

    // ---- tracked objects ----

    private async Task DeleteTrackedAsync(string path, CancellationToken cancellationToken)
    {
        JsonObject? live;
        try
        {
            live = await _graph.GetObjectAsync(new GraphRequest(path), cancellationToken);
        }
        catch (GraphException ex)
        {
            Log($"tracked {path}: could not be read ({ex.Code}); the prefix sweep still covers it");
            return;
        }

        if (live is null)
        {
            return;
        }

        if (!IsOwned(live))
        {
            Log($"REFUSED to delete tracked {path}: its name no longer starts with '{_prefix}'");
            return;
        }

        string collection = path[..path.LastIndexOf('/')];
        await DeleteOwnedAsync("tracked object", collection, live, cancellationToken);
        await TryPurgeAsync(live.RequireId(), cancellationToken);
    }

    // ---- listing ----

    private async Task<List<JsonObject>> ListOwnedAsync(string label, string path, string? serverFilterProperty, CancellationToken cancellationToken)
    {
        var owned = new List<JsonObject>();
        if (serverFilterProperty is not null)
        {
            try
            {
                var request = new GraphRequest(path).WithFilter($"startswith({serverFilterProperty},'{_prefix}')");
                await foreach (var item in _graph.GetAllPagesAsync(request, cancellationToken))
                {
                    if (item is JsonObject obj && IsOwned(obj))
                    {
                        owned.Add(obj);
                    }
                }

                return owned;
            }
            catch (GraphException ex)
            {
                Log($"{label}: the server-side filter was rejected ({ex.Code}); listing the whole collection");
                owned.Clear();
            }
        }

        try
        {
            await foreach (var item in _graph.GetAllPagesAsync(new GraphRequest(path), cancellationToken))
            {
                if (item is JsonObject obj && IsOwned(obj))
                {
                    owned.Add(obj);
                }
            }
        }
        catch (GraphException ex)
        {
            // Not being able to list means nothing could be verified or removed: report it, a green run must not hide it.
            Leftover($"{label}: the collection {path} could not be listed ({ex.Code}: {ex.GraphMessage}); objects with the prefix may remain");
        }

        return owned;
    }

    private async Task<List<JsonObject>> ListOwnedByNameOrUpnAsync(CancellationToken cancellationToken)
    {
        var byId = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var user in await ListOwnedAsync("user", "/users", "displayName", cancellationToken))
        {
            byId[user.RequireId()] = user;
        }

        try
        {
            var request = new GraphRequest("/users").WithFilter($"startswith(userPrincipalName,'{_prefix}')").WithCount().WithConsistencyLevelEventual();
            await foreach (var item in _graph.GetAllPagesAsync(request, cancellationToken))
            {
                if (item is JsonObject obj && IsOwned(obj))
                {
                    byId[obj.RequireId()] = obj;
                }
            }
        }
        catch (GraphException ex)
        {
            Log($"user: the userPrincipalName filter was rejected ({ex.Code}); only the displayName match was used");
        }

        return [.. byId.Values];
    }

    private async Task<List<JsonObject>> ListOwnedServicePrincipalsAsync(IReadOnlyList<JsonObject> ownedApplications, CancellationToken cancellationToken)
    {
        var byId = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var principal in await ListOwnedAsync("service principal", "/servicePrincipals", "displayName", cancellationToken))
        {
            byId[principal.RequireId()] = principal;
        }

        // A service principal keeps working after its application is deleted, and may carry a different display name.
        foreach (var application in ownedApplications)
        {
            string? appId = application.Text("appId");
            if (appId is null)
            {
                continue;
            }

            try
            {
                var request = new GraphRequest("/servicePrincipals").WithFilter($"appId eq '{appId}'");
                await foreach (var item in _graph.GetAllPagesAsync(request, cancellationToken))
                {
                    if (item is JsonObject obj)
                    {
                        byId[obj.RequireId()] = obj;
                    }
                }
            }
            catch (GraphException ex)
            {
                Log($"service principal of application {appId}: lookup failed ({ex.Code})");
            }
        }

        return [.. byId.Values];
    }

    // ---- deleting ----

    private async Task DeleteRoleAssignmentsAsync(string filter, CancellationToken cancellationToken)
    {
        try
        {
            var assignments = await _graph.ListAsync(new GraphRequest("/roleManagement/directory/roleAssignments").WithFilter(filter), cancellationToken);
            foreach (var assignment in assignments)
            {
                string id = assignment.RequireId();
                try
                {
                    await _graph.DeleteAsync(new GraphRequest("/roleManagement/directory/roleAssignments/" + id), cancellationToken);
                    Log($"deleted role assignment {id} ({filter})");
                }
                catch (GraphException ex) when (ex.IsNotFound)
                {
                }
                catch (GraphException ex)
                {
                    Leftover($"role assignment {id} ({filter}) could not be deleted: {ex.Code}: {ex.GraphMessage}");
                }
            }
        }
        catch (GraphException ex)
        {
            Log($"role assignments ({filter}): could not be listed ({ex.Code}); the owner objects are deleted anyway");
        }
    }

    private async Task DeleteOwnedAsync(string label, string collectionPath, JsonObject obj, CancellationToken cancellationToken)
    {
        string name = Describe(obj);
        if (!IsOwned(obj))
        {
            Log($"REFUSED to delete {label} {name}: the name does not start with '{_prefix}'");
            return;
        }

        string id = obj.RequireId();
        try
        {
            await _graph.DeleteAsync(new GraphRequest(collectionPath.TrimEnd('/') + "/" + id), cancellationToken);
            Log($"deleted {label} {name} ({id})");
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            Log($"{label} {name} ({id}) was already gone");
        }
        catch (GraphException ex)
        {
            Leftover($"{label} {name} ({id}) could not be deleted: {ex.Code}: {ex.GraphMessage}");
        }
    }

    private async Task PurgeSoftDeletedAsync(CancellationToken cancellationToken)
    {
        foreach (string type in new[] { "application", "servicePrincipal", "group", "user", "administrativeUnit" })
        {
            string path = "/directory/deletedItems/microsoft.graph." + type;
            foreach (var item in await ListOwnedDeletedAsync(type, path, cancellationToken))
            {
                await TryPurgeAsync(item.RequireId(), cancellationToken, Describe(item));
            }
        }
    }

    private async Task<List<JsonObject>> ListOwnedDeletedAsync(string type, string path, CancellationToken cancellationToken)
    {
        var owned = new List<JsonObject>();
        try
        {
            var request = new GraphRequest(path).WithFilter($"startswith(displayName,'{_prefix}')").WithCount().WithConsistencyLevelEventual();
            await foreach (var item in _graph.GetAllPagesAsync(request, cancellationToken))
            {
                if (item is JsonObject obj && IsOwned(obj))
                {
                    owned.Add(obj);
                }
            }

            return owned;
        }
        catch (GraphException ex)
        {
            Log($"deleted {type}: the server-side filter was rejected ({ex.Code}); listing the whole recycle bin");
            owned.Clear();
        }

        try
        {
            await foreach (var item in _graph.GetAllPagesAsync(new GraphRequest(path), cancellationToken))
            {
                if (item is JsonObject obj && IsOwned(obj))
                {
                    owned.Add(obj);
                }
            }
        }
        catch (GraphException ex)
        {
            Log($"deleted {type}: the recycle bin could not be listed ({ex.Code}); soft-deleted copies may remain for 30 days");
        }

        return owned;
    }

    private async Task TryPurgeAsync(string id, CancellationToken cancellationToken, string? name = null)
    {
        try
        {
            await _graph.DeleteAsync(new GraphRequest("/directory/deletedItems/" + id), cancellationToken);
            Log($"purged soft-deleted {name ?? id} ({id})");
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            // Not soft-deleted (a permanently deleted type) or already purged.
        }
        catch (GraphException ex)
        {
            Log($"could not purge soft-deleted {name ?? id} ({id}): {ex.Code}: {ex.GraphMessage}; it disappears on its own after 30 days");
        }
    }

    // ---- verification ----

    private async Task VerifyAsync(CancellationToken cancellationToken)
    {
        var remaining = new List<(string Label, JsonObject Item)>();
        foreach (var item in await ListOwnedAsync("application", "/applications", "displayName", cancellationToken))
        {
            remaining.Add(("application", item));
        }

        foreach (var item in await ListOwnedAsync("service principal", "/servicePrincipals", "displayName", cancellationToken))
        {
            remaining.Add(("service principal", item));
        }

        foreach (var item in await ListOwnedAsync("group", "/groups", "displayName", cancellationToken))
        {
            remaining.Add(("group", item));
        }

        foreach (var item in await ListOwnedByNameOrUpnAsync(cancellationToken))
        {
            remaining.Add(("user", item));
        }

        foreach (var item in await ListOwnedAsync("administrative unit", "/directory/administrativeUnits", null, cancellationToken))
        {
            remaining.Add(("administrative unit", item));
        }

        foreach (var item in await ListOwnedAsync("named location", "/identity/conditionalAccess/namedLocations", null, cancellationToken))
        {
            remaining.Add(("named location", item));
        }

        foreach (var (label, item) in remaining)
        {
            Leftover($"{label} {Describe(item)} ({item.Text("id")}) is still present after the sweep");
        }
    }

    // ---- helpers ----

    private bool IsOwned(JsonObject obj)
    {
        foreach (string property in NameProperties)
        {
            if (obj.Text(property) is { } value && value.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Describe(JsonObject obj)
    {
        return "'" + (obj.Text("displayName") ?? obj.Text("userPrincipalName") ?? obj.Text("uniqueName") ?? obj.Text("id") ?? "?") + "'";
    }

    private void Log(string line)
    {
        _log.Add(line);
    }

    private void Leftover(string line)
    {
        _log.Add("LEFTOVER " + line);
        _leftovers.Add(line);
    }
}
