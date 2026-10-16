using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Validation;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using EArm.Graph;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>The identity of a PIM for Groups eligibility: the target group, the principal and the relationship (<c>member</c> or <c>owner</c>).</summary>
public sealed record GroupEligibilityTuple(string GroupId, string PrincipalId, string AccessId);

/// <summary>
/// The Graph side of PIM for Groups eligibility, shared by <see cref="GroupEligibilityScheduleRequestsProvider"/>: the eligibility schedules of a group
/// (<c>GET /identityGovernance/privilegedAccess/group/eligibilitySchedules?$filter=groupId eq '…'</c>; Graph requires a groupId or principalId filter),
/// matched client-side on principalId and accessId through <see cref="PimScheduleRequests.ListSchedulesAsync"/>; the request body of
/// <c>POST …/eligibilityScheduleRequests</c>; the comparable live view; and the target group read (<c>GET /groups/{id}</c> or
/// <c>GET /groups(uniqueName='…')</c>) with the kinds PIM for Groups refuses (dynamic membership, synchronised from on-premises). Never cached.
/// </summary>
public static class GroupEligibilitySchedulesLookup
{
    /// <summary>The Graph collection of eligibility schedules (current and future-dated eligibilities).</summary>
    public const string SchedulesPath = "/identityGovernance/privilegedAccess/group/eligibilitySchedules";

    /// <summary>The Graph collection every write goes through.</summary>
    public const string RequestsPath = "/identityGovernance/privilegedAccess/group/eligibilityScheduleRequests";

    /// <summary>The group properties the target group check selects.</summary>
    public static readonly string[] GroupProperties = ["id", "uniqueName", "displayName", "groupTypes", "onPremisesSyncEnabled"];

    /// <summary>
    /// The Direct eligibility schedules of a group: <c>GET …/eligibilitySchedules?$filter=groupId eq '{id}'</c> (every page). Inherited schedules and
    /// revoked or failed ones are left out.
    /// </summary>
    public static Task<IReadOnlyList<JsonObject>> ReadGroupSchedulesAsync(IGraphClient graph, string groupId, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        return PimScheduleRequests.ListSchedulesAsync(graph, SchedulesPath, GroupFilter(groupId), s => JsonObjects.KeyEquals(JsonObjects.GetString(s, "groupId"), groupId), api, hint, cancellationToken);
    }

    /// <summary>
    /// The Direct eligibility of the tuple, or <c>null</c>: <c>GET …/eligibilitySchedules?$filter=groupId eq '…' and principalId eq '…'</c>, re-matched
    /// client-side on the group, the principal (GUID-aware) and the accessId.
    /// </summary>
    public static async Task<JsonObject?> FindAsync(IGraphClient graph, GroupEligibilityTuple tuple, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tuple);
        string filter = GroupFilter(tuple.GroupId) + " and principalId eq '" + PimScheduleRequests.Quote(RoleAssignmentsRules.NormalizeId(tuple.PrincipalId)) + "'";
        var schedules = await PimScheduleRequests.ListSchedulesAsync(graph, SchedulesPath, filter, s => Matches(s, tuple), api, hint, cancellationToken).ConfigureAwait(false);
        return schedules.FirstOrDefault();
    }

    /// <summary>True when a live schedule (or request) carries the tuple's group, principal and accessId.</summary>
    public static bool Matches(JsonObject schedule, GroupEligibilityTuple tuple)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(tuple);
        return JsonObjects.KeyEquals(JsonObjects.GetString(schedule, "groupId"), tuple.GroupId)
            && JsonObjects.KeyEquals(JsonObjects.GetString(schedule, "principalId"), tuple.PrincipalId)
            && string.Equals(JsonObjects.GetString(schedule, "accessId")?.Trim(), tuple.AccessId.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The tuple as the request body, the applied extras and the live view carry it (ids normalised, accessId lowercase).</summary>
    public static JsonObject TupleObject(GroupEligibilityTuple tuple)
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
    /// The body of <c>POST …/eligibilityScheduleRequests</c>: the action, the tuple, the schedule (Graph documents <c>scheduleInfo</c> as required on every
    /// request), and justification / ticketInfo when given (request metadata).
    /// </summary>
    public static JsonObject RequestBody(string action, GroupEligibilityTuple tuple, JsonNode? scheduleInfo, JsonNode? justification, JsonNode? ticketInfo)
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

    private static string GroupFilter(string groupId)
    {
        return "groupId eq '" + PimScheduleRequests.Quote(RoleAssignmentsRules.NormalizeId(groupId)) + "'";
    }
}
