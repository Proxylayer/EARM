using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Core.Json;
using EArm.Core.Secrets;
using EArm.Expressions.Values;

namespace EArm.Engine.Providers;

/// <summary>Small <see cref="JsonObject"/> helpers shared by the diff, the provider base and the fake Graph client.</summary>
public static class JsonObjects
{
    /// <summary>Case-sensitive lookup first, then case-insensitive (Graph property names are camelCase; templates may differ).</summary>
    public static bool TryGetIgnoreCase(JsonObject? obj, string key, out JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        value = null;
        if (obj is null)
        {
            return false;
        }

        if (obj.TryGetPropertyValue(key, out value))
        {
            return true;
        }

        foreach (var property in obj)
        {
            if (property.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    /// <summary>The actual key spelling in <paramref name="obj"/> for a case-insensitive name, or <c>null</c>.</summary>
    public static string? FindKey(JsonObject? obj, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (obj is null)
        {
            return null;
        }

        if (obj.ContainsKey(key))
        {
            return key;
        }

        foreach (var property in obj)
        {
            if (property.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return property.Key;
            }
        }

        return null;
    }

    /// <summary>The plain string value of a leaf (never a secret or unknown), or <c>null</c>.</summary>
    public static string? GetString(JsonObject? obj, string key)
    {
        return TryGetIgnoreCase(obj, key, out var value) && JsonValues.TryGetString(value, out var text) && !SecretJson.IsSecret(value) && !UnknownJson.IsUnknown(value) ? text : null;
    }

    /// <summary>The Graph <c>id</c> of an object, or <c>null</c>.</summary>
    public static string? GetId(JsonObject? obj)
    {
        return GetString(obj, "id");
    }

    /// <summary>True for <c>null</c>, a JSON null leaf, or an empty array (plan section 3.1: <c>null</c>/<c>[]</c> ≡ absent).</summary>
    public static bool IsAbsent(JsonNode? node)
    {
        return node switch
        {
            null => true,
            JsonArray array => array.Count == 0,
            JsonValue value => !SecretJson.IsSecret(value) && !UnknownJson.IsUnknown(value) && value.GetValueKind() == JsonValueKind.Null,
            _ => false,
        };
    }

    /// <summary>Removes the node at a path (relative to <paramref name="root"/>) when present; property names match case-insensitively.</summary>
    public static bool Remove(JsonObject root, JsonPath path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);
        if (path.IsRoot)
        {
            return false;
        }

        JsonNode? current = root;
        for (int i = 0; i < path.Depth - 1; i++)
        {
            var segment = path.Segments[i];
            if (segment.IsIndex)
            {
                current = current is JsonArray array && segment.Index < array.Count ? array[segment.Index] : null;
            }
            else
            {
                current = TryGetIgnoreCase(current as JsonObject, segment.Name!, out var child) ? child : null;
            }

            if (current is null)
            {
                return false;
            }
        }

        var last = path.Last;
        if (last.IsIndex)
        {
            if (current is JsonArray array && last.Index < array.Count)
            {
                array.RemoveAt(last.Index);
                return true;
            }

            return false;
        }

        if (current is JsonObject obj && FindKey(obj, last.Name!) is { } key)
        {
            obj.Remove(key);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Reads a value at a dotted path (<c>conditions.users</c>); a path that does not exist yields <c>false</c>.
    /// A property whose own name contains a dot (<c>@odata.type</c>) is matched as a whole first.
    /// </summary>
    public static bool TryGetDotted(JsonObject? obj, string dottedPath, out JsonNode? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dottedPath);
        if (TryGetIgnoreCase(obj, dottedPath, out value))
        {
            return true;
        }

        value = null;
        JsonNode? current = obj;
        foreach (var segment in dottedPath.Split('.'))
        {
            if (!TryGetIgnoreCase(current as JsonObject, segment, out current))
            {
                return false;
            }
        }

        value = current;
        return true;
    }

    /// <summary>Sets a value at a dotted path (<c>passwordProfile.forceChangePasswordNextSignIn</c>), creating objects on the way, only when the leaf key is absent.</summary>
    public static bool SetDefault(JsonObject root, string dottedPath, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(dottedPath);

        var segments = dottedPath.Split('.');
        var current = root;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            string key = FindKey(current, segments[i]) ?? segments[i];
            if (TryGetIgnoreCase(current, key, out var child))
            {
                if (child is not JsonObject childObject)
                {
                    // The author set the intermediate property to something that is not an object: leave it alone.
                    return false;
                }

                current = childObject;
            }
            else
            {
                var created = new JsonObject();
                current[key] = created;
                current = created;
            }
        }

        string leaf = segments[^1];
        if (FindKey(current, leaf) is not null)
        {
            return false;
        }

        current[leaf] = JsonValues.Clone(value);
        return true;
    }

    /// <summary>Case-insensitive, GUID-aware string equality for key values.</summary>
    public static bool KeyEquals(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (Guid.TryParse(left, out var leftGuid) && Guid.TryParse(right, out var rightGuid))
        {
            return leftGuid == rightGuid;
        }

        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every unknown leaf under <paramref name="node"/> with its path, for messages.</summary>
    public static IReadOnlyList<(JsonPath Path, string Symbol)> UnknownLeaves(JsonNode? node, JsonPath basePath)
    {
        ArgumentNullException.ThrowIfNull(basePath);
        var found = new List<(JsonPath, string)>();
        Collect(node, basePath, found);
        return found;

        static void Collect(JsonNode? node, JsonPath path, List<(JsonPath, string)> found)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var property in obj)
                    {
                        Collect(property.Value, path.Append(property.Key), found);
                    }

                    break;
                case JsonArray array:
                    for (int i = 0; i < array.Count; i++)
                    {
                        Collect(array[i], path.Append(i), found);
                    }

                    break;
                default:
                    if (UnknownJson.IsUnknown(node, out var unknown))
                    {
                        found.Add((path, unknown.Symbol));
                    }

                    break;
            }
        }
    }
}
