using System.Linq;
using System.Text.Json.Nodes;
using EntraArm.Core;
using EntraArm.Core.Diagnostics;
using EntraArm.Core.Secrets;
using EntraArm.Expressions.Evaluation;
using EntraArm.Expressions.Validation;
using EntraArm.Expressions.Values;
using Xunit;

namespace EntraArm.Tests;

/// <summary>Group 7 (WI-16): user-defined functions (the template's <c>functions</c> section) following the ARM rules.</summary>
public sealed class UserFunctionTests
{
    private const string Functions = """
        "functions": [ { "namespace": "c", "members": {
          "greet":   { "parameters": [ { "name": "s", "type": "string" }, { "name": "n", "type": "int" } ], "output": { "type": "string", "value": "[concat(parameters('s'), string(parameters('N')))]" } },
          "secret":  { "parameters": [ { "name": "s", "type": "string" } ], "output": { "type": "securestring", "value": "[parameters('s')]" } },
          "wrong":   { "output": { "type": "int", "value": "[concat('a')]" } },
          "boom":    { "output": { "type": "string", "value": "[div(1, 0)]" } },
          "outer":   { "output": { "type": "string", "value": "[c.inner()]" } },
          "inner":   { "output": { "type": "string", "value": "x" } },
          "vars":    { "output": { "type": "string", "value": "[variables('v')]" } },
          "refs":    { "output": { "type": "string", "value": "[reference('app').id]" } },
          "tpl":     { "parameters": [ { "name": "own", "type": "string" } ], "output": { "type": "string", "value": "[parameters('env')]" } },
          "copy":    { "output": { "type": "int", "value": "[copyIndex()]" } },
          "obj":     { "parameters": [ { "name": "o", "type": "object" } ], "output": { "type": "object", "value": "[union(parameters('o'), createObject('added', true()))]" } },
          "untyped": { "parameters": [ { "name": "v" } ], "output": { "value": "[parameters('v')]" } }
        } } ],
        "variables": { "v": "value" },
        "resources": []
        """;

    private static readonly TemplateDocument Document = Expr.ParseBody(Functions);

    private static EvaluationContext Context(EvaluationScope scope = EvaluationScope.Resource)
    {
        return Expr.Context(
            Expr.Parameters(("env", "dev"), ("pw", SecretJson.CreateString("s3cret"))),
            variables: Document.Variables,
            resources: Expr.Index(("Microsoft.Entra/applications", "app")),
            scope: scope,
            userFunctions: Document.Functions);
    }

    [Theory]
    [InlineData("[c.greet('a', 1)]", "\"a1\"")]
    [InlineData("[C.GREET('a', 2)]", "\"a2\"")]
    [InlineData("[c.greet(concat('x', 'y'), add(1, 2))]", "\"xy3\"")]
    [InlineData("[c.inner()]", "\"x\"")]
    [InlineData("[c.obj(createObject('a', 1))]", "{\"a\":1,\"added\":true}")]
    [InlineData("[c.untyped(createArray(1))]", "[1]")]
    [InlineData("[length(c.greet('abc', 10))]", "5")]
    public void UserFunctions_BindParametersAndEvaluateTheBody(string expression, string expectedJson)
    {
        Expr.AssertJson(expectedJson, Expr.Eval(expression, Context()));
    }

    [Fact]
    public void UserFunctions_AreAllowedInEveryCallerScope_ButNotInsideAnotherFunction()
    {
        Assert.Equal("a1", Expr.AssertString(Expr.Eval("[c.greet('a', 1)]", Context(EvaluationScope.Variable))));
        Assert.Equal("a1", Expr.AssertString(Expr.Eval("[c.greet('a', 1)]", Context(EvaluationScope.ParameterDefault))));
        Assert.Equal("a1", Expr.AssertString(Expr.Eval("[c.greet('a', 1)]", Context(EvaluationScope.Output))));

        var nested = Expr.Fails("[c.outer()]", DiagnosticCodes.FunctionNotAllowedHere, Context());
        Assert.Contains("c.inner() cannot be called inside user-defined function c.outer", nested.Message);
        Assert.Contains("(in functions[0].members.outer.output.value)", nested.Message);
        Expr.Fails("[c.greet('a', 1)]", DiagnosticCodes.FunctionNotAllowedHere, Context(EvaluationScope.UserFunction));
    }

