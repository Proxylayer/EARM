using System;
using System.Text.Json.Nodes;
using EArm.Core.Secrets;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>Pure rules of the federated identity credentials provider.</summary>
public sealed class FederatedIdentityCredentialsRulesTests
{
    [Theory]
    [InlineData("github-main", true)]
    [InlineData("abc", true)]
    [InlineData("ab", false)]
    [InlineData("1good", true)]
    [InlineData("under_score", true)]
    [InlineData("_bad", false)]
    [InlineData("-bad", false)]
    [InlineData("has space", false)]
    public void NameProblem_FollowsTheGraphPattern(string name, bool valid)
    {
        string? problem = FederatedIdentityCredentialsRules.NameProblem(name);
        Assert.Equal(valid, problem is null);
        if (!valid)
        {
            Assert.Contains(name, problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NameProblem_UsesTheOverlayPatternWhenGiven()
    {
        Assert.Null(FederatedIdentityCredentialsRules.NameProblem("x", "^x$"));
        Assert.NotNull(FederatedIdentityCredentialsRules.NameProblem("github-main", "^x$"));
        string longName = "a" + new string('b', 119);
        Assert.Null(FederatedIdentityCredentialsRules.NameProblem(longName));
        Assert.NotNull(FederatedIdentityCredentialsRules.NameProblem(longName + "c"));
    }

    [Fact]
    public void IssuerProblem_RequiresAnAbsoluteHttpsUrl_AndIgnoresNonPlainValues()
    {
        Assert.Null(FederatedIdentityCredentialsRules.IssuerProblem(JsonValue.Create("https://token.actions.githubusercontent.com")));
        Assert.Contains("https", FederatedIdentityCredentialsRules.IssuerProblem(JsonValue.Create("http://insecure")), StringComparison.Ordinal);
        Assert.Contains("non-empty", FederatedIdentityCredentialsRules.IssuerProblem(JsonValue.Create(" ")), StringComparison.Ordinal);
        Assert.NotNull(FederatedIdentityCredentialsRules.IssuerProblem(JsonValue.Create("not a url")));
        Assert.Null(FederatedIdentityCredentialsRules.IssuerProblem(null));
        Assert.Null(FederatedIdentityCredentialsRules.IssuerProblem(SecretJson.CreateString("http://secret")));
    }

    [Fact]
    public void SubjectProblem_RequiresANonEmptyString()
    {
        Assert.Null(FederatedIdentityCredentialsRules.SubjectProblem(JsonValue.Create("repo:x")));
        Assert.Contains("subject", FederatedIdentityCredentialsRules.SubjectProblem(JsonValue.Create("")), StringComparison.Ordinal);
        Assert.NotNull(FederatedIdentityCredentialsRules.SubjectProblem(JsonValue.Create(42)));
        Assert.Null(FederatedIdentityCredentialsRules.SubjectProblem(null));
    }

    [Fact]
    public void Pairs_AreCaseSensitive_AndNamesAreNot()
    {
        var pair = FederatedIdentityCredentialsRules.PairOf((JsonObject)JsonNode.Parse("""{ "issuer": "https://i", "subject": "S" }""")!);
        Assert.Equal(("https://i", "S"), pair);
        Assert.Null(FederatedIdentityCredentialsRules.PairOf((JsonObject)JsonNode.Parse("""{ "issuer": "https://i" }""")!));
        Assert.True(FederatedIdentityCredentialsRules.SamePair(("https://i", "S"), ("https://i", "S")));
        Assert.False(FederatedIdentityCredentialsRules.SamePair(("https://i", "S"), ("https://i", "s")));
        Assert.True(FederatedIdentityCredentialsRules.NamesEqual("GitHub-Main", "github-main"));
        Assert.False(FederatedIdentityCredentialsRules.NamesEqual("a", "b"));
    }

    [Fact]
    public void FindPairHolder_SkipsTheCredentialItself_AndIncompleteSiblings()
    {
        var siblings = FederatedIdentityCredentialsRules.Summarize(
        [
            (JsonObject)JsonNode.Parse("""{ "id": "1", "name": "github-main", "issuer": "https://i", "subject": "S" }""")!,
            (JsonObject)JsonNode.Parse("""{ "id": "2", "name": "github-dev", "issuer": "https://i", "subject": "D" }""")!,
            (JsonObject)JsonNode.Parse("""{ "id": "3", "name": "partial", "issuer": "https://i" }""")!,
            (JsonObject)JsonNode.Parse("""{ "id": "4", "issuer": "https://i", "subject": "S" }""")!,
        ]);

        Assert.Equal(3, siblings.Count);
        Assert.Null(FederatedIdentityCredentialsRules.FindPairHolder(siblings, ("https://i", "S"), "GITHUB-MAIN"));
        Assert.Equal("github-main", FederatedIdentityCredentialsRules.FindPairHolder(siblings, ("https://i", "S"), "github-new")!.Name);
        Assert.Null(FederatedIdentityCredentialsRules.FindPairHolder(siblings, ("https://i", "X"), "github-new"));
    }

    [Fact]
    public void CountOthers_CountsDistinctNamesAcrossLiveAndDeclared()
    {
        var siblings = FederatedIdentityCredentialsRules.Summarize(
        [
            (JsonObject)JsonNode.Parse("""{ "name": "a" }""")!,
            (JsonObject)JsonNode.Parse("""{ "name": "b" }""")!,
        ]);

        Assert.Equal(3, FederatedIdentityCredentialsRules.CountOthers(siblings, ["B", "c", "me"], "me"));
        Assert.Equal(2, FederatedIdentityCredentialsRules.CountOthers(siblings, [], "x"));
        Assert.Equal(1, FederatedIdentityCredentialsRules.CountOthers(siblings, [], "a"));
        Assert.Equal(20, FederatedIdentityCredentialsRules.MaxPerApplication);
    }

    [Fact]
    public void ManagedSelects_AreTheDocumentedSets()
    {
        Assert.Equal(["id", "name", "issuer", "subject", "audiences", "description"], FederatedIdentityCredentialsRules.ManagedProperties);
        Assert.Equal(["id", "name", "issuer", "subject", "audiences", "description", "claimsMatchingExpression"], FederatedIdentityCredentialsRules.PreviewManagedProperties);
        Assert.Equal(["id", "name", "issuer", "subject"], FederatedIdentityCredentialsRules.SummaryProperties);
    }
}
