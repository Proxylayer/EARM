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

public sealed class DeploymentFunctionTests
{
    private const string AppId = "/providers/Microsoft.Entra/applications/app";
    private const string SpType = "Microsoft.Entra/servicePrincipals";

    private static TemplateResourceIndex SampleIndex()
    {
        return Expr.Index(
            ("Microsoft.Entra/applications", "app"),
            ("Microsoft.Entra/groups", "dup"),
            ("Microsoft.Entra/roleDefinitions", "dup"),
            ("Microsoft.Entra/users", "u@contoso.com"),
            ("Microsoft.Entra/applications/federatedIdentityCredentials", "app/fic"));
    }

    // ---- resourceId ----

    [Theory]
    [InlineData("[resourceId('Microsoft.Entra/applications', 'app')]", AppId)]
    [InlineData("[resourceId('microsoft.entra/APPLICATIONS', 'App')]", "/providers/Microsoft.Entra/applications/App")]
    [InlineData("[resourceId('Microsoft.Entra/applications/federatedIdentityCredentials', 'app', 'fic')]", "/providers/Microsoft.Entra/applications/app/federatedIdentityCredentials/fic")]
    [InlineData("[resourceId('Microsoft.Entra/applications/federatedIdentityCredentials', 'app/fic')]", "/providers/Microsoft.Entra/applications/app/federatedIdentityCredentials/fic")]
    [InlineData("[resourceId('Microsoft.Entra/applications/passwordCredentials', concat('a', 'pp'), 'secret')]", "/providers/Microsoft.Entra/applications/app/passwordCredentials/secret")]
    [InlineData("[resourceId('Microsoft.Entra/conditionalAccess/policies', 'p')]", "/providers/Microsoft.Entra/conditionalAccess/policies/p")]
    [InlineData("[resourceId('Microsoft.Entra/users', 'u@contoso.com')]", "/providers/Microsoft.Entra/users/u@contoso.com")]
    [InlineData("[resourceId('Microsoft.Entra/conditionalAccess/authenticationContextClassReferences', 'c1')]", "/providers/Microsoft.Entra/conditionalAccess/authenticationContextClassReferences/c1")]
    public void ResourceId_RendersTenantScopeIds(string expression, string expected)
    {
        Assert.Equal(expected, Expr.AssertString(Expr.Eval(expression)));
    }

    [Theory]
    [InlineData("[resourceId('Microsoft.Entra/applications', 'a', 'b')]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[resourceId('Microsoft.Entra/applications/federatedIdentityCredentials', 'a')]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[resourceId('Microsoft.Entra/applications', '')]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[resourceId('Microsoft.Entra/applications/federatedIdentityCredentials', 'a/')]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[resourceId('Microsoft.Entra/widgets', 'a')]", DiagnosticCodes.UnknownResourceType)]
    [InlineData("[resourceId('Microsoft.Entra/applications')]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[resourceId('Microsoft.Entra/applications', 1)]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[resourceId(1, 'a')]", DiagnosticCodes.InvalidFunctionArguments)]
    public void ResourceId_RejectsBadArguments(string expression, string code)
    {
        Expr.Fails(expression, code);
    }

    [Fact]
    public void ResourceId_RejectsASlashInsideASingleSegmentName()
    {
        // A declared name 'a/b' on a one-level type is InvalidResourceName; resourceId() currently accepts it and
        // renders /providers/Microsoft.Entra/applications/a/b, which TryParseResourceId cannot parse back.
        Expr.Fails("[resourceId('Microsoft.Entra/applications', 'a/b')]", DiagnosticCodes.InvalidFunctionArguments);
    }

    [Fact]
    public void ResourceId_WithADeployTimeSegment_IsASymbolicIdentity()
    {
        var context = Expr.Context(resources: SampleIndex());

        var unknown = Expr.AssertUnknown(Expr.Eval("[resourceId('Microsoft.Entra/servicePrincipals', reference('app').appId)]", context));

        Assert.Equal($"resourceId('Microsoft.Entra/servicePrincipals', {Expr.AppRef}.appId)", unknown.Symbol);
        Assert.Equal(UnknownOrigins.Reference, unknown.Origins);
        Assert.False(unknown.Tainted);
        Assert.NotNull(unknown.ResourceId);
        Assert.Equal(SpType, unknown.ResourceId.Type);
        Assert.Equal([Expr.AppRef + ".appId"], unknown.ResourceId.NameSegments);
    }