    [Fact]
    public void Arguments_AreTypeChecked_AgainstTheDeclaredParameterTypes()
    {
        var wrongType = Expr.Fails("[c.greet(1, 1)]", DiagnosticCodes.InvalidFunctionArguments, Context());
        Assert.Contains("argument 1 ('s') must be of type string, got", wrongType.Message);
        Assert.Equal(9, wrongType.Offset);

        var wrongInt = Expr.Fails("[c.greet('a', 'b')]", DiagnosticCodes.InvalidFunctionArguments, Context());
        Assert.Contains("argument 2 ('n') must be of type int", wrongInt.Message);

        var arity = Expr.Fails("[c.greet('a')]", DiagnosticCodes.InvalidFunctionArguments, Context());
        Assert.Contains("takes exactly 2 argument(s) but 1 were supplied", arity.Message);
        Expr.Fails("[c.inner('extra')]", DiagnosticCodes.InvalidFunctionArguments, Context());
        Expr.Fails("[c.greet(lambda('x', 1), 1)]", DiagnosticCodes.InvalidFunctionArguments, Context());
        Expr.Fails("[c.obj('not an object')]", DiagnosticCodes.InvalidFunctionArguments, Context());
    }

    [Fact]
    public void UnknownAndSecureArguments_PassTheTypeCheck()
    {
        var unknown = Expr.AssertUnknown(Expr.Eval("[c.greet(reference('app').displayName, 1)]", Context()));
        Assert.Equal("concat(" + Expr.AppRef + ".displayName, '1')", unknown.Symbol);
        Assert.Equal(UnknownOrigins.Reference, unknown.Origins);

        Assert.True(SecretJson.IsSecret(Expr.Eval("[c.greet(parameters('pw'), 1)]", Context()), out var tainted));
        Assert.Equal("s3cret1", tainted.Value);
    }

    [Fact]
    public void SecureOutputs_TaintTheResult()
    {
        Assert.True(SecretJson.IsSecret(Expr.Eval("[c.secret('plain')]", Context()), out var secret));
        Assert.Equal("plain", secret.Value);
        Assert.Equal("\"***\"", Expr.Eval("[c.secret('plain')]", Context())!.ToJsonString());
    }

    [Fact]
    public void OutputType_IsEnforced()
    {
        var error = Expr.Fails("[c.wrong()]", DiagnosticCodes.ExpressionEvaluationFailed, Context());

        Assert.Contains("the output is declared as int but evaluated to", error.Message);
        Assert.Contains("(see functions[0].members.wrong.output)", error.Message);
    }

    [Fact]
    public void BodyFailures_AreReportedAtTheCallSite_WithTheBodyPath()
    {
        var error = Expr.Fails("[concat('x', c.boom())]", DiagnosticCodes.ExpressionEvaluationFailed, Context());

        Assert.StartsWith("c.boom(): ", error.Message, System.StringComparison.Ordinal);
        Assert.Contains("(in functions[0].members.boom.output.value)", error.Message);
        Assert.Contains("(at offset", error.Message);
        Assert.Equal(13, error.Offset);
        Assert.NotNull(error.InnerException);
    }

    [Theory]
    [InlineData("[c.vars()]", DiagnosticCodes.FunctionNotAllowedHere, "variables() cannot be used inside a user-defined function")]
    [InlineData("[c.refs()]", DiagnosticCodes.ReferenceNotAllowedHere, "reference() cannot be used inside a user-defined function")]
    [InlineData("[c.tpl('x')]", DiagnosticCodes.UndefinedParameter, "'env' is not a parameter of user-defined function c.tpl")]
    [InlineData("[c.copy()]", DiagnosticCodes.CopyIndexOutsideCopy, "")]
    [InlineData("[d.greet('a', 1)]", DiagnosticCodes.UnknownFunction, "declares no 'functions' namespace 'd'")]
    [InlineData("[c.nope()]", DiagnosticCodes.UnknownFunction, "namespace 'c' has no member 'nope'")]
    public void TheBody_FollowsTheArmPhaseRules(string expression, string code, string messageFragment)
    {
        var error = Expr.Fails(expression, code, Context());

        if (messageFragment.Length > 0)
        {
            Assert.Contains(messageFragment, error.Message);
        }
    }

