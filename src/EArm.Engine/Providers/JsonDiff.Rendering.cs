using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core.Json;
using EArm.Core.Secrets;
using EArm.Expressions.Values;

namespace EArm.Engine.Providers;

public static partial class JsonDiff
{
    /// <summary>A rendering copy: unknown leaves as <c>(deploy-time)</c>, secret leaves as <c>***</c>.</summary>
    public static JsonNode? Render(JsonNode? node)
    {
        return SecretJson.Redact(UnknownJson.Render(node));
    }

    /// <summary>A rendering copy of an object (see <see cref="Render(JsonNode?)"/>).</summary>
    public static JsonObject? RenderObject(JsonObject? node)
    {
        return Render(node) as JsonObject;
    }

    /// <summary>
    /// A rendering copy of a LIVE node with the counterparts of the secret desired leaves masked: where
    /// <paramref name="desired"/> holds a secret, the live value at the same position (which on a rerun IS the
    /// secret) is shown as <c>***</c>, never verbatim. A secret leaf masks its live leaf; a container that holds a
    /// secret and has no structural counterpart (a secret-bearing array, a shape mismatch) masks every live leaf
    /// below it; objects are matched by property name (case-insensitive) and recursed.
    /// </summary>
    public static JsonNode? RenderLive(JsonNode? live, JsonNode? desired)
    {
        if (live is null)
        {
            return null;
        }

        if (!SecretJson.ContainsSecret(desired))
        {
            return Render(live);
        }

        if (desired is JsonObject desiredObject && live is JsonObject liveObject)
        {
            var copy = new JsonObject();
            foreach (var property in liveObject)
            {
                JsonObjects.TryGetIgnoreCase(desiredObject, property.Key, out var desiredValue);
                copy[property.Key] = RenderLive(property.Value, desiredValue);
            }

            return copy;
        }

        return MaskLeaves(live);
    }

    /// <summary>A rendering copy of a live object (see <see cref="RenderLive(JsonNode?, JsonNode?)"/>).</summary>
    public static JsonObject? RenderLiveObject(JsonObject? live, JsonObject? desired)
    {
        return RenderLive(live, desired) as JsonObject;
    }

    /// <summary>
    /// Masks, in place, every leaf under the given overlay <c>secure</c> paths of a rendering copy (property names
    /// case-insensitive, array indexes literal; a segment that does not exist is skipped), so a live value that the
    /// type declares secret never renders even when the template does not declare it.
    /// </summary>
    public static void MaskSecurePaths(JsonObject? rendered, IReadOnlyList<JsonPath> securePaths)
    {
        ArgumentNullException.ThrowIfNull(securePaths);
        if (rendered is null)
        {
            return;
        }

        foreach (var path in securePaths)
        {
            if (!path.IsRoot)
            {
                MaskAt(rendered, path.Segments, 0);
            }
        }
    }

    private static void MaskAt(JsonNode? node, IReadOnlyList<JsonPathSegment> segments, int depth)
    {
        var segment = segments[depth];
        bool last = depth == segments.Count - 1;
        switch (node)
        {
            case JsonObject obj when !segment.IsIndex && JsonObjects.FindKey(obj, segment.Name!) is { } key:
                if (last)
                {
                    obj[key] = MaskLeaves(obj[key]);
                }
                else
                {
                    MaskAt(obj[key], segments, depth + 1);
                }

                break;

            case JsonArray array when segment.IsIndex && segment.Index < array.Count:
                if (last)
                {
                    array[segment.Index] = MaskLeaves(array[segment.Index]);
                }
                else
                {
                    MaskAt(array[segment.Index], segments, depth + 1);
                }

                break;
        }
    }

    /// <summary>A copy of <paramref name="node"/> with every non-null leaf replaced by <c>***</c>.</summary>
    private static JsonNode? MaskLeaves(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                {
                    var copy = new JsonObject();
                    foreach (var property in obj)
                    {
                        copy[property.Key] = MaskLeaves(property.Value);
                    }

                    return copy;
                }

            case JsonArray array:
                {
                    var copy = new JsonArray();
                    foreach (var item in array)
                    {
                        copy.Add(MaskLeaves(item));
                    }

                    return copy;
                }

            default:
                return JsonValue.Create(SecretValue.Mask);
        }
    }
}
