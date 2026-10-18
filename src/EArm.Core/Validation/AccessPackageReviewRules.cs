using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Core.Json;

namespace EArm.Core.Validation;

/// <summary>
/// The pure rules of an access package assignment policy's <c>reviewSettings</c> (accessPackageAssignmentReviewSettings, Learn:
/// resources/accesspackageassignmentreviewsettings, resources/entitlementmanagementschedule, resources/patternedrecurrence), shared by the offline
/// validator (literal values) and the provider (evaluated values). Only what the JSON Schema cannot express:
/// <list type="bullet">
/// <item>enabled reviews that are not self-reviews need <c>primaryReviewers</c>, and a <c>schedule</c> with a <c>startDateTime</c>;</item>
/// <item>a <c>groupMembers</c> reviewer needs <c>groupId</c>, a <c>singleUser</c> reviewer <c>userId</c>;</item>
/// <item>the schedule: an ISO 8601 <c>startDateTime</c>; an <c>expiration</c> of type <c>afterDuration</c> needs a positive ISO 8601 <c>duration</c>
/// (<c>P14D</c>), <c>afterDateTime</c> an <c>endDateTime</c>;</item>
/// <item>the recurrence: a pattern type; <c>daysOfWeek</c> for weekly and relative patterns; <c>month</c> (1-12) for yearly patterns; a
/// <c>dayOfMonth</c> (1-31) for an absolute yearly pattern; a range <c>endDate</c> (yyyy-MM-dd) for <c>endDate</c> and a <c>numberOfOccurrences</c> of at
/// least 1 for <c>numbered</c>.</item>
/// </list>
/// Values the caller cannot judge yet are skipped through the <c>isOpaque</c> predicate.
/// </summary>
public static class AccessPackageReviewRules
{
    public const string ResourceType = "Microsoft.Entra/entitlementManagement/accessPackages/assignmentPolicies";

    private const string ReviewSettings = "reviewSettings";

    private static readonly string[] WeeklyOrRelative = ["weekly", "relativeMonthly", "relativeYearly"];

    private static readonly string[] Yearly = ["absoluteYearly", "relativeYearly"];

    /// <summary>The problems of a policy's review settings (paths relative to <c>properties</c>). Nothing is changed.</summary>
    /// <param name="properties">The declared (offline) or evaluated (plan time) policy properties.</param>
    /// <param name="isOpaque">True for a value that cannot be judged yet (a template expression, a deploy-time value).</param>
    public static IReadOnlyList<PimRequestFinding> Check(JsonObject properties, Func<JsonNode?, bool> isOpaque)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(isOpaque);
        var findings = new List<PimRequestFinding>();
        if (Member(properties, ReviewSettings, isOpaque) is not (string key, JsonObject review))
        {
            return findings;
        }

        var root = JsonPath.Root.Append(key);
        bool enabled = Bool(review, "isEnabled", isOpaque) == true;
        bool selfReview = Bool(review, "isSelfReview", isOpaque) == true;
        bool reviewersOpaque = FindKey(review, "primaryReviewers") is { } reviewersKey && isOpaque(review[reviewersKey]);
        if (enabled && !selfReview && !reviewersOpaque && Member(review, "primaryReviewers", isOpaque) is not (_, JsonArray { Count: > 0 }))
        {
            findings.Add(new(root.Append(FindKey(review, "primaryReviewers") ?? "primaryReviewers"), $"{key}.primaryReviewers must name at least one reviewer when the review is enabled and isSelfReview is not true (a groupMembers group, a singleUser, requestorManager ...)"));
        }

        CheckReviewers(review, "primaryReviewers", root, isOpaque, findings);
        CheckReviewers(review, "fallbackReviewers", root, isOpaque, findings);

        bool scheduleOpaque = FindKey(review, "schedule") is { } scheduleKey && isOpaque(review[scheduleKey]);
        if (Member(review, "schedule", isOpaque) is (string sKey, JsonObject schedule))
        {
            CheckSchedule(schedule, root.Append(sKey), $"{key}.{sKey}", enabled, isOpaque, findings);
        }
        else if (enabled && !scheduleOpaque)
        {
            findings.Add(new(root.Append("schedule"), $"{key}.schedule is required when the review is enabled: startDateTime, expiration (how long each review lasts) and recurrence (how often it repeats)"));
        }

