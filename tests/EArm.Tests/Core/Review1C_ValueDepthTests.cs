using System;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Secrets;
using EArm.Expressions;
using EArm.Expressions.Values;
using EArm.Tests.Engine;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Review 1C, finding I-03: a value nested thousands of levels deep (a chain of <c>createArray(variables('v{i-1}'))</c> variables) overflowed the
/// stack in the recursive value walkers and aborted the process. Values are now capped at 64 levels where they are created (a
/// <c>ValueTooDeep</c> diagnostic) and every recursive walker checks the stack, so even a hand-built tree fails with a catchable exception.
/// </summary>
public sealed class Review1C_ValueDepthTests
{
    // ---- the cap, in-process ----

    [Theory]
    [InlineData(100)]
    [InlineData(5_000)]
    [InlineData(10_000)]
    public void AChainOfNestedCreateArrayVariables_IsAValueTooDeepDiagnostic_NotAStackOverflow(int length)
    {
        string template = Review1C.Template(Review1C.VariableChain(length, "createArray") + ", \"resources\": []");

        var diagnostics = Review1C.Validate(template);

        // v0 is a string, v{k} has depth k: v64 is the last value within the limit, v65 the first beyond it.
        var errors = diagnostics.Where(d => d.Code == DiagnosticCodes.ValueTooDeep).ToList();
        Assert.Equal(length - 65, errors.Count);
        Assert.Equal("variables.v65", errors[0].Path!.ToString());
        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.ExpressionEvaluationFailed);
    }

    [Theory]
    [InlineData("createArray")]
    [InlineData("array")]
    public void TheDepthLimit_IsExactly64Levels(string function)
    {
        // v{k} wraps v{k-1}: depth k. 64 passes, 65 fails.
        string at64 = Review1C.Template(Review1C.VariableChain(65, function) + ", \"resources\": []");
        string at65 = Review1C.Template(Review1C.VariableChain(66, function) + ", \"resources\": []");

        Assert.DoesNotContain(Review1C.Validate(at64), d => d.Code == DiagnosticCodes.ValueTooDeep);
        var over = Assert.Single(Review1C.Validate(at65), d => d.Code == DiagnosticCodes.ValueTooDeep);
        Assert.Equal("variables.v65", over.Path!.ToString());
        Assert.Contains("64 levels", over.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateObject_Items_Map_AndUserFunctionOutputs_AreCappedToo()
    {
        string deep63 = Nested("createArray(", "'x'", ")", 63);
        string body = "\"functions\": [ { \"namespace\": \"f\", \"members\": { \"wrap\": { \"parameters\": [ { \"name\": \"x\", \"type\": \"array\" } ], \"output\": { \"type\": \"array\", \"value\": \"[createArray(parameters('x'))]\" } } } } ], "
            + "\"variables\": { "
            + "\"base\": \"[" + deep63 + "]\", "
            + "\"deep64\": \"[createArray(variables('base'))]\", "
            + "\"objectAt64\": \"[createObject('k', variables('base'))]\", "
            + "\"unionAt64\": \"[union(createObject('a', variables('base')), createObject('b', variables('base')))]\", "
            + "\"functionAt64\": \"[f.wrap(variables('base'))]\", "
            + "\"objectOver\": \"[createObject('k', variables('deep64'))]\", "
            + "\"itemsOver\": \"[items(createObject('k', variables('base')))]\", "
            + "\"mapOver\": \"[map(createArray(variables('base')), lambda('e', createArray(lambdaVariables('e'))))]\", "
            + "\"functionOver\": \"[f.wrap(variables('deep64'))]\" "
            + "}, \"resources\": []";

        var diagnostics = Review1C.Validate(Review1C.Template(body));

        // base has depth 63. Wrapping once gives 64 (allowed); createObject over a 64-deep value, items() (two levels), a map that wraps each
        // element and a user function that wraps a 64-deep argument all end at 65 or more.
        var tooDeep = diagnostics.Where(d => d.Code == DiagnosticCodes.ValueTooDeep).Select(d => d.Path!.ToString()).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.Equal(["variables.functionOver", "variables.itemsOver", "variables.mapOver", "variables.objectOver"], tooDeep);
    }

    [Fact]
    public void ParameterValues_CountTowardsTheLimit_WhenWrapped()
    {
        string parameterText = "{ \"parameters\": { \"p\": { \"value\": " + Review1C.NestedArrays(58) + " } } }";
        string body = "\"parameters\": { \"p\": { \"type\": \"array\" } }, \"variables\": { \"ok\": \"[createArray(parameters('p'))]\", \"deep\": \"[createArray(createArray(createArray(createArray(createArray(createArray(createArray(parameters('p'))))))))]\" }, \"resources\": []";

        var diagnostics = Review1C.Validate(Review1C.Template(body), ParameterSet.Parse(parameterText));

        Assert.Equal(["variables.deep"], diagnostics.Where(d => d.Code == DiagnosticCodes.ValueTooDeep).Select(d => d.Path!.ToString()).ToArray());
    }

    [Fact]
    public void JsonText_PastTheParserDepth_IsADiagnostic()
    {
        string text = Review1C.NestedArrays(70).Replace("'", "''", StringComparison.Ordinal);
        string body = "\"variables\": { \"j\": \"[json('" + text + "')]\" }, \"resources\": []";

        var diagnostics = Review1C.Validate(Review1C.Template(body));

        Assert.Contains(diagnostics, d => d.Path?.ToString() == "variables.j" && d.IsError);
    }

    // ---- every recursive walker, on a tree no expression could have built ----

    [Fact]
    public void EveryRecursiveWalker_FailsWithAValueTooDeepException_OnAHandBuiltTreeOfTwentyThousandLevels()
    {
        var deep = Review1C.DeepTree(20_000);
        var other = Review1C.DeepTree(20_000);

        Assert.Throws<ValueTooDeepException>(() => SecretJson.ContainsSecret(deep));
        Assert.Throws<ValueTooDeepException>(() => SecretJson.Clone(deep));
        Assert.Throws<ValueTooDeepException>(() => SecretJson.Taint(deep));
        Assert.Throws<ValueTooDeepException>(() => SecretJson.Redact(deep));
        Assert.Throws<ValueTooDeepException>(() => SecretJson.Reveal(deep));
        Assert.Throws<ValueTooDeepException>(() => CanonicalJson.Serialize(deep));
        Assert.Throws<ValueTooDeepException>(() => CanonicalJson.Hash(deep));
        Assert.Throws<ValueTooDeepException>(() => JsonValues.Clone(deep));
        Assert.Throws<ValueTooDeepException>(() => JsonValues.AreEqual(deep, other));
        Assert.Throws<ValueTooDeepException>(() => JsonValues.StructuralHash(deep));
        Assert.Throws<ValueTooDeepException>(() => UnknownJson.ContainsUnknown(deep));
        Assert.Throws<ValueTooDeepException>(() => UnknownJson.ContainsTaint(deep));
        Assert.Throws<ValueTooDeepException>(() => UnknownJson.OriginsOf(deep));
        Assert.Throws<ValueTooDeepException>(() => UnknownJson.Render(deep));
        Assert.Throws<ValueTooDeepException>(() => TaintHelper.Taint(deep));
        Assert.Throws<ValueTooDeepException>(() => TaintHelper.RevealSecrets(deep));
    }

    [Fact]
    public void ExceedsDepth_IsIterative_AndExact()
    {
        Assert.False(ValueGuard.ExceedsDepth(Review1C.DeepTree(64)));
        Assert.True(ValueGuard.ExceedsDepth(Review1C.DeepTree(65)));
        Assert.True(ValueGuard.ExceedsDepth(Review1C.DeepTree(1_000_000)));
        Assert.False(ValueGuard.ExceedsDepth(JsonValue.Create("scalar")));
        Assert.False(ValueGuard.ExceedsDepth(null));
        Assert.False(ValueGuard.ExceedsDepth(new JsonObject()));
    }

    // ---- in a process of its own: the test host must survive ----

    [Fact]
    public void TenThousandDeepChain_InAChildProcess_ExitsTwo_WithAValueTooDeepDiagnostic()
    {
        using var directory = new TestDirectory(nameof(TenThousandDeepChain_InAChildProcess_ExitsTwo_WithAValueTooDeepDiagnostic));
        string path = directory.File("chain.entra.json");
        System.IO.File.WriteAllText(path, Review1C.Template(Review1C.VariableChain(10_000, "createArray") + ", \"resources\": []"));

        var result = Review1C.RunChild(["deployment", "validate", "-f", path, "-o", "json"], TimeSpan.FromSeconds(60));

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        var report = JsonNode.Parse(result.StandardOutput)!.AsObject();
        Assert.Equal("Failed", report["status"]!.GetValue<string>());
        Assert.Contains(report["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "ValueTooDeep");
        Assert.DoesNotContain("Stack overflow", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    private static string Nested(string open, string leaf, string close, int depth)
    {
        return string.Concat(Enumerable.Repeat(open, depth)) + leaf + string.Concat(Enumerable.Repeat(close, depth));
    }
}
