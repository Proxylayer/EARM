using System;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Engine;
using EArm.Engine.Edges;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Providers.Edges;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;
using static EArm.Providers.Tests.Infrastructure.DirectoryTemplates;

namespace EArm.Providers.Tests;

/// <summary>
/// The principal-side <c>appRoleAssignments</c> edge kind (users, groups, service principals) and the standalone
/// <c>Microsoft.Entra/appRoleAssignments</c> provider driven through the engine: create / rerun / undeclare, every
/// invalid-item message, the principal-side read, the already-exists adoption, delete tolerance and the recreate plan.
/// </summary>
public sealed class AppRoleAssignmentScenarioTests
{
    private const string AssignmentType = AppRoleAssignmentsProvider.ResourceType;
    private const string ResourceAppId = "11111111-2222-3333-4444-555555555555";
    private const string UserRoleId = "6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0001";
    private const string AppRoleIdOnly = "6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0002";
    private const string DisabledRoleId = "6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0003";
    private const string BothRoleId = "6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0004";

    private sealed record Tenant(ProviderHost Host, string ResourceId);

    private static Tenant NewTenant()
    {
        var host = new ProviderHost();
        string resourceId = host.Directory.AddServicePrincipal(ResourceAppId, "Orders API", sp => sp["appRoles"] = new JsonArray(
            Role(UserRoleId, "Orders.UserOnly", true, "User"),
            Role(AppRoleIdOnly, "Orders.AppOnly", true, "Application"),
            Role(DisabledRoleId, "Orders.Disabled", false, "User", "Application"),
            Role(BothRoleId, "Orders.Both", true, "User", "Application")))["id"]!.GetValue<string>();
        return new Tenant(host, resourceId);
    }

    private static JsonObject Role(string id, string value, bool enabled, params string[] memberTypes)
    {
        return new JsonObject
        {
            ["id"] = id,
            ["value"] = value,
            ["isEnabled"] = enabled,
            ["allowedMemberTypes"] = new JsonArray(memberTypes.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray()),
        };
    }

    private static string Assignments(params string[] items)
    {
        return "\"appRoleAssignments\": [" + string.Join(", ", items) + "]";
    }

    private static string Item(string resourceAppId, string appRole)
    {
        return "{ \"resourceAppId\": \"" + resourceAppId + "\", \"appRole\": \"" + appRole + "\" }";
    }

    private static string ById(string resourceId, string appRoleId)
    {
        return "{ \"resourceId\": \"" + resourceId + "\", \"appRoleId\": \"" + appRoleId + "\" }";
    }

    private static string AssignedTo(string resourceId)
    {
        return "/servicePrincipals/" + resourceId + "/appRoleAssignedTo";
    }

    // ---- edge: create / rerun / undeclare ----

