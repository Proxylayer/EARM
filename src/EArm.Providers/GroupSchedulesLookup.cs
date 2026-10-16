using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Validation;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using EArm.Graph;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>The identity of a PIM for Groups schedule (an eligibility or an active assignment): the target group, the principal and the relationship (<c>member</c> or <c>owner</c>).</summary>
public sealed record GroupScheduleTuple(string GroupId, string PrincipalId, string AccessId);

/// <summary>
/// The Graph side of PIM for Groups, shared by <see cref="GroupEligibilityScheduleRequestsProvider"/> and <see cref="GroupAssignmentScheduleRequestsProvider"/>:
/// the schedules of a group (<c>GET {schedules}?$filter=groupId eq '…'</c>; Graph requires a groupId or principalId filter), matched client-side on
/// principalId and accessId through <see cref="PimScheduleRequests.ListSchedulesAsync"/>; the request body of <c>POST {requests}</c>; the comparable live
/// view; and the target group read (<c>GET /groups/{id}</c> or <c>GET /groups(uniqueName='…')</c>) with the kinds PIM for Groups refuses (dynamic
/// membership, synchronised from on-premises). The schedule collection and an extra filter (an active assignment keeps <c>assignmentType</c> assigned)
/// are the caller's. Never cached.
/// </summary>
public static class GroupSchedulesLookup
{
    /// <summary>The Graph collection of eligibility schedules (current and future-dated eligibilities).</summary>
    public const string EligibilitySchedulesPath = "/identityGovernance/privilegedAccess/group/eligibilitySchedules";

    /// <summary>The Graph collection every eligibility write goes through.</summary>
    public const string EligibilityRequestsPath = "/identityGovernance/privilegedAccess/group/eligibilityScheduleRequests";

    /// <summary>The Graph collection of active assignment schedules (PIM assignments, activations and the memberships of an onboarded group).</summary>
    public const string AssignmentSchedulesPath = "/identityGovernance/privilegedAccess/group/assignmentSchedules";

    /// <summary>The Graph collection every active assignment write goes through.</summary>
    public const string AssignmentRequestsPath = "/identityGovernance/privilegedAccess/group/assignmentScheduleRequests";

    /// <summary>The group properties the target group check selects.</summary>
    public static readonly string[] GroupProperties = ["id", "uniqueName", "displayName", "groupTypes", "onPremisesSyncEnabled"];

    /// <summary>
    /// The Direct schedules of a group in <paramref name="schedulesPath"/>: <c>GET {schedulesPath}?$filter=groupId eq '{id}'</c> (every page), kept to
    /// those <paramref name="keep"/> accepts. Inherited schedules and revoked or failed ones are left out.
    /// </summary>
    public static Task<IReadOnlyList<JsonObject>> ReadGroupSchedulesAsync(IGraphClient graph, string schedulesPath, Func<JsonObject, bool> keep, string groupId, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentNullException.ThrowIfNull(keep);
        return PimScheduleRequests.ListSchedulesAsync(graph, schedulesPath, GroupFilter(groupId), s => JsonObjects.KeyEquals(JsonObjects.GetString(s, "groupId"), groupId) && keep(s), api, hint, cancellationToken);
    }

