using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EArm.Graph;

namespace EArm.TestSupport;

/// <summary>
/// Standalone access reviews (accessReviewScheduleDefinition), as Learn documents them
/// (https://learn.microsoft.com/en-us/graph/api/resources/accessreviewscheduledefinition?view=graph-rest-1.0):
/// <list type="bullet">
/// <item>List (paged; <c>$filter</c> only as <c>contains(scope/...)</c>, anything else is a 400; <c>$select</c> projects), GET by id.</item>
/// <item>POST (201, the object): <c>id</c> and the read-only members are refused; Graph fills <c>status</c>, <c>createdBy</c>, the timestamps, every
/// recurrence field (dates come back as <c>yyyy-MM-dd</c>), <c>queryRoot: null</c> on each reviewer, the undeclared settings, and copies
/// <c>fallbackReviewers</c> / <c>backupReviewers</c> into each other.</item>
/// <item>PUT (204) is a full replace: <c>scope</c> is required and, like <c>instanceEnumerationScope</c>, cannot change; <c>reviewers</c> change only while
/// the stored and the new reviewers are all <c>/users/{id}</c>; read-only members and two different reviewer aliases are refused. No PATCH.</item>
/// <item>DELETE (204); a missing id is a 404.</item>
/// <item>With <see cref="AccessReviewQueriesRewritten"/>, the scope, instance enumeration scope and reviewer queries are stored the way the real tenant
/// stores them: an absolute query gets a <c>/v1.0</c> prefix, and a path ending in <c>/members</c> or <c>/transitiveMembers</c> the
/// <c>/microsoft.graph.user</c> cast (<c>/groups/{id}/transitiveMembers</c> is served back as <c>/v1.0/groups/{id}/transitiveMembers/microsoft.graph.user</c>).</item>
/// </list>
/// </summary>
public sealed partial class FakeDirectory
{
    /// <summary>The collection of the access review definitions.</summary>
    public const string AccessReviewDefinitionsPath = "/identityGovernance/accessReviews/definitions";

    private static readonly string[] AccessReviewReadOnly = ["createdBy", "createdDateTime", "lastModifiedDateTime", "status", "instances"];

