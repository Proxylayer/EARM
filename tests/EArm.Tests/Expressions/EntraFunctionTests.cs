using System;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Secrets;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Static;
using EArm.Expressions.Validation;
using EArm.Expressions.Values;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Group 6: <see cref="WellKnownIds"/>, <c>wellKnownApp</c>, <c>appRole</c> / <c>permissionScope</c> /
/// <c>roleTemplate</c> through the tables, a live template object and <see cref="IEntraLookup"/>, the phase rules,
/// redaction, <c>tenant()</c> / <c>environment()</c>, and the dependency the reference scanner records.
/// </summary>
public sealed class EntraFunctionTests
{
    private const string Graph = WellKnownIds.MicrosoftGraphAppId;
    private const string UserRead = "e1fe6dd8-ba31-4d61-89e7-88639da4683d";
    private const string UserReadAll = "df021288-bdef-4463-88db-98f22de89214";
    private const string GlobalReader = "f2ef992c-3afb-46b9-b7cf-a126ee74c451";
    private const string DirectoryReaders = "88d8e3e3-8f55-4a1e-953a-9b9898b8876b";
    private const string AppId = "aaaaaaaa-1111-2222-3333-444444444444";
    private const string OtherAppId = "bbbbbbbb-1111-2222-3333-444444444444";
    private const string Secret = "s3cret-value";
    private const string AppResourceId = "/providers/Microsoft.Entra/applications/app";

    private static readonly ResourceIdentifier App = ResourceIdentifier.Create("Microsoft.Entra/applications", "app");

    private static EvaluationContext Context(IEntraLookup? lookup = null, IReferenceResolver? references = null, EvaluationScope scope = EvaluationScope.Resource, TemplateResourceIndex? resources = null, DeploymentInfo? deployment = null)
    {
        return new EvaluationContext
        {
            Parameters = Expr.Parameters(("pw", SecretJson.CreateString(Secret)), ("secureAppId", SecretJson.CreateString(AppId)), ("plainAppId", AppId)),
            Resources = resources ?? Expr.Index(("Microsoft.Entra/applications", "app"), ("Microsoft.Entra/groups", "g")),
            References = references ?? StaticReferenceResolver.Instance,
            EntraLookup = lookup ?? PlaceholderEntraLookup.Instance,
            Deployment = deployment ?? DeploymentInfo.Offline,
            Scope = scope,
        }.Bind(Expr.Evaluator);
    }

    private static JsonObject LiveApp(string appId = AppId)
    {
        return new JsonObject
        {
            ["id"] = "A1",
            ["appId"] = appId,
            ["appRoles"] = new JsonArray(new JsonObject { ["id"] = "role-admin", ["value"] = "Orders.Admin" }),
            ["api"] = new JsonObject { ["oauth2PermissionScopes"] = new JsonArray(new JsonObject { ["id"] = "scope-read", ["value"] = "Orders.Read" }) },
        };
    }

    private static InMemoryReferenceResolver Published(JsonObject live)
    {
        var resolver = new InMemoryReferenceResolver();
        resolver.Publish(App, live, "A1");
        return resolver;
    }

    // ---- WellKnownIds ----

    [Theory]
    [InlineData("MicrosoftGraph", Graph)]
    [InlineData("microsoftgraph", Graph)]
    [InlineData(" MicrosoftGraph ", Graph)]
    [InlineData("AzureKeyVault", WellKnownIds.AzureKeyVaultAppId)]
    [InlineData("AzureResourceManager", WellKnownIds.AzureServiceManagementAppId)]
    [InlineData("AzureServiceManagement", WellKnownIds.AzureServiceManagementAppId)]
    public void WellKnownIds_AppsResolveCaseInsensitively(string name, string expected)
    {
        Assert.True(WellKnownIds.TryGetAppId(name, out var appId));
        Assert.Equal(expected, appId);
    }

