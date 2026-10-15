using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Secrets;
using EArm.Core.Types;
using EArm.Expressions;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Functions;
using EArm.Expressions.Static;
using EArm.Expressions.Values;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Secure values never reach messages or unknown symbols: redaction through <see cref="FunctionCall"/>, the evaluator's
/// dispatch wrap, the resourceId slash rules, withheld property names and the <see cref="SecureTextMask"/> backstop.
/// </summary>
public sealed class SecureRedactionTests
{
    private const string Pw = "s3cret-text";
    private const string Fmt = "{1} s3cret-fmt";
    private const string Slashy = "s3c/ret";
    private const string Deep = "a/b/c-s3cret";
    private const string FicType = "Microsoft.Entra/applications/federatedIdentityCredentials";

    private static readonly string[] Secrets = [Pw, Fmt, Slashy, Deep, "s3cret"];

    private static JsonValue SecretNumber(string json)
    {
        return SecretJson.Create(new SecretValue(json, JsonValueKind.Number));
    }

    private static EvaluationContext SecureContext(FunctionRegistry? functions = null, bool withResources = true)
    {
        return Expr.Context(
            Expr.Parameters(
                ("pw", SecretJson.CreateString(Pw)),
                ("fmt", SecretJson.CreateString(Fmt)),
                ("slashy", SecretJson.CreateString(Slashy)),
                ("deep", SecretJson.CreateString(Deep)),
                ("frac", SecretNumber("1.5")),
                ("big", SecretNumber("20000")),
                ("max", SecretNumber("9223372036854775807")),
                ("zero", SecretNumber("0")),
                ("badfmt", SecretJson.CreateString("x")),
                ("plain", "visible")),
            variables: new Dictionary<string, JsonNode?> { ["v"] = 1 },
            resources: withResources ? Expr.Index(("Microsoft.Entra/applications", "app")) : null,
            functions: functions);
    }

    private static void AssertNoSecret(string text)
    {
        foreach (var secret in Secrets)
        {
            Assert.DoesNotContain(secret, text);
        }
    }

    // ---- 1. unknown symbols of HandlesUnknown functions ----

    [Theory]
    [InlineData("[createObject(reference('app').x, parameters('pw'))]", "createObject(" + Expr.AppRef + ".x, ***)")]
    [InlineData("[reference(resourceId('Microsoft.Entra/applications', parameters('pw')), '2026-10-01-preview')]", "ref(***, '2026-10-01-preview')")]
    [InlineData("[reference(resourceId('Microsoft.Entra/applications', parameters('pw')), reference('app').v)]", "reference(***, " + Expr.AppRef + ".v)")]
    [InlineData("[concat(parameters('pw'), reference('app').x)]", "concat(***, " + Expr.AppRef + ".x)")]
    [InlineData("[length(concat(parameters('pw'), reference('app').x))]", "length(concat(***, " + Expr.AppRef + ".x))")]
    [InlineData("[resourceId('Microsoft.Entra/applications/federatedIdentityCredentials', parameters('pw'), reference('app').x)]", "resourceId('Microsoft.Entra/applications/federatedIdentityCredentials', ***, " + Expr.AppRef + ".x)")]
    [InlineData("[union(createArray(parameters('pw')), createArray(reference('app').x))]", "union([\"***\"], [\"(deploy-time)\"])")]
    [InlineData("[empty(concat(reference('app').x, parameters('pw')))]", "empty(concat(" + Expr.AppRef + ".x, ***))")]
    [InlineData("[toLower(concat(parameters('pw'), reference('app').x))]", "toLower(concat(***, " + Expr.AppRef + ".x))")]
    [InlineData("[coalesce(reference('app').x, parameters('pw'))]", Expr.AppRef + ".x")]
    public void UnknownSymbols_NeverContainSecretText(string expression, string symbol)
    {
        var unknown = Expr.AssertUnknown(Expr.Eval(expression, SecureContext()));

        Assert.Equal(symbol, unknown.Symbol);
        AssertNoSecret(unknown.Symbol);
        Assert.True((unknown.Origins & UnknownOrigins.Reference) != 0);
    }

