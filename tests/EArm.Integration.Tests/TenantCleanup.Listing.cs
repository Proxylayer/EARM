using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;

namespace EArm.Integration.Tests;

/// <summary>The listings of <see cref="TenantCleanup"/>: the prefixed objects of each collection, strengths, authentication contexts, users and service principals.</summary>
internal sealed partial class TenantCleanup
{
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
}