    [Fact]
    public void WellKnownIds_TablesAreConsistent()
    {
        Assert.False(WellKnownIds.TryGetAppId("Nope", out _));
        Assert.Contains("MicrosoftGraph", WellKnownIds.AppNames);
        Assert.Equal(WellKnownIds.Apps.Count, WellKnownIds.AppNames.Count);

        Assert.True(WellKnownIds.TryGetGraphScopeId("user.read", out var scope));
        Assert.Equal(UserRead, scope);
        Assert.True(WellKnownIds.TryGetGraphAppRoleId("USER.READ.ALL", out var role));
        Assert.Equal(UserReadAll, role);
        Assert.True(WellKnownIds.TryGetRoleTemplateId("global reader", out var roleTemplate));
        Assert.Equal(GlobalReader, roleTemplate);
        Assert.False(WellKnownIds.TryGetGraphScopeId("Nope.Scope", out _));

        // Every scope id is confirmed against the Microsoft Graph permissions reference (Review1D: all entries Verified).
        var email = Assert.Single(WellKnownIds.GraphScopes, s => s.Value == "email");
        Assert.True(email.Verified);
        Assert.True(WellKnownIds.TryGetGraphScopeId("email", out var emailId));
        Assert.Equal(email.Id, emailId);
        Assert.All(WellKnownIds.GraphScopes, s => Assert.True(s.Verified));
        Assert.All(WellKnownIds.GraphAppRoles, r => Assert.True(r.Verified));

        Assert.True(WellKnownIds.IsMicrosoftGraph("00000003-0000-0000-C000-000000000000"));
        Assert.False(WellKnownIds.IsMicrosoftGraph(AppId));
        Assert.False(WellKnownIds.IsMicrosoftGraph("not-a-guid"));

        Assert.True(WellKnownIds.TryGetAuthenticationStrengthId("mfa", out var mfa));
        Assert.True(WellKnownIds.TryGetAuthenticationStrengthId("Multifactor authentication", out var mfaByDisplayName));
        Assert.Equal("00000000-0000-0000-0000-000000000002", mfa);
        Assert.Equal(mfa, mfaByDisplayName);

        // Every table is a function of its key (no duplicates ignoring case) and every id is a GUID.
        Assert.Equal(WellKnownIds.GraphScopes.Count, WellKnownIds.GraphScopes.Select(s => s.Value.ToUpperInvariant()).Distinct().Count());
        Assert.Equal(WellKnownIds.GraphAppRoles.Count, WellKnownIds.GraphAppRoles.Select(s => s.Value.ToUpperInvariant()).Distinct().Count());
        Assert.Equal(WellKnownIds.RoleTemplates.Count, WellKnownIds.RoleTemplates.Select(s => s.DisplayName.ToUpperInvariant()).Distinct().Count());
        Assert.All(WellKnownIds.GraphScopes.Concat(WellKnownIds.GraphAppRoles), p => Assert.True(Guid.TryParse(p.Id, out _)));
        Assert.All(WellKnownIds.RoleTemplates, r => Assert.True(Guid.TryParse(r.Id, out _)));
        Assert.All(WellKnownIds.Apps, a => Assert.True(Guid.TryParse(a.AppId, out _)));
        Assert.Contains("All", WellKnownIds.ConditionalAccess.PrincipalAndApplicationValues);
        Assert.Contains("AllTrusted", WellKnownIds.ConditionalAccess.LocationValues);
    }

    // ---- wellKnownApp ----

    [Theory]
    [InlineData("[wellKnownApp('MicrosoftGraph')]", Graph)]
    [InlineData("[wellKnownApp('microsoftgraph')]", Graph)]
    [InlineData("[wellKnownApp('AzureStorage')]", WellKnownIds.AzureStorageAppId)]
    [InlineData("[toUpper(wellKnownApp('AzureDevOps'))]", "499B84AC-1321-427F-AA17-267CA6975798")]
    public void WellKnownApp_ReturnsTheAppId(string expression, string expected)
    {
        Assert.Equal(expected, Expr.AssertString(Expr.Eval(expression, Context())));
    }

