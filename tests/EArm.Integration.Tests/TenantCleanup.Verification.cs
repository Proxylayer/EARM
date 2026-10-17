using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;

namespace EArm.Integration.Tests;

/// <summary>The verification of <see cref="TenantCleanup"/>: nothing prefixed is left, and every accepted delete is confirmed gone.</summary>
internal sealed partial class TenantCleanup
{
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
}
