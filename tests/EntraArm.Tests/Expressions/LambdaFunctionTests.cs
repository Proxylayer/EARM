using System.Linq;
using System.Text.Json.Nodes;
using EntraArm.Core.Diagnostics;
using EntraArm.Core.Json;
using EntraArm.Core.Secrets;
using EntraArm.Expressions.Evaluation;
using EntraArm.Expressions.Validation;
using EntraArm.Expressions.Values;
using Xunit;

namespace EntraArm.Tests;

/// <summary>Group 5 (WI-16): <c>map filter reduce sort toObject groupBy lambdaVariables</c>, their error codes, and unknown / secure propagation.</summary>
public sealed class LambdaFunctionTests
{
    private const string Secret = "abc";

    private static EvaluationContext Context()
    {
        return Expr.Context(
            Expr.Parameters(("pw", SecretJson.CreateString(Secret)), ("n", 3)),
            resources: Expr.Index(("Microsoft.Entra/applications", "app")));
    }

    private static ExpressionAnalysis Analyze(string body)
    {
        return new ExpressionValidator().Analyze(Expr.ParseBody(body));
    }

    // ---- happy paths (table-driven) ----

    [Theory]
    [InlineData("[map(createArray(1, 2, 3), lambda('x', mul(lambdaVariables('x'), 2)))]", "[2,4,6]")]
    [InlineData("[map(createArray('a', 'b'), lambda('x', 'i', concat(lambdaVariables('x'), string(lambdaVariables('i')))))]", "[\"a0\",\"b1\"]")]
    [InlineData("[map(createArray(), lambda('x', lambdaVariables('x')))]", "[]")]
    [InlineData("[map(createArray(createObject('k', 1)), lambda('o', lambdaVariables('o').k))]", "[1]")]
    [InlineData("[filter(createArray(1, 2, 3, 4), lambda('x', greater(lambdaVariables('x'), 2)))]", "[3,4]")]
    [InlineData("[filter(createArray('a', 'b', 'c'), lambda('x', 'i', equals(mod(lambdaVariables('i'), 2), 0)))]", "[\"a\",\"c\"]")]
    [InlineData("[filter(createArray(1, 2), lambda('x', false()))]", "[]")]
    [InlineData("[reduce(createArray(1, 2, 3), 0, lambda('cur', 'next', add(lambdaVariables('cur'), lambdaVariables('next'))))]", "6")]
    [InlineData("[reduce(createArray(10, 20), 0, lambda('cur', 'next', 'i', add(lambdaVariables('cur'), mul(lambdaVariables('next'), lambdaVariables('i')))))]", "20")]
    [InlineData("[reduce(createArray(), 'seed', lambda('cur', 'next', 'x'))]", "\"seed\"")]
    [InlineData("[reduce(createArray('a', 'b'), '', lambda('cur', 'next', concat(lambdaVariables('cur'), lambdaVariables('next'))))]", "\"ab\"")]
    [InlineData("[sort(createArray(3, 1, 2), lambda('a', 'b', less(lambdaVariables('a'), lambdaVariables('b'))))]", "[1,2,3]")]
    [InlineData("[sort(createArray('b', 'a', 'c'), lambda('a', 'b', less(lambdaVariables('b'), lambdaVariables('a'))))]", "[\"c\",\"b\",\"a\"]")]
    [InlineData("[sort(createArray(), lambda('a', 'b', true()))]", "[]")]
    [InlineData("[toObject(createArray('a', 'b'), lambda('x', lambdaVariables('x')))]", "{\"a\":\"a\",\"b\":\"b\"}")]
    [InlineData("[toObject(createArray(1, 2), lambda('x', concat('k', string(lambdaVariables('x')))), lambda('x', mul(lambdaVariables('x'), 10)))]", "{\"k1\":10,\"k2\":20}")]
    [InlineData("[groupBy(createArray('ab', 'ac', 'b'), lambda('x', substring(lambdaVariables('x'), 0, 1)))]", "{\"a\":[\"ab\",\"ac\"],\"b\":[\"b\"]}")]
    [InlineData("[map(createArray(1), lambda('x', map(createArray(2), lambda('x', add(lambdaVariables('x'), 10)))))]", "[[12]]")]
    [InlineData("[map(createArray(1), lambda('x', map(createArray(2), lambda('y', add(lambdaVariables('x'), lambdaVariables('y'))))))]", "[[3]]")]
    [InlineData("[map(createArray(1), lambda('X', lambdaVariables('x')))]", "[1]")]
    [InlineData("[length(map(range(0, parameters('n')), lambda('i', lambdaVariables('i'))))]", "3")]
    public void LambdaFunctions_EvaluateConcretely(string expression, string expectedJson)
    {
        Expr.AssertJson(expectedJson, Expr.Eval(expression, Context()));
    }

