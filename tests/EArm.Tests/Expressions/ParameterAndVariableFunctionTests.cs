using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Secrets;
using EArm.Expressions;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Functions;
using EArm.Expressions.Values;
using Xunit;

namespace EArm.Tests;

public sealed class ParameterAndVariableFunctionTests
{
    // ---- parameters ----

    [Fact]
    public void Parameters_ReturnsACopyAndIsCaseInsensitive()
    {
        var parameters = Expr.Parameters(("obj", new JsonObject { ["a"] = 1 }), ("s", "text"));
        var context = Expr.Context(parameters);

        var copy = Assert.IsType<JsonObject>(Expr.Eval("[parameters('obj')]", context));
        copy["a"] = 2;

        Expr.AssertJson("1", Expr.Eval("[parameters('OBJ').a]", context));
        Assert.Equal("text", Expr.AssertString(Expr.Eval("[parameters('s')]", context)));
        Expr.AssertJson("1", parameters["obj"]!["a"]);
    }

    [Fact]
    public void Parameters_RejectsUndefinedNamesAndNonStringArguments()
    {
        var context = Expr.Context(Expr.Parameters(("s", "text")));

        Assert.Equal(1, Expr.Fails("[parameters('missing')]", DiagnosticCodes.UndefinedParameter, context).Offset);
        Expr.Fails("[parameters(1)]", DiagnosticCodes.InvalidFunctionArguments, context);
        Expr.Fails("[parameters()]", DiagnosticCodes.InvalidFunctionArguments, context);
    }

    [Fact]
    public void Parameters_InsideAUserFunction_AreTheFunctionsOwnParameters()
    {
        // Inside a user-defined function parameters() names only the function's own parameters; a name that
        // is not one of them (here: none are bound) is UndefinedParameter, not a placeholder.
        var error = Expr.Fails("[parameters('x')]", DiagnosticCodes.UndefinedParameter, Expr.Context(scope: EvaluationScope.UserFunction));

        Assert.Contains("'x' is not a parameter of user-defined function", error.Message);
        Assert.Contains("the function declares no parameters", error.Message);
        Assert.Equal(1, error.Offset);
    }

    // ---- variables ----

    [Fact]
    public void Variables_EvaluateLazilyAndTransitively()
    {
        var variables = new Dictionary<string, JsonNode?>
        {
            ["a"] = "[concat('x', 'y')]",
            ["b"] = "[variables('a')]",
            ["obj"] = new JsonObject { ["k"] = "[variables('A')]", ["lit"] = "[[esc" },
            ["n"] = 5,
        };
        var context = Expr.Context(variables: variables);

        Assert.Equal("xy", Expr.AssertString(Expr.Eval("[variables('b')]", context)));
        Assert.Equal("xy", Expr.AssertString(Expr.Eval("[variables('obj').k]", context)));
        Assert.Equal("[esc", Expr.AssertString(Expr.Eval("[variables('obj').lit]", context)));
        Expr.AssertJson("5", Expr.Eval("[variables('N')]", context));
        Assert.True(context.Variables.IsBound);
        Assert.Equal(4, context.Variables.Count);
        Assert.True(context.Variables.Contains("OBJ"));
        Assert.True(context.Variables.TryGetDefinition("a", out var definition));
        Assert.Equal("[concat('x', 'y')]", definition!.GetValue<string>());
    }

    [Fact]
    public void Variables_AreMemoisedOnceAndHandedOutAsCopies()
    {
        int calls = 0;
        var registry = FunctionRegistry.CreateDefault();
        registry.Register(new FunctionDescriptor { Name = "countCalls", Group = "test", MinArgs = 0, MaxArgs = 0, Implementation = _ => JsonValue.Create(++calls) });
        var variables = new Dictionary<string, JsonNode?> { ["v"] = new JsonObject { ["n"] = "[countCalls()]" } };
        var context = Expr.Context(variables: variables, functions: registry);

        Assert.False(context.Variables.TryGetEvaluated("v", out _));
        var first = Assert.IsType<JsonObject>(Expr.Eval("[variables('v')]", context));
        first["n"] = 99;
        var second = Assert.IsType<JsonObject>(Expr.Eval("[variables('v')]", context));
        Expr.AssertJson("2", Expr.Eval("[add(variables('v').n, variables('v').n)]", context));

        Assert.Equal(1, calls);
        Expr.AssertJson("1", second["n"]);
        Assert.True(context.Variables.TryGetEvaluated("v", out var evaluated));
        Expr.AssertJson("1", evaluated!["n"]);
    }