    [Fact]
    public void WellKnownApp_RejectsUnknownNames_ListingTheTable()
    {
        var error = Expr.Fails("[wellKnownApp('Nope')]", DiagnosticCodes.InvalidFunctionArguments, Context());

        Assert.Contains("'Nope' is not a well-known application", error.Message);
        Assert.Contains("MicrosoftGraph", error.Message);
        Assert.Contains("AzureDevOps", error.Message);
        Expr.Fails("[wellKnownApp(1)]", DiagnosticCodes.InvalidFunctionArguments, Context());
        Expr.Fails("[wellKnownApp()]", DiagnosticCodes.InvalidFunctionArguments, Context());
    }

    [Theory]
    [InlineData(EvaluationScope.ParameterDefault)]
    [InlineData(EvaluationScope.Variable)]
    [InlineData(EvaluationScope.UserFunction)]
    [InlineData(EvaluationScope.Output)]
    public void WellKnownApp_IsPure_AndAllowedEverywhere(EvaluationScope scope)
    {
        Assert.Equal(Graph, Expr.AssertString(Expr.Eval("[wellKnownApp('MicrosoftGraph')]", Context(scope: scope))));
        Expr.AssertUnknown(Expr.Eval("[environment()]", Context(scope: scope)));
    }

    // ---- permissionScope / appRole from the tables ----

    [Theory]
    [InlineData("[permissionScope(wellKnownApp('MicrosoftGraph'), 'User.Read')]", UserRead)]
    [InlineData("[permissionScope('00000003-0000-0000-c000-000000000000', 'user.read')]", UserRead)]
    [InlineData("[permissionScope('00000003-0000-0000-C000-000000000000', 'openid')]", "37f7f235-527c-4136-accd-4a02d197296e")]
    [InlineData("[appRole(wellKnownApp('MicrosoftGraph'), 'User.Read.All')]", UserReadAll)]
    [InlineData("[appRole(wellKnownApp('MicrosoftGraph'), 'directory.read.all')]", "7ab1d382-f21e-4acd-a863-ba3e13f7da61")]
    public void GraphPermissions_ResolveOfflineFromTheEmbeddedTables(string expression, string expected)
    {
        Assert.Equal(expected, Expr.AssertString(Expr.Eval(expression, Context())));
    }

    [Fact]
    public void GraphPermissions_NotInTheTable_AreDeployTimeUnknownsOffline()
    {
        var scope = Expr.AssertUnknown(Expr.Eval("[permissionScope(wellKnownApp('MicrosoftGraph'), 'Nope.Scope')]", Context()));
        var role = Expr.AssertUnknown(Expr.Eval("[appRole('" + AppId + "', 'Orders.Admin')]", Context()));

        Assert.Equal("permissionScope('" + Graph + "', 'Nope.Scope')", scope.Symbol);
        Assert.Equal(UnknownOrigins.Deployment, scope.Origins);
        Assert.Equal("appRole('" + AppId + "', 'Orders.Admin')", role.Symbol);
        Assert.Equal(UnknownOrigins.Deployment, role.Origins);
        Assert.Null(role.Source);
    }

    // ---- appRole / permissionScope naming a template resource ----

    [Theory]
    [InlineData("[appRole('app', 'Orders.Admin')]")]
    [InlineData("[appRole('" + AppResourceId + "', 'Orders.Admin')]")]
    [InlineData("[appRole('Microsoft.Entra/applications/app', 'Orders.Admin')]")]
    [InlineData("[appRole(resourceId('Microsoft.Entra/applications', 'app'), 'Orders.Admin')]")]
    public void AppRole_OfATemplateApplication_IsUnknownStatically_WithTheApplicationAsSource(string expression)
    {
        var unknown = Expr.AssertUnknown(Expr.Eval(expression, Context()));

        Assert.Equal("appRole(ref(" + AppResourceId + ").appId, 'Orders.Admin')", unknown.Symbol);
        Assert.Equal(UnknownOrigins.Reference, unknown.Origins);
        Assert.Equal(App, unknown.Source);
        Assert.False(unknown.Tainted);
    }