    [Fact]
    public void Sort_IsStable()
    {
        var sorted = Assert.IsType<JsonArray>(Expr.Eval(
            "[sort(createArray(createObject('k', 1, 'v', 'a'), createObject('k', 0, 'v', 'b'), createObject('k', 1, 'v', 'c'), createObject('k', 0, 'v', 'd')), lambda('l', 'r', less(lambdaVariables('l').k, lambdaVariables('r').k)))]",
            Context()));

        Assert.Equal(["b", "d", "a", "c"], sorted.Select(o => o!["v"]!.GetValue<string>()).ToList());
    }

    [Fact]
    public void LambdaResults_AreFreshNodes_AndTheSourceIsUntouched()
    {
        var context = Expr.Context(Expr.Parameters(("arr", new JsonArray(1, 2))));

        var mapped = Assert.IsType<JsonArray>(Expr.Eval("[map(parameters('arr'), lambda('x', add(lambdaVariables('x'), 1)))]", context));
        var filtered = Assert.IsType<JsonArray>(Expr.Eval("[filter(parameters('arr'), lambda('x', equals(lambdaVariables('x'), 2)))]", context));

        Expr.AssertJson("[2,3]", mapped);
        Expr.AssertJson("[2]", filtered);
        Expr.AssertJson("[1,2]", context.Parameters["arr"]);
        Assert.Null(mapped.Parent);
    }

    // ---- error codes (table-driven) ----

