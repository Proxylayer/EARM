using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Az;
using EArm.Engine.Providers;
using EArm.Expressions.Values;

namespace EArm.Engine.Edges;

/// <summary>Small helpers shared by the edge-kind handlers: item normalisation and <c>$select=id</c> collection reads.</summary>
public static class EdgeReads
{
    /// <summary>Normalises a declared array item that must be an object id: a string, or a deploy-time placeholder.</summary>
    public static EdgeItem StringId(JsonNode? item, string expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (UnknownJson.IsUnknown(item, out var unknown))
        {
            return EdgeItem.Unknown(unknown.Symbol);
        }

        if (item is JsonValue value && value.TryGetValue<string>(out var text))
        {
            string trimmed = text.Trim();
            return trimmed.Length == 0 ? EdgeItem.Invalid($"expected {expected}, got an empty string.") : new EdgeItem(trimmed);
        }

        return EdgeItem.Invalid($"expected {expected}, got {JsonValues.Describe(item)}.");
    }

    /// <summary>True for <c>null</c> or a JSON null leaf.</summary>
    public static bool IsNull(JsonNode? node)
    {
        return node is null || (node is JsonValue value && !UnknownJson.IsUnknown(value) && value.GetValueKind() == JsonValueKind.Null);
    }

    /// <summary>Reads every object of a navigation (<c>/groups/{id}/members</c>) as <c>id → extra</c>; the extra keeps the <c>@odata.type</c>.</summary>
    public static async Task<Dictionary<string, JsonObject?>> ReadIdsAsync(EdgeGraphContext context, string path, GraphApi api, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(path);
        var request = new GraphRequest(path, api).WithSelect("id").WithTop(999);
        var items = await context.GetAllAsync(request, cancellationToken).ConfigureAwait(false);
        var ids = new Dictionary<string, JsonObject?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (JsonObjects.GetId(item) is { } id)
            {
                var extra = new JsonObject();
                if (JsonObjects.GetString(item, "@odata.type") is { } type)
                {
                    extra["@odata.type"] = type;
                }

                ids[Edge.NormalizeId(id)] = extra;
            }
        }

        return ids;
    }

    /// <summary>True when the string array property contains the value (case-insensitive).</summary>
    public static bool ArrayContains(JsonObject? obj, string property, string value)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(value);
        if (!JsonObjects.TryGetIgnoreCase(obj, property, out var node) || node is not JsonArray array)
        {
            return false;
        }

        foreach (var item in array)
        {
            if (item is JsonValue leaf && leaf.TryGetValue<string>(out var text) && text.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A <c>$ref</c> body.</summary>
    public static JsonObject RefBody(GraphApi api, string collection, string id)
    {
        return new JsonObject { ["@odata.id"] = EdgeGraphContext.ODataId(api, collection, id) };
    }
}
