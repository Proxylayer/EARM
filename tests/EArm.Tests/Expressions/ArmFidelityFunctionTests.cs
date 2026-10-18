using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Secrets;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Values;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// ARM fidelity of the built-in functions, against the Microsoft Learn ARM template function reference: <c>union()</c> merges nested objects,
/// <c>contains()</c> on strings is case-sensitive, <c>managementGroupResourceId()</c> is rejected with a pointer, and the functions
/// <c>distinct shallowMerge tryGet indexFromEnd tryIndexFromEnd mapValues</c> behave as documented (budget and unknown propagation included).
/// </summary>
public sealed class ArmFidelityFunctionTests
{
    private const string Secret = "Sup3r-Secret!";

    private static EvaluationContext Context()
    {
        return Expr.Context(
            Expr.Parameters(("pw", SecretJson.CreateString(Secret))),
            resources: Expr.Index(("Microsoft.Entra/applications", "app")));
    }

    // ---- union: deep merge of objects (ARM docs, second example) ----

    [Fact]
    public void Union_OfObjects_MergesNestedObjectsRecursively_ButReplacesArrays()
    {
        const string expression = "[union(json('{\"property\":{\"one\":\"a\",\"two\":\"b\",\"three\":\"c1\"},\"nestedArray\":[1,2]}'), json('{\"property\":{\"three\":\"c2\",\"four\":\"d\",\"five\":\"e\"},\"nestedArray\":[3,4]}'))]";

        Expr.AssertJson("{\"property\":{\"one\":\"a\",\"two\":\"b\",\"three\":\"c2\",\"four\":\"d\",\"five\":\"e\"},\"nestedArray\":[3,4]}", Expr.Eval(expression));
    }

    [Theory]
    [InlineData("[union(createObject('a', createObject('x', 1, 'y', createObject('p', 1))), createObject('a', createObject('y', createObject('q', 2))))]", "{\"a\":{\"x\":1,\"y\":{\"p\":1,\"q\":2}}}")]
    [InlineData("[union(createObject('a', createObject('x', 1)), createObject('a', 'flat'))]", "{\"a\":\"flat\"}")]
    [InlineData("[union(createObject('a', 'flat'), createObject('a', createObject('x', 1)))]", "{\"a\":{\"x\":1}}")]
    [InlineData("[union(createObject('a', createObject('x', 1)), createObject('a', createObject('y', 2)), createObject('a', createObject('z', 3)))]", "{\"a\":{\"x\":1,\"y\":2,\"z\":3}}")]
    [InlineData("[union(createArray(createArray('one', 'two'), createArray('three')), createArray(createArray('three'), createArray('four', 'two')))]", "[[\"one\",\"two\"],[\"three\"],[\"four\",\"two\"]]")]
    public void Union_FollowsTheArmMergeRules(string expression, string expectedJson)
    {
        Expr.AssertJson(expectedJson, Expr.Eval(expression));
    }

    [Fact]
    public void Union_DoesNotModifyItsArguments()
    {
        var variables = new System.Collections.Generic.Dictionary<string, JsonNode?>
        {
            ["a"] = JsonNode.Parse("{\"n\":{\"x\":1}}"),
            ["b"] = JsonNode.Parse("{\"n\":{\"y\":2}}"),
        };
        var context = Expr.Context(variables: variables);

        Expr.AssertJson("{\"n\":{\"x\":1,\"y\":2}}", Expr.Eval("[union(variables('a'), variables('b'))]", context));
        Expr.AssertJson("{\"n\":{\"x\":1}}", Expr.Eval("[variables('a')]", context));
    }

    // ---- contains: case-sensitive on strings ----

    [Theory]
    [InlineData("[contains('OneTwoThree', 'Two')]", "true")]
    [InlineData("[contains('OneTwoThree', 'two')]", "false")]
    [InlineData("[contains('abc', '')]", "true")]
    [InlineData("[contains(createObject('Key', 1), 'KEY')]", "true")]
    [InlineData("[contains(createArray('a'), 'A')]", "false")]
    [InlineData("[indexOf('OneTwoThree', 'two')]", "3")]
    [InlineData("[lastIndexOf('OneTwoThree', 'E')]", "10")]
    [InlineData("[endsWith('OneTwoThree', 'THREE')]", "true")]
    public void Contains_IsCaseSensitiveOnStrings_TheOtherSearchesAreNot(string expression, string expectedJson)
    {
        Expr.AssertJson(expectedJson, Expr.Eval(expression));
    }

