using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using EArm.Graph;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>The identity of a directory role eligibility: the principal, the role definition and one scope (a directory scope, or an app scope).</summary>
public sealed record EligibilityTuple(string PrincipalId, string RoleDefinitionId, string Scope, bool IsAppScope)
{
    /// <summary>The scope property Graph uses for <see cref="Scope"/>: <c>appScopeId</c> or <c>directoryScopeId</c>.</summary>
    public string ScopeProperty => IsAppScope ? "appScopeId" : "directoryScopeId";
}

/// <summary>
/// The Graph side of PIM eligibility for directory roles, shared by <see cref="RoleEligibilityScheduleRequestsProvider"/>: the eligibility schedules of
/// a principal (<c>GET /roleManagement/directory/roleEligibilitySchedules?$filter=…</c>, every page, kept to <c>memberType</c> Direct and matched
/// client-side), the request body of <c>POST /roleManagement/directory/roleEligibilityScheduleRequests</c>, and the classification of PIM errors
/// (an existing eligibility, a policy rule that failed, an eligibility that is already gone). Never cached.
/// </summary>
public static class RoleEligibilitySchedulesLookup
{
    /// <summary>The Graph collection of eligibility schedules (current and future-dated eligibilities; schedule instances list only the current ones).</summary>
    public const string SchedulesPath = "/roleManagement/directory/roleEligibilitySchedules";

    /// <summary>The Graph collection every write goes through.</summary>
    public const string RequestsPath = "/roleManagement/directory/roleEligibilityScheduleRequests";

    public const string AdminAssign = "adminAssign";

    public const string AdminUpdate = "adminUpdate";

    public const string AdminRemove = "adminRemove";

    /// <summary>Request states that mean the request did not take effect.</summary>
    private static readonly string[] FailedStatuses = ["Failed", "Denied", "Canceled", "Revoked", "FailedAsResourceIsLocked"];