    [Theory]
    [InlineData("[lambda('x', 1)]", DiagnosticCodes.InvalidFunctionArguments, "can only be passed to a function that takes a lambda")]
    [InlineData("[concat('a', lambda('x', 1))]", DiagnosticCodes.InvalidFunctionArguments, "can only be passed to a function that takes a lambda")]
    [InlineData("[map(createArray(1), 2)]", DiagnosticCodes.InvalidFunctionArguments, "argument 2 must be a lambda")]
    [InlineData("[map(createArray(1), 'lambda')]", DiagnosticCodes.InvalidFunctionArguments, "argument 2 must be a lambda")]
    [InlineData("[map(createArray(1), lambda('a', 'b', 'c', 1))]", DiagnosticCodes.InvalidFunctionArguments, "takes between 1 and 2 parameter(s) (element[, index])")]
    [InlineData("[sort(createArray(1), lambda('a', true()))]", DiagnosticCodes.InvalidFunctionArguments, "takes exactly 2 parameter(s) (left, right)")]
    [InlineData("[reduce(createArray(1), 0, lambda('a', 1))]", DiagnosticCodes.InvalidFunctionArguments, "takes between 2 and 3 parameter(s) (current, next[, index])")]
    [InlineData("[toObject(createArray(1), lambda('a', 'b', 'k'))]", DiagnosticCodes.InvalidFunctionArguments, "takes exactly 1 parameter(s) (element)")]
    [InlineData("[map(createArray(1), lambda('x', 'X', 1))]", DiagnosticCodes.InvalidFunctionArguments, "declares the same parameter name twice")]
    [InlineData("[map(createArray(1), lambda('x', lambdaVariables('y')))]", DiagnosticCodes.InvalidFunctionArguments, "'y' is not a parameter of an enclosing lambda (in scope: x)")]
    [InlineData("[lambdaVariables('x')]", DiagnosticCodes.FunctionNotAllowedHere, "can only be used inside the body of a lambda")]
    [InlineData("[filter(createArray(1), lambda('x', 'yes'))]", DiagnosticCodes.InvalidFunctionArguments, "must return a boolean for every element")]
    [InlineData("[sort(createArray(1, 2), lambda('a', 'b', 'x'))]", DiagnosticCodes.InvalidFunctionArguments, "must return a boolean (true when the first element sorts before the second)")]
    [InlineData("[toObject(createArray(1), lambda('x', lambdaVariables('x')))]", DiagnosticCodes.InvalidFunctionArguments, "key lambda must return a string")]
    [InlineData("[groupBy(createArray(true()), lambda('x', lambdaVariables('x')))]", DiagnosticCodes.InvalidFunctionArguments, "key lambda must return a string")]
    [InlineData("[toObject(createArray('a', 'a'), lambda('x', lambdaVariables('x')))]", DiagnosticCodes.ExpressionEvaluationFailed, "duplicate key 'a' (element 1)")]
    [InlineData("[map('abc', lambda('x', 1))]", DiagnosticCodes.InvalidFunctionArguments, "argument 1 must be an array")]
    [InlineData("[reduce(createObject(), 0, lambda('a', 'b', 1))]", DiagnosticCodes.InvalidFunctionArguments, "argument 1 must be an array")]
    [InlineData("[map(createArray(1))]", DiagnosticCodes.InvalidFunctionArguments, "")]
    [InlineData("[filter(createArray(1), lambda('x', true()), 1)]", DiagnosticCodes.InvalidFunctionArguments, "")]
    [InlineData("[lambdaVariables(1)]", DiagnosticCodes.InvalidFunctionArguments, "argument 1 must be a string")]
    [InlineData("[map(createArray(1), lambda('x', div(1, 0)))]", DiagnosticCodes.ExpressionEvaluationFailed, "")]
    [InlineData("[map(nope(), lambda('x', 1))]", DiagnosticCodes.UnknownFunction, "")]
    public void LambdaFunctions_ReportTheDocumentedErrors(string expression, string code, string messageFragment)
    {
        var error = Expr.Fails(expression, code, Context());

        if (messageFragment.Length > 0)
        {
            Assert.Contains(messageFragment, error.Message);
        }
    }

    // ---- unknown propagation ----

    [Fact]
    public void UnknownArray_YieldsAnUnknownResult_WithTheCallAsSymbol()
    {
        var unknown = Expr.AssertUnknown(Expr.Eval("[map(reference('app').arr, lambda('x', lambdaVariables('x')))]", Context()));

        Assert.Equal("map(" + Expr.AppRef + ".arr, lambda('x', lambdaVariables('x')))", unknown.Symbol);
        Assert.Equal(UnknownOrigins.Reference, unknown.Origins);
        Assert.False(unknown.Tainted);

        var reduced = Expr.AssertUnknown(Expr.Eval("[reduce(reference('app').arr, 0, lambda('a', 'b', 1))]", Context()));
        Assert.Equal("reduce(" + Expr.AppRef + ".arr, 0, lambda('a', 'b', 1))", reduced.Symbol);
    }

    [Fact]
    public void UnknownElements_FlowThroughTheBody()
    {
        var mapped = Assert.IsType<JsonArray>(Expr.Eval("[map(createArray(reference('app').x, 'k'), lambda('x', concat('p', lambdaVariables('x'))))]", Context()));

        Assert.Equal(2, mapped.Count);
        Assert.Equal("concat('p', " + Expr.AppRef + ".x)", Expr.AssertUnknown(mapped[0]).Symbol);
        Assert.Equal("pk", Expr.AssertString(mapped[1]));
    }

