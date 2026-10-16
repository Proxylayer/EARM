using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Types;
using EArm.Core.Validation;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Graph;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>
/// What the PIM for Groups schedule request types share (<see cref="GroupEligibilityScheduleRequestsProvider"/>,
/// <see cref="GroupAssignmentScheduleRequestsProvider"/>): the tuple (<c>groupId</c>, <c>principalId</c>, <c>accessId</c>), the sugar <c>group</c> (the
/// group's uniqueName or object id) → <c>groupId</c>, and the live checks.
/// <list type="bullet">
/// <item><b>Prepare</b>: <c>GET /groups/{id}</c> (or <c>GET /groups(uniqueName='…')</c> for the sugar) selecting groupTypes and onPremisesSyncEnabled:
/// a dynamic membership or on-premises synced group is refused (the type's invalid code); then <c>GET /directoryObjects/{principalId}</c>: only users and
/// groups, never the target group itself.</item>
/// <item><b>Read</b>: <c>GET {schedules}?$filter=groupId eq … and principalId eq …</c>, matched on accessId and kept to <c>memberType</c> Direct and the
/// type's <see cref="Keep"/> (<see cref="GroupSchedulesLookup"/>).</item>
/// <item><b>Create / Update / Delete</b>: <c>POST {requests}</c>; <c>scheduleInfo</c> is sent on every request (Graph documents it as required), the
/// remove carries the live schedule.</item>
/// </list>
/// The first request for a group onboards it in PIM for Groups, permanently. A changed tuple is a new schedule: the old one is reported as retained
/// (orphan scope: the declared target groups, <see cref="OrphanScope.DeclaredTargetGroups"/>).
/// </summary>
public abstract class GroupScheduleRequestsProvider : PimScheduleRequestsProvider<GroupScheduleTuple>
{
    private static readonly IReadOnlyList<(string Sugar, string Canonical)> Sugar = [("group", "groupId")];