    // ---- reference ----

    [Fact]
    public void Reference_ByUniqueName_IsAPlaceholderWithTheResourceAsSource()
    {
        var context = Expr.Context(resources: SampleIndex());

        var unknown = Expr.AssertUnknown(Expr.Eval("[reference('app')]", context));

        Assert.Equal(Expr.AppRef, unknown.Symbol);
        Assert.Equal(UnknownOrigins.Reference, unknown.Origins);
        Assert.Equal(ResourceIdentifier.Create("Microsoft.Entra/applications", "app"), unknown.Source);
        Assert.Null(unknown.ResourceId);
        Assert.False(unknown.Tainted);

        var property = Expr.AssertUnknown(Expr.Eval("[reference('app').appId]", context));
        Assert.Equal(Expr.AppRef + ".appId", property.Symbol);
        Assert.Equal(unknown.Source, property.Source);
    }

    [Theory]
    [InlineData("[reference('APP')]")]
    [InlineData("[reference('/providers/Microsoft.Entra/applications/app')]")]
    [InlineData("[reference('/PROVIDERS/microsoft.entra/Applications/app')]")]
    [InlineData("[reference('Microsoft.Entra/applications/app')]")]
    [InlineData("[reference(resourceId('Microsoft.Entra/applications', 'app'))]")]
    [InlineData("[reference(concat('ap', 'p'))]")]
    public void Reference_AcceptsNameIdAndTypeNameForms(string expression)
    {
        var unknown = Expr.AssertUnknown(Expr.Eval(expression, Expr.Context(resources: SampleIndex())));

        Assert.Equal(Expr.AppRef, unknown.Symbol);
    }

    [Fact]
    public void Reference_ToAChild_UsesTheParentChildName()
    {
        var unknown = Expr.AssertUnknown(Expr.Eval("[reference('app/fic')]", Expr.Context(resources: SampleIndex())));

        Assert.Equal("ref(/providers/Microsoft.Entra/applications/app/federatedIdentityCredentials/fic)", unknown.Symbol);
    }

    [Theory]
    [InlineData("[reference('dup')]", DiagnosticCodes.AmbiguousReference)]
    [InlineData("[reference('missing')]", DiagnosticCodes.ReferenceTargetNotFound)]
    [InlineData("[reference('/providers/Microsoft.Entra/bogus/x')]", DiagnosticCodes.InvalidResourceId)]
    [InlineData("[reference('/providers/Microsoft.Entra/applications')]", DiagnosticCodes.InvalidResourceId)]
    [InlineData("[reference(resourceId('Microsoft.Entra/applications', 'other'))]", DiagnosticCodes.ReferenceTargetNotFound)]
    [InlineData("[reference('Microsoft.Entra/groups/other')]", DiagnosticCodes.ReferenceTargetNotFound)]
    [InlineData("[reference(1)]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[reference()]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[reference('app', 1)]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[reference('missing', '2026-10-01-preview')]", DiagnosticCodes.ReferenceTargetNotFound)]
    public void Reference_RejectsUnresolvableTargets(string expression, string code)
    {
        var exception = Expr.Fails(expression, code, Expr.Context(resources: SampleIndex()));

        Assert.Equal(1, exception.Offset);
    }

    [Fact]
    public void Reference_WithoutAnIndex_IsNotCheckedAgainstTheTemplate()
    {
        var context = Expr.Context();

        var unknown = Expr.AssertUnknown(Expr.Eval("[reference(resourceId('Microsoft.Entra/applications', 'other'))]", context));
        Assert.Equal("ref(/providers/Microsoft.Entra/applications/other)", unknown.Symbol);

        Expr.Fails("[reference('other')]", DiagnosticCodes.ReferenceTargetNotFound, context);
    }