        return findings;
    }

    private static void CheckReviewers(JsonObject review, string name, JsonPath root, Func<JsonNode?, bool> isOpaque, List<PimRequestFinding> findings)
    {
        if (Member(review, name, isOpaque) is not (string key, JsonArray reviewers))
        {
            return;
        }

        for (int i = 0; i < reviewers.Count; i++)
        {
            if (reviewers[i] is not JsonObject subject || String(subject, "@odata.type", isOpaque) is not { } type)
            {
                continue;
            }

            string bare = type.TrimStart('#');
            string? required = bare.Equals("microsoft.graph.groupMembers", StringComparison.OrdinalIgnoreCase) ? "groupId"
                : bare.Equals("microsoft.graph.singleUser", StringComparison.OrdinalIgnoreCase) ? "userId"
                : null;
            if (required is not null && FindKey(subject, required) is null)
            {
                findings.Add(new(root.Append(key).Append(i), $"reviewSettings.{key}[{i}] is a {bare["microsoft.graph.".Length..]} reviewer without {required}"));
            }
        }
    }

    private static void CheckSchedule(JsonObject schedule, JsonPath root, string label, bool enabled, Func<JsonNode?, bool> isOpaque, List<PimRequestFinding> findings)
    {
        if (Member(schedule, "startDateTime", isOpaque) is (string startKey, var start))
        {
            if (!TryString(start, out string? text) || !PimScheduleRules.TryParseDateTime(text, out _))
            {
                findings.Add(new(root.Append(startKey), $"{label}.{startKey} must be an ISO 8601 date-time such as 2026-11-01T00:00:00Z"));
            }
        }
        else if (enabled && FindKey(schedule, "startDateTime") is null)
        {
            findings.Add(new(root.Append("startDateTime"), $"{label}.startDateTime is required when the review is enabled: when the first review starts"));
        }

        if (Member(schedule, "expiration", isOpaque) is (string expirationKey, JsonObject expiration))
        {
            CheckExpiration(expiration, root.Append(expirationKey), $"{label}.{expirationKey}", isOpaque, findings);
        }

        if (Member(schedule, "recurrence", isOpaque) is (string recurrenceKey, JsonObject recurrence))
        {
            CheckRecurrence(recurrence, root.Append(recurrenceKey), $"{label}.{recurrenceKey}", isOpaque, findings);
        }
    }

    private static void CheckExpiration(JsonObject expiration, JsonPath root, string label, Func<JsonNode?, bool> isOpaque, List<PimRequestFinding> findings)
    {
        string? type = String(expiration, "type", isOpaque);
        if (Member(expiration, "duration", isOpaque) is (string durationKey, var duration) && duration is not null)
        {
            if (!TryString(duration, out string? text) || !PimScheduleRules.TryParseDuration(text, out _))
            {
                findings.Add(new(root.Append(durationKey), $"{label}.{durationKey} must be a positive ISO 8601 duration such as P14D or P1W"));
            }
        }
        else if (string.Equals(type, PimScheduleRules.AfterDuration, StringComparison.OrdinalIgnoreCase) && FindKey(expiration, "duration") is null)
        {
            findings.Add(new(root.Append("duration"), $"{label}.duration is required for type afterDuration (for example P14D: each review stays open 14 days)"));
        }

        if (string.Equals(type, PimScheduleRules.AfterDateTime, StringComparison.OrdinalIgnoreCase) && FindKey(expiration, "endDateTime") is null)
        {
            findings.Add(new(root.Append("endDateTime"), $"{label}.endDateTime is required for type afterDateTime"));
        }
    }

    private static void CheckRecurrence(JsonObject recurrence, JsonPath root, string label, Func<JsonNode?, bool> isOpaque, List<PimRequestFinding> findings)
    {
        if (Member(recurrence, "pattern", isOpaque) is (string patternKey, JsonObject pattern))
        {
            var path = root.Append(patternKey);
            string? type = String(pattern, "type", isOpaque);
            if (type is null && FindKey(pattern, "type") is null)
            {
                findings.Add(new(path.Append("type"), $"{label}.{patternKey}.type is required: daily, weekly, absoluteMonthly, relativeMonthly, absoluteYearly or relativeYearly (quarterly is absoluteMonthly with interval 3)"));
            }

            if (type is not null && WeeklyOrRelative.Contains(type, StringComparer.OrdinalIgnoreCase)
                && FindKey(pattern, "daysOfWeek") is var daysKey && (daysKey is null || (!isOpaque(pattern[daysKey]) && pattern[daysKey] is not JsonArray { Count: > 0 })))
            {
                findings.Add(new(path.Append(daysKey ?? "daysOfWeek"), $"{label}.{patternKey}.daysOfWeek must list at least one day for a {type} pattern"));
            }

            if (type is not null && Yearly.Contains(type, StringComparer.OrdinalIgnoreCase) && Int(pattern, "month", isOpaque) is not (>= 1 and <= 12) && !OpaqueMember(pattern, "month", isOpaque))
            {
                findings.Add(new(path.Append(FindKey(pattern, "month") ?? "month"), $"{label}.{patternKey}.month must be 1 to 12 for a {type} pattern"));
            }

            if (string.Equals(type, "absoluteYearly", StringComparison.OrdinalIgnoreCase) && Int(pattern, "dayOfMonth", isOpaque) is not (>= 1 and <= 31) && !OpaqueMember(pattern, "dayOfMonth", isOpaque))
            {
                findings.Add(new(path.Append(FindKey(pattern, "dayOfMonth") ?? "dayOfMonth"), $"{label}.{patternKey}.dayOfMonth must be 1 to 31 for an absoluteYearly pattern"));
            }
        }

        if (Member(recurrence, "range", isOpaque) is (string rangeKey, JsonObject range))
        {
            var path = root.Append(rangeKey);
            string? type = String(range, "type", isOpaque);
            if (string.Equals(type, "endDate", StringComparison.OrdinalIgnoreCase)
                && (String(range, "endDate", isOpaque) is not { } end || !DateOnly.TryParseExact(end, "yyyy-MM-dd", out _)) && !OpaqueMember(range, "endDate", isOpaque))
            {
                findings.Add(new(path.Append(FindKey(range, "endDate") ?? "endDate"), $"{label}.{rangeKey}.endDate must be a date (yyyy-MM-dd) for an endDate range"));
            }

            if (string.Equals(type, "numbered", StringComparison.OrdinalIgnoreCase) && Int(range, "numberOfOccurrences", isOpaque) is not >= 1 && !OpaqueMember(range, "numberOfOccurrences", isOpaque))
            {
                findings.Add(new(path.Append(FindKey(range, "numberOfOccurrences") ?? "numberOfOccurrences"), $"{label}.{rangeKey}.numberOfOccurrences must be at least 1 for a numbered range"));
            }
        }
    }

    // ---- helpers ----

    private static string? FindKey(JsonObject obj, string name)
    {
        return obj.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The member and its value, or <c>null</c> when absent or opaque.</summary>
    private static (string Key, JsonNode? Value)? Member(JsonObject obj, string name, Func<JsonNode?, bool> isOpaque)
    {
        return FindKey(obj, name) is { } key && !isOpaque(obj[key]) ? (key, obj[key]) : null;
    }

    private static bool OpaqueMember(JsonObject obj, string name, Func<JsonNode?, bool> isOpaque)
    {
        return FindKey(obj, name) is { } key && isOpaque(obj[key]);
    }

    private static string? String(JsonObject obj, string name, Func<JsonNode?, bool> isOpaque)
    {
        return Member(obj, name, isOpaque) is (_, var value) && TryString(value, out string? text) ? text.Trim() : null;
    }

    private static bool? Bool(JsonObject obj, string name, Func<JsonNode?, bool> isOpaque)
    {
        return Member(obj, name, isOpaque) is (_, JsonValue value) && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? value.GetValue<bool>() : null;
    }

    private static long? Int(JsonObject obj, string name, Func<JsonNode?, bool> isOpaque)
    {
        return Member(obj, name, isOpaque) is (_, JsonValue value) && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue(out long number) ? number : null;
    }

    private static bool TryString(JsonNode? node, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? text)
    {
        text = null;
        return node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.TryGetValue(out text);
    }
}
