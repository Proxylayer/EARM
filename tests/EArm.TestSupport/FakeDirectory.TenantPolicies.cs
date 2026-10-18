using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using EArm.Graph;

namespace EArm.TestSupport;

/// <summary>
/// The routes of the tenant policies (group lifecycle, token lifetime, claims mapping, app management and cross-tenant access policies): a
/// dispatcher <see cref="TryHandleTenantPolicies"/> that each family's partial file plugs into, and the plain collection CRUD those families share
/// (list with paging and <c>$filter</c>, POST create answering the object, GET, merge PATCH answering 204, DELETE answering 204).
/// </summary>
public sealed partial class FakeDirectory
{
    /// <summary>The handled requests of every tenant policy family; <c>false</c> leaves the request to the directory routes.</summary>
    private bool TryHandleTenantPolicies(string method, string path, IReadOnlyDictionary<string, string>? query, JsonNode? body, RetryHint hint, int pageSize, out (HttpStatusCode Status, JsonNode? Body) result)
    {
        string[] segments = PolicySegments(path);
        return TryHandleGroupLifecyclePolicies(method, segments, query, body, hint, pageSize, out result);
    }

    /// <summary>The path segments after the API version, without query.</summary>
    private static string[] PolicySegments(string path)
    {
        string trimmed = path.Trim();
        int apiIndex = trimmed.IndexOf("/v1.0/", StringComparison.OrdinalIgnoreCase);
        if (apiIndex < 0)
        {
            apiIndex = trimmed.IndexOf("/beta/", StringComparison.OrdinalIgnoreCase);
        }

        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && apiIndex >= 0)
        {
            trimmed = trimmed[(apiIndex + 6)..];
        }

        int queryIndex = trimmed.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex >= 0)
        {
            trimmed = trimmed[..queryIndex];
        }

        return trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Uri.UnescapeDataString).ToArray();
    }

    /// <summary>True when <paramref name="segments"/> starts with the (case-insensitive) segments of <paramref name="prefix"/>.</summary>
    private static bool StartsWith(string[] segments, params string[] prefix)
    {
        return segments.Length >= prefix.Length && prefix.Select((p, i) => p.Equals(segments[i], StringComparison.OrdinalIgnoreCase)).All(b => b);
    }

    /// <summary>The plain CRUD of a policy collection: <paramref name="rest"/> is empty (the collection) or one id (the entity).</summary>
    private (HttpStatusCode, JsonNode?) HandlePolicyCollection(string collectionPath, string method, string[] rest, IReadOnlyDictionary<string, string>? query, JsonNode? body, RetryHint hint, int pageSize, Action<JsonObject>? beforeCreate = null)
    {
        var collection = Collection(collectionPath);
        if (rest.Length == 0)
        {
            switch (method)
            {
                case "GET":
                    return (HttpStatusCode.OK, Page(Filter(collection, query, hint), collectionPath.TrimStart('/'), query, pageSize));
                case "POST":
                    var created = Merge(new JsonObject(), body);
                    if (created.ContainsKey("id"))
                    {
                        throw BadRequest("Property 'id' is read-only and cannot be set.", hint);
                    }

                    beforeCreate?.Invoke(created);
                    created.Insert(0, "id", Guid.NewGuid().ToString("D"));
                    collection.Add(created);
                    return (HttpStatusCode.Created, created.DeepClone());
                default:
                    throw MethodNotAllowed($"{method} is not supported on '{collectionPath}'.", hint);
            }
        }

        if (rest.Length != 1)
        {
            throw NotFound(collectionPath + "/" + string.Join('/', rest), hint);
        }

        var entity = collection.FirstOrDefault(o => ValueEquals(GetString(o, "id"), rest[0])) ?? throw NotFound(collectionPath + "/" + rest[0], hint);
        switch (method)
        {
            case "GET":
                return (HttpStatusCode.OK, entity.DeepClone());
            case "PATCH":
                if (body is JsonObject payload && payload.Any(p => p.Key.Equals("id", StringComparison.OrdinalIgnoreCase) && !ValueEquals(GetString(payload, "id"), rest[0])))
                {
                    throw BadRequest("Property 'id' is read-only and cannot be set.", hint);
                }

                Merge(entity, body);
                return (HttpStatusCode.NoContent, null);
            case "DELETE":
                collection.Remove(entity);
                return (HttpStatusCode.NoContent, null);
            default:
                throw MethodNotAllowed($"{method} is not supported on '{collectionPath}/{rest[0]}'.", hint);
        }
    }

    /// <summary>A Graph-shaped list body of the given objects (one page).</summary>
    private static JsonObject ListOf(IEnumerable<JsonObject> items, string relativePath)
    {
        return new JsonObject
        {
            ["@odata.context"] = "https://graph.microsoft.com/v1.0/$metadata#" + relativePath,
            ["value"] = new JsonArray(items.Select(i => (JsonNode)i.DeepClone()).ToArray()),
        };
    }
}
