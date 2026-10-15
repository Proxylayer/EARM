using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Json;
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
        return Create(new UnknownValue(LimitSymbol(symbol), origins, tainted, source, resourceId));
    }

    /// <summary>The longest symbol kept in full; longer ones are shortened (see <see cref="LimitSymbol"/>).</summary>
    internal const int MaxSymbolLength = 1024;

    /// <summary>
    /// A symbol is built from the symbols of the call's arguments, so <c>concat(x, x)</c> nested in variables doubles it at every level and
    /// would grow without bound. A symbol over <see cref="MaxSymbolLength"/> characters keeps its start and gets a hash of the whole text
    /// instead of the rest: still deterministic and, for practical purposes, as distinguishing as the full text.
    /// </summary>
    internal static string LimitSymbol(string symbol)
    {
        if (symbol.Length <= MaxSymbolLength)
        {
            return symbol;
        }

        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(symbol));
        return string.Concat(symbol.AsSpan(0, MaxSymbolLength - 24), "...#", Convert.ToHexStringLower(hash.AsSpan(0, 8)));
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
        return Any(node, static leaf => SecretJson.IsSecret(leaf) || SecretJson.IsTaintedEmpty(leaf) || (IsUnknown(leaf, out var unknown) && unknown.Tainted));
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
        ValueGuard.EnsureStack();
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj)
                {
                    Visit(property.Value, leaf);
                }

                if (obj.Count == 0 && SecretJson.IsTaintedEmpty(obj))
                {
                    // An empty container that stands for a secret-dependent emptiness has no leaf of its own: it is visited as one.
                    leaf(obj);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    Visit(item, leaf);
                }

                if (array.Count == 0 && SecretJson.IsTaintedEmpty(array))
                {
                    leaf(array);
                }

                break;
            default:
                leaf(node);
                break;
        }
    }

    /// <summary>What a copy does with an empty container (see <see cref="SecretJson.MarkTaintedEmpty"/>).</summary>
    internal enum EmptyMarks
    {
        /// <summary>The copy of a marked empty container is marked too (a plain copy).</summary>
        Keep,

        /// <summary>The copy is a plain empty container (a revealed copy).</summary>
        Drop,

        /// <summary>Every empty container copied is marked: the taint of a derived value that has no leaf to carry it.</summary>
        MarkAll,
    }

    internal static JsonNode? Transform(JsonNode? node, Func<JsonNode?, JsonNode?> leaf, EmptyMarks marks = EmptyMarks.Keep)
    {
        ValueGuard.EnsureStack();
        switch (node)
        {
            case JsonObject obj:
                {
                    var copy = new JsonObject();
                    foreach (var property in obj)
                    {
                        copy[property.Key] = Transform(property.Value, leaf, marks);
                    }

                    return MarkedCopy(obj, copy, marks);
                }

            case JsonArray array:
                {
                    var copy = new JsonArray();
                    foreach (var item in array)
                    {
                        copy.Add(Transform(item, leaf, marks));
                    }

                    return MarkedCopy(array, copy, marks);
                }

            default:
                return leaf(node);
        }
    }

    private static JsonNode MarkedCopy(JsonNode source, JsonNode copy, EmptyMarks marks)
    {
        bool mark = marks switch
        {
            EmptyMarks.MarkAll => true,
            EmptyMarks.Keep => SecretJson.IsTaintedEmpty(source),
            _ => false,
        };
        return mark ? SecretJson.MarkTaintedEmpty(copy) : copy;
    }
}