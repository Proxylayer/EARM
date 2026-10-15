using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Loading;
using EArm.Expressions;
using EArm.Expressions.Evaluation;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Review 3J, finding 4: <c>{"state": "enabledForReportingButNotEnforced", "State": "enabled"}</c> passed the conditional access guard (which finds
/// <c>state</c> ignoring case and takes the first match) and both keys were sent. Property names are now compared ignoring letter case when a document
/// is loaded, when <c>json()</c> and inline <c>-p</c> values are parsed, and for the objects expressions build; the declared names under
/// <c>parameters</c>, <c>variables</c> and <c>outputs</c> stay the loader's business (<c>DuplicateDefinition</c>).
/// </summary>
[Collection(CliCollection.Name)]
public sealed class Review3J_CaseVariantKeyTests
{
    private const string Policy = "{ \"type\": \"Microsoft.Entra/conditionalAccess/policies\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"p\", \"properties\": { \"displayName\": \"p\", \"state\": \"enabledForReportingButNotEnforced\", \"State\": \"enabled\", \"conditions\": {} } }";

    private static ReadOnlyMemory<byte> Utf8(string text) => Encoding.UTF8.GetBytes(text);

    // ---- DuplicateKeys ----

    [Fact]
    public void KeysThatDifferOnlyInCase_AreDuplicates_WithBothSpellings()
    {
        var found = Assert.Single(DuplicateKeys.Find(Utf8("{ \"state\": 1, \"State\": 2 }")));

        Assert.Equal("State", found.Key);
        Assert.Equal("state", found.FirstKey);
        Assert.True(found.DiffersInCase);
        Assert.Contains("\"state\" and \"State\"", found.Describe(), StringComparison.Ordinal);
        Assert.Contains("letter case", found.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnExactRepeat_IsDescribedWithoutTheCaseNote()
    {
        var found = Assert.Single(DuplicateKeys.Find(Utf8("{ \"a\": 1, \"a\": 2 }")));

        Assert.False(found.DiffersInCase);
        Assert.DoesNotContain("letter case", found.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void ARepeatInTheConditionalAccessPolicy_IsRejectedWhenTheTemplateLoads_WithoutAnyFlag()
    {
        string text = Review1C.Template("\"resources\": [ " + Policy + " ]");

        var error = Assert.Throws<TemplateLoadException>(() => new TemplateLoader().Parse(text, "ca.entra.json", ParameterSet.Empty));

        Assert.Equal(DiagnosticCodes.DuplicateJsonKey, error.Code);
        Assert.Equal("resources[0].properties.State", error.Path!.ToString());
        Assert.Contains("\"state\" and \"State\"", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDeclaredNames_UnderParametersVariablesAndOutputs_AreLeftToTheLoader()
    {
        string text = Review1C.Template("\"parameters\": { \"a\": { \"type\": \"string\" }, \"A\": { \"type\": \"string\" } }, \"variables\": { \"v\": 1, \"V\": 2 }, \"resources\": [], \"outputs\": { \"o\": { \"type\": \"int\", \"value\": 1 }, \"O\": { \"type\": \"int\", \"value\": 2 } }");

        var document = new TemplateLoader().Parse(text, "names.entra.json", ParameterSet.Empty);

        Assert.Equal(3, document.LoadDiagnostics.Count(d => d.Code == DiagnosticCodes.DuplicateDefinition));
    }

    [Fact]
    public void ButTheValuesInsideThoseNames_AreChecked()
    {
        string text = Review1C.Template("\"variables\": { \"v\": { \"k\": 1, \"K\": 2 } }, \"resources\": []");

        Assert.Equal(DiagnosticCodes.DuplicateJsonKey, Assert.Throws<TemplateLoadException>(() => new TemplateLoader().Parse(text, "v.entra.json", ParameterSet.Empty)).Code);
    }

    [Fact]
    public void AParameterFile_RejectsCaseVariantKeysInsideAValue_AndKeepsItsOwnMessageForCaseVariantNames()
    {
        var value = Assert.Throws<TemplateLoadException>(() => ParameterSet.Parse("{ \"parameters\": { \"o\": { \"value\": { \"a\": 1, \"A\": 2 } } } }", "p.json"));
        var names = Assert.Throws<TemplateLoadException>(() => ParameterSet.Parse("{ \"parameters\": { \"o\": { \"value\": 1 }, \"O\": { \"value\": 2 } } }", "p.json"));

        Assert.Equal(DiagnosticCodes.DuplicateJsonKey, value.Code);
        Assert.Equal("parameters.o.value.A", value.Path!.ToString());
        Assert.Equal(DiagnosticCodes.InvalidParameterFile, names.Code);
    }

    [Fact]
    public void AnInlineJsonParameter_WithCaseVariantKeys_IsADuplicateJsonKeyDiagnostic()
    {
        string text = Review1C.Template("\"parameters\": { \"o\": { \"type\": \"object\" } }, \"resources\": []");

        var document = new TemplateLoader().Parse(text, "inline.entra.json", ParameterSet.Empty.WithInline("o", "{\"state\":\"a\",\"State\":\"b\"}"));

        var diagnostic = Assert.Single(document.LoadDiagnostics, d => d.Code == DiagnosticCodes.DuplicateJsonKey);
        Assert.Contains("letter case", diagnostic.Message, StringComparison.Ordinal);
    }

    // ---- json() and the objects expressions build ----

    [Fact]
    public void Json_RejectsCaseVariantKeys()
    {
        var error = Assert.Throws<ExpressionEvaluationException>(() => Expr.Eval("[json('{\"state\":\"a\",\"State\":\"b\"}')]"));

        Assert.Equal(DiagnosticCodes.ExpressionEvaluationFailed, error.Code);
        Assert.Contains("letter case", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[createObject('state', 'a', 'State', 'b')]")]
    [InlineData("[union(createObject('state', 'a'), createObject('State', 'b'))]")]
    [InlineData("[toObject(createArray('state', 'State'), lambda('x', lambdaVariables('x')))]")]
    public void AnObjectBuiltWithCaseVariantKeys_IsRejectedWhenTheTreeIsEvaluated(string expression)
    {
        var tree = JsonValue.Create(expression);
        var wrapper = new JsonObject { ["properties"] = tree };

        var error = Assert.Throws<ExpressionEvaluationException>(() => Expr.Evaluator.EvaluateTree(wrapper, Expr.Context()));

        Assert.Equal(DiagnosticCodes.DuplicateJsonKey, error.Code);
        Assert.Contains("\"state\" and \"State\"", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Items_ThenToObject_WithCaseVariantKeys_IsRejected_ButDistinctKeysAreNot()
    {
        var okTree = new JsonObject { ["a"] = JsonValue.Create("[union(createObject('state', 'a'), createObject('other', 'b'))]") };
        var badTree = new JsonObject { ["a"] = JsonValue.Create("[union(createObject('k', 1), createObject('K', 2), createObject('x', 3))]") };

        Assert.Equal("b", Expr.Evaluator.EvaluateTree(okTree, Expr.Context())!["a"]!["other"]!.GetValue<string>());
        Assert.Equal(DiagnosticCodes.DuplicateJsonKey, Assert.Throws<ExpressionEvaluationException>(() => Expr.Evaluator.EvaluateTree(badTree, Expr.Context())).Code);
    }

    [Fact]
    public void ALargeObject_WithACaseVariantPair_IsFound()
    {
        var obj = new JsonObject();
        for (int i = 0; i < 40; i++)
        {
            obj["key" + i] = i;
        }

        obj["KEY7"] = 0;

        var error = Assert.Throws<ExpressionEvaluationException>(() => new EvaluationBudget().CheckPropertyNames(obj));

        Assert.Contains("\"key7\" and \"KEY7\"", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACaseVariantPair_InANestedArrayOfObjects_IsFound()
    {
        var tree = JsonNode.Parse("{ \"list\": [ { \"a\": 1 }, { \"b\": { \"x\": 1, \"X\": 2 } } ] }");

        Assert.Throws<ExpressionEvaluationException>(() => new EvaluationBudget().CheckPropertyNames(tree));
        new EvaluationBudget().CheckPropertyNames(JsonNode.Parse("{ \"list\": [ { \"a\": 1 }, { \"b\": { \"x\": 1, \"y\": 2 } } ] }"));
    }

    [Fact]
    public void TheGuardAndTheBody_SeeTheSameKeys_ForTheTemplateOfTheReviewer()
    {
        // The reviewer's template, with the repeated key moved into an expression result: the evaluated properties must fail, not reach a provider.
        string text = Review1C.Template("\"resources\": [ { \"type\": \"Microsoft.Entra/conditionalAccess/policies\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"p\", \"properties\": \"[union(createObject('displayName', 'p', 'state', 'enabledForReportingButNotEnforced'), createObject('State', 'enabled'))]\" } ]");
        var document = new TemplateLoader().Parse(text, "ca.entra.json", ParameterSet.Empty);

        var properties = document.Resources.Single().Properties;
        var error = Assert.Throws<ExpressionEvaluationException>(() => Expr.Evaluator.EvaluateTree(properties, Expr.Context()));

        Assert.Equal(DiagnosticCodes.DuplicateJsonKey, error.Code);
    }

    // ---- mutants of DuplicateKeys (DK02, DK03, DK06, DK07) ----

    private static string Repeats(int count)
    {
        // { "k0": 1, "k0": 2, "k1": 1, "k1": 2, ... } : <count> repeated keys.
        return "{ " + string.Join(", ", Enumerable.Range(0, count).Select(i => $"\"k{i}\": 1, \"k{i}\": 2")) + " }";
    }

    [Theory]
    [InlineData(49, 49)]
    [InlineData(50, 50)]
    [InlineData(51, 50)]
    [InlineData(500, 50)]
    public void TheDefaultLimit_IsFifty_ExactlyAndNotOneOff(int repeated, int expected)
    {
        Assert.Equal(expected, DuplicateKeys.Find(Utf8(Repeats(repeated))).Count);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(1, 2, 1)]
    [InlineData(3, 3, 3)]
    [InlineData(3, 4, 3)]
    [InlineData(3, 2, 2)]
    public void ACustomLimit_StopsTheScan(int limit, int repeated, int expected)
    {
        Assert.Equal(expected, DuplicateKeys.Find(Utf8(Repeats(repeated)), limit).Count);
    }

    [Fact]
    public void ThePathOfARepeatInAnArrayElement_NamesTheIndexAndTheKey()
    {
        var found = Assert.Single(DuplicateKeys.Find(Utf8("[ { \"a\": 1 }, { \"b\": 1 }, { \"key\": 1,\n  \"key\": 2 } ]")));

        Assert.Equal("[2].key", found.Path.ToString());
        Assert.Equal(new SourceLocation(2, 3), found.Location);
    }

    [Fact]
    public void ABinaryByteOrderMark_PassedStraightToFind_IsSkipped()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("{ \"a\": 1, \"a\": 2 }")];

        var found = Assert.Single(DuplicateKeys.Find(bytes));

        Assert.Equal(new SourceLocation(1, 11), found.Location);
    }

    [Fact]
    public void InvalidJson_EndsTheScanQuietly_AndKeepsWhatWasFound()
    {
        Assert.Single(DuplicateKeys.Find(Utf8("{ \"a\": 1, \"a\": 2, \"b\": ")));
        Assert.Empty(DuplicateKeys.Find(Utf8("not json")));
    }

    [Fact]
    public async Task APolicyWithTheRepeatedKey_CannotReachAProvider_ThroughTheCliEither()
    {
        using var directory = new EArm.Tests.Engine.TestDirectory(nameof(APolicyWithTheRepeatedKey_CannotReachAProvider_ThroughTheCliEither));
        string path = directory.File("ca.entra.json");
        await System.IO.File.WriteAllTextAsync(path, Review1C.Template("\"resources\": [ " + Policy + " ]"), new UTF8Encoding(false), TestContext.Current.CancellationToken);

        var result = await CliRunner.ValidateJsonAsync(path);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("DuplicateJsonKey", result.Diagnostics[0]!["code"]!.GetValue<string>());
    }
}