    [Fact]
    public void AppRole_OfATemplateApplication_ResolvesFromTheLiveObject_WithoutALookup()
    {
        var context = Context(lookup: new InMemoryEntraLookup(), references: Published(LiveApp()));

        Assert.Equal("role-admin", Expr.AssertString(Expr.Eval("[appRole('app', 'Orders.Admin')]", context)));
        Assert.Equal("role-admin", Expr.AssertString(Expr.Eval("[appRole('app', 'orders.admin')]", context)));
        Assert.Equal("scope-read", Expr.AssertString(Expr.Eval("[permissionScope('" + AppResourceId + "', 'Orders.Read')]", context)));
    }

    [Fact]
    public void AppRole_OfATemplateApplication_FallsBackToTheLookup_ForValuesTheObjectLacks()
    {
        var lookup = new InMemoryEntraLookup().AddAppRole(AppId, "Orders.Extra", "role-extra");

        Assert.Equal("role-extra", Expr.AssertString(Expr.Eval("[appRole('app', 'Orders.Extra')]", Context(lookup, Published(LiveApp())))));

        var offline = Expr.AssertUnknown(Expr.Eval("[appRole('app', 'Orders.Missing')]", Context(references: Published(LiveApp()))));
        Assert.Equal("appRole('" + AppId + "', 'Orders.Missing')", offline.Symbol);
        Assert.Equal(UnknownOrigins.Deployment, offline.Origins);

        var notFound = Expr.Fails("[appRole('app', 'Orders.Missing')]", DiagnosticCodes.ExpressionEvaluationFailed, Context(lookup, Published(LiveApp())));
        Assert.Contains("declares no app role 'Orders.Missing'", notFound.Message);
    }

    [Fact]
    public void AppRole_OfALiveObjectWithoutAnAppId_Fails()
    {
        var live = new JsonObject { ["id"] = "A1", ["displayName"] = "App" };

        var error = Expr.Fails("[appRole('app', 'r')]", DiagnosticCodes.ExpressionEvaluationFailed, Context(references: Published(live)));

        Assert.Contains("has no appId", error.Message);
    }

    [Fact]
    public void AppRole_WithAnUnknownAppId_IsUnknownThroughTheNestedReference()
    {
        var unknown = Expr.AssertUnknown(Expr.Eval("[appRole(reference('app').appId, 'Orders.Admin')]", Context()));

        Assert.Equal("appRole(ref(" + AppResourceId + ").appId, 'Orders.Admin')", unknown.Symbol);
        Assert.Equal(UnknownOrigins.Reference, unknown.Origins);
    }

    [Theory]
    [InlineData("[appRole('nope', 'r')]", "must be an application id (GUID) or the name or resource id", true)]
    [InlineData("[appRole('g', 'r')]", "got 'g'", true)]
    [InlineData("[appRole('Microsoft.Entra/groups/g', 'r')]", "(a Microsoft.Entra/groups resource)", true)]
    [InlineData("[permissionScope('/providers/Microsoft.Entra/applications/other', 'r')]", "(not a resource of this template)", true)]
    [InlineData("[permissionScope('/providers/bogus', 'r')]", "(not a valid resource id)", true)]
    [InlineData("[appRole(1, 'r')]", "argument 1 must be a string", false)]
    [InlineData("[appRole('app', 2)]", "argument 2 must be a string", false)]
    public void AppRole_RejectsBadTargets(string expression, string messageFragment, bool suggestsWellKnownApp)
    {
        var error = Expr.Fails(expression, DiagnosticCodes.InvalidFunctionArguments, Context());

        Assert.Contains(messageFragment, error.Message);
        Assert.Equal(suggestsWellKnownApp, error.Message.Contains("use wellKnownApp('Name')", StringComparison.Ordinal));
    }