    [Fact]
    public void TaintedUnknowns_KeepTheIdentityButNotTheDisplayText()
    {
        var context = SecureContext();

        var existing = Expr.AssertUnknown(Expr.Eval("[reference(resourceId('Microsoft.Entra/applications', parameters('pw')), '2026-10-01-preview')]", context));
        Assert.True(existing.Tainted);
        Assert.Equal("/providers/Microsoft.Entra/applications/" + Pw, existing.Source?.Id);
        Assert.Equal("ref(***, '2026-10-01-preview').id", Expr.AssertUnknown(Expr.Eval("[reference(resourceId('Microsoft.Entra/applications', parameters('pw')), '2026-10-01-preview').id]", context)).Symbol);

        var symbolic = Expr.AssertUnknown(Expr.Eval("[resourceId('Microsoft.Entra/applications/federatedIdentityCredentials', parameters('pw'), reference('app').x)]", context));
        Assert.True(symbolic.Tainted);
        Assert.Equal(Pw, symbolic.ResourceId?.NameSegments[0]);
        Assert.True(Expr.AssertUnknown(Expr.Eval("[reference(resourceId('Microsoft.Entra/applications', parameters('pw')), reference('app').v)]", context)).Tainted);
        Assert.True(Expr.AssertUnknown(Expr.Eval("[createObject(reference('app').x, parameters('pw'))]", context)).Tainted);
    }

    [Theory]
    [InlineData("[copyIndex('c', parameters('big'))]", "copyIndex('c', ***)")]
    [InlineData("[copyIndex('c', parameters('zero'))]", "copyIndex('c', ***)")]
    [InlineData("[copyIndex(parameters('zero'))]", "copyIndex('c', ***)")]
    [InlineData("[copyIndex('c', 0)]", "copyIndex('c')")]
    public void CopyIndex_InAnUnresolvedFrame_ShowsASecureOffsetAsMask(string expression, string symbol)
    {
        var unknown = Expr.AssertUnknown(Expr.Eval(expression, SecureContext().PushCopy(CopyFrame.Unresolved("c"))));

        Assert.Equal(symbol, unknown.Symbol);
        Assert.Equal(UnknownOrigins.Copy, unknown.Origins);
    }

    // ---- 2. redacted message sites ----

