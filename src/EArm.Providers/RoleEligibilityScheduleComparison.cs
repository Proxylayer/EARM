using System;
using System.Text.Json.Nodes;
using EArm.Core.Validation;
using EArm.Engine.Providers;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>
/// How a declared role eligibility is compared with its live schedule. <see cref="LiveView"/> reduces the schedule to what a template declares (the tuple,
/// scope canonicalised, and <c>scheduleInfo</c> without Graph's nulls); <see cref="Align"/> then writes the declared value into that view wherever the two are
/// equivalent under the PIM rules, so the shared <see cref="JsonDiff"/> reports only real drift:
/// <list type="bullet">
/// <item>request metadata (<c>justification</c>, <c>ticketInfo</c>) is never compared;</item>
/// <item>a declared <c>startDateTime</c> that is already past, or within the tolerance of the live one, is not drift (Graph starts a past schedule now);</item>
/// <item>the expiration type must match; AfterDateTime compares <c>endDateTime</c> within the tolerance; AfterDuration is applied at creation and never
/// compared again (Graph may report the schedule as AfterDateTime); NoExpiration has nothing else.</item>
/// </list>
/// </summary>
public static class RoleEligibilityScheduleComparison
{
    /// <summary>The comparable view of a live schedule: principalId, roleDefinitionId, its scope and <c>scheduleInfo</c> (non-null members only).</summary>
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

        if (JsonObjects.TryGetIgnoreCase(live, "scheduleInfo", out var scheduleNode) && scheduleNode is JsonObject schedule)
        {
            var info = new JsonObject();
            if (JsonObjects.GetString(schedule, "startDateTime") is { } start)
            {
                info["startDateTime"] = start;
            }

            if (JsonObjects.TryGetIgnoreCase(schedule, "expiration", out var expirationNode) && expirationNode is JsonObject expiration)
            {
                var pattern = new JsonObject();
                if (JsonObjects.GetString(expiration, "type") is { } type)
                {
                    pattern["type"] = RoleEligibilityScheduleRules.NormalizeExpirationType(type) ?? type;
                }

                foreach (string member in new[] { "endDateTime", "duration" })
                {
                    if (JsonObjects.GetString(expiration, member) is { } value)
                    {
                        pattern[member] = value;
                    }
                }

                info["expiration"] = pattern;
            }

            view["scheduleInfo"] = info;
        }

