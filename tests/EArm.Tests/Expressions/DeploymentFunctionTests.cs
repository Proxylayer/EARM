using System;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Diagnostics;
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