    [Fact]
    public void AppRole_WithANameSharedByAnApplicationAndAServicePrincipal_IsAmbiguous()
    {
        var context = Context(resources: Expr.Index(("Microsoft.Entra/applications", "x"), ("Microsoft.Entra/servicePrincipals", "x")));

        Expr.Fails("[appRole('x', 'r')]", DiagnosticCodes.AmbiguousReference, context);
    }

    // ---- the tenant lookup ----

    [Fact]
    public void AppRole_ByAppId_ResolvesThroughTheLookup()
    {
        var lookup = new InMemoryEntraLookup()
            .AddAppRole(AppId, "Orders.Admin", "role-admin")
            .AddPermissionScope(AppId, "Orders.Read", "scope-read");

        Assert.Equal("role-admin", Expr.AssertString(Expr.Eval("[appRole('" + AppId + "', 'Orders.Admin')]", Context(lookup))));
        Assert.Equal("scope-read", Expr.AssertString(Expr.Eval("[permissionScope(parameters('plainAppId'), 'orders.read')]", Context(lookup))));
        Assert.Equal(UserRead, Expr.AssertString(Expr.Eval("[permissionScope(wellKnownApp('MicrosoftGraph'), 'User.Read')]", Context(lookup))));

        var unknownApp = Expr.Fails("[appRole('" + OtherAppId + "', 'Orders.Admin')]", DiagnosticCodes.ExpressionEvaluationFailed, Context(lookup));
        Assert.Contains("no service principal or application with appId '" + OtherAppId + "'", unknownApp.Message);
        var unknownValue = Expr.Fails("[permissionScope('" + AppId + "', 'Orders.Write')]", DiagnosticCodes.ExpressionEvaluationFailed, Context(lookup));
        Assert.Contains("declares no permission scope 'Orders.Write'", unknownValue.Message);
    }

    [Fact]
    public void AppRole_LookupTransportFailure_IsReportedAsEvaluationFailure()
    {
        var error = Expr.Fails("[appRole('" + AppId + "', 'r')]", DiagnosticCodes.ExpressionEvaluationFailed, Context(new ThrowingLookup()));

        Assert.Contains("the tenant lookup failed: boom", error.Message);
    }

    // ---- roleTemplate ----

    [Theory]
    [InlineData("[roleTemplate('Global Reader')]", GlobalReader)]
    [InlineData("[roleTemplate('global reader')]", GlobalReader)]
    [InlineData("[roleTemplate('Directory Readers')]", DirectoryReaders)]
    [InlineData("[roleTemplate('11111111-1111-1111-1111-111111111111')]", "11111111-1111-1111-1111-111111111111")]
    [InlineData("[roleTemplate('F2EF992C-3AFB-46B9-B7CF-A126EE74C451')]", "F2EF992C-3AFB-46B9-B7CF-A126EE74C451")]
    public void RoleTemplate_ResolvesBuiltInNames_AndPassesGuidsThrough(string expression, string expected)
    {
        Assert.Equal(expected, Expr.AssertString(Expr.Eval(expression, Context())));
    }