    private static readonly Regex PolicyViolationPattern = new(
        "PolicyValidationFailed|policy rules? failed|(Expiration|Justification|Ticketing|Mfa|Approval|Enablement)Rule",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex GonePattern = new(
        "RoleAssignmentDoesNotExist|RoleEligibilityScheduleNotFound|does not exist|not found",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// The Direct eligibility schedules of a principal: <c>GET …/roleEligibilitySchedules?$filter=principalId eq '{id}'</c> (every page). Inherited
    /// schedules (<c>memberType</c> Group) and revoked or failed ones are left out.
    /// </summary>
    public static Task<IReadOnlyList<JsonObject>> ReadPrincipalSchedulesAsync(IGraphClient graph, string principalId, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        return ListAsync(graph, "principalId eq '" + Quote(RoleAssignmentsRules.NormalizeId(principalId)) + "'", principalId, api, hint, cancellationToken);
    }

    /// <summary>
    /// The Direct eligibility of the tuple, or <c>null</c>: <c>GET …/roleEligibilitySchedules?$filter=principalId eq '…' and roleDefinitionId eq '…' and
    /// directoryScopeId eq '…'</c> (or <c>appScopeId</c>), re-matched client-side (GUID-aware, scope canonicalised).
    /// </summary>
    public static async Task<JsonObject?> FindAsync(IGraphClient graph, EligibilityTuple tuple, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tuple);
        string filter = "principalId eq '" + Quote(RoleAssignmentsRules.NormalizeId(tuple.PrincipalId))
            + "' and roleDefinitionId eq '" + Quote(RoleAssignmentsRules.NormalizeId(tuple.RoleDefinitionId))
            + "' and " + tuple.ScopeProperty + " eq '" + Quote(CanonicalScope(tuple)) + "'";
        var schedules = await ListAsync(graph, filter, tuple.PrincipalId, api, hint, cancellationToken).ConfigureAwait(false);
        return schedules.FirstOrDefault(s => Matches(s, tuple));
    }

    private static async Task<IReadOnlyList<JsonObject>> ListAsync(IGraphClient graph, string filter, string principalId, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var request = new GraphRequest(SchedulesPath, api, hint: hint).WithFilter(filter);
        var items = new List<JsonObject>();
        await foreach (var item in graph.GetAllPagesAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (item is JsonObject schedule && JsonObjects.KeyEquals(JsonObjects.GetString(schedule, "principalId"), principalId) && IsDirectAndActive(schedule))
            {
                items.Add(schedule);
            }
        }

        return items;
    }

    /// <summary>True for a schedule assigned to the principal itself (<c>memberType</c> Direct or absent) that is not revoked or failed.</summary>
    public static bool IsDirectAndActive(JsonObject schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        string? memberType = JsonObjects.GetString(schedule, "memberType");
        string? status = JsonObjects.GetString(schedule, "status");
        return (memberType is null || memberType.Equals("Direct", StringComparison.OrdinalIgnoreCase))
            && (status is null || !FailedStatuses.Contains(status, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>True when a live schedule (or request) carries the tuple's role and scope.</summary>
    public static bool Matches(JsonObject schedule, EligibilityTuple tuple)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(tuple);
        if (!JsonObjects.KeyEquals(JsonObjects.GetString(schedule, "roleDefinitionId"), tuple.RoleDefinitionId))
        {
            return false;
        }

        string? scope = JsonObjects.GetString(schedule, tuple.ScopeProperty);
        return tuple.IsAppScope
            ? string.Equals(scope?.Trim(), tuple.Scope.Trim(), StringComparison.OrdinalIgnoreCase)
            : scope is not null && RoleAssignmentsRules.ScopeEquals(scope, tuple.Scope);
    }

    /// <summary>The tuple a live schedule describes (its directory scope, else its app scope), or <c>null</c> when a member is missing.</summary>
    public static EligibilityTuple? TupleOf(JsonObject schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        string? principalId = JsonObjects.GetString(schedule, "principalId");
        string? roleDefinitionId = JsonObjects.GetString(schedule, "roleDefinitionId");
        if (principalId is null || roleDefinitionId is null)
        {
            return null;
        }

        return JsonObjects.GetString(schedule, "directoryScopeId") is { } directoryScope
            ? new EligibilityTuple(principalId, roleDefinitionId, RoleAssignmentsRules.NormalizeScope(directoryScope), IsAppScope: false)
            : JsonObjects.GetString(schedule, "appScopeId") is { } appScope ? new EligibilityTuple(principalId, roleDefinitionId, appScope, IsAppScope: true) : null;
    }

    /// <summary>
    /// The body of <c>POST …/roleEligibilityScheduleRequests</c>: the action, the tuple and, for adminAssign / adminUpdate, the schedule; justification and
    /// ticketInfo when given (request metadata).
    /// </summary>
    public static JsonObject RequestBody(string action, EligibilityTuple tuple, JsonNode? scheduleInfo, JsonNode? justification, JsonNode? ticketInfo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(tuple);
        var body = new JsonObject
        {
            ["action"] = action,
            ["principalId"] = RoleAssignmentsRules.NormalizeId(tuple.PrincipalId),
            ["roleDefinitionId"] = RoleAssignmentsRules.NormalizeId(tuple.RoleDefinitionId),
            [tuple.ScopeProperty] = CanonicalScope(tuple),
        };

        if (!JsonObjects.IsAbsent(justification))
        {
            body["justification"] = JsonValues.Clone(justification);
        }

        if (scheduleInfo is JsonObject)
        {
            body["scheduleInfo"] = JsonValues.Clone(scheduleInfo);
        }

        if (ticketInfo is JsonObject)
        {
            body["ticketInfo"] = JsonValues.Clone(ticketInfo);
        }

        return body;
    }

    /// <summary>True when Graph refused a request because a rule of the role's PIM policy failed (Graph's <c>RoleAssignmentRequestPolicyValidationFailed</c>).</summary>
    public static bool IsPolicyViolation(GraphException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.Status == System.Net.HttpStatusCode.BadRequest
            && (PolicyViolationPattern.IsMatch(ex.Code ?? string.Empty) || PolicyViolationPattern.IsMatch(ex.GraphMessage ?? string.Empty));
    }

    /// <summary>True when Graph answered an adminRemove with "the eligibility does not exist": nothing is left to remove.</summary>
    public static bool IsAlreadyGone(GraphException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.IsNotFound
            || (ex.Status == System.Net.HttpStatusCode.BadRequest && (GonePattern.IsMatch(ex.Code ?? string.Empty) || GonePattern.IsMatch(ex.GraphMessage ?? string.Empty)));
    }

    /// <summary>True when a request response says the request failed (status Failed, Denied, Canceled...).</summary>
    public static bool HasFailedStatus(JsonObject? request, out string status)
    {
        status = JsonObjects.GetString(request, "status") ?? string.Empty;
        return status.Length > 0 && FailedStatuses.Contains(status, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The canonical form of the tuple's scope (directory scopes canonicalised, app scopes trimmed).</summary>
    public static string CanonicalScope(EligibilityTuple tuple)
    {
        ArgumentNullException.ThrowIfNull(tuple);
        return tuple.IsAppScope ? tuple.Scope.Trim() : RoleAssignmentsRules.NormalizeScope(tuple.Scope);
    }

    private static string Quote(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }
}
