using System;
using System.Linq;
using System.Text.Json.Nodes;
using EntraArm.Core.Json;
using EntraArm.Core.Secrets;
using EntraArm.TestSupport;
using Xunit;

namespace EntraArm.Tests;

/// <summary>
/// Properties of <see cref="CanonicalJson"/> and <see cref="JsonPath"/> over seeded random documents: canonical text is
/// idempotent through a parse, independent of member order and layout, exclusions remove exactly the named paths, the hash
/// follows the text, paths round-trip through their rendered form.
/// </summary>
public sealed class CanonicalJsonPropertyTests
{
    private const int Seed = 31337;
    private const int Cases = 300;

    private static JsonNode? ShuffleMembers(JsonNode? node, Random random)
    {
        switch (node)
        {
            case JsonObject obj:
                {
                    var copy = new JsonObject();
                    foreach (var pair in obj.OrderBy(_ => random.Next()))
                    {
                        copy[pair.Key] = ShuffleMembers(pair.Value, random);
                    }

                    return copy;
                }

            case JsonArray array:
                {
                    var copy = new JsonArray();
                    foreach (var item in array)
                    {
                        copy.Add(ShuffleMembers(item, random));
                    }

                    return copy;
                }

            default:
                return node?.DeepClone();
        }
    }

    [Fact]
    public void Serialize_IsIdempotentThroughAParse()
    {
        foreach (var random in JsonGenerator.Cases(Seed, Cases))
        {
            var document = JsonGenerator.Object(random);

            string canonical = CanonicalJson.Serialize(document);
            string again = CanonicalJson.Serialize(JsonNode.Parse(canonical));

            Assert.True(canonical == again, $"{canonical} -> {again}");
        }
    }

