using System;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core.Json;
using EArm.Engine.Providers;
using EArm.TestSupport;
using Xunit;

namespace EArm.Tests.Engine;

/// <summary>
/// Properties of <see cref="JsonDiff"/> over seeded random documents (plain loops, fixed seeds): a document differs
/// from itself by nothing, equivalence ignores member order, array order and GUID case, a patch built from the
/// effective deltas converges the live object onto the desired one, and rendering is idempotent.
/// </summary>
public sealed class JsonDiffPropertyTests
{
    private const int Seed = 90210;
    private const int Cases = 250;

    private static string Show(JsonNode? node)
    {
        return node?.ToJsonString() ?? "null";
    }

    /// <summary>The Graph PATCH merge: <c>null</c> removes, objects merge recursively, everything else (arrays included) replaces.</summary>
    private static JsonObject MergePatch(JsonObject live, JsonObject patch)
    {
        var result = (JsonObject)live.DeepClone();
        foreach (var pair in patch)
        {
            string? existingKey = JsonObjects.FindKey(result, pair.Key);
            if (pair.Value is null)
            {
                if (existingKey is not null)
                {
                    result.Remove(existingKey);
                }

                continue;
            }

            if (pair.Value is JsonObject patchObject && existingKey is not null && result[existingKey] is JsonObject liveObject)
            {
                result[existingKey] = MergePatch(liveObject, patchObject);
                continue;
            }

            if (existingKey is not null)
            {
                result.Remove(existingKey);
            }

            result[pair.Key] = pair.Value.DeepClone();
        }

        return result;
    }

    [Fact]
    public void ADocument_DiffersFromItselfByNothing_AndFromAReorderedCopyByNothing()
    {
        foreach (var random in JsonGenerator.Cases(Seed, Cases))
        {
            var document = JsonGenerator.Object(random);
            var reordered = (JsonObject)JsonGenerator.Reorder(document, random)!;

            Assert.True(JsonDiff.Diff(document, document.DeepClone() as JsonObject).Count == 0, "self diff: " + Show(document));
            Assert.True(JsonDiff.Diff(document, reordered).Count == 0, $"reordered diff: {Show(document)} vs {Show(reordered)}");
            Assert.True(JsonDiff.Diff(reordered, document).Count == 0, $"reordered diff (reverse): {Show(document)} vs {Show(reordered)}");
        }
    }

    [Fact]
    public void AreEquivalent_IsReflexiveSymmetric_AndInvariantUnderReordering()
    {
        foreach (var random in JsonGenerator.Cases(Seed + 1, Cases))
        {
            var a = JsonGenerator.Object(random);
            var b = JsonGenerator.Object(random);
            // Members, array elements and GUID case are all shuffled: equivalence ignores every kind of order.
            var reordered = JsonGenerator.Reorder(a, random);

            Assert.True(JsonDiff.AreEquivalent(a, a), "reflexive: " + Show(a));
            Assert.True(JsonDiff.AreEquivalent(a, reordered), "reorder: " + Show(a));
            Assert.Equal(JsonDiff.AreEquivalent(a, b), JsonDiff.AreEquivalent(b, a));
            if (JsonDiff.AreEquivalent(a, b) && JsonDiff.AreEquivalent(b, reordered))
            {
                Assert.True(JsonDiff.AreEquivalent(a, reordered), "transitive");
            }
        }
    }

    [Fact]
    public void AreEquivalent_AgreesWithAnEmptyDiffInBothDirections_ForObjectsWithoutPartialOverlap()
    {
        foreach (var random in JsonGenerator.Cases(Seed + 2, Cases))
        {
            var a = JsonGenerator.Object(random);
            var b = JsonGenerator.Object(random);

            bool equivalent = JsonDiff.AreEquivalent(a, b);
            bool noDeltas = JsonDiff.Diff(a, b).Count == 0 && JsonDiff.Diff(b, a).Count == 0;

            // Equivalent documents never differ; a pair with deltas in either direction is never equivalent.
            Assert.True(!equivalent || noDeltas, $"equivalent but diffs: {Show(a)} / {Show(b)}");
            Assert.True(equivalent || !noDeltas, $"not equivalent but no deltas: {Show(a)} / {Show(b)}");
        }
    }

    [Fact]
    public void ThePatchOfTheEffectiveDeltas_ConvergesTheLiveObjectOntoTheDesiredOne()
    {
        foreach (var random in JsonGenerator.Cases(Seed + 3, Cases))
        {
            var desired = JsonGenerator.Object(random);
            var live = JsonGenerator.Object(random);
            var deltas = JsonDiff.Diff(desired, live);

            var body = JsonDiff.BuildPatchBody(desired, live, deltas);
            var after = MergePatch(live, body);

            var remaining = JsonDiff.Diff(desired, after);
            Assert.True(remaining.Count == 0, $"desired {Show(desired)} live {Show(live)} patch {Show(body)} after {Show(after)} remaining {string.Join(",", remaining.Select(d => d.Path + ":" + d.Kind))}");
            if (deltas.All(d => !d.HasEffect))
            {
                Assert.Empty(body);
            }

            // Patching again is a no-op.
            var again = JsonDiff.BuildPatchBody(desired, after, remaining);
            Assert.Empty(again);
        }
    }

    [Fact]
    public void EveryPatchedPropertyIsDeclaredInTheDesiredObject_OrIsAClearingNull()
    {
        foreach (var random in JsonGenerator.Cases(Seed + 4, Cases))
        {
            var desired = JsonGenerator.Object(random);
            var live = JsonGenerator.Object(random);

            var body = JsonDiff.BuildPatchBody(desired, live, JsonDiff.Diff(desired, live));

            foreach (var pair in body)
            {
                bool declared = JsonObjects.FindKey(desired, pair.Key) is not null;
                Assert.True(declared, $"patch property '{pair.Key}' is not declared: desired {Show(desired)}");
            }
        }
    }