    /// <summary>
    /// The Direct schedule of the tuple in <paramref name="schedulesPath"/>, or <c>null</c>: <c>GET {schedulesPath}?$filter=groupId eq '…' and
    /// principalId eq '…'</c>, re-matched client-side on the group, the principal (GUID-aware) and the accessId.
    /// </summary>
    public static async Task<JsonObject?> FindAsync(IGraphClient graph, string schedulesPath, Func<JsonObject, bool> keep, GroupScheduleTuple tuple, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tuple);
        ArgumentNullException.ThrowIfNull(keep);
        string filter = GroupFilter(tuple.GroupId) + " and principalId eq '" + PimScheduleRequests.Quote(RoleAssignmentsRules.NormalizeId(tuple.PrincipalId)) + "'";
        var schedules = await PimScheduleRequests.ListSchedulesAsync(graph, schedulesPath, filter, s => Matches(s, tuple) && keep(s), api, hint, cancellationToken).ConfigureAwait(false);
        return schedules.FirstOrDefault();
    }

    /// <summary>True when a live schedule (or request) carries the tuple's group, principal and accessId.</summary>
    public static bool Matches(JsonObject schedule, GroupScheduleTuple tuple)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(tuple);
        return JsonObjects.KeyEquals(JsonObjects.GetString(schedule, "groupId"), tuple.GroupId)
            && JsonObjects.KeyEquals(JsonObjects.GetString(schedule, "principalId"), tuple.PrincipalId)
            && string.Equals(JsonObjects.GetString(schedule, "accessId")?.Trim(), tuple.AccessId.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The tuple as the request body, the applied extras and the live view carry it (ids normalised, accessId lowercase).</summary>
    public static JsonObject TupleObject(GroupScheduleTuple tuple)
    {
        ArgumentNullException.ThrowIfNull(tuple);
        return new JsonObject
        {
            ["accessId"] = GroupEligibilityScheduleRules.NormalizeAccessId(tuple.AccessId) ?? tuple.AccessId,
            ["groupId"] = RoleAssignmentsRules.NormalizeId(tuple.GroupId),
            ["principalId"] = RoleAssignmentsRules.NormalizeId(tuple.PrincipalId),
        };
    }

    /// <summary>
    /// The body of <c>POST {requests}</c>: the action, the tuple, the schedule (Graph documents <c>scheduleInfo</c> as required on every
    /// request), and justification / ticketInfo when given (request metadata).
    /// </summary>
    public static JsonObject RequestBody(string action, GroupScheduleTuple tuple, JsonNode? scheduleInfo, JsonNode? justification, JsonNode? ticketInfo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(tuple);
        var body = new JsonObject { ["action"] = action };
        foreach (var pair in TupleObject(tuple).ToList())
        {
            body[pair.Key] = pair.Value?.DeepClone();
        }

        return PimScheduleRequests.WithScheduleAndMetadata(body, scheduleInfo, justification, ticketInfo);
    }

    /// <summary>The comparable view of a live schedule: groupId, principalId, accessId and <c>scheduleInfo</c> (non-null members only).</summary>
    public static JsonObject LiveView(JsonObject live)
    {
        ArgumentNullException.ThrowIfNull(live);
        var view = new JsonObject();
        if (JsonObjects.GetString(live, "groupId") is { } groupId)
        {
            view["groupId"] = RoleAssignmentsRules.NormalizeId(groupId);
        }

        if (JsonObjects.GetString(live, "principalId") is { } principalId)
        {
            view["principalId"] = RoleAssignmentsRules.NormalizeId(principalId);
        }

        if (JsonObjects.GetString(live, "accessId") is { } accessId)
        {
            view["accessId"] = GroupEligibilityScheduleRules.NormalizeAccessId(accessId) ?? accessId;
        }

        if (PimScheduleComparison.ScheduleView(live) is { } schedule)
        {
            view["scheduleInfo"] = schedule;
        }

        return view;
    }

    /// <summary>
    /// The target group: <c>GET /groups/{id}</c> for an object id, else <c>GET /groups(uniqueName='…')</c>, selecting <see cref="GroupProperties"/>;
    /// <c>null</c> when it does not exist.
    /// </summary>
    public static async Task<JsonObject?> ReadGroupAsync(IGraphClient graph, string idOrUniqueName, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(idOrUniqueName);
        string path = RoleAssignmentsRules.IsGuid(idOrUniqueName)
            ? "/groups/" + GraphPath.Segment(RoleAssignmentsRules.NormalizeId(idOrUniqueName))
            : GraphPath.AlternateKey("/groups", "uniqueName", idOrUniqueName.Trim());
        return await graph.GetAsync(new GraphRequest(path, api, hint: hint).WithSelect(GroupProperties), cancellationToken).ConfigureAwait(false) as JsonObject;
    }

    /// <summary>Why PIM for Groups cannot govern the group (dynamic membership, synchronised from on-premises), or <c>null</c> when it can.</summary>
    public static string? UnsupportedGroupReason(JsonObject group)
    {
        ArgumentNullException.ThrowIfNull(group);
        string name = JsonObjects.GetString(group, "uniqueName") ?? JsonObjects.GetString(group, "displayName") ?? JsonObjects.GetId(group) ?? "?";
        if (JsonObjects.TryGetIgnoreCase(group, "groupTypes", out var types) && types is JsonArray array
            && array.Any(t => JsonValues.TryGetString(t, out var type) && type.Equals("DynamicMembership", StringComparison.OrdinalIgnoreCase)))
        {
            return $"group '{name}' has dynamic membership; PIM for Groups cannot govern dynamic membership groups (Learn: concept-pim-for-groups). Use an assigned-membership group.";
        }

        if (JsonObjects.TryGetIgnoreCase(group, "onPremisesSyncEnabled", out var synced) && JsonValues.TryGetBool(synced, out bool isSynced) && isSynced)
        {
            return $"group '{name}' is synchronised from on-premises (onPremisesSyncEnabled: true); PIM for Groups cannot govern synchronised groups (Learn: concept-pim-for-groups). Use a cloud group.";
        }

        return null;
    }

    /// <summary>
    /// The <see cref="EngineDiagnosticCodes.PimGroupAccessDenied"/> exception for a 403 (<c>UnauthorizedAccessException</c>) from a PIM for Groups
    /// endpoint: the active roles the caller needs (Learn: privilegedaccessgroup-post-eligibilityschedulerequests,
    /// privilegedaccessgroup-list-eligibilityschedulerequests and their assignment counterparts), with Graph's answer. Under
    /// <see cref="RetryHint.ExpectReplicationLag"/> the client has already repeated the 403 on the replication schedule
    /// (<see cref="GraphException.IsPimGroupReplicationForbidden"/>): Graph answers it for a group created seconds earlier.
    /// </summary>
    public static ProviderException AccessDenied(GraphException ex, string subject, ResourceIdentifier? id)
    {
        ArgumentNullException.ThrowIfNull(ex);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        return new ProviderException(
            EngineDiagnosticCodes.PimGroupAccessDenied,
            $"{subject}: Graph refused the PIM for Groups call ({ex.Message}). The signed-in user needs an ACTIVE (not just eligible) directory-scope role: "
            + "Groups Administrator, Identity Governance Administrator, User Administrator or Directory Writers for a group that is not role-assignable; "
            + "Privileged Role Administrator for a role-assignable group; or ownership of the group. Privileged Role Administrator alone does not cover groups "
            + "that are not role-assignable. Activate the role in PIM (or assign it), then sign in again (earm auth login) so the token carries it. An application "
            + "needs PrivilegedEligibilitySchedule.ReadWrite.AzureADGroup / PrivilegedAssignmentSchedule.ReadWrite.AzureADGroup (Learn: "
            + "privilegedaccessgroup-post-eligibilityschedulerequests, privilegedaccessgroup-list-eligibilityschedulerequests). If the group (or the principal) was "
            + "just created, PIM may need more time: Graph answers this 403 for a while after the group is created (EARM already retried it for 90 s when the "
            + "group was created in the same run); run the deployment again in a minute.",
            id);
    }

    private static string GroupFilter(string groupId)
    {
        return "groupId eq '" + PimScheduleRequests.Quote(RoleAssignmentsRules.NormalizeId(groupId)) + "'";
    }
}