    [Theory]
    [InlineData("[json(parameters('pw'))]", DiagnosticCodes.ExpressionEvaluationFailed, "the secure value is not valid JSON")]
    [InlineData("[int(parameters('pw'))]", DiagnosticCodes.ExpressionEvaluationFailed, "'***' is not an integer")]
    [InlineData("[int(parameters('frac'))]", DiagnosticCodes.ExpressionEvaluationFailed, "*** is not an integer")]
    [InlineData("[float(parameters('pw'))]", DiagnosticCodes.ExpressionEvaluationFailed, "'***' is not a number")]
    [InlineData("[bool(parameters('pw'))]", DiagnosticCodes.InvalidFunctionArguments, "cannot convert '***' to a boolean")]
    [InlineData("[format(parameters('fmt'), 'x')]", DiagnosticCodes.ExpressionEvaluationFailed, "invalid format string.")]
    [InlineData("[uri(parameters('pw'), 'x')]", DiagnosticCodes.ExpressionEvaluationFailed, "cannot combine the base and relative URIs.")]
    [InlineData("[substring(parameters('pw'), 50)]", DiagnosticCodes.ExpressionEvaluationFailed, "start index *** is outside the string (length ***)")]
    [InlineData("[substring(parameters('pw'), 1, 50)]", DiagnosticCodes.ExpressionEvaluationFailed, "length *** from index *** exceeds the string (length ***)")]
    [InlineData("[dateTimeAdd('2024-01-01T00:00:00Z', parameters('pw'))]", DiagnosticCodes.ExpressionEvaluationFailed, "'***' is not an ISO 8601 duration")]
    [InlineData("[dateTimeAdd(parameters('pw'), 'P1D')]", DiagnosticCodes.ExpressionEvaluationFailed, "'***' is not a valid ISO 8601 date/time")]
    [InlineData("[dateTimeAdd('2024-01-01T00:00:00Z', 'P1D', parameters('badfmt'))]", DiagnosticCodes.ExpressionEvaluationFailed, "invalid date format '***'")]
    [InlineData("[dateTimeToEpoch(parameters('pw'))]", DiagnosticCodes.ExpressionEvaluationFailed, "'***' is not a valid ISO 8601 date/time")]
    [InlineData("[dateTimeFromEpoch(parameters('max'))]", DiagnosticCodes.ExpressionEvaluationFailed, "epoch value *** is out of range")]
    [InlineData("[copyIndex(parameters('pw'))]", DiagnosticCodes.CopyIndexOutsideCopy, "no enclosing copy loop named '***'")]
    [InlineData("[parameters(parameters('pw'))]", DiagnosticCodes.UndefinedParameter, "Parameter '***' is not defined")]
    [InlineData("[variables(parameters('pw'))]", DiagnosticCodes.UndefinedVariable, "Variable '***' is not defined")]
    [InlineData("[resourceId(parameters('pw'), 'a')]", DiagnosticCodes.UnknownResourceType, "'***' is not a known resource type")]
    [InlineData("[resourceId('Microsoft.Entra/applications', parameters('slashy'))]", DiagnosticCodes.InvalidFunctionArguments, "name argument 1 must not contain '/' (got ***)")]
    [InlineData("[resourceId('Microsoft.Entra/applications/federatedIdentityCredentials', 'app', parameters('slashy'))]", DiagnosticCodes.InvalidFunctionArguments, "name argument 2 must not contain '/' (got ***)")]
    [InlineData("[resourceId('Microsoft.Entra/applications/federatedIdentityCredentials', parameters('deep'))]", DiagnosticCodes.InvalidFunctionArguments, "name *** has 3 segment(s) but type 'Microsoft.Entra/applications/federatedIdentityCredentials' takes 2 ('parent/child' form)")]
    [InlineData("[range(0, parameters('big'))]", DiagnosticCodes.InvalidFunctionArguments, "count must be between 0 and 10000, got ***")]
    [InlineData("[range(parameters('max'), 2)]", DiagnosticCodes.ExpressionEvaluationFailed, "the range starting at *** with 2 element(s) exceeds the 64-bit integer range")]
    [InlineData("[add(parameters('max'), 1)]", DiagnosticCodes.ExpressionEvaluationFailed, "integer overflow (***, 1)")]
    [InlineData("[padLeft('a', parameters('big'))]", DiagnosticCodes.InvalidFunctionArguments, "got ***")]
    [InlineData("[base64ToString(parameters('pw'))]", DiagnosticCodes.ExpressionEvaluationFailed, "the argument is not valid base64.")]
    [InlineData("[reference(parameters('pw'))]", DiagnosticCodes.ReferenceTargetNotFound, "no resource named '***'")]
    [InlineData("[reference(parameters('pw'), '2026-10-01-preview')]", DiagnosticCodes.ReferenceTargetNotFound, "'***' is not a resource of this template")]
    [InlineData("[reference(resourceId('Microsoft.Entra/applications', parameters('pw')))]", DiagnosticCodes.ReferenceTargetNotFound, "'***' is not a resource of this template")]
    [InlineData("[reference(concat('/providers/Microsoft.Entra/bogus/', parameters('pw')))]", DiagnosticCodes.InvalidResourceId, "'***' is not a valid resource id")]
    [InlineData("[if(parameters('pw'), 1, 2)]", DiagnosticCodes.InvalidFunctionArguments, "got secure value")]
    [InlineData("[less(parameters('pw'), 1)]", DiagnosticCodes.InvalidFunctionArguments, "got string and integer")]
    [InlineData("[createArray(parameters('pw'))[5]]", DiagnosticCodes.ExpressionEvaluationFailed, "Index 5 is outside the array")]
    public void MessagesWithTaintedArguments_AreRedacted(string expression, string code, string fragment)
    {
        var exception = Expr.Fails(expression, code, SecureContext());

        Assert.Contains(fragment, exception.Message);
        AssertNoSecret(exception.Message);
        AssertNoSecret(exception.MessageWithOffset);
    }

    [Fact]
    public void ExistingReferenceNotFound_RedactsTheSecureId()
    {
        var context = SecureContext(functions: null) with { References = new InMemoryReferenceResolver() };

        var exception = Expr.Fails("[reference(resourceId('Microsoft.Entra/applications', parameters('pw')), '2026-10-01-preview')]", DiagnosticCodes.ReferenceTargetNotFound, context);

        Assert.Contains("existing resource '***' (apiVersion 2026-10-01-preview) was not found", exception.Message);
        AssertNoSecret(exception.Message);
    }

