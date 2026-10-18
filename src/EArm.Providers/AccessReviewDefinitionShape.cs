using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using EArm.Engine.Providers;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>
/// The pure JSON rules of an access review definition (Learn: resources/accessreviewscheduledefinition, accessreviewscheduledefinition-update):
/// <list type="bullet">
/// <item><see cref="ProjectLive"/>: the live definition cut down to the shape the template declares, so Graph's filled-in members (every recurrence
/// field, a reviewer's <c>queryRoot: null</c>, the settings the template leaves out) are not drift. Arrays of objects keep every live entry (an extra
/// live reviewer is drift); each entry is cut down to the declared entry with the same <c>query</c>, else the one at the same position.</item>
/// <item><see cref="MergeForPut"/>: the full document for Graph's replace-only <c>PUT</c>: the live definition without its read-only members, the
/// declared values merged in (objects merge, a declared array replaces the live one).</item>
/// <item><see cref="Problems"/> and <see cref="ReviewerChangeRefusal"/>: what Graph refuses, found before a request is sent.</item>
/// </list>
/// </summary>
internal static partial class AccessReviewDefinitionShape
{
    public const string ReviewersProperty = "reviewers";

    public const string FallbackReviewersProperty = "fallbackReviewers";

    /// <summary>The deprecated alias of <see cref="FallbackReviewersProperty"/>: Graph copies either into the other.</summary>
    public const string BackupReviewersProperty = "backupReviewers";

    /// <summary>The members Graph assigns; never sent in a body (Learn: resources/accessreviewscheduledefinition, "Read-only").</summary>
    public static readonly IReadOnlyList<string> ReadOnlyProperties = ["createdBy", "createdDateTime", "lastModifiedDateTime", "status", "instances"];

    /// <summary>A copy of <paramref name="live"/> restricted to what <paramref name="desired"/> declares (see the type).</summary>
    public static JsonNode? ProjectLive(JsonNode? desired, JsonNode? live)
    {
        switch (desired, live)
        {
            case (JsonObject desiredObject, JsonObject liveObject):
            {
                var projected = new JsonObject();
                foreach (var (name, value) in desiredObject)
                {
                    if (JsonObjects.FindKey(liveObject, name) is { } liveKey)
                    {
                        projected[liveKey] = ProjectLive(value, liveObject[liveKey]);
                    }
                }

                return projected;
            }

            case (JsonArray desiredArray, JsonArray liveArray) when desiredArray.Any(e => e is JsonObject):
            {
                var used = new HashSet<int>();
                var projected = new JsonArray();
                for (int d = 0; d < desiredArray.Count; d++)
                {
                    int match = Match(desiredArray[d], d, liveArray, used);
                    if (match >= 0)
                    {
                        used.Add(match);
                        projected.Add(ProjectLive(desiredArray[d], liveArray[match]));
                    }
                }

                for (int i = 0; i < liveArray.Count; i++)
                {
                    if (!used.Contains(i))
                    {
                        projected.Add(liveArray[i]?.DeepClone());
                    }
                }

                return projected;
            }

            case (JsonValue desiredValue, JsonValue liveValue) when SameDate(desiredValue, liveValue):
                // Graph serves recurrence dates as yyyy-MM-dd even when a timestamp was sent (Learn's create example sends one): the same day is no drift.
                return desiredValue.DeepClone();

            default:
                return live?.DeepClone();
        }
    }

    /// <summary>
    /// The <c>PUT</c> document: <paramref name="live"/> without its read-only members, with <paramref name="desired"/> merged in. Only one of
    /// <c>fallbackReviewers</c> / <c>backupReviewers</c> is sent (the declared one, else <c>fallbackReviewers</c>), so a stale copy of the alias
    /// cannot contradict the change.
    /// </summary>
    public static JsonObject MergeForPut(JsonObject live, JsonObject desired)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(desired);
        var document = (JsonObject)live.DeepClone();
        foreach (string key in document.Select(p => p.Key).ToList())
        {
            if (ReadOnlyProperties.Contains(key, StringComparer.OrdinalIgnoreCase) || key.StartsWith("@odata.", StringComparison.Ordinal) || key.EndsWith("@odata.context", StringComparison.Ordinal))
            {
                document.Remove(key);
            }
        }

        bool declaresBackup = JsonObjects.FindKey(desired, BackupReviewersProperty) is not null;
        bool declaresFallback = JsonObjects.FindKey(desired, FallbackReviewersProperty) is not null;
        string dropped = declaresBackup && !declaresFallback ? FallbackReviewersProperty : BackupReviewersProperty;
        if (JsonObjects.FindKey(document, dropped) is { } droppedKey)
        {
            document.Remove(droppedKey);
        }