    [Fact]
    public void Serialize_IgnoresMemberOrderAndLayout_ButNotArrayOrder()
    {
        foreach (var random in JsonGenerator.Cases(Seed + 1, Cases))
        {
            var document = JsonGenerator.Object(random);

            string canonical = CanonicalJson.Serialize(document);

            Assert.Equal(canonical, CanonicalJson.Serialize(ShuffleMembers(document, random)));
            Assert.Equal(canonical, CanonicalJson.Serialize(JsonNode.Parse(document.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }))));
            Assert.DoesNotContain("\n", canonical, StringComparison.Ordinal);
            Assert.DoesNotContain(": ", canonical.Replace("\": ", string.Empty, StringComparison.Ordinal).Replace("\\\": ", string.Empty, StringComparison.Ordinal).Replace("\\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        }

        var swapped = JsonNode.Parse("[1,2]");
        Assert.NotEqual(CanonicalJson.Serialize(swapped), CanonicalJson.Serialize(JsonNode.Parse("[2,1]")));
    }

    [Fact]
    public void Hash_FollowsTheCanonicalText()
    {
        foreach (var random in JsonGenerator.Cases(Seed + 2, Cases))
        {
            var document = JsonGenerator.Object(random);
            var shuffled = ShuffleMembers(document, random);

            string hash = CanonicalJson.Hash(document);

            Assert.Equal(64, hash.Length);
            Assert.Matches("^[0-9a-f]{64}$", hash);
            Assert.Equal(hash, CanonicalJson.Hash(shuffled));
            Assert.Equal(hash, CanonicalJson.Sha256(CanonicalJson.Serialize(document)));
            Assert.NotEqual(hash, CanonicalJson.Hash(new JsonObject { ["wrapped"] = document.DeepClone() }));
        }
    }

    [Fact]
    public void Exclusions_RemoveExactlyTheNamedPaths()
    {
        foreach (var random in JsonGenerator.Cases(Seed + 3, Cases))
        {
            var document = JsonGenerator.Object(random, 3, 6);
            var keys = document.Select(p => p.Key).ToList();
            if (keys.Count == 0)
            {
                Assert.Equal("{}", CanonicalJson.Serialize(document, [JsonPath.Of("anything")]));
                continue;
            }

            string victim = keys[random.Next(keys.Count)];

            var reduced = JsonNode.Parse(CanonicalJson.Serialize(document, [JsonPath.Of(victim)]))!.AsObject();

            Assert.False(reduced.ContainsKey(victim));
            Assert.Equal(keys.Count - 1, reduced.Count);
            foreach (string key in keys.Where(k => k != victim))
            {
                Assert.Equal(CanonicalJson.Serialize(document[key]), CanonicalJson.Serialize(reduced[key]));
            }

            Assert.Equal(CanonicalJson.Serialize(document), CanonicalJson.Serialize(document, []));
            Assert.Equal(CanonicalJson.Serialize(document), CanonicalJson.Serialize(document, [JsonPath.Of("no-such-key-" + random.Next())]));
        }
    }

    [Fact]
    public void ExcludedArrayElements_AreDroppedByIndex()
    {
        var document = JsonNode.Parse("{ \"a\": [10, 20, 30], \"b\": { \"c\": [ { \"d\": 1 }, { \"d\": 2 } ] } }")!;

        string text = CanonicalJson.Serialize(document, [JsonPath.Parse("a[1]"), JsonPath.Parse("b.c[0]")]);

        Assert.Equal("{\"a\":[10,30],\"b\":{\"c\":[{\"d\":2}]}}", text);
    }

    [Theory]
    [InlineData("1.50", "1.5")]
    [InlineData("2.0", "2")]
    [InlineData("100", "100")]
    [InlineData("0.10", "0.1")]
    [InlineData("-3.250", "-3.25")]
    [InlineData("1e2", "100")]
    [InlineData("9223372036854775807", "9223372036854775807")]
    public void Numbers_AreNormalised(string input, string expected)
    {
        Assert.Equal(expected, CanonicalJson.Serialize(JsonNode.Parse(input)));
        Assert.Equal(expected, CanonicalJson.Serialize(JsonNode.Parse(expected)));
    }

    [Fact]
    public void Scalars_NullsAndSecrets_AreWrittenSafely()
    {
        Assert.Equal("null", CanonicalJson.Serialize(null));
        Assert.Equal("true", CanonicalJson.Serialize(JsonValue.Create(true)));
        string unicode = CanonicalJson.Serialize(JsonValue.Create("ünï 日本語 🎉 \"q\" \\ \n"));
        Assert.Equal("ünï 日本語 🎉 \"q\" \\ \n", JsonNode.Parse(unicode)!.GetValue<string>());
        string masked = CanonicalJson.Serialize(new JsonObject { ["password"] = SecretJson.CreateString("hunter2-canary"), ["name"] = "x" });
        Assert.DoesNotContain("hunter2-canary", masked, StringComparison.Ordinal);
        Assert.Contains("***", masked, StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => CanonicalJson.Serialize(new JsonObject(), null!));
        Assert.Throws<ArgumentNullException>(() => CanonicalJson.Sha256(null!));
    }

    // ---- JsonPath ----

    [Fact]
    public void Paths_RoundTripThroughTheirRenderedForm_AndAppendIsConsistent()
    {
        string[] names = ["a", "b2", "with.dot", "with space", "with'quote", "br[ack]et", "ünï", "日本", "$dollar", "UPPER", "x-y", "a\\b", "0", "[0]", string.Empty];
        foreach (var random in JsonGenerator.Cases(Seed + 4, Cases))
        {
            var path = JsonPath.Root;
            int depth = random.Next(0, 6);
            for (int i = 0; i < depth; i++)
            {
                path = random.Next(3) == 0 ? path.Append(random.Next(0, 20)) : path.Append(names[random.Next(names.Length)]);
            }

            string text = path.ToString();

            Assert.True(JsonPath.TryParse(text, out var parsed), $"cannot parse '{text}'");
            Assert.True(parsed == path, $"'{text}' parsed to '{parsed}' instead of '{path}'");
            Assert.Equal(path.GetHashCode(), parsed.GetHashCode());
            Assert.Equal(text, parsed.ToString());
            Assert.True(path.StartsWith(JsonPath.Root));
            Assert.True(path.StartsWith(path));
            Assert.Equal(depth, path.Depth);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("$")]
    public void TheRoot_IsEmptyOrDollar(string text)
    {
        Assert.True(JsonPath.TryParse(text, out var path));
        Assert.True(path == JsonPath.Root);
        Assert.Equal(0, path.Depth);
    }

    [Theory]
    [InlineData("a[")]
    [InlineData("a[x]")]
    [InlineData("a[-1]")]
    [InlineData("a['unterminated")]
    [InlineData("[")]
    [InlineData("a b[0")]
    public void MalformedPaths_AreRejected_WithoutThrowing(string text)
    {
        Assert.False(JsonPath.TryParse(text, out _));
        Assert.Throws<FormatException>(() => JsonPath.Parse(text));
    }

    [Theory]
    [InlineData("a.", "a")]
    [InlineData("a..b", "a.b")]
    [InlineData(" a.b ", "a.b")]
    [InlineData("$.a.b", "a.b")]
    public void TheParserIsLenientAboutEmptySegmentsAndOuterWhitespace(string text, string canonical)
    {
        Assert.True(JsonPath.TryParse(text, out var path));
        Assert.True(path == JsonPath.Parse(canonical), $"{text} -> {path}");
    }
}
