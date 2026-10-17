using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;

namespace EArm.Integration.Tests;

/// <summary>The deletes of <see cref="TenantCleanup"/>: PIM schedules, tracked objects, role assignments, Conditional Access objects (unavailable contexts, untrusted locations) and the owned objects.</summary>
internal sealed partial class TenantCleanup
{
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
}