    [Fact]
    public void Reference_WithApiVersion_LooksUpExistingResources()
    {
        var resolver = new InMemoryReferenceResolver();
        var context = Expr.Context(resources: SampleIndex(), references: resolver);
        var groupId = ResourceIdentifier.Create("Microsoft.Entra/groups", "g");

        Expr.Fails("[reference(resourceId('Microsoft.Entra/groups', 'g'), '2026-10-01-preview')]", DiagnosticCodes.ReferenceTargetNotFound, context);

        resolver.Publish(groupId, new JsonObject { ["id"] = "gid", ["displayName"] = "G", ["tags"] = new JsonArray("t") }, "gid");

        Assert.Equal("gid", Expr.AssertString(Expr.Eval("[reference(resourceId('Microsoft.Entra/groups', 'g'), '2026-10-01-preview').id]", context)));
        Assert.Equal("G", Expr.AssertString(Expr.Eval("[reference('Microsoft.Entra/groups/g', '2026-10-01-preview').displayName]", context)));
        Assert.Equal("t", Expr.AssertString(Expr.Eval("[reference('/providers/Microsoft.Entra/groups/g', '2026-10-01-preview').tags[0]]", context)));
        Expr.Fails("[reference('Microsoft.Entra/groups/g', '2026-10-01-preview').nope]", DiagnosticCodes.ExpressionEvaluationFailed, context);

        // The live object is cloned on every use.
        var live = Assert.IsType<JsonObject>(Expr.Eval("[reference('Microsoft.Entra/groups/g', '2026-10-01-preview')]", context));
        live["id"] = "changed";
        Assert.Equal("gid", Expr.AssertString(Expr.Eval("[reference('Microsoft.Entra/groups/g', '2026-10-01-preview').id]", context)));

        // Same-template references still resolve as placeholders through the in-memory resolver.
        Assert.Equal(Expr.AppRef, Expr.AssertUnknown(Expr.Eval("[reference('app')]", context)).Symbol);

        Assert.True(resolver.Remove(groupId));
        Assert.False(resolver.Remove(groupId));
        Expr.Fails("[reference(resourceId('Microsoft.Entra/groups', 'g'), '2026-10-01-preview')]", DiagnosticCodes.ReferenceTargetNotFound, context);
    }

    [Fact]
    public void Reference_WithApiVersion_Offline_IsAPlaceholderWithTheVersionInTheSymbol()
    {
        var unknown = Expr.AssertUnknown(Expr.Eval("[reference(resourceId('Microsoft.Entra/groups', 'g'), '2026-10-01-preview')]", Expr.Context(resources: SampleIndex())));

        Assert.Equal("ref(/providers/Microsoft.Entra/groups/g, '2026-10-01-preview')", unknown.Symbol);
        Assert.Equal(ResourceIdentifier.Create("Microsoft.Entra/groups", "g"), unknown.Source);
    }

    [Fact]
    public void Reference_PublishedSameTemplateObject_ReturnsTheLiveObject()
    {
        var resolver = new InMemoryReferenceResolver();
        resolver.Publish(ResourceIdentifier.Create("Microsoft.Entra/applications", "app"), new JsonObject { ["appId"] = "aid" }, "oid");
        var context = Expr.Context(resources: SampleIndex(), references: resolver);

        Assert.Equal("aid", Expr.AssertString(Expr.Eval("[reference('app').appId]", context)));
        Assert.Equal("x-aid", Expr.AssertString(Expr.Eval("[concat('x-', reference('app').appId)]", context)));
    }

    [Theory]
    [InlineData(EvaluationScope.ParameterDefault, "[reference('app')]")]
    [InlineData(EvaluationScope.UserFunction, "[reference('app')]")]
    [InlineData(EvaluationScope.Variable, "[reference('app')]")]
    [InlineData(EvaluationScope.ParameterDefault, "[reference('app', '2026-10-01-preview')]")]
    [InlineData(EvaluationScope.UserFunction, "[reference('app', '2026-10-01-preview')]")]
    public void Reference_IsRejectedInScopesThatCannotUseIt(EvaluationScope scope, string expression)
    {
        Expr.Fails(expression, DiagnosticCodes.ReferenceNotAllowedHere, Expr.Context(resources: SampleIndex(), scope: scope));
    }

