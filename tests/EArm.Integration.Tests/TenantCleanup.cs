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
/// right before it is sent. Failures never throw: they are logged and returned as leftovers. With <c>EARM_IT_ALLOW_PIM=1</c> the PIM schedules of the
/// prefixed and tracked principals are removed first, before any object is deleted (<see cref="PimCleanup"/>).
/// </summary>
internal sealed partial class TenantCleanup
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
    private readonly bool _includeAccessPackages;

    /// <param name="graph">The fixture's Graph client.</param>
    /// <param name="prefix">The run prefix; only objects whose name starts with it are touched.</param>
    /// <param name="includePim">
    /// True (<c>EARM_IT_ALLOW_PIM=1</c>): the PIM schedules of the prefixed and tracked principals are removed first (<see cref="PimCleanup"/>); the run consented the
    /// PIM scopes only then.
    /// </param>
    /// <param name="includeAccessPackages">True (<c>EARM_IT_ALLOW_ACCESS_PACKAGES=1</c>): the prefixed entitlement management catalogs are removed with their contents before the groups (<see cref="AccessPackageCleanup"/>).</param>
    public TenantCleanup(IGraphClient graph, string prefix, bool includePim = false, bool includeAccessPackages = false)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (!IntegrationSettings.IsValidPrefix(prefix))
        {
            throw new ArgumentException($"Prefix '{prefix}' is too short or malformed for a tenant sweep.", nameof(prefix));
        }

        _graph = graph;
        _prefix = prefix;
        _includePim = includePim;
        _includeAccessPackages = includeAccessPackages;
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

        // Access packages before groups: a catalog resource (and a role scope) keeps referring to its group.
        if (_includeAccessPackages)
        {
            var accessPackages = new AccessPackageCleanup(_graph, _prefix);
            await accessPackages.SweepAsync(cancellationToken);
            _log.AddRange(accessPackages.Entries);
            foreach (string leftover in accessPackages.Leftovers)
            {
                Leftover(leftover, null);
            }
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
