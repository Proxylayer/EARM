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

/// <summary>
/// The Graph side every PIM schedule request type shares (directory role and PIM for Groups eligibilities and active assignments): the request actions, the schedule list
/// read (every page, kept to <c>memberType</c> Direct and to schedules that are not revoked or failed), the request metadata of a request body, and the
/// classification of PIM errors (a policy rule that failed, a schedule that is already gone, a request that ended in a failed status). Never cached.
/// </summary>
public static class PimScheduleRequests
{
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
    /// <c>GET {schedulesPath}?$filter={filter}</c> (every page), keeping the schedules <paramref name="keep"/> accepts that are Direct and active
    /// (<see cref="IsDirectAndActive"/>).
    /// </summary>
    public static async Task<IReadOnlyList<JsonObject>> ListSchedulesAsync(IGraphClient graph, string schedulesPath, string filter, Func<JsonObject, bool> keep, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(schedulesPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(filter);
        ArgumentNullException.ThrowIfNull(keep);
        var request = new GraphRequest(schedulesPath, api, hint: hint).WithFilter(filter);
        var items = new List<JsonObject>();
        await foreach (var item in graph.GetAllPagesAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (item is JsonObject schedule && IsDirectAndActive(schedule) && keep(schedule))
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

    /// <summary>
    /// True for an active assignment schedule an administrator assigned (<c>assignmentType</c> Assigned, any casing, or absent); an activation of an
    /// eligibility (<c>Activated</c>) belongs to the eligible principal, not to the template.
    /// </summary>
    public static bool IsAssigned(JsonObject schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        string? assignmentType = JsonObjects.GetString(schedule, "assignmentType");
        return assignmentType is null || assignmentType.Equals("Assigned", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Adds the schedule (when given) and the request metadata (justification, ticketInfo) to a request body.</summary>
    public static JsonObject WithScheduleAndMetadata(JsonObject body, JsonNode? scheduleInfo, JsonNode? justification, JsonNode? ticketInfo)
    {
        ArgumentNullException.ThrowIfNull(body);
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

    /// <summary>True when Graph refused a request because a rule of the PIM policy failed (Graph's <c>RoleAssignmentRequestPolicyValidationFailed</c>).</summary>
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

    /// <summary>True when Graph answered an adminAssign with "the eligibility already exists" (<c>RoleAssignmentExists</c>, or a 409).</summary>
    public static bool IsAlreadyExists(GraphException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.IsAlreadyExists || string.Equals(ex.Code, "RoleAssignmentExists", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when a request response says the request failed (status Failed, Denied, Canceled...).</summary>
    public static bool HasFailedStatus(JsonObject? request, out string status)
    {
        status = JsonObjects.GetString(request, "status") ?? string.Empty;
        return status.Length > 0 && FailedStatuses.Contains(status, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A value quoted for an OData string literal (single quotes doubled).</summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Replace("'", "''", StringComparison.Ordinal);
    }
}
