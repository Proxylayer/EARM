using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Secrets;

namespace EArm.Expressions.Values;

/// <summary>
/// Helpers for deploy-time-unknown leaves (<see cref="UnknownValue"/> payload inside a <see cref="JsonValue"/>),
/// the counterpart of <see cref="SecretJson"/> for the <c>Unknown</c> sentinel.
/// </summary>
public static class UnknownJson
{
    /// <summary>Creates an unknown leaf.</summary>
    public static JsonValue Create(UnknownValue unknown)
    {
        ArgumentNullException.ThrowIfNull(unknown);
        return JsonValue.Create(unknown, UnknownJsonContext.Default.UnknownValue)!;
    }

    public static JsonValue Create(string symbol, UnknownOrigins origins, bool tainted = false, ResourceIdentifier? source = null, ResourceIdentifier? resourceId = null)
    {
        return Create(new UnknownValue(symbol, origins, tainted, source, resourceId));
    }

    /// <summary>True when <paramref name="node"/> is an unknown leaf.</summary>
    public static bool IsUnknown(JsonNode? node, [NotNullWhen(true)] out UnknownValue? unknown)
    {
        unknown = null;
        if (node is JsonValue value && value.TryGetValue<UnknownValue>(out var found) && found is not null)
        {
            unknown = found;
            return true;
        }

        return false;
    }

    public static bool IsUnknown(JsonNode? node)
    {
        return IsUnknown(node, out _);
    }

    /// <summary>True when any leaf under <paramref name="node"/> is unknown.</summary>
    public static bool ContainsUnknown(JsonNode? node)
    {
        return Any(node, static leaf => IsUnknown(leaf));
    }

    /// <summary>True when any leaf under <paramref name="node"/> is a secret or a tainted unknown.</summary>
    public static bool ContainsTaint(JsonNode? node)
    {
        return Any(node, static leaf => SecretJson.IsSecret(leaf) || (IsUnknown(leaf, out var unknown) && unknown.Tainted));
    }

    /// <summary>The union of the origins of every unknown leaf under <paramref name="node"/>.</summary>
    public static UnknownOrigins OriginsOf(JsonNode? node)
    {
        var origins = UnknownOrigins.None;
        Visit(node, leaf =>
        {
            if (IsUnknown(leaf, out var unknown))
            {
                origins |= unknown.Origins;
            }
        });
        return origins;
    }

    /// <summary>Returns a deep copy with every unknown leaf replaced by the plain string <c>(deploy-time)</c>.</summary>
    public static JsonNode? Render(JsonNode? node)
    {
        return Transform(node, static leaf => IsUnknown(leaf) ? JsonValue.Create(UnknownValue.Display) : JsonValues.CloneLeaf(leaf));
    }

    private static bool Any(JsonNode? node, Func<JsonNode?, bool> predicate)
    {
        bool found = false;
        Visit(node, leaf => found |= predicate(leaf));
        return found;
    }

    internal static void Visit(JsonNode? node, Action<JsonNode?> leaf)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj)
                {
                    Visit(property.Value, leaf);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    Visit(item, leaf);
                }

                break;
            default:
                leaf(node);
                break;
        }
    }

    internal static JsonNode? Transform(JsonNode? node, Func<JsonNode?, JsonNode?> leaf)
    {
        switch (node)
        {
            case JsonObject obj:
                {
                    var copy = new JsonObject();
                    foreach (var property in obj)
                    {
                        copy[property.Key] = Transform(property.Value, leaf);
                    }

                    return copy;
                }

            case JsonArray array:
                {
                    var copy = new JsonArray();
                    foreach (var item in array)
                    {
                        copy.Add(Transform(item, leaf));
                    }

                    return copy;
                }

            default:
                return leaf(node);
        }
    }
}
