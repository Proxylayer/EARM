using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using EntraArm.Core;
using EntraArm.Core.Diagnostics;
using EntraArm.Core.Json;
using EntraArm.Core.Secrets;
using EntraArm.Expressions.Evaluation;
using EntraArm.Expressions.Values;
using Xunit;

namespace EntraArm.Tests;

/// <summary>Secure-taint and deploy-time-unknown propagation through the evaluator.</summary>
public sealed class TaintAndUnknownTests
{
    private const string Secret = "abc";

    private static EvaluationContext TaintedContext(bool withResources = true)
    {
        return Expr.Context(
            Expr.Parameters(("pw", SecretJson.CreateString(Secret)), ("plain", "p")),
            resources: withResources ? Expr.Index(("Microsoft.Entra/applications", "app")) : null);
    }

    // ---- taint ----

    [Theory]
    [InlineData("[parameters('pw')]", "abc")]
    [InlineData("[toUpper(parameters('pw'))]", "ABC")]
    [InlineData("[concat(parameters('pw'), '-', parameters('plain'))]", "abc-p")]
    [InlineData("[if(equals(parameters('pw'), 'abc'), 'yes', 'no')]", "yes")]
    [InlineData("[first(createArray(parameters('pw')))]", "abc")]
    [InlineData("[createArray(parameters('pw'))[0]]", "abc")]
    [InlineData("[substring(parameters('pw'), 1)]", "bc")]
    [InlineData("[format('{0}!', parameters('pw'))]", "abc!")]
    [InlineData("[coalesce(null(), parameters('pw'))]", "abc")]
    [InlineData("[base64ToString(base64(parameters('pw')))]", "abc")]
    [InlineData("[createObject('p', parameters('pw')).p]", "abc")]
    public void TaintedArguments_YieldTaintedResults(string expression, string revealed)
    {
        var result = Expr.Eval(expression, TaintedContext());

        Assert.True(SecretJson.IsSecret(result, out var secret), "expected a secret but got " + Expr.Render(result));
        Assert.Equal(revealed, secret.Value);
        Assert.Equal(JsonValueKind.String, secret.Kind);
        Assert.Equal("\"***\"", result!.ToJsonString());
        Assert.Null(TemplateJson.AsString(result));
    }

    [Fact]
    public void TaintedNonStringResults_KeepTheirKind()
    {
        Assert.True(SecretJson.IsSecret(Expr.Eval("[equals(parameters('pw'), 'abc')]", TaintedContext()), out var flag));
        Assert.Equal(JsonValueKind.True, flag.Kind);
        Assert.Equal("true", flag.Value);

        Assert.True(SecretJson.IsSecret(Expr.Eval("[length(parameters('pw'))]", TaintedContext()), out var length));
        Assert.Equal(JsonValueKind.Number, length.Kind);
        Assert.Equal("3", length.Value);
    }

    [Fact]
    public void CreateObject_WithASecretLeaf_TaintsTheWholeResult()
    {
        var result = Assert.IsType<JsonObject>(Expr.Eval("[createObject('p', parameters('pw'), 'q', 1)]", TaintedContext()));

        Assert.True(SecretJson.IsSecret(result["p"], out var p));
        Assert.Equal(Secret, p.Value);
        Assert.True(SecretJson.IsSecret(result["q"]));
        Assert.Equal("{\"p\":\"***\",\"q\":\"***\"}", result.ToJsonString());
        Assert.True(SecretJson.ContainsSecret(result));
        Assert.DoesNotContain(Secret, result.ToJsonString());
    }

