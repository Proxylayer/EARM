using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;

namespace EArm.Integration.Tests;

/// <summary>
/// The entitlement management part of the prefix sweep (<c>EARM_IT_ALLOW_ACCESS_PACKAGES=1</c>): every catalog whose displayName starts with the run
/// prefix is emptied and deleted in Graph's order (docs/access-packages.md, "Lifecycle"): the assignment policies and resource role scopes of its packages,
/// the packages, the catalog resources (<c>adminRemove</c>) and the catalog. The tests create no assignments; a package that still has one (a person
/// requested it) cannot be deleted and is reported as a leftover. Catalogs without the prefix are never touched.
/// </summary>
internal sealed class AccessPackageCleanup
{
    private const string Root = "/identityGovernance/entitlementManagement";

    private readonly IGraphClient _graph;
    private readonly string _prefix;
    private readonly List<string> _log = [];
    private readonly List<string> _leftovers = [];

    public AccessPackageCleanup(IGraphClient graph, string prefix)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        _graph = graph;
        _prefix = prefix;
    }

    public IReadOnlyList<string> Entries => _log;

    public IReadOnlyList<string> Leftovers => _leftovers;

    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        var catalogs = (await _graph.ListAsync(new GraphRequest(Root + "/catalogs"), cancellationToken))
            .Where(c => c.Text("displayName")?.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
        if (catalogs.Count == 0)
        {
            return;
        }

        var packages = await _graph.ListAsync(new GraphRequest(Root + "/accessPackages").WithExpand("catalog"), cancellationToken);
        foreach (var catalog in catalogs)
        {
            string catalogId = catalog.RequireId();
            foreach (var package in packages.Where(p => string.Equals(p.Nested("catalog")?.Text("id"), catalogId, StringComparison.OrdinalIgnoreCase)))
            {
                await RemovePackageAsync(package, cancellationToken);
            }

            foreach (var resource in await _graph.ListAsync(new GraphRequest($"{Root}/catalogs/{catalogId}/resources"), cancellationToken))
            {
                await TryAsync(
                    $"catalog resource {resource.Text("displayName")} ({resource.RequireId()}) of catalog '{catalog.Text("displayName")}'",
                    () => _graph.PostAsync(
                        new GraphRequest(Root + "/resourceRequests"),
                        new JsonObject { ["requestType"] = "adminRemove", ["resource"] = new JsonObject { ["id"] = resource.RequireId() }, ["catalog"] = new JsonObject { ["id"] = catalogId } },
                        cancellationToken));
            }

            await TryAsync($"catalog '{catalog.Text("displayName")}' ({catalogId})", () => _graph.DeleteAsync(new GraphRequest($"{Root}/catalogs/{catalogId}"), cancellationToken));
        }
    }

    private async Task RemovePackageAsync(JsonObject package, CancellationToken cancellationToken)
    {
        string packageId = package.RequireId();
        string name = $"access package '{package.Text("displayName")}' ({packageId})";
        foreach (var policy in await _graph.ListAsync(new GraphRequest(Root + "/assignmentPolicies").WithFilter($"accessPackage/id eq '{packageId}'"), cancellationToken))
        {
            await TryAsync($"assignment policy '{policy.Text("displayName")}' of {name}", () => _graph.DeleteAsync(new GraphRequest($"{Root}/assignmentPolicies/{policy.RequireId()}"), cancellationToken));
        }

        var expanded = await _graph.GetObjectAsync(new GraphRequest($"{Root}/accessPackages/{packageId}").WithExpand("resourceRoleScopes($expand=role,scope)"), cancellationToken);
        foreach (var scope in (expanded?["resourceRoleScopes"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            await TryAsync($"resource role scope {scope.RequireId()} of {name}", () => _graph.DeleteAsync(new GraphRequest($"{Root}/accessPackages/{packageId}/resourceRoleScopes/{scope.RequireId()}"), cancellationToken));
        }

        await TryAsync(name, () => _graph.DeleteAsync(new GraphRequest($"{Root}/accessPackages/{packageId}"), cancellationToken));
    }

    private async Task TryAsync(string what, Func<Task> delete)
    {
        try
        {
            await delete();
            _log.Add("deleted " + what);
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            _log.Add("already gone: " + what);
        }
        catch (GraphException ex)
        {
            _leftovers.Add($"{what}: {ex.Code}: {ex.GraphMessage}");
        }
    }
}
