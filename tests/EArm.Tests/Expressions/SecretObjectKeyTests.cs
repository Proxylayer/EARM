using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Secrets;
using EArm.Expressions.Evaluation;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// D4: object keys derived from secure values are masked (<c>***</c>, <c>***#2</c>, ... in first-appearance order) by
/// <c>toObject</c>, <c>groupBy</c> and <c>createObject</c>; equal real keys still group or collide; the values stay tainted.
/// </summary>
public sealed class SecretObjectKeyTests
{
    private const string Secret = "abc";
    private const string OtherSecret = "xyz";

    private static EvaluationContext Context()
    {
        return Expr.Context(Expr.Parameters(
            ("pw", SecretJson.CreateString(Secret)),
            ("pw2", SecretJson.CreateString(OtherSecret)),
            ("js", SecretJson.CreateString("{\"apiKey\":\"value\",\"nested\":{\"innerKey\":1}}"))));
    }

    private static JsonObject EvalObject(string expression)
    {
        return Assert.IsType<JsonObject>(Expr.Eval(expression, Context()));
    }

    private static string[] Keys(JsonObject value)
    {
        return value.Select(p => p.Key).ToArray();
    }

    // ---- createObject ----

    [Fact]
    public void CreateObject_WithASecretKey_RendersAMaskedKey_AndNoSecretText()
    {
        var result = EvalObject("[createObject(parameters('pw'), 1)]");

        Assert.Equal(["***"], Keys(result));
        Assert.DoesNotContain(Secret, result.ToJsonString(), System.StringComparison.Ordinal);
        Assert.True(SecretJson.IsSecret(result["***"]), "the value stays tainted");
    }

    [Fact]
    public void CreateObject_TwoDistinctSecretKeys_AreMaskedInFirstAppearanceOrder()
    {
        var result = EvalObject("[createObject(parameters('pw'), 1, parameters('pw2'), 2)]");

        Assert.Equal(["***", "***#2"], Keys(result));
        Assert.True(SecretJson.IsSecret(result["***"]));
        Assert.True(SecretJson.IsSecret(result["***#2"]));
        string text = result.ToJsonString();
        Assert.DoesNotContain(Secret, text, System.StringComparison.Ordinal);
        Assert.DoesNotContain(OtherSecret, text, System.StringComparison.Ordinal);
    }

    [Fact]
    public void CreateObject_ClearKeysAreUntouched_NextToMaskedOnes()
    {
        var result = EvalObject("[createObject('plain', 1, parameters('pw'), 2, 'other', 3)]");

        Assert.Equal(["plain", "***", "other"], Keys(result));
    }

    [Fact]
    public void CreateObject_ASecretKeyEqualToAClearKey_IsMasked_AndTheLastValueWins()
    {
        // createObject keeps last-write-wins semantics for equal real keys; a key that was secret in any occurrence is masked.
        var clearFirst = EvalObject("[createObject('abc', 1, parameters('pw'), 2)]");
        var secretFirst = EvalObject("[createObject(parameters('pw'), 1, 'abc', 2)]");

        Assert.Equal(["***"], Keys(clearFirst));
        Assert.Equal(["***"], Keys(secretFirst));
        Assert.DoesNotContain(Secret, clearFirst.ToJsonString(), System.StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, secretFirst.ToJsonString(), System.StringComparison.Ordinal);
    }

    [Fact]
    public void CreateObject_TheSameSecretKeyTwice_CollapsesToOneMaskedKey()
    {
        var result = EvalObject("[createObject(parameters('pw'), 1, parameters('pw'), 2)]");

        Assert.Equal(["***"], Keys(result));
        Assert.DoesNotContain(Secret, result.ToJsonString(), System.StringComparison.Ordinal);
    }
    [Fact]
    public void CreateObject_TheValueOfASecretKeyEntry_StaysTainted()
    {
        var result = EvalObject("[createObject('k', parameters('pw'))]");

        Assert.True(SecretJson.IsSecret(result["k"]));
    }

    // ---- toObject ----

    [Fact]
    public void ToObject_WithSecretKeys_MasksThemInFirstAppearanceOrder()
    {
        var result = EvalObject("[toObject(createArray(1, 2), lambda('x', if(equals(lambdaVariables('x'), 1), parameters('pw'), parameters('pw2'))))]");

        Assert.Equal(["***", "***#2"], Keys(result));
        string text = result.ToJsonString();
        Assert.DoesNotContain(Secret, text, System.StringComparison.Ordinal);
        Assert.DoesNotContain(OtherSecret, text, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ToObject_ASecretKeyEqualToAClearKey_CollidesAndIsReportedMasked()
    {
        var error = Expr.Fails(
            "[toObject(createArray(1, 2), lambda('x', if(equals(lambdaVariables('x'), 1), 'abc', parameters('pw'))))]",
            DiagnosticCodes.ExpressionEvaluationFailed,
            Context());

        Assert.Contains("duplicate key ***", error.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ToObject_SecretKeyWithAValueLambda_KeepsTheValuesTainted()
    {
        var result = EvalObject("[toObject(createArray(1), lambda('x', parameters('pw')), lambda('x', parameters('pw2')))]");

        Assert.Equal(["***"], Keys(result));
        Assert.True(SecretJson.IsSecret(result["***"]));
        Assert.DoesNotContain(OtherSecret, result.ToJsonString(), System.StringComparison.Ordinal);
    }

    // ---- groupBy ----

    [Fact]
    public void GroupBy_ClearThenSecretOccurrenceOfTheSameText_MasksTheKey_AndStillGroups()
    {
        var result = EvalObject("[groupBy(createArray(1, 2), lambda('x', if(equals(lambdaVariables('x'), 1), 'abc', parameters('pw'))))]");

        Assert.Equal(["***"], Keys(result));
        Assert.Equal(2, result["***"]!.AsArray().Count);
        Assert.DoesNotContain(Secret, result.ToJsonString(), System.StringComparison.Ordinal);
    }

    [Fact]
    public void GroupBy_SecretThenClearOccurrenceOfTheSameText_MasksTheKey_AndStillGroups()
    {
        var result = EvalObject("[groupBy(createArray(1, 2), lambda('x', if(equals(lambdaVariables('x'), 1), parameters('pw'), 'abc')))]");

        Assert.Equal(["***"], Keys(result));
        Assert.Equal(2, result["***"]!.AsArray().Count);
    }

    [Fact]
    public void GroupBy_TwoDistinctSecretKeys_AreMaskedSeparately()
    {
        var result = EvalObject("[groupBy(createArray(1, 2, 3), lambda('x', if(equals(mod(lambdaVariables('x'), 2), 1), parameters('pw'), parameters('pw2'))))]");

        Assert.Equal(["***", "***#2"], Keys(result));
        Assert.Equal(2, result["***"]!.AsArray().Count);
        Assert.Single(result["***#2"]!.AsArray());
    }

    // ---- json() of a secret ----

    [Fact]
    public void Json_OfASecretString_DoesNotExposeKeysFromTheSecretText()
    {
        // json('{"apiKey":...}') parses structure out of a secure string: the keys are part of the secret text.
        var result = Expr.Eval("[json(parameters('js'))]", Context());

        string text = result is null ? string.Empty : result.ToJsonString();
        Assert.DoesNotContain("apiKey", text, System.StringComparison.Ordinal);
        Assert.DoesNotContain("innerKey", text, System.StringComparison.Ordinal);
    }
}