    [Fact]
    public void Reference_WithApiVersion_IsAllowedInVariablesAndOutputs()
    {
        Assert.Equal("ref(/providers/Microsoft.Entra/applications/app, '2026-10-01-preview')", Expr.AssertUnknown(Expr.Eval("[reference('app', '2026-10-01-preview')]", Expr.Context(resources: SampleIndex(), scope: EvaluationScope.Variable))).Symbol);
        Assert.Equal(Expr.AppRef, Expr.AssertUnknown(Expr.Eval("[reference('app')]", Expr.Context(resources: SampleIndex(), scope: EvaluationScope.Output))).Symbol);
    }

    [Fact]
    public void Reference_OfADeployTimeComputation_MatchesAResourceNamedByTheSameComputation()
    {
        var index = SampleIndex();
        var symbolicSp = new ResourceIdentifier(SpType, [Expr.AppRef + ".appId"]);
        index.Add(new TemplateResourceIndex.Entry(symbolicSp, true, null));
        var context = Expr.Context(resources: index);

        var viaReference = Expr.AssertUnknown(Expr.Eval("[reference(reference('app').appId)]", context));
        var viaResourceId = Expr.AssertUnknown(Expr.Eval("[reference(resourceId('Microsoft.Entra/servicePrincipals', reference('app').appId))]", context));

        Assert.Equal(symbolicSp, viaReference.Source);
        Assert.Equal(symbolicSp, viaResourceId.Source);
        Assert.Equal(viaReference.Symbol, viaResourceId.Symbol);
        Assert.StartsWith("ref(/providers/Microsoft.Entra/servicePrincipals/", viaReference.Symbol);
    }