    [Fact]
    public void EvaluateTree_TaintsPerLeafAndLeavesTheInputUntouched()
    {
        var input = JsonNode.Parse("""{ "a": "[parameters('pw')]", "b": "plain", "c": "[[esc", "n": [ "[add(1, 2)]", 5 ], "o": { "x": "[parameters('plain')]" }, "z": null }""")!;

        var result = Assert.IsType<JsonObject>(Expr.Evaluator.EvaluateTree(input, TaintedContext()));

        Assert.True(SecretJson.IsSecret(result["a"]));
        Assert.Equal("plain", Expr.AssertString(result["b"]));
        Assert.Equal("[esc", Expr.AssertString(result["c"]));
        Expr.AssertJson("3", result["n"]![0]);
        Assert.False(SecretJson.IsSecret(result["n"]![0]));
        Expr.AssertJson("5", result["n"]![1]);
        Assert.Equal("p", Expr.AssertString(result["o"]!["x"]));
        Assert.Null(result["z"]);
        Assert.Equal("[parameters('pw')]", input["a"]!.GetValue<string>());
        Assert.Null(Expr.Evaluator.EvaluateTree(null, TaintedContext()));
    }

    [Fact]
    public void Reference_OfASecretDerivedId_IsATaintedUnknown()
    {
        var unknown = Expr.AssertUnknown(Expr.Eval("[reference(resourceId('Microsoft.Entra/applications', parameters('pw')))]", TaintedContext(withResources: false)));

        Assert.True(unknown.Tainted);
        Assert.True((unknown.Origins & UnknownOrigins.Reference) != 0);
        Assert.True(UnknownJson.ContainsTaint(UnknownJson.Create(unknown)));
    }

    [Fact]
    public void Reference_ErrorMessages_NeverRevealSecretText()
    {
        // The target id is computed from a secure parameter (secrets are revealed for the implementation and the
        // result re-tainted). The "not a resource of this template" error embeds the revealed id, so the secret
        // text reaches diagnostics and logs; secure values must never be printed (plan 1.5 "Secure taint").
        var exception = Expr.Fails("[reference(resourceId('Microsoft.Entra/applications', parameters('pw')))]", DiagnosticCodes.ReferenceTargetNotFound, TaintedContext());

        Assert.DoesNotContain(Secret, exception.Message);
    }

    [Fact]
    public void UnknownAndTaint_CombineThroughFunctions()
    {
        var unknown = Expr.AssertUnknown(Expr.Eval("[concat(parameters('pw'), reference('app').x)]", TaintedContext()));
        Assert.True(unknown.Tainted);
        Assert.Equal(UnknownOrigins.Reference, unknown.Origins);

        var array = Assert.IsType<JsonArray>(Expr.Eval("[createArray(parameters('pw'), reference('app').x)]", TaintedContext()));
        Assert.True(SecretJson.IsSecret(array[0]));
        Assert.True(Expr.AssertUnknown(array[1]).Tainted);

        // WI-16: a secure value that is not an application id is rejected (redacted); a secure appId that no table
        // answers offline becomes a tainted deploy-time unknown whose symbol never shows the value.
        var rejected = Expr.Fails("[permissionScope(parameters('pw'), 'User.Read')]", DiagnosticCodes.InvalidFunctionArguments, TaintedContext());
        Assert.Contains("***", rejected.Message);
        Assert.DoesNotContain(Secret, rejected.Message);

        var secureAppId = Expr.Context(Expr.Parameters(("pw", SecretJson.CreateString("11111111-2222-3333-4444-555555555555"))));
        var lookup = Expr.AssertUnknown(Expr.Eval("[permissionScope(parameters('pw'), 'User.Read')]", secureAppId));
        Assert.True(lookup.Tainted);
        Assert.Equal(UnknownOrigins.Deployment, lookup.Origins);
        Assert.Equal("permissionScope(***, 'User.Read')", lookup.Symbol);
    }

    [Fact]
    public void TaintHelper_TaintsAndRevealsLeaves()
    {
        var tree = new JsonObject { ["s"] = "x", ["n"] = 1, ["u"] = UnknownJson.Create("sym", UnknownOrigins.Copy), ["z"] = null };

        var tainted = Assert.IsType<JsonObject>(TaintHelper.Taint(tree));
        Assert.True(SecretJson.IsSecret(tainted["s"]));
        Assert.True(SecretJson.IsSecret(tainted["n"], out var n));
        Assert.Equal(JsonValueKind.Number, n.Kind);
        Assert.True(Expr.AssertUnknown(tainted["u"]).Tainted);
        Assert.Equal("sym", Expr.AssertUnknown(tainted["u"]).Symbol);
        Assert.Null(tainted["z"]);
        Assert.Null(TaintHelper.Taint(null));

        var revealed = Assert.IsType<JsonObject>(TaintHelper.RevealSecrets(tainted));
        Assert.Equal("x", Expr.AssertString(revealed["s"]));
        Expr.AssertJson("1", revealed["n"]);
        Assert.True(Expr.AssertUnknown(revealed["u"]).Tainted);
    }

