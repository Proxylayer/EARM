using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Secrets;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Static;
using EArm.Expressions.Validation;
using EArm.Expressions.Values;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Review round 4, K5: an empty array / object / null that is the outcome of a computation on a secret (a <c>filter</c> by a verdict that depends on the password, a <c>take</c> or
/// <c>skip</c> by a secret count, an <c>if</c> on a secret condition that selected an empty branch) used to lose its taint: there was no leaf to carry it, so
/// <c>filter(createArray(1), lambda('x', equals(substring(pw, 0, 1), 'S')))</c> in a plain output was an oracle on the first letter of the password. The taint now rides on
/// the empty container itself (<see cref="SecretJson.MarkTaintedEmpty"/>) or on a tainted null, for the right guess and the wrong one alike.
/// </summary>
public sealed class Review4K_TaintedEmptyTests
{
    private const string Pw = "\"pw\": { \"type\": \"securestring\" }";

    private static readonly ExpressionValidator Validator = new();

    private static ExpressionAnalysis Analyze(string outputType, string value, string pw)
    {
        var parameters = ParameterSet.Empty.WithInline("pw", pw);
        string body = "\"parameters\": { " + Pw + " }, \"resources\": [], \"outputs\": { \"o\": { \"type\": \"" + outputType + "\", \"value\": " + System.Text.Json.JsonSerializer.Serialize(value) + " } }";
        return Validator.Analyze(Expr.ParseBody(body, parameters));
    }

    private static bool Flagged(ExpressionAnalysis analysis)
    {
        return analysis.Diagnostics.Any(d => d.Code == DiagnosticCodes.SecureValueInPlainOutput);
    }

    /// <summary>(output type, expression, the secret that makes the result empty / null, the secret that makes it non-empty).</summary>
    public static IEnumerable<TheoryDataRow<string, string, string, string>> Oracles()
    {
        // The password is "s3cret": the first letter is 's'.
        yield return new("array", "[filter(createArray(1), lambda('x', equals(substring(parameters('pw'), 0, 1), 'S')))]", "s3cret", "S3cret");
        yield return new("array", "[filter(createArray(1), lambda('x', equals(substring(parameters('pw'), 0, 1), 's')))]", "S3cret", "s3cret");
        yield return new("array", "[sort(filter(createArray(1), lambda('x', equals(substring(parameters('pw'), 0, 1), 's'))), lambda('a', 'b', less(lambdaVariables('a'), lambdaVariables('b'))))]", "S3cret", "s3cret");
        yield return new("array", "[take(createArray(1, 2), length(substring(parameters('pw'), 0, 0)))]", "s3cret", "s3cret");
        yield return new("array", "[skip(createArray(1), length(parameters('pw')))]", "s3cret", "s3cret");
        yield return new("array", "[range(1, sub(length(parameters('pw')), 6))]", "s3cret", "s3cret");
        yield return new("array", "[if(equals(substring(parameters('pw'), 0, 1), 'S'), createArray(1), createArray())]", "s3cret", "S3cret");
        yield return new("object", "[if(equals(substring(parameters('pw'), 0, 1), 'S'), createObject('a', 1), createObject())]", "s3cret", "S3cret");
        yield return new("string", "[if(equals(substring(parameters('pw'), 0, 1), 'S'), 'x', json('null'))]", "s3cret", "S3cret");
    }

    [Theory]
    [MemberData(nameof(Oracles))]
    public void AnEmptyOrNullResultDerivedFromASecret_IsFlaggedInAPlainOutput_ForBothTheRightAndTheWrongGuess(string type, string expression, string emptyPassword, string nonEmptyPassword)
    {
        Assert.True(Flagged(Analyze(type, expression, emptyPassword)), "the empty / null outcome must still be tainted: " + expression);
        Assert.True(Flagged(Analyze(type, expression, nonEmptyPassword)), "the non-empty outcome is tainted too: " + expression);
    }

    [Theory]
    [MemberData(nameof(Oracles))]
    public void TheSameResult_IsAcceptedInASecureOutput_AndItsRevealedValueIsThePlainEmptyOrNull(string type, string expression, string emptyPassword, string ignored)
    {
        Assert.NotNull(ignored);
        string secureType = type == "string" ? "securestring" : "secureObject";
        if (type == "array")
        {
            // A secure output of an array is a secureObject in ARM terms only for objects; arrays are not secure types, so the array rows only check the flag.
            return;
        }

        var analysis = Analyze(secureType, expression, emptyPassword);

        Assert.False(Flagged(analysis));
        var output = analysis.Static.Outputs["o"];
        Assert.True(output.IsTainted);
        var revealed = SecretJson.Reveal(output.Value);
        Assert.True(revealed is null || revealed is JsonObject { Count: 0 }, Expr.Render(revealed));
    }

    // ---- the carrier itself ----

    [Fact]
    public void Evaluating_AFilterThatKeepsNothing_YieldsAMarkedEmptyArray_ThatRedactsToTheMaskAndRevealsToAPlainEmptyArray()
    {
        var context = Expr.Context(Expr.Parameters(("pw", SecretJson.CreateString("s3cret"))));

        var result = Expr.Eval("[filter(createArray(1), lambda('x', equals(substring(parameters('pw'), 0, 1), 'S')))]", context);

        var array = Assert.IsType<JsonArray>(result);
        Assert.Empty(array);
        Assert.True(SecretJson.IsTaintedEmpty(array));
        Assert.True(SecretJson.ContainsSecret(array));
        Assert.True(UnknownJson.ContainsTaint(array));
        Assert.Equal("\"***\"", SecretJson.Redact(array)!.ToJsonString());
        var revealed = SecretJson.Reveal(array);
        Assert.Equal("[]", revealed!.ToJsonString());
        Assert.False(SecretJson.IsTaintedEmpty(revealed));
    }

    [Fact]
    public void TheMark_SurvivesTheCopiesTheEngineMakes_AndIsNotAddedToAnUntaintedEmptyArray()
    {
        var marked = SecretJson.MarkTaintedEmpty(new JsonArray());
        var plain = new JsonArray();

        Assert.True(SecretJson.IsTaintedEmpty(SecretJson.Clone(marked)));
        Assert.True(SecretJson.IsTaintedEmpty(JsonValues.Clone(marked)));
        Assert.True(SecretJson.IsTaintedEmpty(((JsonObject)SecretJson.Clone(new JsonObject { ["k"] = marked })!)["k"]));
        Assert.False(SecretJson.IsTaintedEmpty(TaintHelper.RevealSecrets(marked)));
        Assert.False(SecretJson.ContainsSecret(plain));
        Assert.False(UnknownJson.ContainsTaint(plain));
        Assert.False(SecretJson.IsTaintedEmpty(JsonValues.Clone(plain)));

        // A container that has members is never "tainted empty", whatever was marked before.
        var filled = new JsonArray(1);
        Assert.Same(filled, SecretJson.MarkTaintedEmpty(filled));
        Assert.False(SecretJson.IsTaintedEmpty(filled));
    }

    [Fact]
    public void AnUntaintedFilter_StillYieldsAnUntaintedEmptyArray()
    {
        var result = Expr.Eval("[filter(createArray(1), lambda('x', equals('a', 'b')))]");

        var array = Assert.IsType<JsonArray>(result);
        Assert.Empty(array);
        Assert.False(SecretJson.IsTaintedEmpty(array));
        Assert.False(SecretJson.ContainsSecret(array));
    }

    [Theory]
    [InlineData("map({0}, lambda('e', lambdaVariables('e')))")]
    [InlineData("filter({0}, lambda('e', true()))")]
    [InlineData("sort({0}, lambda('a', 'b', true()))")]
    [InlineData("reduce({0}, 1, lambda('c', 'n', lambdaVariables('c')))")]
    [InlineData("toObject({0}, lambda('e', 'k'))")]
    [InlineData("groupBy({0}, lambda('e', 'k'))")]
    [InlineData("concat({0}, createArray())")]
    [InlineData("union({0}, createArray())")]
    [InlineData("length({0})")]
    public void ALambdaOrCollectionFunction_OverAnEmptyThatIsASecret_StaysTainted(string call)
    {
        var context = Expr.Context(Expr.Parameters(("pw", SecretJson.CreateString("s3cret"))));
        string source = "take(createArray(1), length(substring(parameters('pw'), 0, 0)))";

        var result = Expr.Eval("[" + string.Format(System.Globalization.CultureInfo.InvariantCulture, call, source) + "]", context);

        Assert.True(UnknownJson.ContainsTaint(result), call + " lost the taint: " + Expr.Render(result));
    }

    [Fact]
    public void ATaintedNull_RevealsToNull_AndCountsAsASecret()
    {
        var context = Expr.Context(Expr.Parameters(("pw", SecretJson.CreateString("s3cret"))));

        var result = Expr.Eval("[if(equals(substring(parameters('pw'), 0, 1), 'S'), 'x', json('null'))]", context);

        Assert.True(SecretJson.IsSecret(result));
        Assert.Null(SecretJson.Reveal(result));
        Assert.Equal("\"***\"", result!.ToJsonString());
    }
}