    [Fact]
    public void Reference_OfAnUnmatchedDeployTimeValue_IsAnUnknownWithoutASource()
    {
        var unknown = Expr.AssertUnknown(Expr.Eval("[reference(reference('app').displayName)]", Expr.Context(resources: SampleIndex())));

        Assert.Null(unknown.Source);
        Assert.Null(unknown.ResourceId);
        Assert.Equal(UnknownOrigins.Reference, unknown.Origins);
        Assert.DoesNotContain("/providers/", unknown.Symbol.Replace(Expr.AppRef, string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void Index_FindsByNameExactThenCaseInsensitiveAndByCopyName()
    {
        var index = new TemplateResourceIndex();
        var exact = ResourceIdentifier.Create("Microsoft.Entra/groups", "Name");
        index.Add(new TemplateResourceIndex.Entry(exact, false, null, "loop"));
        index.Add(new TemplateResourceIndex.Entry(ResourceIdentifier.Create("Microsoft.Entra/users", "name@x.com"), false, null));
        index.Add(new TemplateResourceIndex.Entry(ResourceIdentifier.Create("Microsoft.Entra/roleDefinitions", "name"), false, null));
        index.Add(new TemplateResourceIndex.Entry(exact, false, null));

        Assert.Equal(3, index.Entries.Count);
        Assert.Equal(exact, Assert.Single(index.FindByName("Name")).Id);
        Assert.Equal("Microsoft.Entra/roleDefinitions", Assert.Single(index.FindByName("name")).Id.Type);
        Assert.Equal(2, index.FindByName("NAME").Count);
        Assert.Equal("loop", Assert.Single(index.FindByCopyName("LOOP")).CopyName);
        Assert.Empty(index.FindByCopyName("other"));
        Assert.True(index.Contains(exact));
        Assert.True(index.TryGet(exact, out var entry));
        Assert.Equal("loop", entry.CopyName);
        Assert.Equal(3, index.Ids.Count);
        Assert.Empty(TemplateResourceIndex.Empty.Entries);
    }

    // ---- copyIndex ----

    [Theory]
    [InlineData("[copyIndex()]", 2)]
    [InlineData("[copyIndex(1)]", 3)]
    [InlineData("[copyIndex('c')]", 2)]
    [InlineData("[copyIndex('C')]", 2)]
    [InlineData("[copyIndex('c', 5)]", 7)]
    [InlineData("[add(copyIndex(), 10)]", 12)]
    public void CopyIndex_ReadsTheConcreteFrame(string expression, long expected)
    {
        var context = Expr.Context().PushCopy(new CopyFrame("c", 2, 3));

        Assert.NotNull(context.CurrentCopy);
        Assert.True(context.CurrentCopy.IsConcrete);
        Expr.AssertJson(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), Expr.Eval(expression, context));
    }

    [Fact]
    public void CopyIndex_NestedLoops_InnermostIsTheDefault()
    {
        var context = Expr.Context().PushCopy(new CopyFrame("outer", 1)).PushCopy(new CopyFrame("inner", 4));

        Expr.AssertJson("4", Expr.Eval("[copyIndex()]", context));
        Expr.AssertJson("1", Expr.Eval("[copyIndex('outer')]", context));
        Expr.AssertJson("4", Expr.Eval("[copyIndex('inner')]", context));
        Assert.Null(context.Base.CurrentCopy);
    }

    [Theory]
    [InlineData("[copyIndex('other')]")]
    [InlineData("[copyIndex('other', 1)]")]
    public void CopyIndex_UnknownLoopName_IsAnError(string expression)
    {
        Expr.Fails(expression, DiagnosticCodes.CopyIndexOutsideCopy, Expr.Context().PushCopy(new CopyFrame("c", 0)));
    }

    [Fact]
    public void CopyIndex_OutsideALoop_IsAnError()
    {
        Expr.Fails("[copyIndex()]", DiagnosticCodes.CopyIndexOutsideCopy);
        Expr.Fails("[copyIndex('c', 'x')]", DiagnosticCodes.InvalidFunctionArguments, Expr.Context().PushCopy(new CopyFrame("c", 0)));
        Expr.Fails("[copyIndex(1, 2, 3)]", DiagnosticCodes.InvalidFunctionArguments);
    }

    [Theory]
    [InlineData("[copyIndex()]", "copyIndex('c')")]
    [InlineData("[copyIndex('c')]", "copyIndex('c')")]
    [InlineData("[copyIndex(1)]", "copyIndex('c', 1)")]
    [InlineData("[copyIndex('c', 2)]", "copyIndex('c', 2)")]
    [InlineData("[concat('n-', copyIndex())]", "concat('n-', copyIndex('c'))")]
    public void CopyIndex_InAnUnresolvedFrame_IsACopyUnknown(string expression, string symbol)
    {
        var frame = CopyFrame.Unresolved("c", 3);
        Assert.False(frame.IsConcrete);
        Assert.Equal(3, frame.Count);

        var unknown = Expr.AssertUnknown(Expr.Eval(expression, Expr.Context().PushCopy(frame)));

        Assert.Equal(symbol, unknown.Symbol);
        Assert.Equal(UnknownOrigins.Copy, unknown.Origins);
    }

    // ---- deployment, tenant, environment ----

    [Fact]
    public void Deployment_ReturnsTheOfflineInfoByDefault()
    {
        Assert.Equal("Incremental", Expr.AssertString(Expr.Eval("[deployment().mode]")));
        Assert.Equal("validate", Expr.AssertString(Expr.Eval("[deployment().name]")));
        Assert.Null(Expr.Eval("[deployment().templateHash]"));
        Assert.Null(Expr.Eval("[deployment().tenantId]"));
        Assert.Equal("Incremental", Expr.AssertString(Expr.Eval("[deployment().properties.mode]")));
        Assert.Null(Expr.Eval("[deployment().properties.templateHash]"));

        // The supplied parameters are only known online.
        var parameters = Expr.AssertUnknown(Expr.Eval("[deployment().properties.parameters]"));
        Assert.Equal("deployment().properties.parameters", parameters.Symbol);
        Assert.Equal(UnknownOrigins.Deployment, parameters.Origins);
    }

    [Fact]
    public void Deployment_ReflectsTheSuppliedInfo()
    {
        var info = new DeploymentInfo("orders", "hash-1", "tid-1", "Complete")
        {
            Tenant = new JsonObject { ["tenantId"] = "tid-1", ["defaultDomain"] = "contoso.onmicrosoft.com" },
            Environment = new JsonObject { ["name"] = "AzureCloud", ["graph"] = "https://graph.microsoft.com" },
        };
        var context = Expr.Context(deployment: info);

        Assert.Equal("Complete", Expr.AssertString(Expr.Eval("[deployment().mode]", context)));
        Assert.Equal("hash-1", Expr.AssertString(Expr.Eval("[deployment().templateHash]", context)));
        Assert.Equal("tid-1", Expr.AssertString(Expr.Eval("[tenant().tenantId]", context)));
        Assert.Equal("contoso.onmicrosoft.com", Expr.AssertString(Expr.Eval("[tenant().defaultDomain]", context)));
        Assert.Equal("https://graph.microsoft.com", Expr.AssertString(Expr.Eval("[environment().graph]", context)));

        // The returned objects are copies.
        var tenant = Assert.IsType<JsonObject>(Expr.Eval("[tenant()]", context));
        tenant["tenantId"] = "changed";
        Assert.Equal("tid-1", info.Tenant["tenantId"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("[tenant()]", "tenant()")]
    [InlineData("[tenant().tenantId]", "tenant().tenantId")]
    [InlineData("[environment()]", "environment()")]
    [InlineData("[environment().authentication.loginEndpoint]", "environment().authentication.loginEndpoint")]
    [InlineData("[toLower(tenant().defaultDomain)]", "toLower(tenant().defaultDomain)")]
    public void TenantAndEnvironment_AreUnknownOffline(string expression, string symbol)
    {
        var unknown = Expr.AssertUnknown(Expr.Eval(expression));

        Assert.Equal(symbol, unknown.Symbol);
        Assert.Equal(UnknownOrigins.Deployment, unknown.Origins);
    }

    [Fact]
    public void DeploymentTenantEnvironment_TakeNoArguments()
    {
        Expr.Fails("[deployment('x')]", DiagnosticCodes.InvalidFunctionArguments);
        Expr.Fails("[tenant(1)]", DiagnosticCodes.InvalidFunctionArguments);
        Expr.Fails("[environment(1)]", DiagnosticCodes.InvalidFunctionArguments);
    }

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

    // ---- registry ----

    [Fact]
    public void Registry_DescribesTheSupportedRejectedAndStubFunctions()
    {
        var registry = FunctionRegistry.Default;

        Assert.True(registry.Contains("CONCAT"));
        Assert.NotNull(registry.Find("resourceId"));
        Assert.Null(registry.Find("nope"));
        Assert.Equal(["extensionResourceId", "listKeys", "listSecrets", "managementGroup", "managementGroupResourceId", "pickZones", "providers", "resourceGroup", "subscription", "subscriptionResourceId", "tenantResourceId"], registry.NamesOfKind(FunctionKind.Rejected));
        // Lambdas and the Entra functions are implemented; nothing in the default registry is a stub.
        Assert.Empty(registry.NamesOfKind(FunctionKind.NotImplementedYet));
        foreach (var name in new[] { "map", "filter", "reduce", "sort", "toObject", "groupBy", "lambdaVariables", "wellKnownApp", "appRole", "permissionScope", "roleTemplate", "tenant", "environment" })
        {
            Assert.Equal(FunctionKind.Supported, registry.Find(name)!.Kind);
        }

        Assert.True(registry.Find("map")!.Lazy);
        Assert.True(registry.Find("appRole")!.HandlesUnknown);
        Assert.True(registry.NamesOfKind(FunctionKind.Supported).Count >= 80);
        Assert.Contains("if", registry.Names);

        var descriptor = registry.Find("substring")!;
        Assert.Equal("between 1 and 3", descriptor.ArityDescription);
        Assert.True(descriptor.AcceptsArgumentCount(2));
        Assert.False(descriptor.AcceptsArgumentCount(4));
        Assert.Equal("at least 1", registry.Find("concat")!.ArityDescription);
        Assert.Equal("exactly 0", registry.Find("true")!.ArityDescription);
        Assert.True(registry.Find("if")!.Lazy);
        Assert.True(registry.Find("utcNow")!.NonDeterministic);

        Assert.Throws<ArgumentException>(() => new FunctionRegistry().Register(new FunctionDescriptor { Name = "x", Group = "g" }));
        Assert.NotSame(FunctionRegistry.CreateDefault(), FunctionRegistry.Default);
    }
}
