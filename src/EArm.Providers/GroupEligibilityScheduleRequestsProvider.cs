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
/// <c>Microsoft.Entra/privilegedAccess/groupEligibilityScheduleRequests</c>: PIM for Groups eligibility, a principal (a user or a group) made eligible
/// for the membership or the ownership (<c>accessId</c>) of a group. No ARM analogue; the shape follows Graph's
/// <c>privilegedAccessGroupEligibilityScheduleRequest</c> and the schedule / metadata rules of <c>roleEligibilityScheduleRequests</c>. The resource
/// name is a deterministic GUID (a label); the eligibility is found by (<c>groupId</c>, <c>principalId</c>, <c>accessId</c>) and read live on every run.
/// Sugar: <c>group</c> (the group's uniqueName or object id) → <c>groupId</c>. The pure rules are <see cref="GroupEligibilityScheduleRules"/>; the shared
/// lifecycle (adminAssign / adminUpdate / adminRemove, comparison, RoleAssignmentExists adoption, PimPolicyViolation) is
/// <see cref="PimScheduleRequestsProvider{TTuple}"/>.
/// <para>
/// Requests per operation:
/// <list type="bullet">
/// <item><b>Prepare</b>: <c>GET /groups/{id}</c> (or <c>GET /groups(uniqueName='…')</c> for the sugar) selecting groupTypes and onPremisesSyncEnabled:
/// a dynamic membership or on-premises synced group is refused (<see cref="InvalidEligibility"/>); then <c>GET /directoryObjects/{principalId}</c>: only
/// users and groups can be eligible.</item>
/// <item><b>Read</b>: <c>GET /identityGovernance/privilegedAccess/group/eligibilitySchedules?$filter=groupId eq … and principalId eq …</c>, matched on
/// accessId and kept to <c>memberType</c> Direct (<see cref="GroupEligibilitySchedulesLookup"/>).</item>
/// <item><b>Create / Update / Delete</b>: <c>POST /identityGovernance/privilegedAccess/group/eligibilityScheduleRequests</c> with action adminAssign /
/// adminUpdate / adminRemove; <c>scheduleInfo</c> is sent on every request (Graph documents it as required), the remove carries the live schedule.</item>
/// </list>
/// The first request for a group onboards it in PIM for Groups, permanently (it cannot be offboarded). A group's default PIM policy can forbid permanent
/// eligibility: the <see cref="PimScheduleRequestsProvider{TTuple}.PimPolicyViolation"/> message points at an expiration or the group's policy. A changed
/// tuple is a new eligibility: the old one is reported as retained (orphan scope: the declared target groups, <see cref="OrphanScope.DeclaredTargetGroups"/>).
/// Eligibility never touches the group's members or owners, so it does not conflict with a pruning <c>members</c> / <c>owners</c> list.
/// </para>
/// </summary>
[EntraProvider(ResourceType)]
public sealed class GroupEligibilityScheduleRequestsProvider : PimScheduleRequestsProvider<GroupEligibilityTuple>
{
    public const string ResourceType = GroupEligibilityScheduleRules.ResourceType;

    /// <summary><see cref="ProviderException"/> code: the declared eligibility cannot be requested (unknown or unsupported group, wrong principal kind, bad shape).</summary>
    public const string InvalidEligibility = EngineDiagnosticCodes.GroupEligibilityInvalid;

    private static readonly IReadOnlyList<(string Sugar, string Canonical)> Sugar = [("group", "groupId")];

    public GroupEligibilityScheduleRequestsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog, ResourceType)
    {
    }

    public GroupEligibilityScheduleRequestsProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog, ResourceType), catalog)
    {
    }

    protected override string RequestsPath => GroupEligibilitySchedulesLookup.RequestsPath;

    protected override string InvalidCode => InvalidEligibility;

    protected override string PolicyOwner => "the group's PIM policy";

    protected override string PolicyAdvice => "A group's PIM policy often forbids permanent eligibility (its Expiration_Admin_Eligibility rule): declare scheduleInfo with an AfterDateTime or AfterDuration expiration, or allow permanent eligibility in the group's policy (Microsoft.Entra/roleManagementPolicies); satisfy any justification or ticketing rule with justification / ticketInfo.";

    protected override string NameExample => "[guid('ops-admins', variables('principalId'), 'member')]";

    protected override IReadOnlyList<(string Sugar, string Canonical)> SugarProperties => Sugar;

    protected override IReadOnlyList<PimRequestFinding> Check(JsonObject properties)
    {
        return GroupEligibilityScheduleRules.Check(properties, IsOpaque);
    }

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
            var found = await GroupEligibilitySchedulesLookup.ReadGroupAsync(graph, group, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false)
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
            var found = await GroupEligibilitySchedulesLookup.ReadGroupAsync(graph, declaredGroupId, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false)
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
                throw Invalid(desired, $"principalId {principalId} is the target group itself; a group cannot be eligible for its own membership or ownership.");
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
                    throw Invalid(desired, $"principal {principalId} is not a user or a group; PIM for Groups makes only users and groups eligible for a group's membership or ownership.");
            }

            Set(properties, "principalId", JsonValue.Create(RoleAssignmentsRules.NormalizeId(principalId)));
        }

        Normalize(properties);
    }

    private void RequireSupportedGroup(ResourceInstance desired, JsonObject group)
    {
        if (GroupEligibilitySchedulesLookup.UnsupportedGroupReason(group) is { } reason)
        {
            throw Invalid(desired, reason);
        }
    }

    // ---- the type's Graph side ----

    protected override Task<JsonObject?> FindAsync(IGraphClient graph, GroupEligibilityTuple tuple, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        return GroupEligibilitySchedulesLookup.FindAsync(graph, tuple, api, hint, cancellationToken);
    }

    protected override JsonObject RequestBody(string action, GroupEligibilityTuple tuple, JsonNode? scheduleInfo, JsonNode? justification, JsonNode? ticketInfo)
    {
        return GroupEligibilitySchedulesLookup.RequestBody(action, tuple, scheduleInfo, justification, ticketInfo);
    }

    /// <summary>Graph documents <c>scheduleInfo</c> as required on every request: the adminRemove carries the live schedule.</summary>
    protected override JsonNode? RemoveSchedule(ExistingResource live)
    {
        ArgumentNullException.ThrowIfNull(live);
        return PimScheduleComparison.ScheduleView(live.Raw) ?? new JsonObject { ["expiration"] = new JsonObject { ["type"] = PimScheduleRules.NoExpiration } };
    }

    protected override JsonObject Extra(GroupEligibilityTuple tuple)
    {
        return GroupEligibilitySchedulesLookup.TupleObject(tuple);
    }

    protected override JsonObject LiveView(JsonObject live)
    {
        return GroupEligibilitySchedulesLookup.LiveView(live);
    }

    protected override GroupEligibilityTuple RequireTuple(ResourceKey key, ResourceIdentifier? id)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!key.TryGet("groupId", out var groupId) || !key.TryGet("principalId", out var principalId) || !key.TryGet("accessId", out var accessId))
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: the key {key.Describe()} does not carry groupId, principalId and accessId.", id);
        }

        return new GroupEligibilityTuple(groupId, principalId, GroupEligibilityScheduleRules.NormalizeAccessId(accessId) ?? accessId);
    }

    // ---- orphan report ----

    /// <summary>The orphan report lists the Direct eligibilities of the target groups the declared resources name (reported as retained).</summary>
    public override OrphanScope OrphanScope => OrphanScope.DeclaredTargetGroups;

    public override async Task<IReadOnlyList<OrphanCandidate>> ListOrphanCandidatesAsync(OrphanQuery query, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(graph);
        var candidates = new List<OrphanCandidate>();
        foreach (var groupId in OrphanListing.DistinctKeyValues(query.DeclaredKeys, "groupId"))
        {
            foreach (var schedule in await GroupEligibilitySchedulesLookup.ReadGroupSchedulesAsync(graph, groupId, query.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false))
            {
                if (JsonObjects.GetId(schedule) is { } id)
                {
                    candidates.Add(new OrphanCandidate(id, id, null, "eligible " + (JsonObjects.GetString(schedule, "accessId") ?? "member") + ": " + OrphanListing.Describe(schedule, "groupId", "principalId", "accessId")));
                }
            }
        }

        return candidates;
    }
}
