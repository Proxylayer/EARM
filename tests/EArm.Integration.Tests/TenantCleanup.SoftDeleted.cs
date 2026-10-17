using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;

namespace EArm.Integration.Tests;

/// <summary>The recycle-bin purge of <see cref="TenantCleanup"/>: the prefixed soft-deleted objects are purged after the live sweep.</summary>
internal sealed partial class TenantCleanup
{
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
}
