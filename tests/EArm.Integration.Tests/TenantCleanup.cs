using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;

namespace EArm.Integration.Tests;

/// <summary>
/// The prefix sweep of the cleanup guard. It finds every object whose name (<c>displayName</c>, <c>userPrincipalName</c>
/// or <c>uniqueName</c>) starts with the run prefix and deletes it in dependency order, then purges the soft-deleted
/// copies. Objects without the prefix are never listed for deletion, and every delete is re-checked against the prefix
/// right before it is sent. Failures never throw: they are logged and returned as leftovers. With <c>EARM_IT_ALLOW_PIM=1</c> the PIM schedules of the
/// prefixed and tracked principals are removed first, before any object is deleted (<see cref="PimCleanup"/>).
/// </summary>
internal sealed class TenantCleanup
{
    private static readonly string[] NameProperties = ["displayName", "userPrincipalName", "uniqueName"];
    private static readonly TimeSpan TransientBadRequestWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TransientBadRequestInterval = TimeSpan.FromSeconds(5);

    // Graph applies an 'isTrusted: false' PATCH with a delay; until then DELETE keeps answering 1177.
    private static readonly TimeSpan TrustedLocationWindow = TimeSpan.FromSeconds(90);

    // A deleted object can stay in a collection listing for a while after Graph accepted its DELETE.
    private static readonly TimeSpan DeletionVisibleWindow = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    // The authentication context ids the fixture allocates (IntegrationFixture.AllocateContextIdAsync); the sweep never deletes others.
    private const int LowestContextNumber = 50;
    private const int HighestContextNumber = 99;

    private const string PoliciesPath = "/identity/conditionalAccess/policies";
    private const string StrengthsPath = "/policies/authenticationStrengthPolicies";
    private const string ContextsPath = "/identity/conditionalAccess/authenticationContextClassReferences";
    private const string NamedLocationsPath = "/identity/conditionalAccess/namedLocations";

    private readonly IGraphClient _graph;
    private readonly string _prefix;
    private readonly List<string> _log = [];
    private readonly List<string> _leftovers = [];
    private readonly HashSet<string> _deletedIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _leftoverIds = new(StringComparer.OrdinalIgnoreCase);

    private readonly bool _includePim;

    /// <param name="graph">The fixture's Graph client.</param>
    /// <param name="prefix">The run prefix; only objects whose name starts with it are touched.</param>
    /// <param name="includePim">
    /// True (<c>EARM_IT_ALLOW_PIM=1</c>): the PIM schedules of the prefixed and tracked principals are removed first (<see cref="PimCleanup"/>); the run consented the
    /// PIM scopes only then.
    /// </param>
    public TenantCleanup(IGraphClient graph, string prefix, bool includePim = false)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (!IntegrationSettings.IsValidPrefix(prefix))
        {
            throw new ArgumentException($"Prefix '{prefix}' is too short or malformed for a tenant sweep.", nameof(prefix));
        }

