using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using EArm.Graph;

namespace EArm.TestSupport;

/// <summary>The query options of <see cref="FakeDirectory"/>: the advanced-query rules, <c>$select</c> and default property sets, <c>$filter</c> and paging.</summary>
public sealed partial class FakeDirectory
{
    // ---- advanced queries ----

    /// <summary>The largest <c>$top</c> Graph accepts on a directory collection (users, groups, applications, servicePrincipals, administrative units, deleted items).</summary>
    private const int MaxDirectoryPageSize = 999;

    private static readonly HashSet<string> DirectoryObjectRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "users", "groups", "applications", "servicePrincipals", "directoryObjects", "directory/administrativeUnits", "directory/deletedItems",
    };

    /// <summary>
    /// Graph rejects <c>$search</c>, <c>$count</c> and the advanced <c>$filter</c> operators (<c>ne</c>, <c>endswith</c>, <c>not</c>) on
    /// directory objects unless <c>ConsistencyLevel: eventual</c> is sent, and a <c>$filter</c> of that kind also needs <c>$count=true</c>.
    /// </summary>
    private static void EnforceQueryRules(Route route, IReadOnlyDictionary<string, string>? query, IReadOnlyDictionary<string, string>? headers, RetryHint hint)
    {
        if (query is null || query.Count == 0 || !DirectoryObjectRoots.Contains(route.Root))
        {
            return;
        }

        if (query.TryGetValue("$top", out var top) && int.TryParse(top, NumberStyles.Integer, CultureInfo.InvariantCulture, out var topValue) && topValue > MaxDirectoryPageSize)
        {
            throw new GraphException(HttpStatusCode.BadRequest, "Request_UnsupportedQuery", $"Invalid page size specified: '{topValue}'. Must be between 1 and {MaxDirectoryPageSize} inclusive.", hint);
        }

        bool search = query.ContainsKey("$search");
        bool count = query.TryGetValue("$count", out var countValue) && countValue.Equals("true", StringComparison.OrdinalIgnoreCase);
        query.TryGetValue("$filter", out var filter);
        bool advancedFilter = !string.IsNullOrWhiteSpace(filter) && AdvancedFilter.IsMatch(filter);
        if (!search && !count && !advancedFilter)
        {
            return;
        }

        bool eventual = headers is not null && headers.Any(h => h.Key.Equals("ConsistencyLevel", StringComparison.OrdinalIgnoreCase) && h.Value.Equals("eventual", StringComparison.OrdinalIgnoreCase));
        if (!eventual)
        {
            string what = search ? "$search" : count ? "$count" : "this $filter";
            throw new GraphException(HttpStatusCode.BadRequest, "Request_UnsupportedQuery", $"The request with {what} is only supported on directory objects with the header 'ConsistencyLevel: eventual'.", hint);
        }

        if (advancedFilter && !count)
        {
            throw new GraphException(HttpStatusCode.BadRequest, "Request_UnsupportedQuery", "Operator applied to the $filter property is not supported without $count=true (advanced query).", hint);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex AdvancedFilter = new(@"(\s(ne)\s)|endswith\s*\(|(^|\s|\()not(\s|\()", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // ---- $select and default property sets ----

    private static IReadOnlyList<string>? SelectOf(IReadOnlyDictionary<string, string>? query)
    {
        if (query is null || !query.TryGetValue("$select", out var select) || string.IsNullOrWhiteSpace(select))
        {
            return null;
        }

        var names = select.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return names.Length == 0 ? null : names;
    }

    /// <summary>Graph answers a <c>$select</c> of a property the type does not have with a 400, even when nothing matches.</summary>
    private static void ValidateSelect(FakeDirectorySchema schema, IReadOnlyList<string>? select, RetryHint hint)
    {
        if (select is null)
        {
            return;
        }

        foreach (var name in select)
        {
            if (name != "*" && !schema.IsKnown(name))
            {
                throw new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", $"Could not find a property named '{name}' on type '{schema.Name}'.", hint);
            }
        }
    }

    /// <summary>
    /// What Graph returns for a typed object: <c>id</c>, the <c>@odata.*</c> annotations and either the selected properties or, without <c>$select</c>, only the
    /// type's default property set (stored properties outside both are left out; a selected property the object holds no value for is left out too, where Graph would
    /// answer <c>null</c>: keeping the stored order and omitting it avoids a diff against objects seeded without it). Untyped objects, and <c>$select=*</c>, are returned whole.
    /// </summary>
    private static JsonObject ProjectObject(JsonObject entity, FakeDirectorySchema? schema, IReadOnlyList<string>? select)
    {
        if (schema is null || (select is not null && select.Contains("*")))
        {
            return (JsonObject)entity.DeepClone();
        }

        IReadOnlyCollection<string> wanted = select ?? schema.Default;
        var result = new JsonObject();
        foreach (var pair in entity)
        {
            if (pair.Key.Contains('@', StringComparison.Ordinal) || pair.Key.Equals("id", StringComparison.OrdinalIgnoreCase) || wanted.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                result[pair.Key] = pair.Value?.DeepClone();
            }
        }

        return result;
    }
    private static List<JsonObject> Project(IEnumerable<JsonObject> items, FakeDirectorySchema? schema, IReadOnlyDictionary<string, string>? query, RetryHint hint)
    {
        var list = items.ToList();
        if (schema is null)
        {
            return list;
        }

        var select = SelectOf(query);
        ValidateSelect(schema, select, hint);
        return list.Select(o => ProjectObject(o, schema, select)).ToList();
    }

    private static JsonNode ProjectOne(JsonObject entity, FakeDirectorySchema? schema, IReadOnlyDictionary<string, string>? query, RetryHint hint)
    {
        if (schema is null)
        {
            return entity.DeepClone();
        }

        var select = SelectOf(query);
        ValidateSelect(schema, select, hint);
        return ProjectObject(entity, schema, select);
    }

    /// <summary>A navigation that mixes object types (members, owners): each object is projected by its own type.</summary>
    private List<JsonObject> ProjectMixed(IEnumerable<JsonObject> items, IReadOnlyDictionary<string, string>? query)
    {
        var select = SelectOf(query);
        return items.Select(o => ProjectObject(o, SchemaOfId(GetString(o, "id")), select?.Where(n => SchemaOfId(GetString(o, "id"))?.IsKnown(n) != false).ToList())).ToList();
    }

    private FakeDirectorySchema? SchemaOfId(string? id)
    {
        return id is not null && LocateById(id, out var collectionPath) is not null ? FakeDirectorySchema.ForCollection(collectionPath) : null;
    }

    // ---- query ----

    private static List<JsonObject> Filter(IEnumerable<JsonObject> objects, IReadOnlyDictionary<string, string>? query, RetryHint hint)
    {
        var list = objects.ToList();
        if (query is null || !query.TryGetValue("$filter", out var filter) || string.IsNullOrWhiteSpace(filter))
        {
            return list;
        }

        var clauses = filter.Split(" and ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var clause in clauses)
        {
            var negation = System.Text.RegularExpressions.Regex.Match(clause, @"^\s*not\s*\((?<inner>.*)\)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            string text = negation.Success ? negation.Groups["inner"].Value : clause;
            var before = list;
            var match = FilterClause.Match(text);
            if (!match.Success)
            {
                throw new GraphException(HttpStatusCode.BadRequest, "Request_UnsupportedQuery", $"Unsupported $filter clause '{clause}' (the fake supports eq, ne, startswith and endswith joined by and).", hint);
            }

            string property = match.Groups["prop"].Success ? match.Groups["prop"].Value.Trim() : match.Groups["fprop"].Value.Trim();
            string literal = (match.Groups["lit"].Success ? match.Groups["lit"].Value : match.Groups["flit"].Value).Trim();
            string? expected = literal.Equals("null", StringComparison.OrdinalIgnoreCase)
                ? null
                : literal.StartsWith('\'') && literal.EndsWith('\'') && literal.Length >= 2
                    ? literal[1..^1].Replace("''", "'", StringComparison.Ordinal)
                    : literal;
            string op = match.Groups["op"].Success ? match.Groups["op"].Value.ToLowerInvariant() : match.Groups["fn"].Value.ToLowerInvariant();
            list = op switch
            {
                "eq" => list.Where(o => ValueEquals(GetScalar(o, property), expected)).ToList(),
                "ne" => list.Where(o => !ValueEquals(GetScalar(o, property), expected)).ToList(),
                "startswith" => list.Where(o => GetScalar(o, property)?.StartsWith(expected ?? string.Empty, StringComparison.OrdinalIgnoreCase) == true).ToList(),
                _ => list.Where(o => GetScalar(o, property)?.EndsWith(expected ?? string.Empty, StringComparison.OrdinalIgnoreCase) == true).ToList(),
            };
            if (negation.Success)
            {
                list = before.Where(o => !list.Contains(o)).ToList();
            }
        }

        return list;
    }

    private static readonly System.Text.RegularExpressions.Regex FilterClause = new(
        @"^\s*(?:(?<prop>[A-Za-z_@][\w.@/]*)\s+(?<op>eq|ne)\s+(?<lit>'(?:[^']|'')*'|[^\s'()]+)|(?<fn>startswith|endswith)\s*\(\s*(?<fprop>[A-Za-z_@][\w.@/]*)\s*,\s*(?<flit>'(?:[^']|'')*')\s*\))\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static JsonObject Page(List<JsonObject> objects, string relativePath, IReadOnlyDictionary<string, string>? query, int pageSize)
    {
        int skip = 0;
        if (query is not null && query.TryGetValue("$skiptoken", out var token) && int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            skip = Math.Max(0, parsed);
        }

        int size = pageSize;
        if (query is not null && query.TryGetValue("$top", out var top) && int.TryParse(top, NumberStyles.Integer, CultureInfo.InvariantCulture, out var topValue) && topValue > 0)
        {
            size = Math.Min(size, topValue);
        }

        var page = new JsonArray();
        foreach (var item in objects.Skip(skip).Take(size))
        {
            page.Add(item.DeepClone());
        }

        var result = new JsonObject
        {
            ["@odata.context"] = "https://graph.microsoft.com/v1.0/$metadata#" + relativePath,
            ["value"] = page,
        };

        if (skip + size < objects.Count)
        {
            var parameters = new List<string> { "$skiptoken=" + (skip + size).ToString(CultureInfo.InvariantCulture) };
            if (query is not null)
            {
                foreach (var pair in query)
                {
                    if (pair.Key is not "$skiptoken")
                    {
                        parameters.Add(pair.Key + "=" + Uri.EscapeDataString(pair.Value));
                    }
                }
            }

            result["@odata.nextLink"] = "https://graph.microsoft.com/v1.0/" + relativePath + "?" + string.Join("&", parameters);
        }

        return result;
    }
}