    private static readonly Regex IndividualUserReviewer = new(@"^/(v1\.0/|beta/)?users/[^/?\s]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The signed-in principal Graph records as <c>createdBy</c> on a new definition.</summary>
    public string AccessReviewCreatorId { get; set; } = "99999999-0000-0000-0000-000000000001";

    /// <summary>Whether a POST / PUT stores the queries in Graph's rewritten form (see the type); off by default (stored as sent).</summary>
    public bool AccessReviewQueriesRewritten { get; set; }

    /// <summary>
    /// <paramref name="query"/> as the real tenant stores it: a leading <c>/v1.0</c> on an absolute, unversioned query, and the
    /// <c>/microsoft.graph.user</c> cast after a trailing <c>/members</c> or <c>/transitiveMembers</c> path segment. Relative queries
    /// (<c>./owners</c>) and the <c>?</c> query string are left alone; an already rewritten query is unchanged.
    /// </summary>
    public static string RewriteAccessReviewQuery(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!query.StartsWith('/'))
        {
            return query;
        }

        if (!query.StartsWith("/v1.0/", StringComparison.OrdinalIgnoreCase) && !query.StartsWith("/beta/", StringComparison.OrdinalIgnoreCase))
        {
            query = "/v1.0" + query;
        }

        int mark = query.IndexOf('?', StringComparison.Ordinal);
        string path = mark < 0 ? query : query[..mark];
        string rest = mark < 0 ? string.Empty : query[mark..];
        if (path.EndsWith("/members", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/transitiveMembers", StringComparison.OrdinalIgnoreCase))
        {
            path += "/microsoft.graph.user";
        }

        return path + rest;
    }

    private bool TryHandleAccessReviews(string method, string path, IReadOnlyDictionary<string, string>? query, JsonNode? body, RetryHint hint, int pageSize, out (HttpStatusCode Status, JsonNode? Body) result)
    {
        result = default;
        string[] segments = PolicySegments(path);
        if (!StartsWith(segments, "identityGovernance", "accessReviews", "definitions"))
        {
            return false;
        }

        var collection = Collection(AccessReviewDefinitionsPath);
        var rest = segments[3..];
        if (rest.Length == 0)
        {
            result = method switch
            {
                "GET" => (HttpStatusCode.OK, Page(SelectAccessReviews(FilterAccessReviews(collection, query, hint), query), AccessReviewDefinitionsPath.TrimStart('/'), query, pageSize)),
                "POST" => CreateAccessReview(collection, body, hint),
                _ => throw MethodNotAllowed($"{method} is not supported on '{AccessReviewDefinitionsPath}'.", hint),
            };
            return true;
        }

        if (rest.Length != 1)
        {
            // instances, decisions, stop...: not modelled.
            throw NotFound(path, hint);
        }

        var stored = collection.FirstOrDefault(o => ValueEquals(GetString(o, "id"), rest[0])) ?? throw NotFound(AccessReviewDefinitionsPath + "/" + rest[0], hint);
        switch (method)
        {
            case "GET":
                result = (HttpStatusCode.OK, SelectAccessReviews([stored], query)[0]);
                return true;
            case "PUT":
                ReplaceAccessReview(stored, body, hint);
                result = (HttpStatusCode.NoContent, null);
                return true;
            case "DELETE":
                collection.Remove(stored);
                result = (HttpStatusCode.NoContent, null);
                return true;
            default:
                throw MethodNotAllowed($"{method} is not supported on an access review definition (Graph offers PUT only).", hint);
        }
    }

    private static List<JsonObject> FilterAccessReviews(List<JsonObject> collection, IReadOnlyDictionary<string, string>? query, RetryHint hint)
    {
        if (query is not null && query.TryGetValue("$filter", out var filter) && !string.IsNullOrWhiteSpace(filter))
        {
            // Learn (accessreviewset-list-definitions): $filter is supported only as contains() on the scope query; nothing else (displayName eq ...) works.
            if (!filter.TrimStart().StartsWith("contains(scope", StringComparison.OrdinalIgnoreCase))
            {
                throw new GraphException(HttpStatusCode.BadRequest, "BadRequest", $"Invalid filter clause: '{filter}' is not supported on access review definitions.", hint);
            }

            var match = Regex.Match(filter, @"contains\(\s*scope/[\w/.\\]+\s*,\s*'(?<text>(?:[^']|'')*)'\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            string text = match.Success ? match.Groups["text"].Value.Replace("''", "'", StringComparison.Ordinal) : string.Empty;
            return collection.Where(o => GetString(o["scope"] as JsonObject, "query")?.Contains(text, StringComparison.OrdinalIgnoreCase) == true).ToList();
        }

        return collection.ToList();
    }

    private static List<JsonObject> SelectAccessReviews(List<JsonObject> items, IReadOnlyDictionary<string, string>? query)
    {
        var select = SelectOf(query);
        if (select is null || select.Contains("*"))
        {
            return items.Select(o => (JsonObject)o.DeepClone()).ToList();
        }

        return items.Select(o =>
        {
            var projected = new JsonObject();
            foreach (var pair in o)
            {
                if (pair.Key.Equals("id", StringComparison.OrdinalIgnoreCase) || select.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                {
                    projected[pair.Key] = pair.Value?.DeepClone();
                }
            }

            return projected;
        }).ToList();
    }

    private (HttpStatusCode, JsonNode?) CreateAccessReview(List<JsonObject> collection, JsonNode? body, RetryHint hint)
    {
        if (body is not JsonObject payload)
        {
            throw BadRequest("The request body must be an accessReviewScheduleDefinition.", hint);
        }

        if (payload.Any(p => p.Key.Equals("id", StringComparison.OrdinalIgnoreCase)))
        {
            throw BadRequest("Property 'id' is read-only and cannot be set.", hint);
        }

        RefuseReadOnlyAccessReviewMembers(payload, hint);
        foreach (string required in new[] { "displayName", "descriptionForAdmins", "descriptionForReviewers" })
        {
            if (string.IsNullOrWhiteSpace(GetString(payload, required)))
            {
                throw BadRequest($"The property '{required}' is required.", hint);
            }
        }

        if (payload["scope"] is not JsonObject)
        {
            throw BadRequest("The property 'scope' is required.", hint);
        }

        RefuseDifferentReviewerAliases(payload, hint);
        var created = NormalizeAccessReview((JsonObject)payload.DeepClone(), AccessReviewQueriesRewritten);
        string now = Clock.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        created.Insert(0, "id", Guid.NewGuid().ToString("D"));
        created["status"] = "NotStarted";
        created["createdDateTime"] = now;
        created["lastModifiedDateTime"] = now;
        created["createdBy"] = new JsonObject { ["id"] = AccessReviewCreatorId, ["displayName"] = "EARM test runner", ["userPrincipalName"] = "runner@contoso.com" };
        collection.Add(created);
        return (HttpStatusCode.Created, created.DeepClone());
    }

    private void ReplaceAccessReview(JsonObject stored, JsonNode? body, RetryHint hint)
    {
        if (body is not JsonObject payload)
        {
            throw BadRequest("The request body must be an accessReviewScheduleDefinition.", hint);
        }

        if (payload.Any(p => p.Key.Equals("id", StringComparison.OrdinalIgnoreCase) && !ValueEquals(GetString(payload, "id"), GetString(stored, "id"))))
        {
            throw BadRequest("Property 'id' is read-only and cannot be set.", hint);
        }

        RefuseReadOnlyAccessReviewMembers(payload, hint);
        RefuseDifferentReviewerAliases(payload, hint);
        if (payload["scope"] is not JsonObject)
        {
            throw BadRequest("The property 'scope' is required: PUT replaces the whole definition.", hint);
        }

        var replacement = NormalizeAccessReview((JsonObject)payload.DeepClone(), AccessReviewQueriesRewritten);
        if (!JsonNode.DeepEquals(replacement["scope"], stored["scope"]))
        {
            throw BadRequest("The scope of an access review cannot be updated.", hint);
        }

        if (!JsonNode.DeepEquals(replacement["instanceEnumerationScope"], stored["instanceEnumerationScope"]))
        {
            throw BadRequest("The instanceEnumerationScope of an access review cannot be updated.", hint);
        }

        if (!JsonNode.DeepEquals(replacement["reviewers"], stored["reviewers"])
            && (ReviewerQueries(stored["reviewers"]).Concat(ReviewerQueries(replacement["reviewers"])).Any(q => !IndividualUserReviewer.IsMatch(q))))
        {
            throw BadRequest("Reviewers are only updatable if individual users are assigned as reviewers.", hint);
        }

        foreach (string readOnly in new[] { "status", "createdDateTime", "createdBy" })
        {
            replacement[readOnly] = stored[readOnly]?.DeepClone();
        }

        replacement["lastModifiedDateTime"] = Clock.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        string id = GetString(stored, "id")!;
        stored.Clear();
        stored["id"] = id;
        foreach (var pair in replacement.ToList())
        {
            if (!pair.Key.Equals("id", StringComparison.OrdinalIgnoreCase))
            {
                replacement.Remove(pair.Key);
                stored[pair.Key] = pair.Value;
            }
        }
    }

    private static void RefuseReadOnlyAccessReviewMembers(JsonObject payload, RetryHint hint)
    {
        foreach (string readOnly in AccessReviewReadOnly)
        {
            if (payload.Any(p => p.Key.Equals(readOnly, StringComparison.OrdinalIgnoreCase)))
            {
                throw BadRequest($"Property '{readOnly}' is read-only and cannot be set.", hint);
            }
        }
    }

    private static void RefuseDifferentReviewerAliases(JsonObject payload, RetryHint hint)
    {
        if (payload["fallbackReviewers"] is JsonNode fallback && payload["backupReviewers"] is JsonNode backup && !JsonNode.DeepEquals(Normalized(fallback), Normalized(backup)))
        {
            throw BadRequest("backupReviewers and fallbackReviewers must not differ.", hint);
        }

        static JsonNode? Normalized(JsonNode node)
        {
            var copy = node.DeepClone();
            FillQueryRoots(copy);
            return copy;
        }
    }

    /// <summary>What Graph fills into a definition it stores (see the type).</summary>
    private static JsonObject NormalizeAccessReview(JsonObject definition, bool rewriteQueries)
    {
        if (definition["scope"] is JsonObject scope && scope["@odata.type"] is null)
        {
            scope.Insert(0, "@odata.type", "#microsoft.graph.accessReviewQueryScope");
        }

        if (!definition.ContainsKey("instanceEnumerationScope"))
        {
            definition["instanceEnumerationScope"] = null;
        }

        var fallback = definition["fallbackReviewers"] ?? definition["backupReviewers"];
        definition["reviewers"] = definition["reviewers"]?.DeepClone() ?? new JsonArray();
        definition["fallbackReviewers"] = fallback?.DeepClone() ?? new JsonArray();
        definition["backupReviewers"] = fallback?.DeepClone() ?? new JsonArray();
        FillQueryRoots(definition["reviewers"]);
        FillQueryRoots(definition["fallbackReviewers"]);
        FillQueryRoots(definition["backupReviewers"]);
        if (rewriteQueries)
        {
            RewriteQueries(definition["scope"]);
            RewriteQueries(definition["instanceEnumerationScope"]);
            foreach (string reviewers in new[] { "reviewers", "fallbackReviewers", "backupReviewers" })
            {
                foreach (var entry in definition[reviewers] is JsonArray array ? array : [])
                {
                    RewriteQueries(entry);
                }
            }
        }

        definition["stageSettings"] ??= new JsonArray();
        definition["additionalNotificationRecipients"] ??= new JsonArray();

        if (definition["settings"] is not JsonObject settings)
        {
            settings = new JsonObject();
            definition["settings"] = settings;
        }

        settings["mailNotificationsEnabled"] ??= false;
        settings["reminderNotificationsEnabled"] ??= false;
        settings["justificationRequiredOnApproval"] ??= false;
        settings["defaultDecisionEnabled"] ??= false;
        settings["defaultDecision"] ??= "None";
        settings["instanceDurationInDays"] ??= 0;
        settings["autoApplyDecisionsEnabled"] ??= false;
        settings["recommendationsEnabled"] ??= false;
        settings["decisionHistoriesForReviewersEnabled"] ??= false;
        settings["applyActions"] ??= new JsonArray();
        if (settings["recurrence"] is JsonObject recurrence)
        {
            if (recurrence["pattern"] is not JsonObject pattern)
            {
                pattern = new JsonObject();
                recurrence["pattern"] = pattern;
            }

            pattern["dayOfMonth"] ??= 0;
            pattern["daysOfWeek"] ??= new JsonArray();
            pattern["firstDayOfWeek"] ??= "sunday";
            pattern["index"] ??= "first";
            pattern["month"] ??= 0;
            if (recurrence["range"] is not JsonObject range)
            {
                range = new JsonObject();
                recurrence["range"] = range;
            }

            foreach (string date in new[] { "startDate", "endDate" })
            {
                if (GetString(range, date) is { Length: > 10 } timestamp)
                {
                    range[date] = timestamp[..10];
                }
            }

            range["endDate"] ??= "9999-12-31";
            range["numberOfOccurrences"] ??= 0;
            if (!range.ContainsKey("recurrenceTimeZone"))
            {
                range["recurrenceTimeZone"] = null;
            }
        }

        return definition;
    }

    private static void RewriteQueries(JsonNode? holder)
    {
        if (holder is JsonObject entry && GetString(entry, "query") is { } query)
        {
            entry["query"] = RewriteAccessReviewQuery(query);
        }
    }

    private static void FillQueryRoots(JsonNode? reviewers)
    {
        if (reviewers is not JsonArray array)
        {
            return;
        }

        foreach (var entry in array.OfType<JsonObject>())
        {
            if (!entry.ContainsKey("queryRoot"))
            {
                entry["queryRoot"] = null;
            }
        }
    }

    private static IEnumerable<string> ReviewerQueries(JsonNode? reviewers)
    {
        return reviewers is JsonArray array
            ? array.OfType<JsonObject>().Select(r => GetString(r, "query")).Where(q => q is not null).Select(q => q!)
            : [];
    }
}
