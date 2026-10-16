using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using EArm.Graph;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>
/// What the PIM schedule request types for directory roles share (<see cref="RoleEligibilityScheduleRequestsProvider"/>,
/// <see cref="RoleAssignmentScheduleRequestsProvider"/>), shaped like ARM's <c>Microsoft.Authorization/role*ScheduleRequests</c>: the tuple
/// (<c>principalId</c>, <c>roleDefinitionId</c>, <c>directoryScopeId</c> or <c>appScopeId</c>), the sugar <c>role</c> → <c>roleDefinitionId</c> and
/// <c>administrativeUnit</c> → <c>directoryScopeId</c> (as for <c>roleAssignments</c>), the scope defaulting to <c>/</c>, and the principal check.
/// <list type="bullet">
/// <item><b>Prepare</b>: the role / unit name lookups of <see cref="RoleAssignmentsLookup"/>, then <c>GET /directoryObjects/{principalId}</c> and, for a
/// group, <c>GET /groups/{id}?$select=id,isAssignableToRole</c>: a group that is not role-assignable is refused (the type's invalid code).</item>
/// <item><b>Read</b>: <c>GET {schedules}?$filter=principalId eq … and roleDefinitionId eq … and directoryScopeId eq …</c>, keeping <c>memberType</c>
/// Direct and the type's <see cref="Keep"/> (<see cref="RoleSchedulesLookup"/>).</item>
/// </list>
/// A changed tuple is a new schedule: the old one is reported as retained (orphan scope: the declared principals).
/// </summary>
public abstract class RoleScheduleRequestsProvider : PimScheduleRequestsProvider<RoleScheduleTuple>
{
    private static readonly IReadOnlyList<(string Sugar, string Canonical)> Sugar = [("role", "roleDefinitionId"), ("administrativeUnit", "directoryScopeId")];

