using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Providers;

namespace EArm.Integration.Tests;

/// <summary>Direct Graph reads and requests of the PIM scenarios and probes: schedule lists, role template ids and the shape of a schedule's expiration.</summary>
internal static class PimReads
{
    /// <summary>
    /// <c>GET {schedulesPath}?$filter={filter}</c> (every page, nothing filtered client-side). A 404 (the principal or group is gone) reads as no schedules.
    /// </summary>
    public static async Task<IReadOnlyList<JsonObject>> SchedulesAsync(IGraphClient graph, string schedulesPath, string filter, CancellationToken cancellationToken)
    {
        try
        {
            return await graph.ListAsync(new GraphRequest(schedulesPath).WithFilter(filter), cancellationToken);
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            return [];
        }
    }

    public static Task<IReadOnlyList<JsonObject>> ByPrincipalAsync(IGraphClient graph, string schedulesPath, string principalId, CancellationToken cancellationToken)
    {
        return SchedulesAsync(graph, schedulesPath, "principalId eq '" + principalId + "'", cancellationToken);
    }

    public static Task<IReadOnlyList<JsonObject>> ByGroupAsync(IGraphClient graph, string schedulesPath, string groupId, CancellationToken cancellationToken)
    {
        return SchedulesAsync(graph, schedulesPath, "groupId eq '" + groupId + "'", cancellationToken);
    }

    /// <summary>The template id of a built-in directory role by display name (<c>GET /roleManagement/directory/roleDefinitions?$filter=displayName eq '…'</c>).</summary>
    public static async Task<string> RoleTemplateIdAsync(IGraphClient graph, string displayName, CancellationToken cancellationToken)
    {
        var request = new GraphRequest("/roleManagement/directory/roleDefinitions").WithFilter($"displayName eq '{displayName.Replace("'", "''", StringComparison.Ordinal)}'").WithSelect("id", "templateId", "displayName");
        var found = (await graph.ListAsync(request, cancellationToken)).FirstOrDefault()
            ?? throw new InvalidOperationException($"No directory role named '{displayName}' exists in the tenant.");
        return (found.Text("templateId") ?? found.RequireId()).ToLowerInvariant();
    }

    /// <summary>The schedules of <paramref name="schedules"/> for the role (template id), compared case-insensitively.</summary>
    public static List<JsonObject> OfRole(IEnumerable<JsonObject> schedules, string roleDefinitionId)
    {
        return schedules.Where(s => string.Equals(s.Text("roleDefinitionId"), roleDefinitionId, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>The schedules of <paramref name="schedules"/> for the principal and accessId (both compared case-insensitively).</summary>
    public static List<JsonObject> OfAccess(IEnumerable<JsonObject> schedules, string principalId, string accessId)
    {
        return schedules
            .Where(s => string.Equals(s.Text("principalId"), principalId, StringComparison.OrdinalIgnoreCase) && string.Equals(s.Text("accessId"), accessId, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary><c>scheduleInfo.expiration.type</c> of a schedule or request.</summary>
    public static string? ExpirationType(JsonObject schedule)
    {
        return schedule.Nested("scheduleInfo.expiration").Text("type");
    }

    /// <summary><c>scheduleInfo.expiration.endDateTime</c> of a schedule or request, parsed.</summary>
    public static DateTimeOffset? EndDateTime(JsonObject schedule)
    {
        string? text = schedule.Nested("scheduleInfo.expiration").Text("endDateTime");
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value : null;
    }

    /// <summary>The <c>scheduleInfo</c> of a schedule in one line (start, expiration type, end, duration) for observations.</summary>
    public static string DescribeSchedule(JsonObject schedule)
    {
        return (schedule["scheduleInfo"]?.ToJsonString() ?? "(no scheduleInfo)")
            + $" status={schedule.Text("status")} memberType={schedule.Text("memberType")} assignmentType={schedule.Text("assignmentType")} id={schedule.Text("id")}";
    }

    /// <summary>A whole-second UTC instant in the ISO 8601 form the templates use (<c>2026-11-05T10:11:12Z</c>).</summary>
    public static string Iso(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>A whole-second UTC instant <paramref name="days"/> from now.</summary>
    public static DateTimeOffset DaysFromNow(int days)
    {
        var now = DateTimeOffset.UtcNow.AddDays(days);
        return new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero);
    }

    /// <summary>A <c>scheduleInfo</c> that ends at <paramref name="end"/> (AfterDateTime) or after <paramref name="duration"/> (AfterDuration), or never.</summary>
    public static JsonObject ScheduleInfo(DateTimeOffset? end = null, string? duration = null)
    {
        var expiration = end is { } at
            ? new JsonObject { ["type"] = "afterDateTime", ["endDateTime"] = Iso(at) }
            : duration is not null ? new JsonObject { ["type"] = "afterDuration", ["duration"] = duration } : new JsonObject { ["type"] = "noExpiration" };
        return new JsonObject { ["startDateTime"] = Iso(DateTimeOffset.UtcNow.AddMinutes(-1)), ["expiration"] = expiration };
    }

    /// <summary>A directory role schedule request body (<paramref name="action"/>: adminAssign / adminUpdate / adminRemove) at the tenant scope.</summary>
    public static JsonObject RoleRequest(string action, string principalId, string roleDefinitionId, JsonObject? scheduleInfo)
    {
        var tuple = new RoleScheduleTuple(principalId, roleDefinitionId, "/", IsAppScope: false);
        return RoleSchedulesLookup.RequestBody(action, tuple, scheduleInfo, JsonValue.Create("EARM integration probe"), null);
    }

    /// <summary>A PIM for Groups schedule request body.</summary>
    public static JsonObject GroupRequest(string action, string groupId, string principalId, string accessId, JsonObject? scheduleInfo)
    {
        return GroupSchedulesLookup.RequestBody(action, new GroupScheduleTuple(groupId, principalId, accessId), scheduleInfo, JsonValue.Create("EARM integration probe"), null);
    }

    /// <summary>
    /// POSTs a schedule request and returns the request object Graph answered. <paramref name="expectReplicationLag"/> (the default) retries "does not
    /// exist" answers while a minutes-old principal or group replicates; pass false where such an answer is the observation (a remove of a missing schedule).
    /// </summary>
    public static async Task<JsonObject?> SendAsync(IGraphClient graph, string requestsPath, JsonObject body, CancellationToken cancellationToken, bool expectReplicationLag = true)
    {
        var response = await graph.PostAsync(new GraphRequest(requestsPath).WithHint(expectReplicationLag ? RetryHint.ExpectReplicationLag : RetryHint.Default), body, cancellationToken);
        return response.Body as JsonObject;
    }
}