    [Fact]
    public void Variables_DetectCyclesAndUndefinedNames()
    {
        var context = Expr.Context(variables: new Dictionary<string, JsonNode?> { ["a"] = "[variables('b')]", ["b"] = "[variables('a')]", ["self"] = "[concat(variables('self'), 'x')]" });

        Assert.Equal(1, Expr.Fails("[variables('a')]", DiagnosticCodes.VariableCycle, context).Offset);
        Expr.Fails("[variables('self')]", DiagnosticCodes.VariableCycle, context);
        Expr.Fails("[variables('nope')]", DiagnosticCodes.UndefinedVariable, context);
        Assert.Throws<ExpressionEvaluationException>(() => context.Variables.Get("nope"));
    }

    [Theory]
    [InlineData(EvaluationScope.ParameterDefault)]
    [InlineData(EvaluationScope.UserFunction)]
    public void Variables_AreNotAllowedInDefaultsOrUserFunctions(EvaluationScope scope)
    {
        var context = Expr.Context(variables: new Dictionary<string, JsonNode?> { ["a"] = 1 }, scope: scope);

        Expr.Fails("[variables('a')]", DiagnosticCodes.FunctionNotAllowedHere, context);
    }

    [Fact]
    public void Variables_MustBeBoundBeforeEvaluation()
    {
        var variables = new LazyVariables(new Dictionary<string, JsonNode?> { ["a"] = 1 });

        Assert.False(variables.IsBound);
        Assert.Throws<InvalidOperationException>(() => variables.Get("a"));
    }

    // ---- LazyParameters over a loaded document ----

    private static (LazyParameters Parameters, EvaluationContext Context) LoadLazyParameters()
    {
        var document = Expr.ParseBody(
            """
            "parameters": {
              "withDefault":   { "type": "string", "defaultValue": "[concat('d', 'v')]" },
              "year":          { "type": "string", "defaultValue": "[utcNow('yyyy')]" },
              "a":             { "type": "string", "defaultValue": "[parameters('b')]" },
              "b":             { "type": "string", "defaultValue": "[parameters('a')]" },
              "pw":            { "type": "securestring" },
              "missing":       { "type": "string" },
              "missingSecure": { "type": "securestring" },
              "supplied":      { "type": "string" },
              "secureDefault": { "type": "securestring", "defaultValue": "[concat('a', 'b')]" },
              "obj":           { "type": "object", "defaultValue": { "k": "[concat('v', '1')]", "esc": "[[x" } },
              "chained":       { "type": "string", "defaultValue": "[concat(parameters('withDefault'), '!')]" }
            },
            "resources": []
            """,
            ParameterSet
                .Parse("""{ "parameters": { "pw": { "reference": { "keyVault": { "id": "kv" }, "secretName": "s" } } } }""", "p.json")
                .WithInline("supplied", "[[x"));

        var parameters = new LazyParameters(document.ParameterValues);
        var context = new EvaluationContext { Parameters = parameters }.Bind(Expr.Evaluator);
        return (parameters, context);
    }

    [Fact]
    public void LazyParameters_EvaluateDefaultsInParameterDefaultScope()
    {
        var (parameters, context) = LoadLazyParameters();

        Assert.Equal("dv", Expr.AssertString(parameters.Get("withDefault")));
        Assert.Equal("dv!", Expr.AssertString(Expr.Eval("[parameters('chained')]", context)));
        Assert.Matches("^[0-9]{4}$", Expr.AssertString(parameters.Get("YEAR")));
        Assert.Equal("v1", Expr.AssertString(parameters.Get("obj")!["k"]));
        Assert.Equal("[x", Expr.AssertString(parameters.Get("obj")!["esc"]));
    }

    [Fact]
    public void LazyParameters_DetectDefaultCycles()
    {
        var (parameters, context) = LoadLazyParameters();

        Assert.Equal(DiagnosticCodes.ParameterDefaultCycle, Assert.Throws<ExpressionEvaluationException>(() => parameters.Get("a")).Code);
        Expr.Fails("[parameters('b')]", DiagnosticCodes.ParameterDefaultCycle, context);
    }