    [Fact]
    public void NonSecureArguments_StillRenderTheirText()
    {
        var context = SecureContext();

        Assert.Contains("'nope' is not an integer", Expr.Fails("[int('nope')]", DiagnosticCodes.ExpressionEvaluationFailed, context).Message);
        Assert.Contains("'visible' is not an integer", Expr.Fails("[int(parameters('plain'))]", DiagnosticCodes.ExpressionEvaluationFailed, context).Message);
        Assert.Contains("start index 50 is outside the string (length 7)", Expr.Fails("[substring('visible', 50)]", DiagnosticCodes.ExpressionEvaluationFailed, context).Message);
        Assert.Contains("integer overflow (9223372036854775807, 1)", Expr.Fails("[add(9223372036854775807, 1)]", DiagnosticCodes.ExpressionEvaluationFailed, context).Message);
        Assert.Contains("got 20000", Expr.Fails("[range(0, 20000)]", DiagnosticCodes.InvalidFunctionArguments, context).Message);
    }

    // ---- 3. dispatch wrap ----

    private static FunctionRegistry ThrowingRegistry()
    {
        var registry = FunctionRegistry.CreateDefault();
        registry.Register(new FunctionDescriptor { Name = "boom", Group = "test", MinArgs = 0, MaxArgs = 1, Implementation = _ => throw new InvalidOperationException("kaboom details") });
        registry.Register(new FunctionDescriptor { Name = "echo", Group = "test", MinArgs = 1, MaxArgs = 1, Implementation = call => throw new ArgumentException("bad value: " + call.String(0)) });
        registry.Register(new FunctionDescriptor { Name = "lazyBoom", Group = "test", MinArgs = 0, MaxArgs = 0, Lazy = true, Implementation = _ => throw new NotSupportedException("lazy") });
        registry.Register(new FunctionDescriptor { Name = "custom", Group = "test", MinArgs = 0, MaxArgs = 0, Implementation = _ => throw new ExpressionEvaluationException(DiagnosticCodes.InvalidCopy, "custom failure", 7) });
        return registry;
    }

    [Fact]
    public void UnexpectedExceptions_BecomeEvaluationFailuresWithTheInnerPreserved()
    {
        var context = SecureContext(ThrowingRegistry());

        var exception = Expr.Fails("[boom()]", DiagnosticCodes.ExpressionEvaluationFailed, context);
        Assert.Equal("boom(): unexpected failure (InvalidOperationException: kaboom details).", exception.Message);
        Assert.Equal(1, exception.Offset);
        var inner = Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Equal("kaboom details", inner.Message);

        var lazy = Expr.Fails("[lazyBoom()]", DiagnosticCodes.ExpressionEvaluationFailed, context);
        Assert.Equal("lazyBoom(): unexpected failure (NotSupportedException: lazy).", lazy.Message);
        Assert.IsType<NotSupportedException>(lazy.InnerException);

        var nested = Expr.Fails("[toUpper(boom())]", DiagnosticCodes.ExpressionEvaluationFailed, context);
        Assert.StartsWith("boom(): unexpected failure", nested.Message);
        Assert.Equal(9, nested.Offset);
    }

    [Fact]
    public void UnexpectedExceptions_OmitTheInnerTextWhenTheCallIsTainted()
    {
        var context = SecureContext(ThrowingRegistry());

        var boom = Expr.Fails("[boom(parameters('pw'))]", DiagnosticCodes.ExpressionEvaluationFailed, context);
        Assert.Equal("boom(): unexpected failure (InvalidOperationException).", boom.Message);
        Assert.IsType<InvalidOperationException>(boom.InnerException);

        var echo = Expr.Fails("[echo(parameters('pw'))]", DiagnosticCodes.ExpressionEvaluationFailed, context);
        Assert.Equal("echo(): unexpected failure (ArgumentException).", echo.Message);
        AssertNoSecret(echo.Message);
        Assert.Contains(Pw, Assert.IsType<ArgumentException>(echo.InnerException).Message);

        Assert.Contains("bad value: visible", Expr.Fails("[echo(parameters('plain'))]", DiagnosticCodes.ExpressionEvaluationFailed, context).Message);
    }

    [Fact]
    public void ExpressionExceptions_PassThroughTheWrapUnchanged()
    {
        var exception = Expr.Fails("[custom()]", DiagnosticCodes.InvalidCopy, SecureContext(ThrowingRegistry()));

        Assert.Equal("custom failure", exception.Message);
        Assert.Equal(7, exception.Offset);
        Assert.Null(exception.InnerException);
    }

