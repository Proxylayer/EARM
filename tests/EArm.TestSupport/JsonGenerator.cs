using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace EArm.TestSupport;

/// <summary>
/// Deterministic random JSON for property-style tests (plain <c>[Theory]</c> loops over a seeded <see cref="Random"/>;
/// no property-testing package). Keys and scalars come from small pools so two generated documents overlap often, and
/// strings are always valid UTF-16 (no lone surrogates).
/// </summary>
public static class JsonGenerator
{
    private static readonly string[] Keys = ["id", "displayName", "description", "enabled", "tags", "settings", "members", "limits", "owner", "Mode", "x-custom", "ünï", "a b"];

    private static readonly string[] Words =
    [
        string.Empty, "alpha", "Beta", "gamma delta", "with \"quotes\"", "back\\slash", "tab\there", "line\nbreak", "ünïcødé", "日本語", "emoji 🎉", "[bracket]", "{brace}", "00000000-0000-0000-0000-000000000000",
        "6D2F1C0E-2B7A-4C1E-9A1B-4F3F1F6A0001", "6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0001", "contoso.com", "  padded  ",
    ];

    /// <summary>A random object with up to <paramref name="maxMembers"/> members and nesting up to <paramref name="depth"/>.</summary>
    public static JsonObject Object(Random random, int depth = 3, int maxMembers = 6)
    {
        ArgumentNullException.ThrowIfNull(random);
        var result = new JsonObject();
        int count = random.Next(0, maxMembers + 1);
        for (int i = 0; i < count; i++)
        {
            string key = Keys[random.Next(Keys.Length)];
            if (!result.ContainsKey(key))
            {
                result[key] = Value(random, depth - 1);
            }
        }

        return result;
    }

    /// <summary>A random JSON value; containers only while <paramref name="depth"/> is positive.</summary>
    public static JsonNode? Value(Random random, int depth)
    {
        ArgumentNullException.ThrowIfNull(random);
        int kinds = depth > 0 ? 9 : 6;
        switch (random.Next(kinds))
        {
            case 0:
                return null;
            case 1:
                return JsonValue.Create(random.Next(2) == 0);
            case 2:
                return JsonValue.Create(random.Next(-5, 1000));
            case 3:
                return JsonValue.Create(Words[random.Next(Words.Length)]);
            case 4:
                return JsonValue.Create(Math.Round((random.NextDouble() * 200) - 100, 3));
            case 5:
                return JsonValue.Create(Words[random.Next(Words.Length)] + random.Next(3).ToString(CultureInfo.InvariantCulture));
            case 6:
                return Object(random, depth, 4);
            case 7:
                return ScalarArray(random);
            default:
                return ObjectArray(random, depth);
        }
    }

    public static JsonArray ScalarArray(Random random)
    {
        var array = new JsonArray();
        int count = random.Next(0, 5);
        for (int i = 0; i < count; i++)
        {
            array.Add(JsonValue.Create(Words[random.Next(Words.Length)]));
        }

        return array;
    }

    public static JsonArray ObjectArray(Random random, int depth)
    {
        var array = new JsonArray();
        int count = random.Next(0, 4);
        for (int i = 0; i < count; i++)
        {
            array.Add(Object(random, depth - 1, 3));
        }

        return array;
    }

    /// <summary>
    /// A copy that must compare equivalent under the order-insensitive diff rules: object members and array elements are shuffled,
    /// GUID strings change case.
    /// </summary>
    public static JsonNode? Reorder(JsonNode? node, Random random, bool shuffleArrays = true)
    {
        ArgumentNullException.ThrowIfNull(random);
        switch (node)
        {
            case JsonObject obj:
                {
                    var copy = new JsonObject();
                    foreach (var pair in obj.OrderBy(_ => random.Next()))
                    {
                        copy[pair.Key] = Reorder(pair.Value, random, shuffleArrays);
                    }

                    return copy;
                }

            case JsonArray array:
                {
                    var copy = new JsonArray();
                    foreach (var item in shuffleArrays ? array.OrderBy(_ => random.Next()).AsEnumerable() : (System.Collections.Generic.IEnumerable<JsonNode?>)array)
                    {
                        copy.Add(Reorder(item, random, shuffleArrays));
                    }

                    return copy;
                }

            case JsonValue value when value.TryGetValue<string>(out var text) && Guid.TryParse(text, out _):
                return JsonValue.Create(random.Next(2) == 0 ? text.ToUpperInvariant() : text.ToLowerInvariant());
            default:
                return node?.DeepClone();
        }
    }

    /// <summary>An independent random sequence per case: <c>Random(seed + index)</c>.</summary>
    public static IEnumerable<Random> Cases(int seed, int count)
    {
        for (int i = 0; i < count; i++)
        {
            yield return new Random(seed + i);
        }
    }
}