    // ---- unknown propagation ----

    [Theory]
    [InlineData("[concat('x', reference('app').appId)]", "concat('x', " + Expr.AppRef + ".appId)")]
    [InlineData("[length(reference('app'))]", "length(" + Expr.AppRef + ")")]
    [InlineData("[reference('app')['appId']]", Expr.AppRef + "['appId']")]
    [InlineData("[reference('app').roles[0]]", Expr.AppRef + ".roles[0]")]
    [InlineData("[reference('app').a.b]", Expr.AppRef + ".a.b")]
    [InlineData("[reference('app').x[reference('app').y]]", Expr.AppRef + ".x[" + Expr.AppRef + ".y]")]
    [InlineData("[createArray(1)[reference('app').i]]", "createArray(1)[" + Expr.AppRef + ".i]")]
    [InlineData("[equals(reference('app').x, reference('app').x)]", "equals(" + Expr.AppRef + ".x, " + Expr.AppRef + ".x)")]
    [InlineData("[if(reference('app').flag, 'a', 'b')]", "if(" + Expr.AppRef + ".flag, 'a', 'b')")]
    [InlineData("[string(reference('app'))]", "string(" + Expr.AppRef + ")")]
    [InlineData("[coalesce(reference('app').x, 'y')]", Expr.AppRef + ".x")]
    [InlineData("[coalesce(null(), reference('app').x)]", Expr.AppRef + ".x")]
    [InlineData("[createObject('k', reference('app').x).k]", Expr.AppRef + ".x")]
    [InlineData("[first(createArray(reference('app').x))]", Expr.AppRef + ".x")]
    [InlineData("[toLower(concat(reference('app').x, 'Y'))]", "toLower(concat(" + Expr.AppRef + ".x, 'Y'))")]
    [InlineData("[add(reference('app').n, 1)]", "add(" + Expr.AppRef + ".n, 1)")]
    [InlineData("[union(createArray(reference('app').x), createArray(1))]", "union([\"(deploy-time)\"], [1])")]
    [InlineData("[empty(reference('app').x)]", "empty(" + Expr.AppRef + ".x)")]
    public void UnknownInputs_YieldUnknownResultsWithCanonicalSymbols(string expression, string symbol)
    {
        var unknown = Expr.AssertUnknown(Expr.Eval(expression, TaintedContext()));

        Assert.Equal(symbol, unknown.Symbol);
        Assert.Equal(UnknownOrigins.Reference, unknown.Origins);
        Assert.False(unknown.Tainted);
    }

    [Fact]
    public void ConstructorsAndSlices_KeepUnknownElementsWithoutBecomingUnknown()
    {
        var context = TaintedContext();

        Expr.AssertJson("1", Expr.Eval("[length(createArray(reference('app')))]", context));
        var array = Assert.IsType<JsonArray>(Expr.Eval("[take(createArray(reference('app').x, 2), 1)]", context));
        Assert.Equal(Expr.AppRef + ".x", Expr.AssertUnknown(Assert.Single(array)).Symbol);
        var obj = Assert.IsType<JsonObject>(Expr.Eval("[createObject('k', reference('app').x)]", context));
        Assert.True(UnknownJson.ContainsUnknown(obj));
        Assert.Equal("{\"k\":\"(deploy-time)\"}", obj.ToJsonString());
        Expr.AssertUnknown(Expr.Eval("[createObject(reference('app').x, 1)]", context));
        Expr.AssertJson("[\"k\"]", Expr.Eval("[objectKeys(createObject('k', reference('app').x))]", context));
    }