    // ---- 4. copyIndex overflow ----

    [Theory]
    [InlineData("[copyIndex('loop', 9223372036854775807)]")]
    [InlineData("[copyIndex(9223372036854775807)]")]
    [InlineData("[copyIndex('loop', 9223372036854775806)]")]
    public void CopyIndex_OffsetOverflow_IsAnEvaluationFailure(string expression)
    {
        var context = SecureContext().PushCopy(new CopyFrame("loop", 2));

        var exception = Expr.Fails(expression, DiagnosticCodes.ExpressionEvaluationFailed, context);

        Assert.Contains("index 2 plus offset 922337203685477580", exception.Message);
        Assert.Contains("exceeds the 64-bit integer range", exception.Message);
        Assert.IsType<OverflowException>(exception.InnerException);
    }

    [Fact]
    public void CopyIndex_OffsetOverflow_WithASecureOffsetIsRedacted()
    {
        var exception = Expr.Fails("[copyIndex('loop', parameters('max'))]", DiagnosticCodes.ExpressionEvaluationFailed, SecureContext().PushCopy(new CopyFrame("loop", 2)));

        Assert.Contains("index 2 plus offset *** exceeds", exception.Message);
        Expr.AssertJson("9223372036854775807", Expr.Eval("[copyIndex('loop', 9223372036854775805)]", SecureContext().PushCopy(new CopyFrame("loop", 2))));
    }

    // ---- 5. resourceId slash matrix ----

    [Fact]
    public void ResourceId_SingleSegmentType_RejectsASlash()
    {
        var exception = Expr.Fails("[resourceId('Microsoft.Entra/applications', 'a/b')]", DiagnosticCodes.InvalidFunctionArguments);

        Assert.Equal("resourceId(): name argument 1 must not contain '/' (got 'a/b'); a name of type 'Microsoft.Entra/applications' is a single segment.", exception.Message);
    }

    [Fact]
    public void ResourceId_ChildType_AcceptsBothFormsAndRoundTrips()
    {
        const string expected = "/providers/Microsoft.Entra/applications/app/federatedIdentityCredentials/fic";
        var catalog = ResourceTypeCatalog.Default;

        string single = Expr.AssertString(Expr.Eval("[resourceId('Microsoft.Entra/applications/federatedIdentityCredentials', 'app/fic')]"));
        string pair = Expr.AssertString(Expr.Eval("[resourceId('Microsoft.Entra/applications/federatedIdentityCredentials', 'app', 'fic')]"));

        Assert.Equal(expected, single);
        Assert.Equal(expected, pair);
        Assert.True(catalog.TryParseResourceId(single, out var parsed));
        Assert.Equal("app/fic", parsed!.Name);
        Assert.Equal(FicType, parsed.Type);
        Assert.Equal(["app", "fic"], parsed.NameSegments);
    }

    [Theory]
    [InlineData("[resourceId('Microsoft.Entra/applications/federatedIdentityCredentials', 'app/fic/x')]", "resourceId(): name 'app/fic/x' has 3 segment(s) but type 'Microsoft.Entra/applications/federatedIdentityCredentials' takes 2 ('parent/child' form).")]
    [InlineData("[resourceId('Microsoft.Entra/applications/federatedIdentityCredentials', 'app', 'fic/x')]", "resourceId(): name argument 2 must not contain '/' (got 'fic/x'); pass one argument per segment, resourceId(type, parent, child), or a single 'parent/child' name.")]
    [InlineData("[resourceId('Microsoft.Entra/applications/federatedIdentityCredentials', 'ap/p', 'fic')]", "resourceId(): name argument 1 must not contain '/' (got 'ap/p'); pass one argument per segment, resourceId(type, parent, child), or a single 'parent/child' name.")]
    [InlineData("[resourceId('Microsoft.Entra/applications/federatedIdentityCredentials', 'app/')]", "resourceId(): name segments must not be empty.")]
    [InlineData("[resourceId('Microsoft.Entra/applications/federatedIdentityCredentials', 'app')]", "resourceId(): type 'Microsoft.Entra/applications/federatedIdentityCredentials' takes 2 name argument(s) but 1 were supplied.")]
    [InlineData("[resourceId('Microsoft.Entra/applications', 'a', 'b')]", "resourceId(): type 'Microsoft.Entra/applications' takes 1 name argument(s) but 2 were supplied.")]
    [InlineData("[resourceId('Microsoft.Entra/conditionalAccess/policies', 'a/b')]", "resourceId(): name argument 1 must not contain '/' (got 'a/b'); a name of type 'Microsoft.Entra/conditionalAccess/policies' is a single segment.")]
    [InlineData("[resourceId('Microsoft.Entra/users', 'a/b@contoso.com')]", "resourceId(): name argument 1 must not contain '/' (got 'a/b@contoso.com'); a name of type 'Microsoft.Entra/users' is a single segment.")]
    public void ResourceId_SlashMatrix_RejectsWithPreciseWording(string expression, string message)
    {
        var exception = Expr.Fails(expression, DiagnosticCodes.InvalidFunctionArguments);

        Assert.Equal(message, exception.Message);
    }