        _graph = graph;
        _prefix = prefix;
        _includePim = includePim;
    }

    /// <summary>What the sweep did, one line per action.</summary>
    public IReadOnlyList<string> Entries => _log;

    /// <summary>Live objects that could not be removed (soft-deleted copies that could not be purged are only logged).</summary>
    public IReadOnlyList<string> Leftovers => _leftovers;

    /// <summary>
    /// Runs the whole sweep. <paramref name="trackedPaths"/> are entity paths (<c>/applications/{id}</c>) of objects a
    /// test created directly; they are removed first (after the PIM schedules, with PIM on), but only when their name still starts with the prefix.
    /// </summary>
    public async Task SweepAsync(IReadOnlyCollection<string> trackedPaths, CancellationToken cancellationToken)
    {
        _leftovers.Clear();
        _deletedIds.Clear();
        _leftoverIds.Clear();

        // The owned principals are listed before anything is deleted: their PIM schedules (adminRemove) go first, also those of tracked principals,
        // whose schedules cannot be listed once the principal is deleted.
        var roleDefinitions = (await ListOwnedAsync("role definition", "/roleManagement/directory/roleDefinitions", null, cancellationToken))
            .Where(r => r["isBuiltIn"] is not JsonValue builtIn || !builtIn.TryGetValue<bool>(out bool isBuiltIn) || !isBuiltIn)
            .ToList();
        var administrativeUnits = await ListOwnedAsync("administrative unit", "/directory/administrativeUnits", null, cancellationToken);
        var groups = await ListOwnedAsync("group", "/groups", "displayName", cancellationToken);
        var users = await ListOwnedByNameOrUpnAsync(cancellationToken);
        var applications = await ListOwnedAsync("application", "/applications", "displayName", cancellationToken);
        var servicePrincipals = await ListOwnedServicePrincipalsAsync(applications, cancellationToken);

        if (_includePim)
        {
            await RemovePimSchedulesAsync(trackedPaths, groups, users, servicePrincipals, cancellationToken);
        }

        foreach (string path in trackedPaths)
        {
            await DeleteTrackedAsync(path, cancellationToken);
        }

        // Conditional Access first: policies reference groups, locations, strengths and contexts.
        foreach (var policy in await ListOwnedAsync("CA policy", PoliciesPath, null, cancellationToken))
        {
            if (string.Equals(policy.Text("state"), "enabled", StringComparison.OrdinalIgnoreCase))
            {
                Leftover($"CA policy '{policy.Text("displayName")}' ({policy.Text("id")}) is enabled (enforced); the guard never deletes enforced policies - disable or delete it in the portal", policy.Text("id"));
                continue;
            }

            await DeleteOwnedAsync("CA policy", PoliciesPath, policy, cancellationToken);
        }

        foreach (var strength in await ListOwnedStrengthsAsync(cancellationToken))
        {
            await DeleteOwnedAsync("authentication strength", StrengthsPath, strength, cancellationToken, retryTransientBadRequest: true);
        }

        foreach (var context in await ListOwnedContextsAsync(cancellationToken))
        {
            await MarkContextUnavailableAsync(context, cancellationToken);
            await DeleteOwnedAsync("authentication context", ContextsPath, context, cancellationToken, retryTransientBadRequest: true);
        }

        foreach (var location in await ListOwnedAsync("named location", NamedLocationsPath, null, cancellationToken))
        {
            bool unmarked = IsTrusted(location) && await UnmarkTrustedAsync(location, cancellationToken);
            await DeleteOwnedAsync("named location", NamedLocationsPath, location, cancellationToken, retryTransientBadRequest: true, trustedLocation: true, alreadyUnmarked: unmarked);
        }

        // The objects deleted as tracked above are not deleted again.
        roleDefinitions = NotYetDeleted(roleDefinitions);
        administrativeUnits = NotYetDeleted(administrativeUnits);
        groups = NotYetDeleted(groups);
        users = NotYetDeleted(users);
        applications = NotYetDeleted(applications);
        servicePrincipals = NotYetDeleted(servicePrincipals);

        // Directory roles: assignments of owned principals and of owned custom roles go before the roles and principals.
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

    private List<JsonObject> NotYetDeleted(List<JsonObject> objects)
    {
        return objects.Where(o => !_deletedIds.Contains(o.RequireId())).ToList();
    }

    // ---- PIM ----

    /// <summary>
    /// Removes the PIM schedules (<see cref="PimCleanup"/>) of the owned principals: the listed groups, users and service principals (those of owned
    /// applications count as owned, as for the role assignments) and the tracked users, groups and service principals whose name still carries the prefix
    /// (a principal created moments ago can be missing from a filtered listing). Runs before any deletion.
    /// </summary>
    private async Task RemovePimSchedulesAsync(
        IReadOnlyCollection<string> trackedPaths,
        IReadOnlyList<JsonObject> groups,
        IReadOnlyList<JsonObject> users,
        IReadOnlyList<JsonObject> servicePrincipals,
        CancellationToken cancellationToken)
    {
        var principals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ownedGroups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var principal in groups.Concat(users).Concat(servicePrincipals))
        {
            principals.TryAdd(principal.RequireId(), Describe(principal).Trim('\''));
        }

        foreach (var group in groups)
        {
            ownedGroups.TryAdd(group.RequireId(), Describe(group).Trim('\''));
        }

        foreach (string path in trackedPaths)
        {
            string collection = path[..Math.Max(0, path.LastIndexOf('/'))];
            bool isGroup = string.Equals(collection, "/groups", StringComparison.OrdinalIgnoreCase);
            if (!isGroup && !string.Equals(collection, "/users", StringComparison.OrdinalIgnoreCase) && !string.Equals(collection, "/servicePrincipals", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            JsonObject? live;
            try
            {
                live = await _graph.GetObjectAsync(new GraphRequest(path), cancellationToken);
            }
            catch (GraphException ex)
            {
                Log($"pim: tracked {path} could not be read ({ex.Code}); its PIM schedules are covered only if the prefix listing found it");
                continue;
            }

            if (live is null || !IsOwned(live))
            {
                continue;
            }

            string name = Describe(live).Trim('\'');
            principals.TryAdd(live.RequireId(), name);
            if (isGroup)
            {
                ownedGroups.TryAdd(live.RequireId(), name);
            }
        }

        var pim = new PimCleanup(_graph);
        await pim.RemoveAsync(principals, ownedGroups, cancellationToken);
        _log.AddRange(pim.Entries);
        _leftovers.AddRange(pim.Leftovers);
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
        if (string.Equals(collection, ContextsPath, StringComparison.OrdinalIgnoreCase))
        {
            await MarkContextUnavailableAsync(live, cancellationToken);
        }

        bool isLocation = string.Equals(collection, NamedLocationsPath, StringComparison.OrdinalIgnoreCase);
        bool unmarked = isLocation && IsTrusted(live) && await UnmarkTrustedAsync(live, cancellationToken);
        await DeleteOwnedAsync("tracked object", collection, live, cancellationToken, trustedLocation: isLocation, alreadyUnmarked: unmarked);
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

    /// <summary>Custom authentication strengths whose displayName starts with the prefix; built-in strengths are never touched.</summary>
    private async Task<List<JsonObject>> ListOwnedStrengthsAsync(CancellationToken cancellationToken)
    {
        return (await ListOwnedAsync("authentication strength", StrengthsPath, null, cancellationToken))
            .Where(s => !string.Equals(s.Text("policyType"), "builtIn", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Authentication contexts whose displayName starts with the prefix and whose id is in the range the fixture allocates
    /// (<c>c50</c>..<c>c99</c>). A prefixed context outside that range was not created by these tests: it is only reported.
    /// </summary>
    private async Task<List<JsonObject>> ListOwnedContextsAsync(CancellationToken cancellationToken)
    {
        var owned = new List<JsonObject>();
        foreach (var context in await ListOwnedAsync("authentication context", ContextsPath, null, cancellationToken))
        {
            if (IsAllocatableContextId(context.Text("id")))
            {
                owned.Add(context);
            }
            else
            {
                Leftover($"authentication context {Describe(context)} ({context.Text("id")}) carries the prefix but its id is outside c{LowestContextNumber}..c{HighestContextNumber}; the guard does not delete it", context.Text("id"));
            }
        }

        return owned;
    }

    private static bool IsAllocatableContextId(string? id)
    {
        return id is { Length: > 1 } && (id[0] is 'c' or 'C')
            && int.TryParse(id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            && number is >= LowestContextNumber and <= HighestContextNumber;
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
                    Leftover($"role assignment {id} ({filter}) could not be deleted: {ex.Code}: {ex.GraphMessage}", id);
                }
            }
        }
        catch (GraphException ex)
        {
            Log($"role assignments ({filter}): could not be listed ({ex.Code}); the owner objects are deleted anyway");
        }
    }

    /// <summary>Graph refuses to delete a context that is still available, so it is marked unavailable first.</summary>
    private async Task MarkContextUnavailableAsync(JsonObject context, CancellationToken cancellationToken)
    {
        if (context["isAvailable"] is JsonValue available && available.TryGetValue<bool>(out bool isAvailable) && !isAvailable)
        {
            return;
        }

        string id = context.RequireId();
        try
        {
            await _graph.PatchAsync(new GraphRequest(ContextsPath + "/" + id), new JsonObject { ["isAvailable"] = false }, cancellationToken);
            Log($"marked authentication context {Describe(context)} ({id}) unavailable");
        }
        catch (GraphException ex)
        {
            Log($"could not mark authentication context {Describe(context)} ({id}) unavailable: {ex.Code}: {ex.GraphMessage}; the delete is attempted anyway");
        }
    }

    private static bool IsTrusted(JsonObject location)
    {
        return location["isTrusted"] is JsonValue trusted && trusted.TryGetValue<bool>(out bool isTrusted) && isTrusted;
    }

    /// <summary>
    /// Graph refuses to delete a named location that is marked trusted (400, code 1177), so a trusted IP location is
    /// unmarked first. The PATCH carries the live <c>@odata.type</c>; country locations have no <c>isTrusted</c>.
    /// Returns true when Graph accepted the unmark PATCH (it applies it with a delay, see <see cref="DeleteOwnedAsync"/>).
    /// </summary>
    private async Task<bool> UnmarkTrustedAsync(JsonObject location, CancellationToken cancellationToken)
    {
        if (!IsOwned(location))
        {
            return false;
        }

        string id = location.RequireId();
        string? odataType = location.Text("@odata.type");
        if (odataType is null)
        {
            Log($"named location {Describe(location)} ({id}) is trusted but has no @odata.type, so it cannot be unmarked; the delete is attempted anyway");
            return false;
        }

        try
        {
            var body = new JsonObject { ["@odata.type"] = odataType, ["isTrusted"] = false };
            await _graph.PatchAsync(new GraphRequest(NamedLocationsPath + "/" + id), body, cancellationToken);
            Log($"unmarked trusted named location {Describe(location)} ({id})");
            return true;
        }
        catch (GraphException ex)
        {
            Log($"could not unmark trusted named location {Describe(location)} ({id}): {ex.Code}: {ex.GraphMessage}");
            return false;
        }
    }

    /// <summary>
    /// Deletes one owned object. "deleted" is logged only once Graph accepted the DELETE; whether the object is really gone is
    /// confirmed by <see cref="VerifyAsync"/>. <paramref name="trustedLocation"/> turns on the 1177 handling of named
    /// locations: an unmark (unless <paramref name="alreadyUnmarked"/>) and retries until Graph has applied it.
    /// </summary>
    private async Task DeleteOwnedAsync(
        string label,
        string collectionPath,
        JsonObject obj,
        CancellationToken cancellationToken,
        bool retryTransientBadRequest = false,
        bool trustedLocation = false,
        bool alreadyUnmarked = false)
    {
        string name = Describe(obj);
        if (!IsOwned(obj))
        {
            Log($"REFUSED to delete {label} {name}: the name does not start with '{_prefix}'");
            return;
        }

        string id = obj.RequireId();

        // Right after the referencing CA policy is deleted, Graph can still answer 400 ("in use") for a while.
        var deadline = DateTime.UtcNow + TransientBadRequestWindow;
        var started = DateTime.UtcNow;
        DateTime? trustedDeadline = null;
        bool unmarked = alreadyUnmarked;
        while (true)
        {
            try
            {
                await _graph.DeleteAsync(new GraphRequest(collectionPath.TrimEnd('/') + "/" + id), cancellationToken);
                _deletedIds.Add(id);
                Log(trustedDeadline is null
                    ? $"deleted {label} {name} ({id})"
                    : $"deleted {label} {name} ({id}) after waiting {(DateTime.UtcNow - started).TotalSeconds:F0} s for the unmark to apply");
                return;
            }
            catch (GraphException ex) when (ex.IsNotFound)
            {
                Log($"{label} {name} ({id}) was already gone");
                return;
            }
            catch (GraphException ex) when (trustedLocation && IsTrustedLocationError(ex))
            {
                if (trustedDeadline is null)
                {
                    trustedDeadline = DateTime.UtcNow + TrustedLocationWindow;
                    if (!unmarked)
                    {
                        // The listing did not show the location as trusted (a stale read): Graph says it is, so unmark it now.
                        unmarked = await UnmarkTrustedAsync(obj, cancellationToken);
                    }

                    Log($"{label} {name} ({id}): delete answered 1177 (still trusted); retrying every {PollInterval.TotalSeconds:F0} s for up to {TrustedLocationWindow.TotalSeconds:F0} s while the unmark applies");
                }
                else if (DateTime.UtcNow >= trustedDeadline)
                {
                    string unmarkState = unmarked ? "the unmark PATCH was accepted" : "the unmark PATCH was not accepted";
                    Leftover($"{label} {name} ({id}) could not be deleted: still trusted {TrustedLocationWindow.TotalSeconds:F0} s later ({unmarkState}): {ex.Code}: {ex.GraphMessage}", id);
                    return;
                }

                await Task.Delay(PollInterval, cancellationToken);
            }
            catch (GraphException ex) when (retryTransientBadRequest && ex.Status == HttpStatusCode.BadRequest && !IsTrustedLocationError(ex) && DateTime.UtcNow < deadline)
            {
                Log($"{label} {name} ({id}): delete answered 400 ({ex.Code}); retrying");
                await Task.Delay(TransientBadRequestInterval, cancellationToken);
            }
            catch (GraphException ex)
            {
                Leftover($"{label} {name} ({id}) could not be deleted: {ex.Code}: {ex.GraphMessage}", id);
                return;
            }
        }
    }

    /// <summary>Code 1177: the location is still trusted. It needs an unmark (<see cref="UnmarkTrustedAsync"/>) and then time for Graph to apply it.</summary>
    private static bool IsTrustedLocationError(GraphException ex)
    {
        return string.Equals(ex.Code, "1177", StringComparison.Ordinal)
            || ex.GraphMessage.Contains("Trusted location", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// Lists every owned object again. An object whose DELETE Graph accepted in this sweep but that is still listed is
    /// confirmed by reading it (<see cref="ConfirmDeletedAsync"/>): listings lag behind deletes. Anything else that remains
    /// is a leftover (reported once: an object whose delete already failed is not reported again).
    /// </summary>
    private async Task VerifyAsync(CancellationToken cancellationToken)
    {
        var remaining = new List<(string Label, string CollectionPath, JsonObject Item)>();
        void Add(string label, string collectionPath, IEnumerable<JsonObject> items)
        {
            remaining.AddRange(items.Select(item => (label, collectionPath, item)));
        }

        Add("CA policy", PoliciesPath, await ListOwnedAsync("CA policy", PoliciesPath, null, cancellationToken));
        Add("authentication strength", StrengthsPath, await ListOwnedStrengthsAsync(cancellationToken));
        Add("authentication context", ContextsPath, (await ListOwnedAsync("authentication context", ContextsPath, null, cancellationToken)).Where(c => IsAllocatableContextId(c.Text("id"))));
        Add("named location", NamedLocationsPath, await ListOwnedAsync("named location", NamedLocationsPath, null, cancellationToken));
        Add("application", "/applications", await ListOwnedAsync("application", "/applications", "displayName", cancellationToken));
        Add("service principal", "/servicePrincipals", await ListOwnedAsync("service principal", "/servicePrincipals", "displayName", cancellationToken));
        Add("group", "/groups", await ListOwnedAsync("group", "/groups", "displayName", cancellationToken));
        Add("user", "/users", await ListOwnedByNameOrUpnAsync(cancellationToken));
        Add("administrative unit", "/directory/administrativeUnits", await ListOwnedAsync("administrative unit", "/directory/administrativeUnits", null, cancellationToken));

        var pending = new List<(string Label, string CollectionPath, JsonObject Item)>();
        foreach (var entry in remaining)
        {
            string? id = entry.Item.Text("id");
            if (id is not null && _deletedIds.Contains(id))
            {
                pending.Add(entry);
            }
            else if (id is null || !_leftoverIds.Contains(id))
            {
                Leftover($"{entry.Label} {Describe(entry.Item)} ({id}) is still present after the sweep", id);
            }
        }

        await ConfirmDeletedAsync(pending, cancellationToken);
    }

    /// <summary>Polls objects whose DELETE was accepted but that were still listed, until a GET answers 404 or the window ends.</summary>
    private async Task ConfirmDeletedAsync(List<(string Label, string CollectionPath, JsonObject Item)> pending, CancellationToken cancellationToken)
    {
        if (pending.Count == 0)
        {
            return;
        }

        var started = DateTime.UtcNow;
        var deadline = started + DeletionVisibleWindow;
        while (true)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var (label, collectionPath, item) = pending[i];
                string id = item.RequireId();
                try
                {
                    if (await _graph.GetObjectAsync(new GraphRequest(collectionPath + "/" + id), cancellationToken) is null)
                    {
                        Log($"confirmed {label} {Describe(item)} ({id}) is gone ({(DateTime.UtcNow - started).TotalSeconds:F0} s after the verification listing still showed it)");
                        pending.RemoveAt(i);
                    }
                }
                catch (GraphException)
                {
                    // Not confirmable right now; retried until the window ends.
                }
            }

            if (pending.Count == 0 || DateTime.UtcNow >= deadline)
            {
                break;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        foreach (var (label, _, item) in pending)
        {
            Leftover($"{label} {Describe(item)} ({item.Text("id")}): Graph accepted its DELETE, but it is still present {DeletionVisibleWindow.TotalSeconds:F0} s later", item.Text("id"));
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

    private void Leftover(string line, string? id = null)
    {
        _log.Add("LEFTOVER " + line);
        _leftovers.Add(line);
        if (id is not null)
        {
            _leftoverIds.Add(id);
        }
    }
}