    // WI-16: the functions that used to be stubs evaluate concretely offline (tables and lambdas need no tenant).
    [Theory]
    [InlineData("[wellKnownApp('MicrosoftGraph')]", "\"00000003-0000-0000-c000-000000000000\"")]
    [InlineData("[roleTemplate('Directory Readers')]", "\"88d8e3e3-8f55-4a1e-953a-9b9898b8876b\"")]
    [InlineData("[map(createArray(1), lambda('x', lambdaVariables('x')))]", "[1]")]
    [InlineData("[filter(createArray(1, 2), lambda('x', equals(lambdaVariables('x'), 1)))]", "[1]")]
    [InlineData("[concat('a', wellKnownApp('MicrosoftGraph'))]", "\"a00000003-0000-0000-c000-000000000000\"")]
    public void FormerStubFunctions_EvaluateConcretely(string expression, string expectedJson)
    {
        Expr.AssertJson(expectedJson, Expr.Eval(expression, TaintedContext()));
    }

    [Fact]
    public void AppRole_OfAnApplicationThatDoesNotExistYet_IsUnknownThroughTheReferenceOnly()
    {
        var unknown = Expr.AssertUnknown(Expr.Eval("[appRole(reference('app').appId, 'r')]", TaintedContext()));

        Assert.Equal("appRole(" + Expr.AppRef + ".appId, 'r')", unknown.Symbol);
        Assert.Equal(UnknownOrigins.Reference, unknown.Origins);
        Assert.False(unknown.Tainted);
    }

    [Fact]
    public void LambdaAndEntraFunctions_StillEvaluateTheirNonLambdaArguments()
    {
        Expr.Fails("[map(nope(), lambda('x', lambdaVariables('x')))]", DiagnosticCodes.UnknownFunction, TaintedContext());
        Expr.Fails("[wellKnownApp(div(1, 0))]", DiagnosticCodes.ExpressionEvaluationFailed, TaintedContext());
    }

    [Theory]
    [InlineData("[lambda('x', add(1, 1))]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[resourceGroup()]", DiagnosticCodes.UnsupportedArmFunction)]
    [InlineData("[listKeys('a', 'b')]", DiagnosticCodes.UnsupportedArmFunction)]
    [InlineData("[providers('Microsoft.Entra')]", DiagnosticCodes.UnsupportedArmFunction)]
    [InlineData("[subscription().id]", DiagnosticCodes.UnsupportedArmFunction)]
    [InlineData("[nope()]", DiagnosticCodes.UnknownFunction)]
    [InlineData("[c.slug('x')]", DiagnosticCodes.UnknownFunction)]
    public void RejectedUnknownAndMisplacedFunctions_Fail(string expression, string code)
    {
        var exception = Expr.Fails(expression, code, TaintedContext());

        Assert.Equal(1, exception.Offset);
        if (code == DiagnosticCodes.UnsupportedArmFunction)
        {
            Assert.Contains("not available in EntraARM", exception.Message);
        }
    }

    [Fact]
    public void RejectedFunctions_NameTheAlternative()
    {
        Assert.Contains("tenant()", Expr.Fails("[resourceGroup()]", DiagnosticCodes.UnsupportedArmFunction).Message);
        Assert.Contains("resourceId(", Expr.Fails("[subscriptionResourceId('t', 'n')]", DiagnosticCodes.UnsupportedArmFunction).Message);
        Assert.Contains("docs/diagnostics.md", Expr.Fails("[nope()]", DiagnosticCodes.UnknownFunction).Message);
    }

    [Fact]
    public void UserFunctions_ResolveAgainstTheTemplatesFunctionsSection()
    {
        var document = Expr.ParseBody(
            """
            "functions": [ { "namespace": "c", "members": { "slug": { "parameters": [ { "name": "s", "type": "string" } ], "output": { "type": "string", "value": "[toLower(parameters('s'))]" } } } } ],
            "resources": []
            """);
        var context = Expr.Context(userFunctions: document.Functions);

        // WI-16: user-defined functions run their body; names are case-insensitive.
        Assert.Equal("x", Expr.AssertString(Expr.Eval("[c.slug('x')]", context)));
        Assert.Equal("x", Expr.AssertString(Expr.Eval("[C.SLUG('X')]", context)));

        Expr.Fails("[c.slug()]", DiagnosticCodes.InvalidFunctionArguments, context);
        Expr.Fails("[c.slug('a', 'b')]", DiagnosticCodes.InvalidFunctionArguments, context);
        Expr.Fails("[c.nope('x')]", DiagnosticCodes.UnknownFunction, context);
        Expr.Fails("[d.slug('x')]", DiagnosticCodes.UnknownFunction, context);
    }