        return view;
    }

    /// <summary>
    /// A copy of <paramref name="liveView"/> in which every member equivalent to its declared counterpart carries the declared value (see the type summary).
    /// </summary>
    /// <param name="desired">The evaluated, normalised properties.</param>
    /// <param name="liveView">The live view (<see cref="LiveView"/>).</param>
    /// <param name="now">The time a declared start is judged "past" against.</param>
    /// <param name="tolerance">The date-time tolerance.</param>
    /// <param name="isOpaque">True for a value that cannot be judged yet.</param>
    public static JsonObject Align(JsonObject desired, JsonObject liveView, DateTimeOffset now, TimeSpan tolerance, Func<JsonNode?, bool> isOpaque)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(liveView);
        ArgumentNullException.ThrowIfNull(isOpaque);
        var aligned = (JsonObject)JsonValues.Clone(liveView)!;

        foreach (string metadata in RoleEligibilityScheduleRules.MetadataProperties)
        {
            if (JsonObjects.FindKey(desired, metadata) is { } key)
            {
                aligned[key] = JsonValues.Clone(desired[key]);
            }
        }

        if (JsonObjects.FindKey(desired, "scheduleInfo") is { } scheduleKey && desired[scheduleKey] is JsonObject declared && !isOpaque(declared))
        {
            var live = JsonObjects.FindKey(aligned, "scheduleInfo") is { } liveKey ? aligned[liveKey] as JsonObject : null;
            if (live is not null)
            {
                aligned.Remove(JsonObjects.FindKey(aligned, "scheduleInfo")!);
            }

            aligned[scheduleKey] = AlignSchedule(declared, live ?? new JsonObject(), now, tolerance, isOpaque);
        }

        return aligned;
    }

    private static JsonObject AlignSchedule(JsonObject declared, JsonObject live, DateTimeOffset now, TimeSpan tolerance, Func<JsonNode?, bool> isOpaque)
    {
        // Only what the template declares is compared: an undeclared start (Graph fills in "now") is not part of the view.
        var result = new JsonObject();

        if (JsonObjects.FindKey(declared, "startDateTime") is { } startKey && declared[startKey] is { } start)
        {
            string? liveStart = JsonObjects.GetString(live, "startDateTime");
            bool equivalent = !isOpaque(start) && JsonValues.TryGetString(start, out var startText) && RoleEligibilityScheduleRules.TryParseDateTime(startText, out var declaredStart)
                && (declaredStart <= now || (RoleEligibilityScheduleRules.TryParseDateTime(liveStart, out var liveStartValue) && (declaredStart - liveStartValue).Duration() <= tolerance));
            if (equivalent)
            {
                result[startKey] = JsonValues.Clone(start);
            }
            else if (liveStart is not null)
            {
                result[startKey] = liveStart;
            }
        }

        if (JsonObjects.FindKey(declared, "expiration") is not { } expirationKey || declared[expirationKey] is null)
        {
            return result;
        }

        var liveExpiration = JsonObjects.TryGetIgnoreCase(live, "expiration", out var node) && node is JsonObject liveObject ? liveObject : null;
        var expiration = declared[expirationKey] as JsonObject;
        string? type = null;
        if (expiration is not null && !isOpaque(expiration) && JsonObjects.FindKey(expiration, "type") is { } declaredTypeKey
            && expiration[declaredTypeKey] is { } typeNode && !isOpaque(typeNode) && JsonValues.TryGetString(typeNode, out var typeText))
        {
            type = RoleEligibilityScheduleRules.NormalizeExpirationType(typeText);
        }

        string? liveType = RoleEligibilityScheduleRules.NormalizeExpirationType(JsonObjects.GetString(liveExpiration, "type"));
        bool sameKind = type is not null && (type == liveType || (type == RoleEligibilityScheduleRules.AfterDuration && liveType == RoleEligibilityScheduleRules.AfterDateTime));
        if (!sameKind)
        {
            // A different (or not yet known) expiration: the live one as it is, so the diff shows the change.
            if (liveExpiration is not null)
            {
                result[expirationKey] = JsonValues.Clone(liveExpiration);
            }

            return result;
        }

        var declaredExpiration = expiration!;
        string typeKey = JsonObjects.FindKey(declaredExpiration, "type")!;

        var pattern = new JsonObject { [typeKey] = type };
        switch (type)
        {
            case RoleEligibilityScheduleRules.AfterDateTime:
                if (JsonObjects.FindKey(declaredExpiration, "endDateTime") is { } endKey && declaredExpiration[endKey] is { } end)
                {
                    string? liveEnd = JsonObjects.GetString(liveExpiration, "endDateTime");
                    bool equal = !isOpaque(end) && JsonValues.TryGetString(end, out var endText)
                        && RoleEligibilityScheduleRules.TryParseDateTime(endText, out var declaredEnd)
                        && RoleEligibilityScheduleRules.TryParseDateTime(liveEnd, out var liveEndValue)
                        && (declaredEnd - liveEndValue).Duration() <= tolerance;
                    if (equal)
                    {
                        pattern[endKey] = JsonValues.Clone(end);
                    }
                    else if (liveEnd is not null)
                    {
                        pattern[endKey] = liveEnd;
                    }
                }

                break;
            case RoleEligibilityScheduleRules.AfterDuration:
                // Create-time intent: whatever end Graph computed from the duration, the declared duration is what the eligibility was made with.
                if (JsonObjects.FindKey(declaredExpiration, "duration") is { } durationKey)
                {
                    pattern[durationKey] = JsonValues.Clone(declaredExpiration[durationKey]);
                }

                break;
        }

        result[expirationKey] = pattern;
        return result;
    }
}