    [Fact]
    public void RoleTemplate_CustomRoles_GoThroughTheLookup()
    {
        var lookup = new InMemoryEntraLookup().AddRoleDefinition("Orders Support", "RD1");

        Assert.Equal("RD1", Expr.AssertString(Expr.Eval("[roleTemplate('Orders Support')]", Context(lookup))));

        var offline = Expr.AssertUnknown(Expr.Eval("[roleTemplate('Orders Support')]", Context()));
        Assert.Equal("roleTemplate('Orders Support')", offline.Symbol);
        Assert.Equal(UnknownOrigins.Deployment, offline.Origins);

        var notFound = Expr.Fails("[roleTemplate('Nope Role')]", DiagnosticCodes.ExpressionEvaluationFailed, Context(lookup));
        Assert.Contains("no role definition named 'Nope Role'", notFound.Message);
        Assert.Contains("Built-in roles are matched by display name", notFound.Message);
        Assert.Contains("reference('<name>').id", notFound.Message);
        Expr.Fails("[roleTemplate(1)]", DiagnosticCodes.InvalidFunctionArguments, Context());
    }

    // ---- phase rules ----

    public static TheoryData<string, EvaluationScope, string> ResourceScopeOnlyCases()
    {
        var data = new TheoryData<string, EvaluationScope, string>();
        foreach (var expression in new[] { "[appRole('" + Graph + "', 'User.Read.All')]", "[permissionScope('" + Graph + "', 'User.Read')]", "[roleTemplate('Global Reader')]", "[roleDefinitions('Global Reader')]" })
        {
            data.Add(expression, EvaluationScope.ParameterDefault, "a parameter default value");
            data.Add(expression, EvaluationScope.Variable, "a variable");
            data.Add(expression, EvaluationScope.UserFunction, "a user-defined function");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ResourceScopeOnlyCases))]
    public void TenantDependentFunctions_AreRejectedOutsideResourcesAndOutputs(string expression, EvaluationScope scope, string where)
    {
        var error = Expr.Fails(expression, DiagnosticCodes.FunctionNotAllowedHere, Context(scope: scope));

        Assert.Contains("cannot be used in " + where, error.Message);
        Assert.Contains("it needs the tenant", error.Message);
        Assert.Equal(1, error.Offset);
    }

    [Fact]
    public void TenantDependentFunctions_AreAllowedInOutputs()
    {
        Assert.Equal(UserReadAll, Expr.AssertString(Expr.Eval("[appRole('" + Graph + "', 'User.Read.All')]", Context(scope: EvaluationScope.Output))));
        Assert.Equal(GlobalReader, Expr.AssertString(Expr.Eval("[roleTemplate('Global Reader')]", Context(scope: EvaluationScope.Output))));
    }

    [Fact]
    public void Validator_ReportsTenantDependentFunctionsInVariablesAndDefaults()
    {
        var analysis = new ExpressionValidator().Analyze(Expr.ParseBody(
            "\"parameters\": { \"p\": { \"type\": \"string\", \"defaultValue\": \"[roleTemplate('Global Reader')]\" } }, "
            + "\"variables\": { \"v\": \"[appRole('" + Graph + "', 'User.Read.All')]\", \"ok\": \"[wellKnownApp('MicrosoftGraph')]\" }, "
            + "\"resources\": [ { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"g\", \"properties\": { \"x\": \"[tenant().tenantId]\" } } ]"));

        var codes = analysis.Diagnostics.Select(d => (d.Code, d.Path!.ToString())).ToList();
        Assert.Contains((DiagnosticCodes.FunctionNotAllowedHere, "parameters.p.defaultValue"), codes);
        Assert.Contains((DiagnosticCodes.FunctionNotAllowedHere, "variables.v"), codes);
        Assert.DoesNotContain(codes, c => c.Item2 == "variables.ok");
        Assert.DoesNotContain(codes, c => c.Item2.StartsWith("resources", StringComparison.Ordinal));
    }

    // ---- redaction ----

    [Fact]
    public void SecureArguments_NeverReachMessagesOrSymbols()
    {
        var rejected = Expr.Fails("[appRole(parameters('pw'), 'r')]", DiagnosticCodes.InvalidFunctionArguments, Context());
        Assert.Contains("got ***", rejected.Message);
        Assert.DoesNotContain(Secret, rejected.Message);

        var placeholder = Expr.AssertUnknown(Expr.Eval("[appRole(parameters('secureAppId'), 'r')]", Context()));
        Assert.Equal("appRole(***, 'r')", placeholder.Symbol);
        Assert.True(placeholder.Tainted);
        Assert.Equal(UnknownOrigins.Deployment, placeholder.Origins);

        var secureValue = Expr.AssertUnknown(Expr.Eval("[appRole(wellKnownApp('MicrosoftGraph'), parameters('pw'))]", Context()));
        Assert.Equal("appRole('" + Graph + "', ***)", secureValue.Symbol);
        Assert.True(secureValue.Tainted);

        var notFound = Expr.Fails("[appRole(parameters('secureAppId'), 'r')]", DiagnosticCodes.ExpressionEvaluationFailed, Context(new InMemoryEntraLookup()));
        Assert.Contains("no match was found for the secure arguments", notFound.Message);
        Assert.DoesNotContain(AppId, notFound.Message);

        var role = Expr.Fails("[roleTemplate(parameters('pw'))]", DiagnosticCodes.ExpressionEvaluationFailed, Context(new InMemoryEntraLookup()));
        Assert.Contains("no role definition matches the secure argument", role.Message);
        Assert.DoesNotContain(Secret, role.Message);
        Assert.Equal("roleTemplate(***)", Expr.AssertUnknown(Expr.Eval("[roleTemplate(parameters('pw'))]", Context())).Symbol);

        var transport = Expr.Fails("[appRole(parameters('secureAppId'), 'r')]", DiagnosticCodes.ExpressionEvaluationFailed, Context(new ThrowingLookup()));
        Assert.Equal("appRole(): the tenant lookup failed.", transport.Message);
    }

    // ---- tenant() / environment() ----

    [Fact]
    public void TenantAndEnvironment_ReturnTheDeploymentInfo_WhenSupplied()
    {
        var deployment = new DeploymentInfo("d", "sha256:x", "11111111-1111-1111-1111-111111111111", "Incremental")
        {
            Tenant = new JsonObject { ["tenantId"] = "11111111-1111-1111-1111-111111111111", ["defaultDomain"] = "contoso.com", ["displayName"] = "Contoso" },
            Environment = new JsonObject { ["name"] = "AzureCloud", ["graph"] = "https://graph.microsoft.com", ["authentication"] = new JsonObject { ["loginEndpoint"] = "https://login.microsoftonline.com/" } },
        };
        var context = Context(deployment: deployment);

        Assert.Equal("11111111-1111-1111-1111-111111111111", Expr.AssertString(Expr.Eval("[tenant().tenantId]", context)));
        Assert.Equal("contoso.com", Expr.AssertString(Expr.Eval("[tenant().defaultDomain]", context)));
        Assert.Equal("Contoso", Expr.AssertString(Expr.Eval("[tenant().displayName]", context)));
        Assert.Equal("AzureCloud", Expr.AssertString(Expr.Eval("[environment().name]", context)));
        Assert.Equal("https://login.microsoftonline.com/", Expr.AssertString(Expr.Eval("[environment().authentication.loginEndpoint]", context)));
        Assert.Equal("https://graph.microsoft.com", Expr.AssertString(Expr.Eval("[environment().graph]", context)));
        Assert.Equal("d", Expr.AssertString(Expr.Eval("[deployment().name]", context)));

        // Each call hands out a copy: mutating the result does not change later calls.
        var tenant = Assert.IsType<JsonObject>(Expr.Eval("[tenant()]", context));
        tenant["displayName"] = "tampered";
        Assert.Equal("Contoso", Expr.AssertString(Expr.Eval("[tenant().displayName]", context)));
    }

    [Fact]
    public void TenantAndEnvironment_AreDeployTimeUnknownsOffline()
    {
        var tenant = Expr.AssertUnknown(Expr.Eval("[tenant()]", Context()));
        var tenantId = Expr.AssertUnknown(Expr.Eval("[tenant().tenantId]", Context()));
        var environment = Expr.AssertUnknown(Expr.Eval("[environment().name]", Context()));

        Assert.Equal("tenant()", tenant.Symbol);
        Assert.Equal(UnknownOrigins.Deployment, tenant.Origins);
        Assert.Equal("tenant().tenantId", tenantId.Symbol);
        Assert.Equal("environment().name", environment.Symbol);
        Assert.Equal(UnknownOrigins.Deployment, environment.Origins);
        Expr.Fails("[tenant('x')]", DiagnosticCodes.InvalidFunctionArguments, Context());
    }

    // ---- the reference scanner ----

    [Fact]
    public void Scanner_RecordsATemplateApplicationNamedByAppRole_AsAnImplicitDependency()
    {
        var analysis = new ExpressionValidator().Analyze(Expr.ParseBody(
            "\"resources\": [ { \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"app\", \"properties\": { \"displayName\": \"A\" } }, "
            + "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"g\", \"properties\": { \"displayName\": \"G\", \"mailEnabled\": false, \"securityEnabled\": true, "
            + "\"description\": \"[appRole('app', 'Orders.Admin')]\", \"notes\": \"[permissionScope(resourceId('Microsoft.Entra/applications', 'app'), 'Orders.Read')]\", "
            + "\"graph\": \"[appRole(wellKnownApp('MicrosoftGraph'), 'User.Read.All')]\", \"other\": \"[appRole('" + AppId + "', 'X')]\" } } ]"));

        Assert.Empty(analysis.Diagnostics);
        var group = analysis.Static.Resources[1];
        var occurrences = group.References.Where(o => o.Kind == ReferenceOccurrenceKind.Reference && App.Equals(o.Target)).ToList();
        Assert.Equal(2, occurrences.Count);
        Assert.Contains(occurrences, o => o.ExpressionText.StartsWith("appRole('app'", StringComparison.Ordinal) && o.Path.ToString() == "resources[1].properties.description");
        Assert.Contains(occurrences, o => o.ExpressionText.StartsWith("permissionScope(", StringComparison.Ordinal) && o.Path.ToString() == "resources[1].properties.notes");
        Assert.DoesNotContain(group.References, o => o.Path.ToString() is "resources[1].properties.graph" or "resources[1].properties.other");
    }

    [Fact]
    public void Scanner_ReportsAnAppRoleTargetThatIsNotATemplateApplication()
    {
        var analysis = new ExpressionValidator().Analyze(Expr.ParseBody(
            "\"resources\": [ { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"g\", \"properties\": { \"x\": \"[appRole('nope', 'r')]\" } } ]"));

        var error = Assert.Single(analysis.Diagnostics);
        Assert.Equal(DiagnosticCodes.InvalidFunctionArguments, error.Code);
        Assert.Equal("resources[0].properties.x", error.Path?.ToString());
        Assert.Contains("'nope'", error.Message);
    }

    /// <summary>A lookup whose transport fails (a faulted task, as an async implementation over Graph would produce).</summary>
    private sealed class ThrowingLookup : IEntraLookup
    {
        public System.Threading.Tasks.Task<EntraLookupResult> FindAppRoleIdAsync(string appId, string value)
        {
            return System.Threading.Tasks.Task.FromException<EntraLookupResult>(new InvalidOperationException("boom"));
        }

        public System.Threading.Tasks.Task<EntraLookupResult> FindPermissionScopeIdAsync(string appId, string value)
        {
            return System.Threading.Tasks.Task.FromException<EntraLookupResult>(new InvalidOperationException("boom"));
        }

        public System.Threading.Tasks.Task<EntraLookupResult> FindRoleDefinitionIdAsync(string displayName)
        {
            return System.Threading.Tasks.Task.FromException<EntraLookupResult>(new InvalidOperationException("boom"));
        }
    }
}
