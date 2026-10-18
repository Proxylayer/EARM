using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Loading;
using EArm.Core.Types;
using EArm.Engine.Planning;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Static;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Review 1C, finding I-07: names produced by an expression skipped the catalog's <c>nameKind</c> and <c>namePattern</c> checks (only literal
/// names were validated), and the patterns ended in <c>$</c>, which also matches before a trailing line feed: <c>"abc\n"</c>, <c>" a b "</c> reached
/// Graph as a <c>uniqueName</c>. <see cref="ResourceNameRules"/> now holds one set of rules for literal and computed names; patterns are matched
/// against the whole text.
/// </summary>
public sealed class Review1C_ResourceNameRulesTests
{
    private static ResourceTypeDefinition Groups => ResourceTypeCatalog.Default.Find("Microsoft.Entra/groups")!;

    private static ResourceTypeDefinition Type(string type)
    {
        return ResourceTypeCatalog.Default.Find(type) ?? throw new InvalidOperationException(type);
    }

    // ---- the rules ----

    [Theory]
    [InlineData("earm-it-c0ceb0-phish", true)]
    [InlineData("123456789012345678901234567890", true)]
    [InlineData("1234567890123456789012345678901", false)]
    [InlineData("earm-it-c0ceb0-phishing-resistant", false)]
    public void AnAuthenticationStrengthName_IsAtMostThirtyCharacters(string name, bool valid)
    {
        var strengths = Type("Microsoft.Entra/conditionalAccess/authenticationStrengths");

        string? problem = ResourceNameRules.Validate(strengths, name);

        Assert.Equal(30, strengths.NameMaxLength);
        Assert.Equal(valid, problem is null);
        if (!valid)
        {
            Assert.Contains($"is {name.Length} characters long", problem, StringComparison.Ordinal);
            Assert.Contains("at most 30 characters", problem, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Microsoft.Entra/conditionalAccess/namedLocations")]
    [InlineData("Microsoft.Entra/conditionalAccess/policies")]
    [InlineData("Microsoft.Entra/groups")]
    public void OnlyTypesWithADocumentedOrObservedLimit_CarryANameMaxLength(string type)
    {
        Assert.Null(Type(type).NameMaxLength);
        Assert.Null(ResourceNameRules.Validate(Type(type), new string('a', 200)));
    }

    [Theory]
    [InlineData("abc", true)]
    [InlineData("a b", true)]
    [InlineData("a?b#c", true)]
    [InlineData("abc\n", false)]
    [InlineData("abc\r\n", false)]
    [InlineData("\nabc", false)]
    [InlineData(" a b ", false)]
    [InlineData("a b ", false)]
    [InlineData(" ab", false)]
    [InlineData("a\tb", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("...", false)]
    [InlineData("a.", true)]
    [InlineData(".a", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void ANameForGroups_FollowsTheCatalogPatternAndTheUniversalRules(string name, bool valid)
    {
        string? problem = ResourceNameRules.Validate(Groups, name);

        Assert.Equal(valid, problem is null);
    }

    [Fact]
    public void TheDollarOfAPattern_MeansTheEndOfTheText_NotBeforeAFinalLineFeed()
    {
        Assert.False(ResourceNameRules.MatchesPattern("^[a-z]+$", "abc\n"));
        Assert.True(ResourceNameRules.MatchesPattern("^[a-z]+$", "abc"));
        Assert.False(ResourceNameRules.MatchesPattern("^[a-z]+$", "\nabc"));
        Assert.Equal(@"\A[a-z]+\z", ResourceNameRules.StrictPattern("^[a-z]+$"));
        Assert.Equal(@"\A[a-z]+\$", ResourceNameRules.StrictPattern(@"^[a-z]+\$"));
        Assert.Equal(@"\A[a-z]+\\\z", ResourceNameRules.StrictPattern(@"^[a-z]+\\$"));
        Assert.Equal("[a-z]+", ResourceNameRules.StrictPattern("[a-z]+"));
    }

    [Fact]
    public void EveryCatalogPattern_RejectsATrailingLineFeed()
    {
        foreach (var definition in ResourceTypeCatalog.Default.Types.Where(t => t.NamePattern is not null))
        {
            string pattern = definition.NamePattern!;
            // Find some text the pattern accepts (the type's own valid sample), then add a line feed.
            string sample = definition.NameKind switch
            {
                ResourceNameKind.Guid or ResourceNameKind.AppId => "00000000-0000-4000-8000-000000000000",
                ResourceNameKind.UserPrincipalName => "user@contoso.com",
                ResourceNameKind.Id => "c1",
                _ => "abc",
            };
            if (!ResourceNameRules.MatchesPattern(pattern, sample))
            {
                continue;
            }

            Assert.False(ResourceNameRules.MatchesPattern(pattern, sample + "\n"), definition.Type + " accepts a trailing line feed");
        }
    }

    [Fact]
    public void DotOnlyNames_AreInvalid_ForEveryTypeAndEverySegment()
    {
        foreach (var definition in ResourceTypeCatalog.Default.Types)
        {
            foreach (string dots in new[] { ".", ".." })
            {
                string name = definition.Depth == 1 ? dots : string.Join('/', Enumerable.Repeat("parent", definition.Depth - 1).Append(dots));
                string? problem = ResourceNameRules.Validate(definition, name);

                Assert.True(problem is not null, $"{definition.Type} accepts the name '{name}'");
            }

            if (definition.Depth > 1)
            {
                string parentDots = string.Join('/', Enumerable.Repeat("..", 1).Concat(Enumerable.Repeat("child", definition.Depth - 1)));
                Assert.NotNull(ResourceNameRules.Validate(definition, parentDots));
            }
        }
    }

    [Fact]
    public void NameKinds_AreCheckedOnTheLastSegment()
    {
        Assert.NotNull(ResourceNameRules.Validate(Type("Microsoft.Entra/servicePrincipals"), "not-a-guid"));
        Assert.Null(ResourceNameRules.Validate(Type("Microsoft.Entra/servicePrincipals"), "00000000-0000-4000-8000-000000000000"));
        Assert.NotNull(ResourceNameRules.Validate(Type("Microsoft.Entra/users"), "no-at-sign"));
        Assert.Null(ResourceNameRules.Validate(Type("Microsoft.Entra/users"), "alice@contoso.com"));
        Assert.NotNull(ResourceNameRules.Validate(Type("Microsoft.Entra/roleAssignments"), "00000000-0000-4000-8000-00000000000g"));
    }

    [Fact]
    public void TheMessages_ShowControlCharactersAsEscapes_OnASingleLine()
    {
        string problem = ResourceNameRules.Validate(Groups, "abc\n##vso[task.setvariable variable=EVIL]1")!;

        Assert.DoesNotContain('\n', problem);
        Assert.Contains("abc\\n##vso", problem, StringComparison.Ordinal);
        Assert.Contains("control character", problem, StringComparison.Ordinal);
    }

    // ---- literal and computed names meet the same rules ----

    private static List<Diagnostic> ValidateNames(string nameJson, string extraBody = "", ParameterSet? parameters = null)
    {
        return Review1C.Validate(Review1C.Template(extraBody + "\"resources\": [ " + Review1C.Group(nameJson, "{ \"displayName\": \"g\", \"mailEnabled\": false, \"securityEnabled\": true }") + " ]"), parameters);
    }

    [Theory]
    [InlineData("\"abc\\n\"")]
    [InlineData("\" a b \"")]
    [InlineData("\".\"")]
    [InlineData("\"..\"")]
    public void ALiteralName_IsRejected(string nameJson)
    {
        var error = Assert.Single(ValidateNames(nameJson), d => d.Code == DiagnosticCodes.InvalidResourceName);

        Assert.Equal("resources[0].name", error.Path!.ToString());
    }

    [Theory]
    [InlineData("\"[concat('abc', base64ToString('Cg=='))]\"")]
    [InlineData("\"[concat(' a b ')]\"")]
    [InlineData("\"[concat('.')]\"")]
    [InlineData("\"[concat('.', '.')]\"")]
    [InlineData("\"[concat('a', base64ToString('DQo='))]\"")]
    public void AComputedName_IsRejectedOffline_WithTheSameRules(string nameJson)
    {
        var error = Assert.Single(ValidateNames(nameJson), d => d.Code == DiagnosticCodes.InvalidResourceName);

        Assert.Equal("resources[0].name", error.Path!.ToString());
    }

    [Fact]
    public void AName_FromAParameter_IsCheckedOnceTheValueIsKnown()
    {
        string body = "\"parameters\": { \"n\": { \"type\": \"string\" } }, ";

        var bad = ValidateNames("\"[parameters('n')]\"", body, ParameterSet.Empty.WithInline("n", " a b "));
        var good = ValidateNames("\"[parameters('n')]\"", body, ParameterSet.Empty.WithInline("n", "a b"));
        var missing = ValidateNames("\"[parameters('n')]\"", body);

        Assert.Contains(bad, d => d.Code == DiagnosticCodes.InvalidResourceName);
        Assert.DoesNotContain(good, d => d.Code == DiagnosticCodes.InvalidResourceName);
        Assert.DoesNotContain(missing, d => d.Code == DiagnosticCodes.InvalidResourceName);
    }

    [Fact]
    public void ACopyIterationName_IsCheckedAtExpansion()
    {
        var catalog = ResourceTypeCatalog.Default;
        string resource = "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"[if(equals(copyIndex(), 1), concat('bad', base64ToString('Cg==')), format('good-{0}', copyIndex()))]\", "
            + "\"copy\": { \"name\": \"c\", \"count\": 3 }, \"properties\": { \"displayName\": \"g\", \"mailEnabled\": false, \"securityEnabled\": true } }";
        var document = new TemplateLoader(catalog).Parse(Review1C.Template("\"resources\": [ " + resource + " ]"), "copy.entra.json", ParameterSet.Empty);
        var staticContext = new StaticEvaluator().Evaluate(document);
        var diagnostics = new List<Diagnostic>();

        var instances = new ResourceExpander(ExpressionEvaluator.Default, catalog).Expand(staticContext, staticContext.Context, diagnostics);

        Assert.Equal(["good-0", "good-2"], instances.Select(i => i.Name).ToArray());
        var error = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.InvalidResourceName);
        Assert.StartsWith("copy 'c' iteration 1: ", error.Message, StringComparison.Ordinal);
        Assert.Contains("control character", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InstanceEvaluator_AppliesTheRules_ToAnEvaluatedName()
    {
        var catalog = ResourceTypeCatalog.Default;
        var document = new TemplateLoader(catalog).Parse(
            Review1C.Template("\"parameters\": { \"n\": { \"type\": \"string\" } }, \"resources\": [ " + Review1C.Group("\"[parameters('n')]\"", "{ \"displayName\": \"g\", \"mailEnabled\": false, \"securityEnabled\": true }") + " ]"),
            "eval.entra.json",
            ParameterSet.Empty.WithInline("n", "abc\n"));
        var staticContext = new StaticEvaluator().Evaluate(document);
        var declaration = document.Resources[0];

        var evaluation = InstanceEvaluator.EvaluateName(ExpressionEvaluator.Default, declaration, Groups, staticContext.ContextFor(declaration));

        Assert.Null(evaluation.Id);
        Assert.Equal(DiagnosticCodes.InvalidResourceName, evaluation.ErrorCode);
        Assert.Contains("control character", evaluation.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLiteralNamesOfTheSamples_AreStillAccepted()
    {
        foreach (string sample in new[] { "orders-api.entra.json", "orders-api.flat.entra.json", "tenant-security.entra.json", "app-with-sp-and-secret.entra.json" })
        {
            string path = TestPaths.Sample(sample);
            var document = new TemplateLoader().Parse(File.ReadAllText(path, Encoding.UTF8), path, ParameterSet.Empty);
            var errors = Review1C.Validate(File.ReadAllText(path, Encoding.UTF8)).Where(d => d.IsError).ToList();

            Assert.DoesNotContain(errors, d => d.Code == DiagnosticCodes.InvalidResourceName);
            Assert.NotEmpty(document.Resources);
        }
    }
}
