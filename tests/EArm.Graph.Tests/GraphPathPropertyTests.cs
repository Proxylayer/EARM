using System;
using System.Linq;
using System.Text;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary>
/// Properties of <see cref="GraphPath"/> over seeded random strings (plain loops): a segment decodes back to the original,
/// nothing outside the safe set survives unencoded, alternate-key literals double their quotes and decode back, joins are
/// slash-normalised and idempotent.
/// </summary>
public sealed class GraphPathPropertyTests
{
    private const int Seed = 777;
    private const int Cases = 400;

    private static readonly string[] Pieces =
    [
        "a", "Z", "9", "-", ".", "_", "~", "@", ":", "(", ")", ",", "=", "$", "!", "*", "'", "\"", " ", "/", "\\", "?", "#", "%", "&", "+", ";", "<", ">", "|", "{", "}", "[", "]", "^", "`",
        "ü", "ß", "日", "本", "🎉", "%41", "..", "//",
    ];

    private static System.Collections.Generic.IEnumerable<Random> Randoms(int seed, int count)
    {
        return Enumerable.Range(0, count).Select(i => new Random(seed + i));
    }

    private static string RandomText(Random random)
    {
        var builder = new StringBuilder();
        int count = random.Next(0, 24);
        for (int i = 0; i < count; i++)
        {
            builder.Append(Pieces[random.Next(Pieces.Length)]);
        }

        // "." and ".." are refused as a whole segment (Review1B_PathTraversalTests), and control characters are refused outright; keep the corpus legal.
        var text = builder.ToString();
        return text is "." or ".." ? text + "x" : text;
    }

    [Fact]
    public void Segment_RoundTripsThroughPercentDecoding_AndLeavesOnlySafeCharacters()
    {
        foreach (var random in Randoms(Seed, Cases))
        {
            string text = RandomText(random);

            string encoded = GraphPath.Segment(text);

            Assert.True(Uri.UnescapeDataString(encoded) == text, $"'{text}' -> '{encoded}'");
            Assert.All(encoded, c => Assert.True(c < 128 && (char.IsAsciiLetterOrDigit(c) || "-._~@:(),=$!*'%".Contains(c, StringComparison.Ordinal)), $"unsafe '{c}' in '{encoded}'"));
            Assert.DoesNotContain("/", encoded, StringComparison.Ordinal);
            Assert.DoesNotContain("?", encoded, StringComparison.Ordinal);
            Assert.DoesNotContain("#", encoded, StringComparison.Ordinal);
            Assert.DoesNotContain(" ", encoded, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Segment_IsInjective_OverDistinctInputs()
    {
        var seen = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var random in Randoms(Seed + 1, Cases))
        {
            string text = RandomText(random);
            string encoded = GraphPath.Segment(text);

            if (seen.TryGetValue(encoded, out var previous))
            {
                Assert.Equal(previous, text);
            }

            seen[encoded] = text;
        }
    }

    [Fact]
    public void AlternateKey_DoublesQuotes_AndTheLiteralDecodesBackToTheValue()
    {
        foreach (var random in Randoms(Seed + 2, Cases))
        {
            string value = RandomText(random);

            string path = GraphPath.AlternateKey("applications", "uniqueName", value);

            Assert.StartsWith("applications(uniqueName='", path, StringComparison.Ordinal);
            Assert.EndsWith("')", path, StringComparison.Ordinal);
            string literal = path["applications(uniqueName='".Length..^2];
            string decoded = Uri.UnescapeDataString(literal).Replace("''", "'", StringComparison.Ordinal);
            Assert.True(decoded == value, $"'{value}' -> '{path}' -> '{decoded}'");
            // Every quote that survives in the literal is part of a doubled pair.
            Assert.Equal(0, literal.Replace("''", string.Empty, StringComparison.Ordinal).Count(c => c == '\''));
            Assert.Equal(GraphPath.KeyValue(value), literal);
        }
    }

    [Fact]
    public void AlternateKey_TrimsTheCollectionSlash_AndValidatesItsArguments()
    {
        Assert.Equal("users(userPrincipalName='a@b.c')", GraphPath.AlternateKey("users/", "userPrincipalName", "a@b.c"));
        Assert.Equal("servicePrincipals(appId='x')", GraphPath.AlternateKey("servicePrincipals//", "appId", "x"));
        Assert.Throws<ArgumentException>(() => GraphPath.AlternateKey(" ", "k", "v"));
        Assert.Throws<ArgumentException>(() => GraphPath.AlternateKey("c", string.Empty, "v"));
        Assert.Throws<ArgumentNullException>(() => GraphPath.AlternateKey("c", "k", null!));
        Assert.Throws<ArgumentNullException>(() => GraphPath.Segment(null!));
        Assert.Throws<ArgumentNullException>(() => GraphPath.KeyValue(null!));
        Assert.Throws<ArgumentNullException>(() => GraphPath.Join(null!));
    }

    [Fact]
    public void Join_NormalisesSlashes_KeepsALeadingSlash_AndIsIdempotent()
    {
        foreach (var random in Randoms(Seed + 3, Cases))
        {
            string[] fragments = Enumerable.Range(0, random.Next(0, 6))
                .Select(_ => new[] { "users", "/users/", "//a//", string.Empty, "x/y", "id-" + random.Next(100) }[random.Next(6)])
                .ToArray();

            string joined = GraphPath.Join(fragments);

            Assert.StartsWith("/", joined, StringComparison.Ordinal);
            Assert.Equal(joined, GraphPath.Join(joined));
            Assert.Equal(joined, GraphPath.Join(joined, string.Empty));
            Assert.False(joined.Length > 1 && joined.EndsWith('/'), joined);
        }

        Assert.Equal("/", GraphPath.Join());
        Assert.Equal("/", GraphPath.Join(string.Empty, null!));
        Assert.Equal("/a/b/c", GraphPath.Join("a", "/b/", "c"));
    }

    [Fact]
    public void Segment_EncodesSurrogatePairsAsFourByteUtf8_AndPairsOnly()
    {
        Assert.Equal("%F0%9F%8E%89", GraphPath.Segment("🎉"));
        Assert.Equal("%E6%97%A5", GraphPath.Segment("日"));
        Assert.Equal("%C3%BC", GraphPath.Segment("ü"));
        // A lone surrogate is not a rune: it is encoded as the replacement character, never thrown on.
        string lone = GraphPath.Segment("a\ud800b");
        Assert.StartsWith("a", lone, StringComparison.Ordinal);
        Assert.EndsWith("b", lone, StringComparison.Ordinal);
    }
}