    protected GroupScheduleRequestsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog, string resourceType)
        : base(definition, catalog, resourceType)
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

    protected override void NormalizeRules(JsonObject properties)
    {
        GroupEligibilityScheduleRules.Normalize(properties, IsOpaque);
    }

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

        // Group: group (uniqueName | object id) → groupId; the target group must exist and be a kind PIM for Groups governs.
        string? groupId = null;
        if (Text(properties, "group") is { } group)
        {
            var found = await GroupSchedulesLookup.ReadGroupAsync(graph, group, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false)
                ?? throw Invalid(desired, $"no group with uniqueName or id '{group}' exists in the tenant; deploy a Microsoft.Entra/groups resource for it (and depend on it), or declare groupId (for example [reference(resourceId('Microsoft.Entra/groups', '{group}')).id]).");
            RequireSupportedGroup(desired, found);
            groupId = RoleAssignmentsRules.NormalizeId(JsonObjects.GetId(found) ?? throw Invalid(desired, $"the group '{group}' was read without an id."));
            Set(properties, "groupId", JsonValue.Create(groupId));
            Remove(properties, "group");
        }
        else if (Unknown(properties, "group") is { } unknownGroup)
        {
            Set(properties, "groupId", unknownGroup);
            Remove(properties, "group");
        }
        else if (Text(properties, "groupId") is { } declaredGroupId)
        {
            var found = await GroupSchedulesLookup.ReadGroupAsync(graph, declaredGroupId, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false)
                ?? throw Invalid(desired, $"group {declaredGroupId} does not exist.");
            RequireSupportedGroup(desired, found);
            groupId = RoleAssignmentsRules.NormalizeId(declaredGroupId);
            Set(properties, "groupId", JsonValue.Create(groupId));
        }

        // Principal: must exist and be a user or a group (PIM for Groups governs users and nested groups), and not the target group itself.
        if (Text(properties, "principalId") is { } principalId)
        {
            if (groupId is not null && JsonObjects.KeyEquals(groupId, principalId))
            {
                throw Invalid(desired, $"principalId {principalId} is the target group itself; a group cannot hold its own membership or ownership.");
            }

            string? collection = await RoleAssignmentsLookup.PrincipalCollectionAsync(graph, principalId, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false);
            switch (collection?.ToLowerInvariant())
            {
                case "users":
                case "groups":
                    break;
                case null:
                    throw Invalid(desired, $"principal {principalId} does not exist.");
                default:
                    throw Invalid(desired, $"principal {principalId} is not a user or a group; PIM for Groups gives only users and groups a group's membership or ownership.");
            }

            Set(properties, "principalId", JsonValue.Create(RoleAssignmentsRules.NormalizeId(principalId)));
        }

        Normalize(properties);
    }

    private void RequireSupportedGroup(ResourceInstance desired, JsonObject group)
    {
        if (GroupSchedulesLookup.UnsupportedGroupReason(group) is { } reason)
        {
            throw Invalid(desired, reason);
        }
    }

    // ---- the type's Graph side ----

    protected override Task<JsonObject?> FindAsync(IGraphClient graph, GroupScheduleTuple tuple, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        return GroupSchedulesLookup.FindAsync(graph, SchedulesPath, Keep, tuple, api, hint, cancellationToken);
    }

    protected override JsonObject RequestBody(string action, GroupScheduleTuple tuple, JsonNode? scheduleInfo, JsonNode? justification, JsonNode? ticketInfo)
    {
        return GroupSchedulesLookup.RequestBody(action, tuple, scheduleInfo, justification, ticketInfo);
    }

    /// <summary>Graph documents <c>scheduleInfo</c> as required on every request: the adminRemove carries the live schedule.</summary>
    protected override JsonNode? RemoveSchedule(ExistingResource live)
    {
        ArgumentNullException.ThrowIfNull(live);
        return PimScheduleComparison.ScheduleView(live.Raw) ?? new JsonObject { ["expiration"] = new JsonObject { ["type"] = PimScheduleRules.NoExpiration } };
    }

    protected override JsonObject Extra(GroupScheduleTuple tuple)
    {
        return GroupSchedulesLookup.TupleObject(tuple);
    }

    protected override JsonObject LiveView(JsonObject live)
    {
        return GroupSchedulesLookup.LiveView(live);
    }

    protected override GroupScheduleTuple RequireTuple(ResourceKey key, ResourceIdentifier? id)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!key.TryGet("groupId", out var groupId) || !key.TryGet("principalId", out var principalId) || !key.TryGet("accessId", out var accessId))
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: the key {key.Describe()} does not carry groupId, principalId and accessId.", id);
        }

        return new GroupScheduleTuple(groupId, principalId, GroupEligibilityScheduleRules.NormalizeAccessId(accessId) ?? accessId);
    }

    // ---- orphan report ----

    /// <summary>The orphan report lists the Direct schedules of the target groups the declared resources name (reported as retained).</summary>
    public override OrphanScope OrphanScope => OrphanScope.DeclaredTargetGroups;

    public override async Task<IReadOnlyList<OrphanCandidate>> ListOrphanCandidatesAsync(OrphanQuery query, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(graph);
        var candidates = new List<OrphanCandidate>();
        foreach (var groupId in OrphanListing.DistinctKeyValues(query.DeclaredKeys, "groupId"))
        {
            foreach (var schedule in await GroupSchedulesLookup.ReadGroupSchedulesAsync(graph, SchedulesPath, Keep, groupId, query.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false))
            {
                if (ListsAsOrphan(schedule) && JsonObjects.GetId(schedule) is { } id)
                {
                    candidates.Add(new OrphanCandidate(id, id, null, OrphanLabel + " " + (JsonObjects.GetString(schedule, "accessId") ?? "member") + ": " + OrphanListing.Describe(schedule, "groupId", "principalId", "accessId")));
                }
            }
        }

        return candidates;
    }
}
