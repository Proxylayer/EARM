using System;
using System.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Types;
using System.Text.Json.Nodes;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Review 3J, finding 12 (mutant-driven tests, no product change): the cases of <see cref="ResourceNameRules"/> (RN01, RN03, RN04, RN07, RN09, RN14,
/// RN15) and <see cref="ValueGuard"/> (VG04) that a mutation of the code survived.
/// </summary>
public sealed class Review3J_ResourceNameRulesMutantTests
{
    private static ResourceTypeDefinition Groups => ResourceTypeCatalog.Default.Find("Microsoft.Entra/groups")!;

    private static ResourceTypeDefinition Child => ResourceTypeCatalog.Default.Find("Microsoft.Entra/applications/federatedIdentityCredentials")!;

    // ---- RN01 ----

    [Theory]
    [InlineData("a/b")]
    [InlineData("a/")]
    [InlineData("/a")]
    public void ADepthOneType_RefusesASlash_AndSaysWhatIsExpected(string name)
    {
        string? problem = ResourceNameRules.Validate(Groups, name);

        Assert.NotNull(problem);
        Assert.Contains("a single segment without '/'", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ADepthTwoType_NeedsExactlyTwoSegments_AndNamesThemInTheMessage()
    {
        Assert.Contains("2 segments ('parent/child')", ResourceNameRules.Validate(Child, "only-one")!, StringComparison.Ordinal);
        Assert.Contains("2 segments ('parent/child')", ResourceNameRules.Validate(Child, "a/b/c")!, StringComparison.Ordinal);
        Assert.Null(ResourceNameRules.Validate(Child, "app/fic"));
    }

    // ---- RN03, RN04 ----

    [Theory]
    [InlineData(" a")]
    [InlineData("\u00a0a")]
    [InlineData("\u3000a")]
    public void LeadingWhitespace_IsRefused_WithTheLeadingOrTrailingMessage(string name)
    {
        string? problem = ResourceNameRules.Validate(Groups, name);

        Assert.NotNull(problem);
        Assert.Contains("leading or trailing whitespace", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a ")]
    [InlineData("a\u00a0")]
    [InlineData("a\u3000")]
    public void TrailingWhitespace_IsRefused_WithTheLeadingOrTrailingMessage(string name)
    {
        string? problem = ResourceNameRules.Validate(Groups, name);

        Assert.NotNull(problem);
        Assert.Contains("leading or trailing whitespace", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void InnerWhitespace_IsFine_AndOnlyTheOuterOnesCount()
    {
        Assert.Null(ResourceNameRules.Validate(Groups, "a b"));
        Assert.Null(ResourceNameRules.Validate(Groups, "a  b"));
        Assert.NotNull(ResourceNameRules.SegmentProblem("x", " x"));
        Assert.NotNull(ResourceNameRules.SegmentProblem("x", "x "));
        Assert.Null(ResourceNameRules.SegmentProblem("x", "x"));
    }

    // ---- RN07, RN09 ----

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000001", true)]
    [InlineData("{00000000-0000-0000-0000-000000000001}", true)]
    [InlineData("not-a-guid", false)]
    [InlineData("0000", false)]
    public void AGuidKind_NeedsAGuid(string leaf, bool valid)
    {
        var definition = Groups with { NameKind = ResourceNameKind.Guid, NamePattern = null };

        string? problem = ResourceNameRules.LeafProblem(definition, leaf);

        Assert.Equal(valid, problem is null);
        if (!valid)
        {
            Assert.Contains("must be a GUID", problem, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000001", true)]
    [InlineData("my-app", false)]
    public void AnAppIdKind_NeedsAGuid_WithItsOwnMessage(string leaf, bool valid)
    {
        var definition = Groups with { NameKind = ResourceNameKind.AppId, NamePattern = null };

        string? problem = ResourceNameRules.LeafProblem(definition, leaf);

        Assert.Equal(valid, problem is null);
        if (!valid)
        {
            Assert.Contains("not an application (client) id", problem, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("user@contoso.com", true)]
    [InlineData("@", true)]
    [InlineData("user", false)]
    [InlineData("user.contoso.com", false)]
    public void AUserPrincipalNameKind_NeedsAnAtSign(string leaf, bool valid)
    {
        var definition = Groups with { NameKind = ResourceNameKind.UserPrincipalName, NamePattern = null };

        string? problem = ResourceNameRules.LeafProblem(definition, leaf);

        Assert.Equal(valid, problem is null);
        if (!valid)
        {
            Assert.Contains("user principal name (user@domain)", problem, StringComparison.Ordinal);
        }
    }

    // ---- RN14 ----

    [Fact]
    [Trait("Category", "Timing")]
    public void ACatastrophicPattern_TimesOut_AndTheNameIsRejected_InsteadOfHanging()
    {
        string hostile = new string('a', 40) + "!";
        var watch = Stopwatch.StartNew();

        bool matches = ResourceNameRules.MatchesPattern("^(a+)+$", hostile);

        Assert.False(matches);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(120), "took " + watch.Elapsed);
        var definition = Groups with { NamePattern = "^(a+)+$" };
        Assert.NotNull(ResourceNameRules.LeafProblem(definition, hostile));
        Assert.Null(ResourceNameRules.LeafProblem(definition, "aaaa"));
    }

    // ---- RN15 ----

    [Fact]
    public void ADepthTwoType_ChecksEverySegment_NotOnlyTheLeaf()
    {
        Assert.Contains("leading or trailing whitespace", ResourceNameRules.Validate(Child, " app/fic")!, StringComparison.Ordinal);
        Assert.Contains("leading or trailing whitespace", ResourceNameRules.Validate(Child, "app/fic ")!, StringComparison.Ordinal);
        Assert.Contains("empty segment", ResourceNameRules.Validate(Child, "app/")!, StringComparison.Ordinal);
        Assert.Contains("empty segment", ResourceNameRules.Validate(Child, "/fic")!, StringComparison.Ordinal);
        Assert.Contains("segment of dots only", ResourceNameRules.Validate(Child, "../fic")!, StringComparison.Ordinal);
        Assert.Contains("control character", ResourceNameRules.Validate(Child, "app\u0001/fic")!, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheLeaf_IsHeldToTheNameKindAndThePattern()
    {
        var definition = Child with { NameKind = ResourceNameKind.Guid, NamePattern = null };

        Assert.Null(ResourceNameRules.Validate(definition, "any parent name/00000000-0000-0000-0000-000000000001"));
        Assert.Contains("must be a GUID", ResourceNameRules.Validate(definition, "00000000-0000-0000-0000-000000000001/not-a-guid")!, StringComparison.Ordinal);
    }

    // ---- VG04 ----

    private static JsonNode Nested(int depth, bool objects)
    {
        JsonNode node = objects ? new JsonObject() : new JsonArray();
        for (int i = 1; i < depth; i++)
        {
            node = objects ? new JsonObject { ["a"] = node } : new JsonArray(node);
        }

        return node;
    }

    [Theory]
    [InlineData(64, false)]
    [InlineData(65, true)]
    [InlineData(1, false)]
    public void SixtyFiveNestedObjects_ExceedTheDepth_SixtyFourDoNot(int depth, bool exceeds)
    {
        Assert.Equal(exceeds, ValueGuard.ExceedsDepth(Nested(depth, objects: true)));
        Assert.Equal(exceeds, ValueGuard.ExceedsDepth(Nested(depth, objects: false)));
    }

    [Fact]
    public void TheDepthOfAMixedTree_CountsEveryContainer_AndAScalarHasNone()
    {
        var mixed = new JsonObject { ["a"] = new JsonArray(new JsonObject { ["b"] = new JsonArray(1, 2) }) };

        Assert.False(ValueGuard.ExceedsDepth(mixed, 4));
        Assert.True(ValueGuard.ExceedsDepth(mixed, 3));
        Assert.False(ValueGuard.ExceedsDepth(JsonValue.Create(5), 0));
        Assert.False(ValueGuard.ExceedsDepth(null, 0));
    }
}
