using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Engine.Providers;

namespace EArm.Providers;

/// <summary>
/// The pure JSON rules of an access package assignment policy (Learn: resources/accesspackageassignmentpolicy,
/// accesspackageassignmentpolicy-update):
/// <list type="bullet">
/// <item><see cref="ProjectLive"/>: the live policy cut down to the shape the template declares, so Graph's filled-in sub-properties (every stage
/// field, a subject's read-only <c>description</c>, ...) are not drift. Arrays of objects keep every live entry (an extra live approver is drift); each
/// entry is cut down to the declared entry it matches.</item>
/// <item><see cref="MergeForPut"/>: the full document for Graph's replace-only <c>PUT</c>: the live policy with the declared values merged in (objects
/// merge, a declared array replaces the live one, each declared entry merged over the live entry it matches).</item>
/// </list>
/// Array entries match by <c>groupId</c> / <c>userId</c> (subject sets, compared order-insensitively by the shared diff), a subject set without an id
/// (<c>requestorManager</c>, the sponsors) by its <c>@odata.type</c> (and <c>managerLevel</c>), else by position (stages). The same rules cover the
/// approvers of the stages and the reviewers of <c>reviewSettings</c>.
/// </summary>
internal static class AssignmentPolicyShape
{
    private static readonly string[] IdentityKeys = ["groupId", "userId", "connectedOrganizationId", "id"];

    /// <summary>The read-only top-level properties left out of a <c>PUT</c> body.</summary>
    private static readonly string[] ReadOnlyForPut = ["id", "createdDateTime", "modifiedDateTime", "accessPackage", "catalog"];

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
                foreach (var element in desiredArray)
                {
                    int match = Match(element, liveArray, used);
                    if (match >= 0)
                    {
                        used.Add(match);
                        projected.Add(ProjectLive(element, liveArray[match]));
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

            case (JsonValue desiredValue, JsonValue liveValue) when SameInstant(desiredValue, liveValue):
                // Graph may write a timestamp back with milliseconds or another offset (a review's startDateTime): the same instant is no drift.
                return desiredValue.DeepClone();

            default:
                return live?.DeepClone();
        }
    }

    /// <summary>Two ISO 8601 timestamps (with a time part) that name the same instant.</summary>
    private static bool SameInstant(JsonValue desired, JsonValue live)
    {
        return desired.TryGetValue(out string? left) && live.TryGetValue(out string? right) && left.Contains('T', StringComparison.Ordinal) && right.Contains('T', StringComparison.Ordinal)
            && DateTimeOffset.TryParse(left, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var a)
            && DateTimeOffset.TryParse(right, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var b)
            && a == b;
    }

    /// <summary>The <c>PUT</c> document: <paramref name="live"/> without its read-only properties, with <paramref name="desired"/> merged in.</summary>
    public static JsonObject MergeForPut(JsonObject live, JsonObject desired)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(desired);
        var document = (JsonObject)live.DeepClone();
        foreach (string key in document.Select(p => p.Key).ToList())
        {
            if (ReadOnlyForPut.Contains(key, StringComparer.OrdinalIgnoreCase) || (key.StartsWith("@odata.", StringComparison.Ordinal) && key != "@odata.type")
                || key.EndsWith("@odata.context", StringComparison.Ordinal))
            {
                document.Remove(key);
            }
        }

        RemoveSubjectDescriptions(document, topLevel: true);
        return (JsonObject)Merge(document, desired)!;
    }

    private static JsonNode? Merge(JsonNode? live, JsonNode? desired)
    {
        switch (live, desired)
        {
            case (JsonObject liveObject, JsonObject desiredObject):
            {
                var merged = (JsonObject)liveObject.DeepClone();
                foreach (var (name, value) in desiredObject)
                {
                    string key = JsonObjects.FindKey(merged, name) ?? name;
                    merged[key] = Merge(merged[key], value);
                }

                return merged;
            }

            case (JsonArray liveArray, JsonArray desiredArray):
            {
                var used = new HashSet<int>();
                var merged = new JsonArray();
                foreach (var element in desiredArray)
                {
                    int match = element is JsonObject ? Match(element, liveArray, used) : -1;
                    if (match >= 0)
                    {
                        used.Add(match);
                        merged.Add(Merge(liveArray[match], element));
                    }
                    else
                    {
                        merged.Add(element?.DeepClone());
                    }
                }

                return merged;
            }

            default:
                return desired?.DeepClone();
        }
    }

    /// <summary>The live entry a declared array entry matches: the same identity key value, else the same position; -1 when none.</summary>
    private static int Match(JsonNode? element, JsonArray live, HashSet<int> used)
    {
        if (element is not JsonObject declared)
        {
            return -1;
        }

        foreach (string identity in IdentityKeys)
        {
            if (JsonObjects.GetString(declared, identity) is not { } value)
            {
                continue;
            }

            for (int i = 0; i < live.Count; i++)
            {
                if (!used.Contains(i) && live[i] is JsonObject candidate && EntitlementManagementGraph.SameId(JsonObjects.GetString(candidate, identity), value))
                {
                    return i;
                }
            }

            return -1;
        }

        // A subject set without an id (requestorManager, targetUserSponsors, internalSponsors ...): the same type (and manager level) in any position.
        if (JsonObjects.GetString(declared, "@odata.type") is { } type)
        {
            string? level = declared["managerLevel"]?.ToJsonString();
            for (int i = 0; i < live.Count; i++)
            {
                if (!used.Contains(i) && live[i] is JsonObject candidate && SameODataType(JsonObjects.GetString(candidate, "@odata.type"), type)
                    && (level is null || candidate["managerLevel"]?.ToJsonString() == level))
                {
                    return i;
                }
            }

            return -1;
        }

        int index = declared.Parent is JsonArray parent ? parent.IndexOf(declared) : -1;
        return index >= 0 && index < live.Count && !used.Contains(index) && live[index] is JsonObject ? index : -1;
    }

    /// <summary>Two <c>@odata.type</c> values name the same type (<c>#microsoft.graph.x</c> and <c>microsoft.graph.x</c> alike, any casing).</summary>
    private static bool SameODataType(string? left, string right)
    {
        return left is not null && string.Equals(left.TrimStart('#'), right.TrimStart('#'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A subject set's <c>description</c> is read-only (Learn: resources/groupmembers); it is not sent back.</summary>
    private static void RemoveSubjectDescriptions(JsonNode? node, bool topLevel)
    {
        switch (node)
        {
            case JsonObject obj:
                if (!topLevel && obj.ContainsKey("@odata.type") && obj.ContainsKey("description"))
                {
                    obj.Remove("description");
                }

                foreach (var (_, child) in obj.ToList())
                {
                    RemoveSubjectDescriptions(child, topLevel: false);
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    RemoveSubjectDescriptions(child, topLevel: false);
                }

                break;
        }
    }
}