    [Theory]
    [InlineData("[filter(createArray(1, 2), lambda('x', equals(lambdaVariables('x'), reference('app').n)))]", "filter(")]
    [InlineData("[sort(createArray(1, 2), lambda('a', 'b', less(lambdaVariables('a'), reference('app').n)))]", "sort(")]
    [InlineData("[toObject(createArray(1), lambda('x', reference('app').key))]", "toObject(")]
    [InlineData("[groupBy(createArray(1), lambda('x', reference('app').key))]", "groupBy(")]
    public void UnknownVerdictOrKey_MakesTheWholeResultUnknown(string expression, string symbolPrefix)
    {
        var unknown = Expr.AssertUnknown(Expr.Eval(expression, Context()));

        Assert.StartsWith(symbolPrefix, unknown.Symbol, System.StringComparison.Ordinal);
        Assert.Contains("lambda(", unknown.Symbol);
        Assert.Equal(UnknownOrigins.Reference, unknown.Origins);
    }

    // ---- secure values ----

    [Fact]
    public void SecretElements_StaySecret_AndSecretVerdictsTaintTheResult()
    {
        var mapped = Assert.IsType<JsonArray>(Expr.Eval("[map(createArray(parameters('pw')), lambda('x', toUpper(lambdaVariables('x'))))]", Context()));
        Assert.True(SecretJson.IsSecret(mapped[0], out var upper));
        Assert.Equal("ABC", upper.Value);

        // length('abc') = 3: the verdict is computed from a secret, so the kept element is tainted too.
        var filtered = Assert.IsType<JsonArray>(Expr.Eval("[filter(createArray(3, 4), lambda('x', equals(lambdaVariables('x'), length(parameters('pw')))))]", Context()));
        var kept = Assert.Single(filtered);
        Assert.True(SecretJson.IsSecret(kept, out var secretKept));
        Assert.Equal("3", secretKept.Value);

        var grouped = Assert.IsType<JsonObject>(Expr.Eval("[groupBy(createArray(1), lambda('x', parameters('pw')))]", Context()));
        Assert.True(SecretJson.ContainsSecret(grouped));
        Assert.True(SecretJson.ContainsSecret(Expr.Eval("[toObject(createArray(1), lambda('x', parameters('pw')), lambda('x', lambdaVariables('x')))]", Context())));
    }