    // ---- managementGroupResourceId ----

    [Fact]
    public void ManagementGroupResourceId_IsRejected_WithAPointerToResourceId()
    {
        var error = Expr.Fails("[managementGroupResourceId('Microsoft.Authorization/policyDefinitions', 'p')]", DiagnosticCodes.UnsupportedArmFunction);

        Assert.Contains("resourceId(", error.Message, System.StringComparison.Ordinal);
    }

    // ---- new functions ----

    [Theory]
    [InlineData("[distinct(createArray(1, 2, 2, 3, 1))]", "[1,2,3]")]
    [InlineData("[distinct(createArray('apple', 'banana', 'apple', 'cherry'))]", "[\"apple\",\"banana\",\"cherry\"]")]
    [InlineData("[distinct(createArray(createObject('n', 1), createObject('n', 2), createObject('n', 1)))]", "[{\"n\":1},{\"n\":2}]")]
    [InlineData("[distinct(createArray('a', 'A'))]", "[\"a\",\"A\"]")]
    [InlineData("[distinct(createArray())]", "[]")]
    [InlineData("[shallowMerge(createArray(createObject('one', 'a'), createObject('two', 'b'), createObject('two', 'c')))]", "{\"one\":\"a\",\"two\":\"c\"}")]
    [InlineData("[shallowMerge(json('[{\"one\":\"a\",\"nested\":{\"a\":1,\"nested\":{\"c\":3}}},{\"two\":\"b\",\"nested\":{\"b\":2}}]'))]", "{\"one\":\"a\",\"nested\":{\"b\":2},\"two\":\"b\"}")]
    [InlineData("[shallowMerge(createArray())]", "{}")]
    [InlineData("[tryGet(createObject('name', 'John', 'age', 30), 'region')]", "null")]
    [InlineData("[tryGet(createObject('name', 'John', 'age', 30), 'name')]", "\"John\"")]
    [InlineData("[tryGet(createObject('name', 'John'), 'NAME')]", "\"John\"")]
    [InlineData("[tryGet(createArray('red', 'green'), 0)]", "\"red\"")]
    [InlineData("[tryGet(createArray('red', 'green'), 2)]", "null")]
    [InlineData("[tryGet(createArray('red', 'green'), -1)]", "null")]
    [InlineData("[tryGet(null(), 'x')]", "null")]
    [InlineData("[coalesce(tryGet(createObject(), 'x'), 'fallback')]", "\"fallback\"")]
    [InlineData("[indexFromEnd(createArray('apple', 'banana', 'orange', 'grape'), 2)]", "\"orange\"")]
    [InlineData("[indexFromEnd(createArray('apple', 'banana'), 1)]", "\"banana\"")]
    [InlineData("[indexFromEnd(createArray('apple', 'banana'), 2)]", "\"apple\"")]
    [InlineData("[tryIndexFromEnd(createArray('apple', 'banana', 'orange', 'grape'), 2)]", "\"orange\"")]
    [InlineData("[tryIndexFromEnd(createArray('apple', 'banana', 'orange', 'grape'), 5)]", "null")]
    [InlineData("[tryIndexFromEnd(createArray('apple'), 0)]", "null")]
    [InlineData("[mapValues(createObject('foo', 'foo', 'bar', 'bar'), lambda('val', toUpper(lambdaVariables('val'))))]", "{\"foo\":\"FOO\",\"bar\":\"BAR\"}")]
    [InlineData("[mapValues(createObject(), lambda('v', 1))]", "{}")]
    [InlineData("[mapValues(createObject('a', 1, 'b', 2), lambda('v', add(lambdaVariables('v'), 10)))]", "{\"a\":11,\"b\":12}")]
    public void NewFunctions_FollowTheArmDocumentation(string expression, string expectedJson)
    {
        Expr.AssertJson(expectedJson, Expr.Eval(expression));
    }