    [Theory]
    [InlineData("[resourceId('Microsoft.Entra/applications/federatedIdentityCredentials', reference('app').x, 'fic')]", 0, "fic")]
    [InlineData("[resourceId('Microsoft.Entra/applications/federatedIdentityCredentials', 'app', reference('app').x)]", 1, "app")]
    [InlineData("[resourceId('Microsoft.Entra/applications', reference('app').x)]", 0, null)]
    public void ResourceId_SymbolicSegments_AreExemptFromTheSlashRule(string expression, int symbolicIndex, string? literalSegment)
    {
        var unknown = Expr.AssertUnknown(Expr.Eval(expression, SecureContext()));

        Assert.NotNull(unknown.ResourceId);
        Assert.Equal(Expr.AppRef + ".x", unknown.ResourceId.NameSegments[symbolicIndex]);
        Assert.Contains("/", unknown.ResourceId.NameSegments[symbolicIndex]);
        if (literalSegment is not null)
        {
            Assert.Contains(literalSegment, unknown.ResourceId.NameSegments);
        }
    }

    [Theory]
    [InlineData("Microsoft.Entra/applications", "'orders-api'")]
    [InlineData("Microsoft.Entra/applications/federatedIdentityCredentials", "'orders-api', 'github-main'")]
    [InlineData("Microsoft.Entra/applications/passwordCredentials", "'orders-api', 'ci-secret'")]
    [InlineData("Microsoft.Entra/servicePrincipals", "'6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0002'")]
    [InlineData("Microsoft.Entra/groups", "'orders-admins'")]
    [InlineData("Microsoft.Entra/users", "'bg-orders@contoso.com'")]
    [InlineData("Microsoft.Entra/roleDefinitions", "'Orders Support'")]
    [InlineData("Microsoft.Entra/conditionalAccess/authenticationContextClassReferences", "'c10'")]
    [InlineData("Microsoft.Entra/conditionalAccess/authenticationStrengths", "'Orders phishing-resistant'")]
    [InlineData("Microsoft.Entra/appRoleAssignments", "'6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0002'")]
    [InlineData("Microsoft.Entra/oauth2PermissionGrants", "'6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0002'")]
    [InlineData("Microsoft.Entra/conditionalAccess/namedLocations", "'Contoso offices'")]
    [InlineData("Microsoft.Entra/conditionalAccess/policies", "'CA-Orders-Admins-MFA'")]
    [InlineData("Microsoft.Entra/administrativeUnits", "'Orders AU'")]
    [InlineData("Microsoft.Entra/roleAssignments", "'6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0002'")]
    public void ResourceId_RoundTripsThroughTheCatalogForEveryType(string type, string nameArguments)
    {
        string id = Expr.AssertString(Expr.Eval($"[resourceId('{type}', {nameArguments})]"));

        Assert.True(ResourceTypeCatalog.Default.TryParseResourceId(id, out var parsed), id);
        Assert.Equal(type, parsed!.Type);
        Assert.Equal(id, parsed.Id);
        Assert.Equal(nameArguments.Replace("'", string.Empty, StringComparison.Ordinal).Replace(", ", "/", StringComparison.Ordinal), parsed.Name);
    }

    // ---- 6. withheld property names ----

