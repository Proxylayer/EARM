using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Az;

namespace EntraArm.Integration.Tests;

/// <summary>Small helpers over <see cref="IGraphClient"/> and <see cref="JsonNode"/> shared by the scenarios, the probes and the cleanup guard.</summary>
internal static class GraphTestExtensions
{
    /// <summary>Every item of a collection, following <c>@odata.nextLink</c>.</summary>
    public static async Task<List<JsonObject>> ListAsync(this IGraphClient graph, GraphRequest request, CancellationToken cancellationToken)
    {
        var items = new List<JsonObject>();
        await foreach (var item in graph.GetAllPagesAsync(request, cancellationToken))
        {
            if (item is JsonObject obj)
            {
                items.Add(obj);
            }
        }

        return items;
    }

    /// <summary>GET of a single entity; null for a 404.</summary>
    public static async Task<JsonObject?> GetObjectAsync(this IGraphClient graph, GraphRequest request, CancellationToken cancellationToken)
    {
        return await graph.GetAsync(request, cancellationToken) as JsonObject;
    }

    /// <summary>POST that must return the created object.</summary>
    public static async Task<JsonObject> CreateObjectAsync(this IGraphClient graph, string collectionPath, JsonObject body, CancellationToken cancellationToken)
    {
        var response = await graph.PostAsync(new GraphRequest(collectionPath), body, cancellationToken);
        return response.Body as JsonObject
            ?? throw new InvalidOperationException($"POST {collectionPath} returned no object (status {response.Status}).");
    }

    /// <summary>The string value of a property, or null when it is absent, null or not a string.</summary>
    public static string? Text(this JsonNode? node, string name)
    {
        return node is JsonObject obj && obj[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    /// <summary>The id of an object.</summary>
    public static string RequireId(this JsonNode? node)
    {
        return node.Text("id") ?? throw new InvalidOperationException("The Graph object carries no id: " + (node?.ToJsonString() ?? "null"));
    }

    /// <summary>True when the property is absent or JSON null.</summary>
    public static bool IsNullOrAbsent(this JsonNode? node, string name)
    {
        return node is not JsonObject obj || !obj.ContainsKey(name) || obj[name] is null;
    }

    /// <summary>True when the property is absent, null or an empty array.</summary>
    public static bool IsEmptyOrAbsent(this JsonNode? node, string name)
    {
        return node is not JsonObject obj || obj[name] is null || (obj[name] is JsonArray array && array.Count == 0);
    }

    /// <summary>The string items of an array property (non-string items are skipped).</summary>
    public static List<string> Strings(this JsonNode? node, string name)
    {
        var result = new List<string>();
        if (node is JsonObject obj && obj[name] is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    result.Add(text);
                }
            }
        }

        return result;
    }

    /// <summary>The object at a nested property path (<c>web</c>, <c>conditions.users</c>), or null.</summary>
    public static JsonObject? Nested(this JsonNode? node, string dottedPath)
    {
        JsonNode? current = node;
        foreach (string part in dottedPath.Split('.'))
        {
            current = current is JsonObject obj ? obj[part] : null;
        }

        return current as JsonObject;
    }
}