    [Fact]
    public void DescribeCreate_ListsEveryNonAbsentTopLevelProperty_AsACreate()
    {
        foreach (var random in JsonGenerator.Cases(Seed + 5, Cases))
        {
            var desired = JsonGenerator.Object(random);

            var deltas = JsonDiff.DescribeCreate(desired);

            var expected = desired.Where(p => !JsonObjects.IsAbsent(p.Value)).Select(p => p.Key).Order(StringComparer.Ordinal).ToList();
            Assert.Equal(expected, deltas.Select(d => d.Path.Segments[^1].Name!).Order(StringComparer.Ordinal).ToList());
            Assert.All(deltas, d => Assert.Equal(PropertyChangeType.Create, d.Kind));
        }

        Assert.Empty(JsonDiff.DescribeCreate(null));
        Assert.Empty(JsonDiff.Diff(null, new JsonObject()));
    }

    [Fact]
    public void Render_IsIdempotent_AndNeverMutatesItsInput()
    {
        foreach (var random in JsonGenerator.Cases(Seed + 6, Cases))
        {
            var document = JsonGenerator.Object(random);
            string before = Show(document);

            var once = JsonDiff.Render(document);
            var twice = JsonDiff.Render(once);

            Assert.Equal(Show(once), Show(twice));
            Assert.Equal(before, Show(document));
            Assert.NotSame(document, once);
        }
    }

    [Fact]
    public void Canonical_IsStableForTheSameValue_IgnoresMemberOrderAndGuidCase()
    {
        foreach (var random in JsonGenerator.Cases(Seed + 7, Cases))
        {
            var document = JsonGenerator.Object(random);
            var reordered = JsonGenerator.Reorder(document, random, shuffleArrays: false);

            Assert.Equal(JsonDiff.Canonical(document), JsonDiff.Canonical(document.DeepClone()));
            Assert.Equal(JsonDiff.Canonical(document), JsonDiff.Canonical(reordered));
        }
    }

    /// <summary>Arrays are order-insensitive for <c>AreEquivalent</c>, as for <c>Diff</c> (e.g. role lists Graph returns in another order).</summary>
    [Fact]
    public void AreEquivalent_TreatsAReorderedArrayAsEquivalent()
    {
        var desired = JsonNode.Parse("{ \"allowedMemberTypes\": [\"User\", \"Application\"] }")!;
        var live = JsonNode.Parse("{ \"allowedMemberTypes\": [\"Application\", \"User\"] }")!;

        Assert.True(JsonDiff.AreEquivalent(desired, live));
        Assert.True(JsonDiff.Diff((JsonObject)desired, (JsonObject)live).Count == 0, "Diff already treats the arrays as sets");
    }

    [Theory]
    [InlineData("[1,2,3]", "[3,1,2]", true)]
    [InlineData("[\"a\",\"b\"]", "[\"b\",\"a\"]", true)]
    [InlineData("[{\"id\":1,\"n\":\"x\"},{\"id\":2,\"n\":\"y\"}]", "[{\"n\":\"y\",\"id\":2},{\"N\":\"x\",\"ID\":1}]", true)]
    [InlineData("[1,1,2]", "[1,2,2]", false)]
    [InlineData("[1,1,2]", "[2,1,1]", true)]
    [InlineData("{\"a\":{\"roles\":[\"x\",\"y\"]}}", "{\"a\":{\"roles\":[\"y\",\"x\"]}}", true)]
    [InlineData("[{\"roles\":[\"x\",\"y\"]},{\"roles\":[\"z\"]}]", "[{\"roles\":[\"z\"]},{\"roles\":[\"y\",\"x\"]}]", true)]
    [InlineData("[\"6D2F1C0E-2B7A-4C1E-9A1B-4F3F1F6A0001\",\"6D2F1C0E-2B7A-4C1E-9A1B-4F3F1F6A0002\"]", "[\"6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0002\",\"6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0001\"]", true)]
    [InlineData("{\"a\":null,\"b\":[]}", "{}", true)]
    [InlineData("{\"a\":[]}", "{\"a\":[1]}", false)]
    [InlineData("[1,2]", "[1,2,3]", false)]
    [InlineData("[1,2,3]", "[1,2,4]", false)]
    [InlineData("[{\"roles\":[\"x\",\"y\"]}]", "[{\"roles\":[\"x\",\"z\"]}]", false)]
    public void AreEquivalent_ComparesArraysAsMultisets(string left, string right, bool expected)
    {
        var l = JsonNode.Parse(left);
        var r = JsonNode.Parse(right);

        Assert.Equal(expected, JsonDiff.AreEquivalent(l, r));
        Assert.Equal(expected, JsonDiff.AreEquivalent(r, l));
    }
    [Fact]
    public void ScalarEquals_IsSymmetric_AndIgnoresGuidCase()
    {
        foreach (var random in JsonGenerator.Cases(Seed + 8, Cases))
        {
            var left = JsonGenerator.Value(random, 0);
            var right = JsonGenerator.Value(random, 0);

            Assert.Equal(JsonDiff.ScalarEquals(left, right), JsonDiff.ScalarEquals(right, left));
            Assert.True(JsonDiff.ScalarEquals(left, left?.DeepClone()));
        }

        Assert.True(JsonDiff.ScalarEquals(JsonValue.Create("6D2F1C0E-2B7A-4C1E-9A1B-4F3F1F6A0001"), JsonValue.Create("6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0001")));
        Assert.False(JsonDiff.ScalarEquals(JsonValue.Create("abc"), JsonValue.Create("ABC")));
    }
}