    [Theory]
    [InlineData("[createObject('k', parameters('pw')).nope]")]
    [InlineData("[createObject('k', parameters('pw'))['nope']]")]
    [InlineData("[createObject(parameters('pw'), 1).nope]")]
    [InlineData("[createObject('a', createObject('k', parameters('pw'))).a.nope]")]
    [InlineData("[createObject('k', createArray(parameters('frac'))).nope]")]
    public void NoSuchProperty_WithholdsNamesWhenTheObjectHoldsSecureValues(string expression)
    {
        var exception = Expr.Fails(expression, DiagnosticCodes.ExpressionEvaluationFailed, SecureContext());

        Assert.Contains("has no property 'nope' (the object holds secure values, so its property names are not listed)", exception.Message);
        Assert.DoesNotContain("properties:", exception.Message);
        AssertNoSecret(exception.Message);
    }

    [Fact]
    public void NoSuchProperty_ListsNamesForPlainObjects()
    {
        var context = SecureContext();

        Assert.Contains("has no property 'nope' (properties: k, m)", Expr.Fails("[createObject('k', 1, 'm', 2).nope]", DiagnosticCodes.ExpressionEvaluationFailed, context).Message);
        Assert.Contains("has no property 'nope' (properties: k)", Expr.Fails("[createObject('k', parameters('plain'))['nope']]", DiagnosticCodes.ExpressionEvaluationFailed, context).Message);
        Assert.Contains("has no property 'nope' (properties: k)", Expr.Fails("[createObject('k', reference('app').x).nope]", DiagnosticCodes.ExpressionEvaluationFailed, context).Message);
    }

    // ---- 7. SecureTextMask backstop ----

    private static (LazyParameters Parameters, EvaluationContext Context) LoadSecureParameters()
    {
        var document = Expr.ParseBody(
            """
            "parameters": {
              "pw":     { "type": "securestring" },
              "short2": { "type": "securestring" },
              "three":  { "type": "securestring" },
              "obj":    { "type": "secureObject" },
              "def":    { "type": "securestring", "defaultValue": "[concat('def', 'ault-s3c')]" },
              "bad":    { "type": "securestring", "defaultValue": "[nope()]" },
              "plain":  { "type": "string" }
            },
            "resources": []
            """,
            ParameterSet
                .Parse("""{ "parameters": { "pw": { "value": "s3cret-text" }, "short2": { "value": "ab" }, "three": { "value": "abc" }, "obj": { "value": { "n": 42, "b": true, "s": "longsecret", "list": [ "nested-s3c" ] } }, "plain": { "value": "visible" } } }""", "p.json"));

        var parameters = new LazyParameters(document.ParameterValues);
        var context = new EvaluationContext { Parameters = parameters }.Bind(Expr.Evaluator);
        return (parameters, context);
    }

    private static Diagnostic Message(string text)
    {
        return Diagnostic.Error("X", text);
    }

    [Fact]
    public void SecureTextMask_MasksLongStringSecretsOnly()
    {
        var (parameters, _) = LoadSecureParameters();
        var diagnostics = new List<Diagnostic>
        {
            Message("value s3cret-text leaked"),
            Message("s3cret-text"),
            Message("twice s3cret-text and s3cret-text"),
            Message("short ab stays, abc goes"),
            Message("numbers 42 and booleans true stay"),
            Message("object leaves longsecret and nested-s3c go"),
            Message("defaults default-s3c go too"),
            Message("plain visible stays"),
            Message("nothing to do"),
        };
        var untouched = diagnostics[8];

        SecureTextMask.Apply(parameters, diagnostics);

        Assert.Equal("value *** leaked", diagnostics[0].Message);
        Assert.Equal("***", diagnostics[1].Message);
        Assert.Equal("twice *** and ***", diagnostics[2].Message);
        Assert.Equal("short ab stays, *** goes", diagnostics[3].Message);
        Assert.Equal("numbers 42 and booleans true stay", diagnostics[4].Message);
        Assert.Equal("object leaves *** and *** go", diagnostics[5].Message);
        Assert.Equal("defaults *** go too", diagnostics[6].Message);
        Assert.Equal("plain visible stays", diagnostics[7].Message);
        Assert.Same(untouched, diagnostics[8]);
        Assert.Equal(3, SecureTextMask.MinLength);
        Assert.All(diagnostics, d => Assert.Equal("X", d.Code));
    }