    protected RoleScheduleRequestsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog, string resourceType, ReplicationWait? settle = null)
        : base(definition, catalog, resourceType, settle)
    {
    }

    /// <summary>The Graph collection of the type's schedules.</summary>
    protected abstract string SchedulesPath { get; }

    /// <summary>The word the orphan report prefixes a retained schedule with ("eligible", "active").</summary>
    protected abstract string OrphanLabel { get; }

    /// <summary>The live schedules (Direct, active) that are the type's; all of them by default.</summary>
    protected virtual bool Keep(JsonObject schedule)
    {
        return true;
    }

    /// <summary>The live schedules the orphan report lists; every kept one by default.</summary>
    protected virtual bool ListsAsOrphan(JsonObject schedule)
    {
        return true;
    }

    protected override IReadOnlyList<(string Sugar, string Canonical)> SugarProperties => Sugar;

    // ---- prepare (sugar + live validation) ----

    public override async Task PrepareAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = PreparedProperties(desired);

        if (Check(properties) is { Count: > 0 } findings)
        {
            throw Invalid(desired, string.Join("; ", findings.Select(f => f.Message)));
        }

        // Role: role (template id | built-in name | custom name | custom id) → roleDefinitionId.
        if (Text(properties, "role") is { } role)
        {
            var resolved = await RoleAssignmentsLookup.ResolveRoleAsync(graph, role, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false);
            Set(properties, "roleDefinitionId", JsonValue.Create(resolved.Id ?? throw Invalid(desired, resolved.Error!)));
            Remove(properties, "role");
        }
        else if (Unknown(properties, "role") is { } unknownRole)
        {
            Set(properties, "roleDefinitionId", unknownRole);
            Remove(properties, "role");
        }
        else if (Text(properties, "roleDefinitionId") is { } roleDefinitionId)
        {
            Set(properties, "roleDefinitionId", JsonValue.Create(RoleAssignmentsRules.NormalizeId(roleDefinitionId)));
        }

        // Scope: administrativeUnit (id | name) → directoryScopeId; neither a directory nor an app scope → "/".
        if (Text(properties, "administrativeUnit") is { } unit)
        {
            var resolved = await RoleAssignmentsLookup.ResolveAdministrativeUnitScopeAsync(graph, unit, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false);
            Set(properties, "directoryScopeId", JsonValue.Create(resolved.Id ?? throw Invalid(desired, resolved.Error!)));
            Remove(properties, "administrativeUnit");
        }
        else if (Unknown(properties, "administrativeUnit") is { } unknownUnit)
        {
            Set(properties, "directoryScopeId", unknownUnit);
            Remove(properties, "administrativeUnit");
        }
        else if (Text(properties, "directoryScopeId") is { } scope)
        {
            Set(properties, "directoryScopeId", JsonValue.Create(RoleAssignmentsRules.NormalizeScope(scope)));
        }
        else if (!Declares(properties, "directoryScopeId") && !Declares(properties, "appScopeId"))
        {
            Set(properties, "directoryScopeId", JsonValue.Create(RoleAssignmentsRules.RootScope));
        }

        // Principal: must exist and be a user, a role-assignable group or a service principal.
        if (Text(properties, "principalId") is { } principalId)
        {
            string? collection = await RoleAssignmentsLookup.PrincipalCollectionAsync(graph, principalId, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false);
            bool? roleAssignable = collection == "groups"
                ? await RoleAssignmentsLookup.IsRoleAssignableGroupAsync(graph, principalId, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false)
                : null;
            if (collection == "groups" && roleAssignable != true)
            {
                throw Invalid(desired, $"group {principalId} is not role-assignable; PIM can only give a group a directory role ({ScheduleNoun}) when it was created with isAssignableToRole: true (the property is immutable, so recreate the group).");
            }

            if (RoleAssignmentsLookup.CheckPrincipalKind(collection, roleAssignable, principalId) is { } reason)
            {
                throw Invalid(desired, reason);
            }

            Set(properties, "principalId", JsonValue.Create(RoleAssignmentsRules.NormalizeId(principalId)));
        }

        Normalize(properties);
    }

    // ---- the type's Graph side ----

    protected override Task<JsonObject?> FindAsync(IGraphClient graph, RoleScheduleTuple tuple, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        return RoleSchedulesLookup.FindAsync(graph, SchedulesPath, Keep, tuple, api, hint, cancellationToken);
    }

    protected override JsonObject RequestBody(string action, RoleScheduleTuple tuple, JsonNode? scheduleInfo, JsonNode? justification, JsonNode? ticketInfo)
    {
        return RoleSchedulesLookup.RequestBody(action, tuple, scheduleInfo, justification, ticketInfo);
    }

    protected override JsonObject Extra(RoleScheduleTuple tuple)
    {
        return RoleSchedulesLookup.TupleObject(tuple);
    }

    protected override JsonObject LiveView(JsonObject live)
    {
        return RoleSchedulesLookup.LiveView(live);
    }

    protected override RoleScheduleTuple RequireTuple(ResourceKey key, ResourceIdentifier? id)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!key.TryGet("principalId", out var principalId) || !key.TryGet("roleDefinitionId", out var roleDefinitionId))
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: the key {key.Describe()} does not carry principalId and roleDefinitionId.", id);
        }

        return key.TryGet("appScopeId", out var appScope) && !key.TryGet("directoryScopeId", out _)
            ? new RoleScheduleTuple(principalId, roleDefinitionId, appScope, IsAppScope: true)
            : new RoleScheduleTuple(principalId, roleDefinitionId, key.TryGet("directoryScopeId", out var scope) ? scope : RoleAssignmentsRules.RootScope, IsAppScope: false);
    }

    // ---- orphan report ----

    /// <summary>The orphan report lists the Direct schedules of the principals the declared resources name (reported as retained).</summary>
    public override OrphanScope OrphanScope => OrphanScope.DeclaredPrincipals;

    public override async Task<IReadOnlyList<OrphanCandidate>> ListOrphanCandidatesAsync(OrphanQuery query, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(graph);
        var candidates = new List<OrphanCandidate>();
        foreach (var principalId in OrphanListing.DistinctKeyValues(query.DeclaredKeys, "principalId"))
        {
            foreach (var schedule in await RoleSchedulesLookup.ReadPrincipalSchedulesAsync(graph, SchedulesPath, Keep, principalId, query.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false))
            {
                if (ListsAsOrphan(schedule) && JsonObjects.GetId(schedule) is { } id)
                {
                    string scopeProperty = JsonObjects.GetString(schedule, "directoryScopeId") is not null ? "directoryScopeId" : "appScopeId";
                    candidates.Add(new OrphanCandidate(id, id, null, OrphanLabel + ": " + OrphanListing.Describe(schedule, "principalId", "roleDefinitionId", scopeProperty)));
                }
            }
        }

        return candidates;
    }
}