    // ---- value helpers ----

    [Fact]
    public void JsonValues_AreEqual_ComparesUnknownsBySymbolAndSecretsByValue()
    {
        Assert.True(JsonValues.AreEqual(UnknownJson.Create("s", UnknownOrigins.Reference), UnknownJson.Create("s", UnknownOrigins.Copy, tainted: true)));
        Assert.False(JsonValues.AreEqual(UnknownJson.Create("s", UnknownOrigins.Reference), UnknownJson.Create("t", UnknownOrigins.Reference)));
        Assert.False(JsonValues.AreEqual(UnknownJson.Create("s", UnknownOrigins.Reference), JsonValue.Create("s")));
        Assert.True(JsonValues.AreEqual(SecretJson.CreateString("a"), SecretJson.CreateString("a")));
        Assert.False(JsonValues.AreEqual(SecretJson.CreateString("a"), SecretJson.CreateString("b")));
        Assert.False(JsonValues.AreEqual(SecretJson.CreateString("a"), JsonValue.Create("a")));
        Assert.True(JsonValues.AreEqual(JsonNode.Parse("1"), JsonValue.Create(1.0)));
        Assert.True(JsonValues.AreEqual(null, null));
        Assert.False(JsonValues.AreEqual(null, JsonValue.Create(1)));
        Assert.True(JsonValues.AreEqual(JsonNode.Parse("{\"a\":[1,{\"b\":true}]}"), JsonNode.Parse("{\"a\":[1.0,{\"b\":true}]}")));
    }

    [Fact]
    public void JsonValues_Clone_PreservesSecretAndUnknownLeaves()
    {
        var tree = new JsonObject
        {
            ["secret"] = SecretJson.CreateString("s"),
            ["unknown"] = UnknownJson.Create("sym", UnknownOrigins.Copy, tainted: true, resourceId: ResourceIdentifier.Create("Microsoft.Entra/groups", "g")),
            ["plain"] = new JsonArray(1, "x"),
        };

        var clone = Assert.IsType<JsonObject>(JsonValues.Clone(tree));
        Assert.NotSame(tree, clone);
        Assert.True(SecretJson.IsSecret(clone["secret"], out var secret));
        Assert.Equal("s", secret.Value);
        var unknown = Expr.AssertUnknown(clone["unknown"]);
        Assert.Equal("sym", unknown.Symbol);
        Assert.True(unknown.Tainted);
        Assert.Equal(UnknownOrigins.Copy, unknown.Origins);
        Assert.Equal("/providers/Microsoft.Entra/groups/g", unknown.ResourceId?.Id);
        Expr.AssertJson("[1,\"x\"]", clone["plain"]);
        Assert.Null(JsonValues.Clone(null));

        // JsonNode.DeepClone loses both markers (documented caveat).
        var deep = tree.DeepClone().AsObject();
        Assert.False(UnknownJson.IsUnknown(deep["unknown"]));
        Assert.Equal("(deploy-time)", TemplateJson.AsString(deep["unknown"]));
        Assert.False(SecretJson.IsSecret(deep["secret"]));
    }