    [Theory]
    [InlineData("[distinct('abc')]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[shallowMerge(createArray(1))]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[shallowMerge(createObject())]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[tryGet('abc', 0)]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[tryGet(createArray(1), 'a')]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[tryGet(createObject(), 1)]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[indexFromEnd(createArray('a'), 2)]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[indexFromEnd(createArray('a'), 0)]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[indexFromEnd(createObject(), 1)]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[tryIndexFromEnd(createArray('a'), 'x')]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[mapValues(createArray(1), lambda('v', 1))]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[mapValues(createObject('a', 1), 1)]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[mapValues(createObject('a', 1), lambda('v', 'i', 1))]", DiagnosticCodes.InvalidFunctionArguments)]
    public void NewFunctions_RejectInvalidArguments(string expression, string code)
    {
        Expr.Fails(expression, code);
    }

    [Theory]
    [InlineData("[distinct(createArray(reference('app').appId, 'x'))]")]
    [InlineData("[shallowMerge(reference('app').list)]")]
    [InlineData("[shallowMerge(createArray(createObject('a', 1), reference('app').obj))]")]
    [InlineData("[tryGet(reference('app'), 'appId')]")]
    [InlineData("[tryGet(createObject('a', 1), reference('app').key)]")]
    [InlineData("[indexFromEnd(reference('app').list, 1)]")]
    [InlineData("[tryIndexFromEnd(createArray(1), reference('app').n)]")]
    [InlineData("[mapValues(reference('app').obj, lambda('v', lambdaVariables('v')))]")]
    public void NewFunctions_PropagateUnknownInputs(string expression)
    {
        var unknown = Expr.AssertUnknown(Expr.Eval(expression, Context()));

        Assert.Equal(UnknownOrigins.Reference, unknown.Origins);
    }

    [Fact]
    public void KnownContainers_PassAnUnknownValueTheyHoldThrough()
    {
        Expr.AssertUnknown(Expr.Eval("[tryGet(createObject('a', reference('app').appId), 'a')]", Context()));
        Expr.AssertUnknown(Expr.Eval("[indexFromEnd(createArray(reference('app').appId, 'x'), 2)]", Context()));
        Expr.AssertJson("\"x\"", Expr.Eval("[tryIndexFromEnd(createArray(reference('app').appId, 'x'), 1)]", Context()));

        var merged = Assert.IsType<JsonObject>(Expr.Eval("[shallowMerge(createArray(createObject('a', reference('app').appId), createObject('b', 1)))]", Context()));
        Expr.AssertUnknown(merged["a"]);
        Expr.AssertJson("1", merged["b"]);

        var mapped = Assert.IsType<JsonObject>(Expr.Eval("[mapValues(createObject('a', reference('app').appId, 'b', 'k'), lambda('v', concat('p-', lambdaVariables('v'))))]", Context()));
        Expr.AssertUnknown(mapped["a"]);
        Expr.AssertJson("\"p-k\"", mapped["b"]);
    }

    [Fact]
    public void NewFunctions_KeepSecureValuesSecure()
    {
        Assert.True(UnknownJson.ContainsTaint(Expr.Eval("[tryGet(createObject('k', parameters('pw')), 'k')]", Context())));
        Assert.True(UnknownJson.ContainsTaint(Expr.Eval("[distinct(createArray(parameters('pw'), parameters('pw')))]", Context())));
        Assert.True(UnknownJson.ContainsTaint(Expr.Eval("[mapValues(createObject('k', parameters('pw')), lambda('v', lambdaVariables('v')))]", Context())));
        Assert.DoesNotContain(Secret, Expr.Fails("[indexFromEnd(createArray(parameters('pw')), 3)]", DiagnosticCodes.ExpressionEvaluationFailed, Context()).Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void NewFunctions_AreRegisteredAsSupported()
    {
        var supported = EArm.Expressions.Functions.FunctionRegistry.Default.NamesOfKind(EArm.Expressions.Functions.FunctionKind.Supported);

        foreach (string name in new[] { "distinct", "shallowMerge", "tryGet", "indexFromEnd", "tryIndexFromEnd", "mapValues" })
        {
            Assert.Contains(name, supported);
        }
    }

    [Fact]
    public void LambdaOutsideATakingFunction_NamesMapValuesAmongTheTakers()
    {
        Assert.Contains("mapValues", Expr.Fails("[lambda('x', 1)]", DiagnosticCodes.InvalidFunctionArguments).Message, System.StringComparison.Ordinal);
    }
}