    [Fact]
    public void SecureTextMask_IsANoOpWithoutSecureParameters()
    {
        var document = Expr.ParseBody("\"parameters\": { \"plain\": { \"type\": \"string\" } }, \"resources\": []", ParameterSet.Empty.WithInline("plain", "visible"));
        var parameters = new LazyParameters(document.ParameterValues);
        var diagnostics = new List<Diagnostic> { Message("visible text") };
        var original = diagnostics[0];

        SecureTextMask.Apply(parameters, diagnostics);
        SecureTextMask.Apply(LazyParameters.Empty, diagnostics);

        Assert.Same(original, diagnostics[0]);
        Assert.Throws<ArgumentNullException>(() => SecureTextMask.Apply(null!, diagnostics));
        Assert.Throws<ArgumentNullException>(() => SecureTextMask.Apply(parameters, null!));
    }

    [Fact]
    public void Validator_NeverPublishesSecureParameterText()
    {
        // Properties are only scanned for reference()/resourceId() offline (the rest is evaluated at deployment);
        // outputs, names, conditions and dependsOn are evaluated statically, so the other sites go there.
        var analysis = new EArm.Expressions.Validation.ExpressionValidator().Analyze(Expr.ParseBody(
            """
            "parameters": { "pw": { "type": "securestring" } },
            "resources": [
              { "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "name": "app",
                "properties": {
                  "c": "[reference(resourceId('Microsoft.Entra/groups', parameters('pw'))).id]",
                  "f": "[reference(concat(parameters('pw'), '-x')).id]"
                } } ],
            "outputs": {
              "o1": { "type": "securestring", "value": "[createObject('k', parameters('pw')).nope]" },
              "o2": { "type": "securestring", "value": "[json(parameters('pw'))]" },
              "o3": { "type": "securestring", "value": "[int(parameters('pw'))]" },
              "o4": { "type": "securestring", "value": "[variables(parameters('pw'))]" },
              "o5": { "type": "securestring", "value": "[resourceId('Microsoft.Entra/applications', parameters('pw'), 'x')]" }
            }
            """,
            ParameterSet.Empty.WithInline("pw", Pw)));

        Assert.Equal(2, analysis.Diagnostics.Count(d => d.Path!.ToString().StartsWith("resources[0].properties", StringComparison.Ordinal)));
        Assert.Equal(5, analysis.Diagnostics.Count(d => d.Path!.ToString().StartsWith("outputs", StringComparison.Ordinal)));
        Assert.All(analysis.Diagnostics, d => AssertNoSecret(d.Message));
        Assert.Contains(analysis.Diagnostics, d => d.Code == DiagnosticCodes.ReferenceTargetNotFound && d.Path == EArm.Core.Json.JsonPath.Parse("resources[0].properties.c") && d.Message.Contains("'***' is not a resource of this template", StringComparison.Ordinal));
        // concat(secret, '-x') is a plain (tainted) string, not a deploy-time unknown: reference() resolves it as a bare name.
        Assert.Contains(analysis.Diagnostics, d => d.Code == DiagnosticCodes.ReferenceTargetNotFound && d.Path == EArm.Core.Json.JsonPath.Parse("resources[0].properties.f") && d.Message.Contains("no resource named '***'", StringComparison.Ordinal));
        Assert.Contains(analysis.Diagnostics, d => d.Path == EArm.Core.Json.JsonPath.Parse("outputs.o1.value") && d.Message.Contains("property names are not listed", StringComparison.Ordinal));
        Assert.Contains(analysis.Diagnostics, d => d.Path == EArm.Core.Json.JsonPath.Parse("outputs.o2.value") && d.Message.Contains("the secure value is not valid JSON", StringComparison.Ordinal));
        Assert.Contains(analysis.Diagnostics, d => d.Path == EArm.Core.Json.JsonPath.Parse("outputs.o3.value") && d.Message.Contains("'***' is not an integer", StringComparison.Ordinal));
        Assert.Contains(analysis.Diagnostics, d => d.Path == EArm.Core.Json.JsonPath.Parse("outputs.o4.value") && d.Message.Contains("Variable '***' is not defined", StringComparison.Ordinal));
        Assert.Contains(analysis.Diagnostics, d => d.Path == EArm.Core.Json.JsonPath.Parse("outputs.o5.value") && d.Message.Contains("takes 1 name argument(s) but 2 were supplied", StringComparison.Ordinal));
    }
}