    [Fact]
    public void UnknownJson_Helpers()
    {
        var payload = new UnknownValue("sym", UnknownOrigins.Reference | UnknownOrigins.Parameter);
        var tree = new JsonObject { ["u"] = UnknownJson.Create(payload), ["s"] = SecretJson.CreateString("x"), ["p"] = 1, ["t"] = UnknownJson.Create("t", UnknownOrigins.Copy, tainted: true) };

        Assert.Equal("(deploy-time)", UnknownValue.Display);
        Assert.Equal("(deploy-time)", payload.ToString());
        Assert.Equal("\"(deploy-time)\"", UnknownJson.Create(payload).ToJsonString());
        Assert.Equal("\"(deploy-time)\"", JsonSerializer.Serialize(new UnknownValue("sym", UnknownOrigins.Copy)));
        Assert.True(UnknownJson.ContainsUnknown(tree));
        Assert.False(UnknownJson.ContainsUnknown(JsonNode.Parse("{\"a\":\"(deploy-time)\"}")));
        Assert.True(UnknownJson.ContainsTaint(tree));
        Assert.False(UnknownJson.ContainsTaint(new JsonObject { ["u"] = UnknownJson.Create(payload) }));
        Assert.Equal(UnknownOrigins.Reference | UnknownOrigins.Parameter | UnknownOrigins.Copy, UnknownJson.OriginsOf(tree));
        Assert.Equal(UnknownOrigins.None, UnknownJson.OriginsOf(JsonValue.Create(1)));
        Assert.Equal("{\"u\":\"(deploy-time)\",\"s\":\"***\",\"p\":1,\"t\":\"(deploy-time)\"}", UnknownJson.Render(tree)!.ToJsonString());
        Assert.False(UnknownJson.IsUnknown(UnknownJson.Render(UnknownJson.Create(payload))));
        Assert.Same(payload, Expr.AssertUnknown(UnknownJson.Create(payload)));
        Assert.False(UnknownJson.IsUnknown(null, out var none));
        Assert.Null(none);
        Assert.Throws<ArgumentNullException>(() => UnknownJson.Create(null!));

        var read = JsonSerializer.Deserialize<UnknownValue>("\"opaque\"");
        Assert.NotNull(read);
        Assert.Equal("opaque", read.Symbol);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UnknownValue>("1"));
    }

    [Fact]
    public void JsonValues_Describe_And_Render()
    {
        Assert.Equal("null", JsonValues.Describe(null));
        Assert.Equal("secure value", JsonValues.Describe(SecretJson.CreateString("x")));
        Assert.Equal("deploy-time value", JsonValues.Describe(UnknownJson.Create("s", UnknownOrigins.Copy)));
        Assert.Equal("object", JsonValues.Describe(new JsonObject()));
        Assert.Equal("array", JsonValues.Describe(new JsonArray()));
        Assert.Equal("string", JsonValues.Describe(JsonValue.Create("x")));
        Assert.Equal("integer", JsonValues.Describe(JsonNode.Parse("1")));
        Assert.Equal("number", JsonValues.Describe(JsonNode.Parse("1.5")));
        Assert.Equal("boolean", JsonValues.Describe(JsonValue.Create(true)));

        Assert.Equal("null", JsonValues.Render(null));
        Assert.Equal("***", JsonValues.Render(SecretJson.CreateString("x")));
        Assert.Equal("s", JsonValues.Render(UnknownJson.Create("s", UnknownOrigins.Copy)));
        Assert.Equal("'it''s'", JsonValues.Render(JsonValue.Create("it's")));
        Assert.Equal("42", JsonValues.Render(JsonNode.Parse("42")));
        Assert.Equal("1.5", JsonValues.Render(JsonNode.Parse("1.5")));
        Assert.Equal("[1,\"a\"]", JsonValues.Render(JsonNode.Parse("[1, \"a\"]")));
        Assert.True(JsonValues.IsNumber(JsonNode.Parse("1")));
        Assert.False(JsonValues.IsNumber(SecretJson.Create(new SecretValue("1", JsonValueKind.Number))));
        Assert.True(JsonValues.TryGetInt64(JsonValue.Create(2.0), out long fromDouble));
        Assert.Equal(2, fromDouble);
        Assert.False(JsonValues.TryGetInt64(JsonValue.Create(2.5), out _));
        Assert.True(JsonValues.TryGetDouble(JsonValue.Create(7), out double fromInt));
        Assert.Equal(7, fromInt);
        Assert.False(JsonValues.TryGetBool(UnknownJson.Create("s", UnknownOrigins.Copy), out _));
    }
}