    [Fact]
    public void TemplateParameterInsideABody_NamesTheFunctionsOwnParameters()
    {
        var error = Expr.Fails("[c.tpl('x')]", DiagnosticCodes.UndefinedParameter, Context());

        Assert.Contains("parameters() names only the function's own parameters (own)", error.Message);
        Assert.DoesNotContain("dev", error.Message);
    }

    [Fact]
    public void CopyIndexInsideABody_IsOutsideTheCallersLoop()
    {
        var inLoop = Context().PushCopy(new CopyFrame("loop", 2, 3));

        Expr.AssertJson("2", Expr.Eval("[copyIndex()]", inLoop));
        Expr.Fails("[c.copy()]", DiagnosticCodes.CopyIndexOutsideCopy, inLoop);
    }

    // ---- validation ----

    [Fact]
    public void Validator_ChecksDeclarationsArityTypesAndPlacement()
    {
        var analysis = new ExpressionValidator().Analyze(Expr.ParseBody(Functions.Replace("\"resources\": []", "\"resources\": [ { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01\", \"name\": \"g\", \"properties\": { "
            + "\"ok\": \"[c.greet('a', 1)]\", \"arity\": \"[c.greet('a')]\", \"lambda\": \"[c.greet(lambda('x', 1), 1)]\", \"ns\": \"[d.greet('a', 1)]\", \"member\": \"[c.nope()]\" } } ]", System.StringComparison.Ordinal)));

        var byPath = analysis.Diagnostics.GroupBy(d => d.Path!.ToString()).ToDictionary(g => g.Key, g => g.Select(d => d.Code).ToList());
        Assert.False(byPath.ContainsKey("resources[0].properties.ok"));
        Assert.Equal([DiagnosticCodes.InvalidFunctionArguments], byPath["resources[0].properties.arity"]);
        Assert.Equal([DiagnosticCodes.InvalidFunctionArguments], byPath["resources[0].properties.lambda"]);
        Assert.Equal([DiagnosticCodes.UnknownFunction], byPath["resources[0].properties.ns"]);
        Assert.Equal([DiagnosticCodes.UnknownFunction], byPath["resources[0].properties.member"]);

        // The bodies are checked where they are declared.
        Assert.Equal([DiagnosticCodes.FunctionNotAllowedHere], byPath["functions[0].members.outer.output.value"]);
        Assert.Equal([DiagnosticCodes.FunctionNotAllowedHere], byPath["functions[0].members.vars.output.value"]);
        Assert.Equal([DiagnosticCodes.ReferenceNotAllowedHere], byPath["functions[0].members.refs.output.value"]);
        Assert.Equal([DiagnosticCodes.UndefinedParameter], byPath["functions[0].members.tpl.output.value"]);
        Assert.Contains(DiagnosticCodes.CopyIndexOutsideCopy, byPath["functions[0].members.copy.output.value"]);
        Assert.False(byPath.ContainsKey("functions[0].members.greet.output.value"));
        Assert.False(byPath.ContainsKey("functions[0].members.secret.output.value"));
        Assert.All(analysis.Diagnostics, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
    }

    [Fact]
    public void TheSampleSlug_EvaluatesConcretely()
    {
        var document = Expr.ParseBody("\"functions\": [{ \"namespace\": \"c\", \"members\": { \"slug\": { \"parameters\": [{ \"name\": \"s\", \"type\": \"string\" }], \"output\": { \"type\": \"string\", \"value\": \"[toLower(replace(parameters('s'), ' ', '-'))]\" } } } }], \"resources\": []");
        var context = Expr.Context(userFunctions: document.Functions);

        Assert.Equal("orders-admins", Expr.AssertString(Expr.Eval("[c.slug('Orders Admins')]", context)));
        Assert.Equal("x", Expr.AssertString(Expr.Eval("[c.slug('X')]", context)));
        Assert.False(UnknownJson.ContainsUnknown(Expr.Eval("[c.slug('X')]", context)));
    }
}