    [Fact]
    public void SecretKeys_AreNotRenderedInClearText()
    {
        // Plan 1.5: a secure value renders as *** everywhere except Graph bodies. A key computed from a secret is
        // part of the rendered object, so it must not surface the value either.
        var grouped = Assert.IsType<JsonObject>(Expr.Eval("[groupBy(createArray(1), lambda('x', parameters('pw')))]", Context()));
        var keyed = Assert.IsType<JsonObject>(Expr.Eval("[toObject(createArray(1), lambda('x', parameters('pw')))]", Context()));

        Assert.DoesNotContain(Secret, grouped.ToJsonString(), System.StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, keyed.ToJsonString(), System.StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateSecretKeys_AreReportedWithoutTheValue()
    {
        var error = Expr.Fails("[toObject(createArray(parameters('pw'), parameters('pw')), lambda('x', lambdaVariables('x')))]", DiagnosticCodes.ExpressionEvaluationFailed, Context());

        Assert.Contains("duplicate key ***", error.Message);
        Assert.DoesNotContain(Secret, error.Message);
    }

    // ---- validation (offline) ----

    [Fact]
    public void Validator_ReportsLambdaMisuse_WithoutEvaluating()
    {
        var analysis = Analyze("\"functions\": [ { \"namespace\": \"c\", \"members\": { \"f\": { \"parameters\": [ { \"name\": \"s\", \"type\": \"string\" } ], \"output\": { \"type\": \"string\", \"value\": \"[parameters('s')]\" } } } } ], "
            + "\"resources\": [ { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"g\", \"properties\": { "
            + "\"a\": \"[lambda('x', 1)]\", "
            + "\"b\": \"[c.f(lambda('x', 1))]\", "
            + "\"c\": \"[map(createArray(1), 1)]\", "
            + "\"d\": \"[map(createArray(1), lambda('a', 'b', 'c', 1))]\", "
            + "\"e\": \"[map(createArray(1), lambda('x', lambdaVariables('y')))]\", "
            + "\"f\": \"[lambdaVariables('x')]\", "
            + "\"g\": \"[map(createArray(1), lambda('x', lambdaVariables('x')))]\", "
            + "\"h\": \"[map(createArray(1), lambda('x', reference(lambdaVariables('x'))))]\" } } ]");

        var codes = analysis.Diagnostics.ToDictionary(d => d.Path!.ToString(), d => (d.Code, d.Message));
        Assert.Equal(DiagnosticCodes.InvalidFunctionArguments, codes["resources[0].properties.a"].Code);
        Assert.Equal(DiagnosticCodes.InvalidFunctionArguments, codes["resources[0].properties.b"].Code);
        Assert.Contains("takes values, not lambdas", codes["resources[0].properties.b"].Message);
        Assert.Equal(DiagnosticCodes.InvalidFunctionArguments, codes["resources[0].properties.c"].Code);
        Assert.Contains("argument 2 must be a lambda", codes["resources[0].properties.c"].Message);
        Assert.Equal(DiagnosticCodes.InvalidFunctionArguments, codes["resources[0].properties.d"].Code);
        Assert.Equal(DiagnosticCodes.InvalidFunctionArguments, codes["resources[0].properties.e"].Code);
        Assert.Contains("'y' is not a parameter of an enclosing lambda", codes["resources[0].properties.e"].Message);
        Assert.Equal(DiagnosticCodes.FunctionNotAllowedHere, codes["resources[0].properties.f"].Code);
        Assert.False(codes.ContainsKey("resources[0].properties.g"));
        Assert.Equal(DiagnosticCodes.ReferenceNotStaticallyResolvable, codes["resources[0].properties.h"].Code);
        Assert.Contains("lambdaVariables('x')", codes["resources[0].properties.h"].Message);
        Assert.All(analysis.Diagnostics, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        Assert.Equal(7, analysis.Diagnostics.Count);
    }

    [Fact]
    public void Validator_AcceptsLambdasOverTemplateValues()
    {
        var analysis = Analyze("\"parameters\": { \"cidrs\": { \"type\": \"array\", \"defaultValue\": [\"10.0.0.0/8\", \"192.168.0.0/16\"] } }, "
            + "\"variables\": { \"ranges\": \"[map(parameters('cidrs'), lambda('c', createObject('cidrAddress', lambdaVariables('c'))))]\" }, "
            + "\"resources\": [ { \"type\": \"Microsoft.Entra/conditionalAccess/namedLocations\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"Offices\", "
            + "\"properties\": { \"@odata.type\": \"#microsoft.graph.ipNamedLocation\", \"isTrusted\": true, \"ipRanges\": \"[variables('ranges')]\" } } ]");

        Assert.Empty(analysis.Diagnostics);
        var properties = Assert.IsType<JsonObject>(Expr.Evaluator.EvaluateTree(analysis.Static.Resources[0].Declaration.Properties, analysis.Static.Context));
        Expr.AssertJson("[{\"cidrAddress\":\"10.0.0.0/8\"},{\"cidrAddress\":\"192.168.0.0/16\"}]", properties["ipRanges"]);
        Assert.DoesNotContain(analysis.Diagnostics, d => d.Path == JsonPath.Parse("resources[0].properties.ipRanges"));
    }
}