    [Fact]
    public void LazyParameters_PendingKeyVaultReference_IsATaintedUnknown()
    {
        var (parameters, _) = LoadLazyParameters();

        var unknown = Expr.AssertUnknown(parameters.Get("pw"));
        Assert.Equal("parameters('pw')", unknown.Symbol);
        Assert.Equal(UnknownOrigins.Parameter, unknown.Origins);
        Assert.True(unknown.Tainted);
        Assert.Equal(["pw"], parameters.PendingReferenceNames);
    }

    [Fact]
    public void LazyParameters_MissingValues_AreUnknownAndTaintedWhenSecure()
    {
        var (parameters, context) = LoadLazyParameters();

        var missing = Expr.AssertUnknown(parameters.Get("missing"));
        Assert.Equal("parameters('missing')", missing.Symbol);
        Assert.Equal(UnknownOrigins.Parameter, missing.Origins);
        Assert.False(missing.Tainted);

        var missingSecure = Expr.AssertUnknown(Expr.Eval("[parameters('missingSecure')]", context));
        Assert.True(missingSecure.Tainted);
        Assert.Equal(["missing", "missingSecure"], parameters.MissingNames);

        var derived = Expr.AssertUnknown(Expr.Eval("[concat('x-', parameters('missing'))]", context));
        Assert.Equal("concat('x-', parameters('missing'))", derived.Symbol);
        Assert.Equal(UnknownOrigins.Parameter, derived.Origins);
    }

    [Fact]
    public void LazyParameters_SuppliedValuesAreDataNotExpressions()
    {
        var (parameters, context) = LoadLazyParameters();

        Assert.Equal("[[x", Expr.AssertString(parameters.Get("supplied")));
        Assert.Equal("[[x", Expr.AssertString(Expr.Eval("[parameters('supplied')]", context)));
    }

    [Fact]
    public void LazyParameters_SecureDefaults_AreTaintedAfterEvaluation()
    {
        var (parameters, context) = LoadLazyParameters();

        Assert.True(SecretJson.IsSecret(parameters.Get("secureDefault"), out var secret));
        Assert.Equal("ab", secret.Value);
        Assert.True(SecretJson.IsSecret(Expr.Eval("[toUpper(parameters('secureDefault'))]", context), out var upper));
        Assert.Equal("AB", upper.Value);
    }

    [Fact]
    public void LazyParameters_BehaveAsAReadOnlyDictionaryOfCopies()
    {
        var (parameters, _) = LoadLazyParameters();

        Assert.Equal(11, parameters.Count);
        Assert.True(parameters.ContainsKey("WITHDEFAULT"));
        Assert.False(parameters.ContainsKey("nope"));
        Assert.True(parameters.TryGetValue("withDefault", out var value));
        Assert.Equal("dv", Expr.AssertString(value));
        Assert.False(parameters.TryGetValue("nope", out _));
        Assert.Throws<KeyNotFoundException>(() => parameters["nope"]);
        Assert.Equal(DiagnosticCodes.UndefinedParameter, Assert.Throws<ExpressionEvaluationException>(() => parameters.Get("nope")).Code);
        Assert.True(parameters.TryGetBound("pw", out var bound));
        Assert.NotNull(bound.PendingReference);
        Assert.Contains("withDefault", parameters.Keys);

        var obj = Assert.IsType<JsonObject>(parameters.Get("obj"));
        obj["k"] = "changed";
        Assert.Equal("v1", Expr.AssertString(parameters.Get("obj")!["k"]));
        Assert.Equal(11, parameters.Keys.Count());
        Assert.Empty(LazyParameters.Empty);
        Assert.Empty(LazyParameters.Empty.MissingNames);
    }

    [Fact]
    public void LazyParameters_MustBeBoundBeforeDefaultsAreEvaluated()
    {
        var document = Expr.ParseBody("\"parameters\": { \"d\": { \"type\": \"string\", \"defaultValue\": \"[concat('a')]\" }, \"s\": { \"type\": \"string\" } }, \"resources\": []", ParameterSet.Empty.WithInline("s", "v"));
        var parameters = new LazyParameters(document.ParameterValues);

        Assert.Equal("v", Expr.AssertString(parameters.Get("s")));
        Assert.Throws<InvalidOperationException>(() => parameters.Get("d"));
    }
}