    [Fact]
    public async Task User_AssignedASugarRole_PostsOnceUnderTheResource()
    {
        var t = NewTenant();

        var run = await t.Host.CreateAsync(t.Host.Resources(Alice(Assignments(Item(ResourceAppId, "Orders.UserOnly")))));

        Check.Succeeded(run);
        string aliceId = t.Host.IdOf("/users", "userPrincipalName", "alice@contoso.com");
        var post = Assert.Single(run.Writes, w => w.Method == "POST" && w.Path == AssignedTo(t.ResourceId));
        Assert.Equal(aliceId, Check.Body(post)["principalId"]!.GetValue<string>());
        Assert.Equal(t.ResourceId, Check.Body(post)["resourceId"]!.GetValue<string>());
        Assert.Equal(UserRoleId, Check.Body(post)["appRoleId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Rerun_WritesNothing_AndUndeclaringAnItem_RemovesNothing()
    {
        var t = NewTenant();
        var document = t.Host.Resources(Alice(Assignments(Item(ResourceAppId, "Orders.Both"), ById(t.ResourceId, UserRoleId))));
        Check.Succeeded(await t.Host.CreateAsync(document));
        Assert.Equal(2, t.Host.Directory.Count(AssignedTo(t.ResourceId)));

        var rerun = await t.Host.CreateAsync(document);

        Check.Succeeded(rerun);
        Check.NoWrites(rerun);

        // appRoleAssignments on the principal are ensure-only: what is no longer declared stays.
        var onlyOne = await t.Host.CreateAsync(t.Host.Resources(Alice(Assignments(ById(t.ResourceId, UserRoleId)))));

        Check.Succeeded(onlyOne);
        Check.NoWrites(onlyOne);
        Assert.Equal(2, t.Host.Directory.Count(AssignedTo(t.ResourceId)));

        var none = await t.Host.CreateAsync(t.Host.Resources(Alice()));
        Check.Succeeded(none);
        Check.NoWrites(none);
        Assert.Equal(2, t.Host.Directory.Count(AssignedTo(t.ResourceId)));
    }

    [Fact]
    public async Task TheSameRoleDeclaredBySugarAndById_IsOnePost()
    {
        var t = NewTenant();

        var run = await t.Host.CreateAsync(t.Host.Resources(Alice(Assignments(Item(ResourceAppId, "Orders.UserOnly"), ById(t.ResourceId, UserRoleId.ToUpperInvariant())))));

        Check.Succeeded(run);
        Assert.Single(run.Writes, w => w.Method == "POST" && w.Path == AssignedTo(t.ResourceId));
        Assert.Single(t.Host.Directory.Objects(AssignedTo(t.ResourceId)));
    }

    [Fact]
    public async Task GroupAndServicePrincipal_GetTheirOwnMemberTypeChecks()
    {
        var t = NewTenant();
        string clientAppId = Guid.NewGuid().ToString("D");
        t.Host.Directory.AddServicePrincipal(clientAppId, "Client");
        string group = Group(Assignments(Item(ResourceAppId, "Orders.UserOnly")));
        string sp = "{ \"type\": \"Microsoft.Entra/servicePrincipals\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + clientAppId + "\", \"properties\": { " + Assignments(Item(ResourceAppId, "Orders.AppOnly")) + " } }";

        var run = await t.Host.CreateAsync(t.Host.Resources(Join(group, sp)));

        Check.Succeeded(run);
        Assert.Equal(2, t.Host.Directory.Count(AssignedTo(t.ResourceId)));
        var posts = run.Writes.Where(w => w.Method == "POST" && w.Path == AssignedTo(t.ResourceId)).ToList();
        Assert.Contains(posts, p => Check.Body(p)["appRoleId"]!.GetValue<string>() == UserRoleId);
        Assert.Contains(posts, p => Check.Body(p)["appRoleId"]!.GetValue<string>() == AppRoleIdOnly);

        var rerun = await t.Host.CreateAsync(t.Host.Resources(Join(group, sp)));
        Check.Succeeded(rerun);
        Check.NoWrites(rerun);
    }

    [Fact]
    public async Task TheDefaultAccessRole_NeedsNoAppRoleDefinition()
    {
        var t = NewTenant();

        var run = await t.Host.CreateAsync(t.Host.Resources(Alice(Assignments(ById(t.ResourceId, AppRoleAssignmentsRules.DefaultAccessRoleId)))));

        Check.Succeeded(run);
        Assert.Equal(AppRoleAssignmentsRules.DefaultAccessRoleId, Check.Body(Assert.Single(run.Writes, w => w.Method == "POST" && w.Path == AssignedTo(t.ResourceId)))["appRoleId"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheResourceServicePrincipal_DeployedInTheSameTemplate_IsAnsweredWithoutAGraphLookup()
    {
        var host = new ProviderHost();
        const string app = """
            { "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "name": "orders-api",
              "properties": { "displayName": "Orders API", "appRoles": [ { "id": "6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0001", "value": "Orders.Read", "displayName": "Read", "description": "Read orders", "isEnabled": true, "allowedMemberTypes": ["User", "Application"] } ] } }
            """;
        const string sp = """
            { "type": "Microsoft.Entra/servicePrincipals", "apiVersion": "2026-10-01-preview", "name": "[reference('orders-api').appId]",
              "properties": { }, "dependsOn": ["[resourceId('Microsoft.Entra/applications', 'orders-api')]"] }
            """;
        string alice = Alice(Assignments("{ \"resourceAppId\": \"[reference('orders-api').appId]\", \"appRole\": \"Orders.Read\" }"));

        var whatIf = await host.WhatIfAsync(host.Resources(Join(app, sp, alice)));

        Assert.All(whatIf.Plan.Changes, c => Assert.False(c.HasError, c.UnsupportedReason));
        Check.NoWrites(whatIf);

        var run = await host.CreateAsync(host.Resources(Join(app, sp, alice)));

        Check.Succeeded(run);
        string spId = host.IdOf("/servicePrincipals", "displayName", "Orders API");
        Assert.Single(host.Directory.Objects(AssignedTo(spId)));
        Assert.DoesNotContain(run.Requests, r => r.Method == "GET" && r.Path.StartsWith("/servicePrincipals(appId=", StringComparison.Ordinal) && r.Path.Contains("orders", StringComparison.OrdinalIgnoreCase));
    }

    // ---- edge: invalid items ----

    [Theory]
    [InlineData("sugar-unknown-role", "defines no app role with value 'Nope'")]
    [InlineData("disabled-role", "is disabled")]
    [InlineData("app-only-on-user", "cannot be assigned to a user or group (User)")]
    [InlineData("unknown-id-role", "has no app role with id")]
    [InlineData("role-conflict", "differs from the declared appRoleId")]
    [InlineData("unknown-app", "no service principal exists for application id")]
    [InlineData("unknown-resource-id", "is not an existing service principal")]
    [InlineData("resource-mismatch", "differs from the declared resourceId")]
    public async Task InvalidItems_FailTheDeclarerWithEdgeValidationFailed_AndWriteNothing(string scenario, string expected)
    {
        var t = NewTenant();
        string other = Guid.NewGuid().ToString("D");
        t.Host.Directory.AddServicePrincipal(other, "Other API");
        string item = scenario switch
        {
            "sugar-unknown-role" => Item(ResourceAppId, "Nope"),
            "disabled-role" => Item(ResourceAppId, "Orders.Disabled"),
            "app-only-on-user" => Item(ResourceAppId, "Orders.AppOnly"),
            "unknown-id-role" => ById(t.ResourceId, Guid.NewGuid().ToString("D")),
            "role-conflict" => "{ \"resourceId\": \"" + t.ResourceId + "\", \"appRole\": \"Orders.UserOnly\", \"appRoleId\": \"" + BothRoleId + "\" }",
            "unknown-app" => Item(Guid.NewGuid().ToString("D"), "Orders.UserOnly"),
            "unknown-resource-id" => ById(Guid.NewGuid().ToString("D"), UserRoleId),
            "resource-mismatch" => "{ \"resourceAppId\": \"" + other + "\", \"resourceId\": \"" + t.ResourceId + "\", \"appRole\": \"Orders.UserOnly\" }",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        var run = await t.Host.CreateAsync(t.Host.Resources(Alice(Assignments(item))));

        Assert.False(run.Succeeded);
        var op = run.OpOfType("Microsoft.Entra/users");
        Assert.Equal(OperationStatus.Failed, op.Status);
        Assert.Equal(EngineDiagnosticCodes.EdgeValidationFailed, op.ErrorCode);
        Assert.Contains(expected, op.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(run.Writes, w => w.Path.Contains("appRoleAssignedTo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WrongShapes_AreStoppedByTheSchema_BeforeAnyGraphCall()
    {
        var t = NewTenant();
        string withPrincipal = "{ \"principalId\": \"" + Guid.NewGuid() + "\", \"resourceAppId\": \"" + ResourceAppId + "\", \"appRole\": \"Orders.UserOnly\" }";

        var notAnObject = await Assert.ThrowsAsync<ValidationFailedException>(() => t.Host.CreateAsync(t.Host.Resources(Alice(Assignments("\"just-a-string\"")))));
        var principalSide = await Assert.ThrowsAsync<ValidationFailedException>(() => t.Host.CreateAsync(t.Host.Resources(Alice(Assignments(withPrincipal)))));

        Assert.Contains(notAnObject.Diagnostics, d => d.Code == "SchemaViolation");
        Assert.Contains(principalSide.Diagnostics, d => d.Code == "SchemaViolation");
        Assert.Empty(t.Host.Fake.Writes);
    }

    [Fact]
    public async Task EdgeKind_ResolveItem_ReportsShapeErrorsTheSchemaCannotSee()
    {
        var t = NewTenant();
        var handler = new AppRoleAssignmentEdgeKind();
        var declarer = await InstanceOf(t.Host, Alice(null));
        var context = Context(t.Host);

        var notAnObject = await handler.ResolveItemAsync(JsonValue.Create("just-a-string"), EdgeSide.Principal, declarer, GraphApi.V1, context, ProviderHost.Ct);
        var withPrincipal = await handler.ResolveItemAsync(JsonNode.Parse("{ \"principalId\": \"" + Guid.NewGuid() + "\", \"resourceAppId\": \"" + ResourceAppId + "\", \"appRole\": \"Orders.UserOnly\" }"), EdgeSide.Principal, declarer, GraphApi.V1, context, ProviderHost.Ct);
        var badGuid = await handler.ResolveItemAsync(JsonNode.Parse("{ \"resourceId\": \"nope\", \"appRole\": \"Orders.UserOnly\" }"), EdgeSide.Principal, declarer, GraphApi.V1, context, ProviderHost.Ct);
        var noRole = await handler.ResolveItemAsync(JsonNode.Parse("{ \"resourceId\": \"" + t.ResourceId + "\" }"), EdgeSide.Principal, declarer, GraphApi.V1, context, ProviderHost.Ct);
        var valid = await handler.ResolveItemAsync(JsonNode.Parse("{ \"resourceAppId\": \"" + ResourceAppId + "\", \"appRole\": \"Orders.UserOnly\" }"), EdgeSide.Principal, declarer, GraphApi.V1, context, ProviderHost.Ct);

        Assert.Contains("expected an object", notAnObject.Error, StringComparison.Ordinal);
        Assert.Contains("principalId is implied", withPrincipal.Error, StringComparison.Ordinal);
        Assert.Contains("is not a GUID", badGuid.Error, StringComparison.Ordinal);
        Assert.Contains("needs the resource", noRole.Error, StringComparison.Ordinal);
        Assert.Null(valid.Error);
        Assert.Equal(AppRoleAssignmentsRules.TargetKey(t.ResourceId, UserRoleId), valid.OtherId);
        Assert.Equal("servicePrincipals", valid.TargetCollection);
    }

    // ---- edge kind unit-level ----

    [Fact]
    public async Task EdgeKind_BuildsRequests_ThroughItsContractMethods()
    {
        var t = NewTenant();
        Check.Succeeded(await t.Host.CreateAsync(t.Host.Resources(Alice(Assignments(Item(ResourceAppId, "Orders.UserOnly"))))));
        string aliceId = t.Host.IdOf("/users", "userPrincipalName", "alice@contoso.com");
        var handler = new AppRoleAssignmentEdgeKind();
        string key = AppRoleAssignmentsRules.TargetKey(t.ResourceId, UserRoleId);
        var edge = new Edge(EdgeKind.AppRoleAssignment, key, aliceId, "servicePrincipals", null);

        // Identity / description / graph ids come from the composite target key.
        Assert.Equal(AppRoleAssignmentsRules.Identity(aliceId, t.ResourceId, UserRoleId), handler.IdentityOf(edge));
        Assert.Equal([t.ResourceId, aliceId], handler.GraphIdsOf(edge));
        Assert.Contains("assigned to " + aliceId, handler.Describe(edge), StringComparison.Ordinal);

        // A plain (non-composite) target without extras carries no composite at all.
        var broken = new Edge(EdgeKind.AppRoleAssignment, Guid.NewGuid().ToString("D"), aliceId, "servicePrincipals", null);
        Assert.Throws<InvalidOperationException>(() => handler.IdentityOf(broken));

        // The composite may also come from the extras of a plain target.
        var viaExtra = new Edge(EdgeKind.AppRoleAssignment, t.ResourceId, aliceId, "servicePrincipals", new JsonObject { ["appRoleId"] = UserRoleId });
        Assert.Equal(handler.IdentityOf(edge), handler.IdentityOf(viaExtra));

        // Add / remove requests.
        var add = handler.AddRequest(edge, GraphApi.V1);
        Assert.Equal("POST", add.Method);
        Assert.Equal(AssignedTo(t.ResourceId), add.Path);
        Assert.Equal(aliceId, add.Body!["principalId"]!.GetValue<string>());
        var remove = handler.RemoveRequest(edge, new JsonObject { ["id"] = "assignment-1" }, GraphApi.V1);
        Assert.Equal("DELETE", remove.Method);
        Assert.EndsWith("/assignment-1", remove.Path, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => handler.RemoveRequest(edge, null, GraphApi.V1));
        var withExtraId = edge with { Extra = new JsonObject { ["id"] = "from-extra" } };
        Assert.EndsWith("/from-extra", handler.RemoveRequest(withExtraId, null, GraphApi.V1).Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EdgeKind_ReadPrincipal_AnswersEmptyForPrincipalsThatCannotHoldAssignments()
    {
        var t = NewTenant();
        var handler = new AppRoleAssignmentEdgeKind();

        var state = await handler.ReadPrincipalAsync(Guid.NewGuid().ToString("D"), "Microsoft.Entra/applications", GraphApi.V1, Context(t.Host), ProviderHost.Ct);

        Assert.True(state.Exists);
        Assert.Empty(state.Edges!);
        Assert.DoesNotContain(t.Host.Fake.Requests, r => r.Path.Contains("appRoleAssignments", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EdgeKind_ReadTarget_HandlesMalformedKeysMissingResourcesAndPrincipalIndex()
    {
        var t = NewTenant();
        Check.Succeeded(await t.Host.CreateAsync(t.Host.Resources(Alice(Assignments(Item(ResourceAppId, "Orders.UserOnly"))))));
        string aliceId = t.Host.IdOf("/users", "userPrincipalName", "alice@contoso.com");
        var handler = new AppRoleAssignmentEdgeKind();
        var context = Context(t.Host);
        var ct = ProviderHost.Ct;

        Assert.Same(EdgeTargetState.Missing, await handler.ReadTargetAsync("not-a-composite", null, true, GraphApi.V1, context, ct));
        Assert.Same(EdgeTargetState.Missing, await handler.ReadTargetAsync(AppRoleAssignmentsRules.TargetKey(Guid.NewGuid().ToString("D"), UserRoleId), null, true, GraphApi.V1, context, ct));

        var existing = await handler.ReadTargetAsync(AppRoleAssignmentsRules.TargetKey(t.ResourceId, UserRoleId), null, true, GraphApi.V1, context, ct);
        Assert.True(existing.Exists);
        Assert.Contains(existing.Edges!.Keys, k => string.Equals(k, aliceId, StringComparison.OrdinalIgnoreCase));

        var withoutEdges = await handler.ReadTargetAsync(AppRoleAssignmentsRules.TargetKey(t.ResourceId, UserRoleId), null, false, GraphApi.V1, context, ct);
        Assert.True(withoutEdges.Exists);
        Assert.Null(withoutEdges.Edges);
    }

    [Fact]
    public async Task EdgeKind_TargetSideItems_AreInvalid_AndUnknownItemsPassThrough()
    {
        var t = NewTenant();
        var handler = new AppRoleAssignmentEdgeKind();
        var instance = await InstanceOf(t.Host, Alice(null));
        var context = Context(t.Host);

        var target = await handler.ResolveItemAsync(JsonNode.Parse("{}"), EdgeSide.Target, instance, GraphApi.V1, context, ProviderHost.Ct);
        Assert.NotNull(target.Error);
        Assert.Contains("no target-side property", target.Error, StringComparison.Ordinal);

        var unknown = await handler.ResolveItemAsync(EArm.Expressions.Values.UnknownJson.Create("reference('x').id", EArm.Expressions.Values.UnknownOrigins.Reference), EdgeSide.Principal, instance, GraphApi.V1, context, ProviderHost.Ct);
        Assert.True(unknown.IsUnknown);
    }

    // ---- standalone provider ----

    [Fact]
    public async Task Standalone_ByResourceAppIdAndAppRole_ResolvesTheSugar_AndWritesTheComposite()
    {
        var t = NewTenant();
        string principalId = t.Host.Directory.AddUser("bob@contoso.com", "Bob")["id"]!.GetValue<string>();
        string template = Templates.Resource(AssignmentType, Guid.NewGuid().ToString("D"), "{ \"principalId\": \"" + principalId + "\", \"resourceAppId\": \"" + ResourceAppId + "\", \"appRole\": \"Orders.UserOnly\" }");

        var whatIf = await t.Host.WhatIfAsync(t.Host.Resources(template));
        Assert.False(whatIf.Plan.Changes.Single().HasError, whatIf.Plan.Changes.Single().UnsupportedReason);

        var run = await t.Host.CreateAsync(t.Host.Resources(template));

        Check.Succeeded(run);
        var assignment = Assert.Single(t.Host.Directory.Objects(AssignedTo(t.ResourceId)));
        Assert.Equal(assignment["id"]!.GetValue<string>(), run.OpOfType(AssignmentType).GraphId);
        Assert.Equal(principalId, assignment["principalId"]!.GetValue<string>());
        Assert.Equal(t.ResourceId, assignment["resourceId"]!.GetValue<string>());
        Assert.Equal(UserRoleId, assignment["appRoleId"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("unknown-app", "no service principal exists for application id")]
    [InlineData("missing-resource", "does not exist")]
    [InlineData("unknown-role", "defines no app role with value")]
    [InlineData("disabled", "is disabled")]
    [InlineData("wrong-kind", "cannot be assigned to a service principal")]
    [InlineData("mismatch", "differs from the declared resourceId")]
    [InlineData("bad-shape", "needs the resource")]
    public async Task Standalone_InvalidDeclarations_AreUnsupportedRows_AndFailedCreates(string scenario, string expected)
    {
        var t = NewTenant();
        string other = Guid.NewGuid().ToString("D");
        t.Host.Directory.AddServicePrincipal(other, "Other API");
        string appPrincipal = t.Host.Directory.AddServicePrincipal(Guid.NewGuid().ToString("D"), "Client")["id"]!.GetValue<string>();
        string user = t.Host.Directory.AddUser("bob@contoso.com", "Bob")["id"]!.GetValue<string>();
        string props = scenario switch
        {
            "unknown-app" => "{ \"principalId\": \"" + user + "\", \"resourceAppId\": \"" + Guid.NewGuid() + "\", \"appRole\": \"Orders.UserOnly\" }",
            "missing-resource" => "{ \"principalId\": \"" + user + "\", \"resourceId\": \"" + Guid.NewGuid() + "\", \"appRoleId\": \"" + UserRoleId + "\" }",
            "unknown-role" => "{ \"principalId\": \"" + user + "\", \"resourceAppId\": \"" + ResourceAppId + "\", \"appRole\": \"Nope\" }",
            "disabled" => "{ \"principalId\": \"" + user + "\", \"resourceAppId\": \"" + ResourceAppId + "\", \"appRole\": \"Orders.Disabled\" }",
            "wrong-kind" => "{ \"principalId\": \"" + appPrincipal + "\", \"resourceAppId\": \"" + ResourceAppId + "\", \"appRole\": \"Orders.UserOnly\" }",
            "mismatch" => "{ \"principalId\": \"" + user + "\", \"resourceAppId\": \"" + other + "\", \"resourceId\": \"" + t.ResourceId + "\", \"appRole\": \"Orders.UserOnly\" }",
            "bad-shape" => "{ \"principalId\": \"" + user + "\", \"appRole\": \"Orders.UserOnly\" }",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        string template = Templates.Resource(AssignmentType, Guid.NewGuid().ToString("D"), props);

        var run = await t.Host.CreateAsync(t.Host.Resources(template));

        Assert.False(run.Succeeded);
        Assert.Contains(expected, run.Errors, StringComparison.Ordinal);
        Check.NoWrites(run);
    }

    [Fact]
    public async Task Standalone_NonGuidName_IsRejectedOffline_AndAnUnknownPropertyIsAnUnsupportedRow()
    {
        var t = NewTenant();
        string user = t.Host.Directory.AddUser("bob@contoso.com", "Bob")["id"]!.GetValue<string>();
        string valid = "\"principalId\": \"" + user + "\", \"resourceId\": \"" + t.ResourceId + "\", \"appRoleId\": \"" + UserRoleId + "\"";

        var badName = await Assert.ThrowsAsync<ValidationFailedException>(() => t.Host.WhatIfAsync(t.Host.Resources(Templates.Resource(AssignmentType, "not-a-guid", "{ " + valid + " }"))));
        var badProperty = await t.Host.WhatIfAsync(t.Host.Resources(Templates.Resource(AssignmentType, Guid.NewGuid().ToString("D"), "{ " + valid + ", \"extra\": 1 }")));

        Assert.Contains(badName.Diagnostics, d => d.Code == "InvalidResourceName");
        Assert.Contains("is not a property of an app role assignment", Assert.Single(badProperty.Plan.Changes).UnsupportedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Standalone_Plan_ReportsANonGuidNameAndAnUnknownPropertyOnAnInstanceThatBypassedValidation()
    {
        var t = NewTenant();
        var provider = t.Host.Provider<AppRoleAssignmentsProvider>(AssignmentType);
        var instance = await InstanceOf(t.Host, Templates.Resource(AssignmentType, Guid.NewGuid().ToString("D"), "{ \"principalId\": \"" + Guid.NewGuid() + "\", \"resourceId\": \"" + t.ResourceId + "\", \"appRoleId\": \"" + UserRoleId + "\" }"));
        var badName = PlanWith(provider, instance, id: ResourceIdentifier.Create(AssignmentType, "not-a-guid"));

        
        var badProperty = PlanWith(provider, instance, properties: (JsonObject)JsonNode.Parse("{ \"extra\": 1 }")!);

        Assert.Contains("is not a GUID", badName.UnsupportedReason, StringComparison.Ordinal);
        Assert.Contains("is not a property of an app role assignment", badProperty.UnsupportedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Standalone_ChangedComposite_UnderTheSameName_CreatesANewAssignment_AndRetainsTheOldOne()
    {
        var t = NewTenant();
        string user = t.Host.Directory.AddUser("bob@contoso.com", "Bob")["id"]!.GetValue<string>();
        string name = Guid.NewGuid().ToString("D");
        string Declare(string roleId)
        {
            return Templates.Resource(AssignmentType, name, "{ \"principalId\": \"" + user + "\", \"resourceId\": \"" + t.ResourceId + "\", \"appRoleId\": \"" + roleId + "\" }");
        }

        Check.Succeeded(await t.Host.CreateAsync(t.Host.Resources(Declare(UserRoleId))));

        var run = await t.Host.CreateAsync(t.Host.Resources(Declare(BothRoleId)));

        // Identity is the live tuple: a changed tuple is another assignment. The old one is retained (never removed by a changed declaration).
        Check.Succeeded(run);
        Assert.DoesNotContain(run.Writes, w => w.Method == "DELETE");
        Assert.Contains(run.Writes, w => w.Method == "POST");
        var remaining = t.Host.Directory.Objects(AssignedTo(t.ResourceId)).Select(o => o["appRoleId"]!.GetValue<string>()).Order().ToList();
        Assert.Equal(new[] { UserRoleId, BothRoleId }.Order().ToList(), remaining);
    }

    [Fact]
    public async Task Standalone_AlreadyExistsOnPost_AdoptsTheExistingAssignment()
    {
        var t = NewTenant();
        string user = t.Host.Directory.AddUser("bob@contoso.com", "Bob")["id"]!.GetValue<string>();
        string template = Templates.Resource(AssignmentType, Guid.NewGuid().ToString("D"), "{ \"principalId\": \"" + user + "\", \"resourceId\": \"" + t.ResourceId + "\", \"appRoleId\": \"" + UserRoleId + "\" }");
        var document = t.Host.Resources(template);
        // Plan against an empty directory, then let "someone else" create the assignment before the apply reaches Graph.
        t.Host.Fake.Failures.Add("POST", "appRoleAssignedTo", request =>
        {
            t.Host.Directory.Add(AssignedTo(t.ResourceId), new JsonObject { ["principalId"] = user, ["resourceId"] = t.ResourceId, ["appRoleId"] = UserRoleId });
            return new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", "Permission being assigned already exists on the object.", request.Hint);
        });

        var run = await t.Host.CreateAsync(document);

        Check.Succeeded(run);
        Assert.Single(t.Host.Directory.Objects(AssignedTo(t.ResourceId)));
        Assert.Equal(t.Host.Directory.Objects(AssignedTo(t.ResourceId)).Single()["id"]!.GetValue<string>(), run.OpOfType(AssignmentType).GraphId);
    }

    [Fact]
    public async Task Standalone_Delete_ToleratesAnAssignmentThatIsAlreadyGone_AndFailsOnOtherErrors()
    {
        var t = NewTenant();
        string user = t.Host.Directory.AddUser("bob@contoso.com", "Bob")["id"]!.GetValue<string>();
        string template = Templates.Resource(AssignmentType, Guid.NewGuid().ToString("D"), "{ \"principalId\": \"" + user + "\", \"resourceId\": \"" + t.ResourceId + "\", \"appRoleId\": \"" + UserRoleId + "\" }");
        Check.Succeeded(await t.Host.CreateAsync(t.Host.Resources(template)));
        t.Host.Fake.Failures.Add("DELETE", "appRoleAssignedTo", request => new GraphException(HttpStatusCode.NotFound, "Request_ResourceNotFound", "Resource does not exist.", request.Hint));

        var gone = await t.Host.DeleteDeclaredAsync(t.Host.Resources(template));

        Assert.Equal(0, gone.FailedCount);

        // A second tenant: a 403 on the delete is a failed deletion, not silently ignored.
        var u = NewTenant();
        string user2 = u.Host.Directory.AddUser("bob@contoso.com", "Bob")["id"]!.GetValue<string>();
        var second = u.Host.Resources(Templates.Resource(AssignmentType, Guid.NewGuid().ToString("D"), "{ \"principalId\": \"" + user2 + "\", \"resourceId\": \"" + u.ResourceId + "\", \"appRoleId\": \"" + UserRoleId + "\" }"));
        Check.Succeeded(await u.Host.CreateAsync(second));
        u.Host.Fake.Failures.Forbid("appRoleAssignedTo", "DELETE");

        var denied = await u.Host.DeleteDeclaredAsync(second);

        Assert.Equal(1, denied.FailedCount);
    }

    [Fact]
    public async Task Standalone_Read_LooksUpFromThePrincipalSide_NeverListsTheResource()
    {
        var t = NewTenant();
        string user = t.Host.Directory.AddUser("bob@contoso.com", "Bob")["id"]!.GetValue<string>();
        var provider = t.Host.Provider<AppRoleAssignmentsProvider>(AssignmentType);
        var document = t.Host.Resources(Templates.Resource(AssignmentType, Guid.NewGuid().ToString("D"), "{ \"principalId\": \"" + user + "\", \"resourceId\": \"" + t.ResourceId + "\", \"appRoleId\": \"" + UserRoleId + "\" }"));
        Check.Succeeded(await t.Host.CreateAsync(document));
        string assignmentId = t.Host.Directory.Objects(AssignedTo(t.ResourceId)).Single()["id"]!.GetValue<string>();
        var key = new ResourceKey { Type = AssignmentType, Values = new System.Collections.Generic.Dictionary<string, string> { ["principalId"] = user, ["resourceId"] = t.ResourceId, ["appRoleId"] = UserRoleId } };

        t.Host.ClearRequests();
        var found = await provider.ReadAsync(key, t.Host.Fake, ProviderHost.Ct);
        Assert.NotNull(found);
        Assert.Equal(assignmentId, found.GraphId);
        Assert.Contains(t.Host.Fake.Requests, r => r.Path == "/users/" + user + "/appRoleAssignments");
        Assert.DoesNotContain(t.Host.Fake.Requests, r => r.Path.Contains("/appRoleAssignedTo", StringComparison.Ordinal));

        var unkeyed = key with { Values = new System.Collections.Generic.Dictionary<string, string>() };
        await Assert.ThrowsAsync<ProviderException>(() => provider.ReadAsync(unkeyed, t.Host.Fake, ProviderHost.Ct));
    }

    [Fact]
    public async Task Standalone_Update_IsNeverSupported()
    {
        var t = NewTenant();
        var provider = t.Host.Provider<AppRoleAssignmentsProvider>(AssignmentType);
        var instance = await InstanceOf(t.Host, Templates.Resource(AssignmentType, Guid.NewGuid().ToString("D"), "{ \"principalId\": \"" + Guid.NewGuid() + "\", \"resourceId\": \"" + t.ResourceId + "\", \"appRoleId\": \"" + UserRoleId + "\" }"));

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.UpdateAsync(instance, null!, null!, t.Host.Fake, ProviderHost.Ct));

        Assert.Contains("never edited in place", ex.Message, StringComparison.Ordinal);
    }

    // ---- helpers ----

    private static EdgeGraphContext Context(ProviderHost host)
    {
        return new EdgeGraphContext(host.Fake, host.Retry, new LiveReferenceResolver(), [], host.Logs.For<EdgeGraphContext>());
    }

    /// <summary>The first declared instance after a what-if run (properties evaluated).</summary>
    private static async Task<ResourceInstance> InstanceOf(ProviderHost host, string resource)
    {
        var run = await host.WhatIfAsync(host.Resources(resource));
        return run.Plan.Instances.First();
    }

    /// <summary>Plans an instance with a different id or properties (an instance that bypassed offline validation), restoring it afterwards.</summary>
    private static ResourceChange PlanWith(IResourceProvider provider, ResourceInstance instance, ResourceIdentifier? id = null, JsonObject? properties = null)
    {
        var originalId = instance.Id;
        var originalProperties = instance.Properties;
        try
        {
            instance.Id = id ?? originalId;
            instance.Properties = properties ?? originalProperties;
            return provider.Plan(instance, null, PlanOptions.Default);
        }
        finally
        {
            instance.Id = originalId;
            instance.Properties = originalProperties;
        }
    }
}