        return (JsonObject)Merge(document, desired)!;
    }

    /// <summary>The declared values Graph refuses whatever the live object is; empty when none.</summary>
    public static List<string> Problems(JsonObject properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var problems = new List<string>();
        if (JsonObjects.TryGetIgnoreCase(properties, BackupReviewersProperty, out var backup) && JsonObjects.TryGetIgnoreCase(properties, FallbackReviewersProperty, out var fallback)
            && !UnknownJson.IsUnknown(backup) && !UnknownJson.IsUnknown(fallback) && !JsonDiff.AreEquivalent(backup, fallback))
        {
            problems.Add("backupReviewers and fallbackReviewers differ; backupReviewers is the deprecated alias of fallbackReviewers (Graph copies one into the other), so declare fallbackReviewers only");
        }

        return problems;
    }

    /// <summary>
    /// Why Graph would refuse the reviewer change, or <c>null</c>: Learn (accessreviewscheduledefinition-update) says <c>reviewers</c> "is only updatable if
    /// individual users are assigned as reviewers", so a change is refused while the live or the declared reviewers include anything but
    /// <c>/users/{id}</c> (a group, <c>./owners</c>, <c>./manager</c>). A deploy-time-unknown query is not judged. Nothing when the reviewers do not change.
    /// </summary>
    public static string? ReviewerChangeRefusal(JsonObject desired, JsonObject live)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(live);
        if (!JsonObjects.TryGetIgnoreCase(desired, ReviewersProperty, out var declared) || UnknownJson.IsUnknown(declared))
        {
            return null;
        }

        JsonObjects.TryGetIgnoreCase(live, ReviewersProperty, out var current);
        if (JsonDiff.AreEquivalent(declared, ProjectLive(declared, current)))
        {
            return null;
        }

        var offending = Queries(current).Concat(Queries(declared)).Where(q => !IsIndividualUser(q)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return offending.Count == 0
            ? null
            : $"Graph changes the reviewers of an existing access review only while every reviewer is an individual user (/users/{{id}}), and these are not: {string.Join(", ", offending)}. Keep the live reviewers, or delete the review and deploy it again to recreate it with the new ones";
    }

    private static IEnumerable<string> Queries(JsonNode? reviewers)
    {
        if (reviewers is not JsonArray array)
        {
            yield break;
        }

        foreach (var entry in array)
        {
            if (entry is JsonObject reviewer && JsonObjects.TryGetIgnoreCase(reviewer, "query", out var query) && !UnknownJson.IsUnknown(query)
                && JsonValues.TryGetString(query, out var text))
            {
                yield return text;
            }
        }
    }

    private static bool IsIndividualUser(string query)
    {
        return IndividualUserQuery().IsMatch(query);
    }

    [GeneratedRegex(@"^/(v1\.0/|beta/)?users/[^/?\s]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IndividualUserQuery();

    private static JsonNode? Merge(JsonNode? live, JsonNode? desired)
    {
        if (live is JsonObject liveObject && desired is JsonObject desiredObject)
        {
            var merged = (JsonObject)liveObject.DeepClone();
            foreach (var (name, value) in desiredObject)
            {
                string key = JsonObjects.FindKey(merged, name) ?? name;
                merged[key] = Merge(merged[key], value);
            }

            return merged;
        }

        return desired?.DeepClone();
    }

    /// <summary>The live entry a declared array entry matches: the same <c>query</c> (case-insensitive), else the same position; -1 when none.</summary>
    private static int Match(JsonNode? element, int position, JsonArray live, HashSet<int> used)
    {
        if (element is not JsonObject declared)
        {
            return -1;
        }

        if (JsonObjects.GetString(declared, "query") is { } query && !UnknownJson.IsUnknown(declared["query"]))
        {
            for (int i = 0; i < live.Count; i++)
            {
                if (!used.Contains(i) && live[i] is JsonObject candidate && string.Equals(JsonObjects.GetString(candidate, "query"), query, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return position < live.Count && !used.Contains(position) && live[position] is JsonObject ? position : -1;
    }

    /// <summary>A declared timestamp (with a time part) and a live date-only value that name the same day, or two equal date strings.</summary>
    private static bool SameDate(JsonValue desired, JsonValue live)
    {
        return desired.TryGetValue(out string? left) && live.TryGetValue(out string? right)
            && left.Length > 10 && right.Length == 10 && left[10] == 'T' && IsDate(right)
            && string.Equals(left[..10], right, StringComparison.Ordinal);
    }

    private static bool IsDate(string text)
    {
        return DateOnly.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _);
    }
}
