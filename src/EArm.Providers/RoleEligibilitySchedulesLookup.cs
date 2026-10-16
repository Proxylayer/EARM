using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine.Providers;
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
/// a principal (<c>GET /roleManagement/directory/roleEligibilitySchedules?$filter=…</c>, through <see cref="PimScheduleRequests.ListSchedulesAsync"/>,
/// matched client-side), the request body of <c>POST /roleManagement/directory/roleEligibilityScheduleRequests</c> and the comparable live view. Never cached.
/// </summary>
public static class RoleEligibilitySchedulesLookup
{
    /// <summary>The Graph collection of eligibility schedules (current and future-dated eligibilities; schedule instances list only the current ones).</summary>
    public const string SchedulesPath = "/roleManagement/directory/roleEligibilitySchedules";

    /// <summary>The Graph collection every write goes through.</summary>
    public const string RequestsPath = "/roleManagement/directory/roleEligibilityScheduleRequests";

    /// <summary>
    /// The Direct eligibility schedules of a principal: <c>GET …/roleEligibilitySchedules?$filter=principalId eq '{id}'</c> (every page). Inherited
    /// schedules (<c>memberType</c> Group) and revoked or failed ones are left out.
    /// </summary>
    public static Task<IReadOnlyList<JsonObject>> ReadPrincipalSchedulesAsync(IGraphClient graph, string principalId, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        return ListAsync(graph, "principalId eq '" + PimScheduleRequests.Quote(RoleAssignmentsRules.NormalizeId(principalId)) + "'", principalId, api, hint, cancellationToken);
    }

    /// <summary>
    /// The Direct eligibility of the tuple, or <c>null</c>: <c>GET …/roleEligibilitySchedules?$filter=principalId eq '…' and roleDefinitionId eq '…' and
    /// directoryScopeId eq '…'</c> (or <c>appScopeId</c>), re-matched client-side (GUID-aware, scope canonicalised).
    /// </summary>
    public static async Task<JsonObject?> FindAsync(IGraphClient graph, EligibilityTuple tuple, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tuple);
        string filter = "principalId eq '" + PimScheduleRequests.Quote(RoleAssignmentsRules.NormalizeId(tuple.PrincipalId))
            + "' and roleDefinitionId eq '" + PimScheduleRequests.Quote(RoleAssignmentsRules.NormalizeId(tuple.RoleDefinitionId))
            + "' and " + tuple.ScopeProperty + " eq '" + PimScheduleRequests.Quote(CanonicalScope(tuple)) + "'";
        var schedules = await ListAsync(graph, filter, tuple.PrincipalId, api, hint, cancellationToken).ConfigureAwait(false);
        return schedules.FirstOrDefault(s => Matches(s, tuple));
    }

    private static Task<IReadOnlyList<JsonObject>> ListAsync(IGraphClient graph, string filter, string principalId, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        return PimScheduleRequests.ListSchedulesAsync(graph, SchedulesPath, filter, s => JsonObjects.KeyEquals(JsonObjects.GetString(s, "principalId"), principalId), api, hint, cancellationToken);
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

    /// <summary>The tuple as the request body, the applied extras and the live view carry it (ids normalised, scope canonicalised).</summary>
    public static JsonObject TupleObject(EligibilityTuple tuple)
    {
        ArgumentNullException.ThrowIfNull(tuple);
        return new JsonObject
        {
            ["principalId"] = RoleAssignmentsRules.NormalizeId(tuple.PrincipalId),
            ["roleDefinitionId"] = RoleAssignmentsRules.NormalizeId(tuple.RoleDefinitionId),
            [tuple.ScopeProperty] = CanonicalScope(tuple),
        };
    }

    /// <summary>
    /// The body of <c>POST …/roleEligibilityScheduleRequests</c>: the action, the tuple and, for adminAssign / adminUpdate, the schedule; justification and
    /// ticketInfo when given (request metadata).
    /// </summary>
    public static JsonObject RequestBody(string action, EligibilityTuple tuple, JsonNode? scheduleInfo, JsonNode? justification, JsonNode? ticketInfo)
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

    /// <summary>The comparable view of a live schedule: principalId, roleDefinitionId, its scope (canonicalised) and <c>scheduleInfo</c> (non-null members only).</summary>
    public static JsonObject LiveView(JsonObject live)
    {
        ArgumentNullException.ThrowIfNull(live);
        var view = new JsonObject();
        if (JsonObjects.GetString(live, "principalId") is { } principalId)
        {
            view["principalId"] = RoleAssignmentsRules.NormalizeId(principalId);
        }

        if (JsonObjects.GetString(live, "roleDefinitionId") is { } roleDefinitionId)
        {
            view["roleDefinitionId"] = RoleAssignmentsRules.NormalizeId(roleDefinitionId);
        }

        if (JsonObjects.GetString(live, "directoryScopeId") is { } directoryScope)
        {
            view["directoryScopeId"] = RoleAssignmentsRules.NormalizeScope(directoryScope);
        }
        else if (JsonObjects.GetString(live, "appScopeId") is { } appScope)
        {
            view["appScopeId"] = appScope;
        }

        if (PimScheduleComparison.ScheduleView(live) is { } schedule)
        {
            view["scheduleInfo"] = schedule;
        }

        return view;
    }

    /// <summary>The canonical form of the tuple's scope (directory scopes canonicalised, app scopes trimmed).</summary>
    public static string CanonicalScope(EligibilityTuple tuple)
    {
        ArgumentNullException.ThrowIfNull(tuple);
        return tuple.IsAppScope ? tuple.Scope.Trim() : RoleAssignmentsRules.NormalizeScope(tuple.Scope);
    }
}
