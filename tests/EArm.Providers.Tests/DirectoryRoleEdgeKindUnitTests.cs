using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Engine.Edges;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using EArm.Providers.Edges;
using EArm.Providers.Tests.Infrastructure;
using Xunit;
using static EArm.Providers.Tests.Infrastructure.DirectoryTemplates;

namespace EArm.Providers.Tests;

/// <summary>
/// The <c>directoryRoles</c> edge handler driven through its contract methods (the engine-level scenarios live in
/// <see cref="DirectoryRoleEdgeTests"/>): every invalid-item message, role / unit resolution from the template and from
/// Graph, target and principal reads, record round trips and the composite fallbacks.
/// </summary>
public sealed class DirectoryRoleEdgeKindUnitTests
{
    private const string Guid1 = "11111111-1111-1111-1111-111111111111";
    private const string Guid2 = "22222222-2222-2222-2222-222222222222";

    private static readonly DirectoryRoleEdgeKind Handler = new();

    private static ProviderHost NewHost()
    {
        var host = new ProviderHost();
        BuiltInRoles.Seed(host);
        return host;
    }

    private static EdgeGraphContext Context(ProviderHost host, LiveReferenceResolver? resolver = null)
    {
        return new EdgeGraphContext(host.Fake, host.Retry, resolver ?? new LiveReferenceResolver(), [], host.Logs.For<EdgeGraphContext>());
    }

    private static async Task<ResourceInstance> InstanceOf(ProviderHost host, string resource)
    {
        var run = await host.WhatIfAsync(host.Resources(resource));
        return run.Plan.Instances.First();
    }

    private static Task<EdgeItem> Resolve(ProviderHost host, ResourceInstance declarer, string json, EdgeGraphContext? context = null, EdgeSide side = EdgeSide.Principal)
    {
        return Handler.ResolveItemAsync(JsonNode.Parse(json), side, declarer, GraphApi.V1, context ?? Context(host), ProviderHost.Ct);
    }

    // ---- ResolveItemAsync ----

    [Fact]
    public async Task ResolveItem_TargetSide_IsInvalid_AndUnknownItemsPassThrough()
    {
        var host = NewHost();
        var alice = await InstanceOf(host, Alice());

        var target = await Resolve(host, alice, "{ }", side: EdgeSide.Target);
        var unknown = await Handler.ResolveItemAsync(UnknownJson.Create("reference('x').y", UnknownOrigins.Reference), EdgeSide.Principal, alice, GraphApi.V1, Context(host), ProviderHost.Ct);
        var plainMember = await Resolve(host, alice, "{ \"role\": \"Global Reader\" }");
        var withUnknownMember = await Handler.ResolveItemAsync(new JsonObject { ["role"] = UnknownJson.Create("reference('r').id", UnknownOrigins.Reference) }, EdgeSide.Principal, alice, GraphApi.V1, Context(host), ProviderHost.Ct);

        Assert.Contains("no target-side property", target.Error, StringComparison.Ordinal);
        Assert.True(unknown.IsUnknown);
        Assert.Equal("reference('x').y", unknown.Symbol);
        Assert.Null(plainMember.Error);
        Assert.True(withUnknownMember.IsUnknown);
        Assert.Equal("reference('r').id", withUnknownMember.Symbol);
    }

    [Theory]
    [InlineData("\"just-a-string\"", "expected an object")]
    [InlineData("{ \"principalId\": \"11111111-1111-1111-1111-111111111111\", \"role\": \"Global Reader\" }", "principalId is implied")]
    [InlineData("{ \"directoryScopeId\": \"/\" }", "needs the role")]
    [InlineData("{ \"role\": \"Global Reader\", \"colour\": \"red\" }", "is not a property of a directory role assignment")]
    [InlineData("{ \"role\": 5 }", "must be a non-empty string")]
    [InlineData("{ \"role\": \"No Such Role\" }", "No Such Role")]
    [InlineData("{ \"role\": \"Global Reader\", \"roleDefinitionId\": \"" + Guid1 + "\" }", "differs from the declared roleDefinitionId")]
    [InlineData("{ \"role\": \"Global Reader\", \"administrativeUnit\": \"No Such AU\" }", "No Such AU")]
    [InlineData("{ \"role\": \"Global Reader\", \"administrativeUnit\": \"" + Guid1 + "\", \"directoryScopeId\": \"/administrativeUnits/" + Guid2 + "\" }", "differs from the declared directoryScopeId")]
    public async Task ResolveItem_InvalidItems_AreReportedAsWritten(string item, string expected)
    {
        var host = NewHost();
        var alice = await InstanceOf(host, Alice());

        var resolved = await Resolve(host, alice, item);

        Assert.Contains(expected, resolved.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveItem_AnApplicationDeclarer_CannotHoldARole()
    {
        var host = NewHost();
        var app = await InstanceOf(host, "{ \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"test-app\", \"properties\": { \"displayName\": \"Test App\" } }");

        var resolved = await Resolve(host, app, "{ \"role\": \"Global Reader\" }");

        Assert.Contains("only users, role-assignable groups and service principals", resolved.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveItem_GroupRoleAssignability_IsReadFromTheLiveObject_TheDeclaration_OrGraph()
    {
        var host = NewHost();
        var assignable = await InstanceOf(host, Group());
        var plain = await InstanceOf(host, Group(assignable: false).Replace("\"[equals(1, 2)]\"", "false", StringComparison.Ordinal));

        // Declared properties decide (what-if before the group exists).
        Assert.Null((await Resolve(host, assignable, "{ \"role\": \"Global Reader\" }")).Error);
        Assert.Contains("not role-assignable", (await Resolve(host, plain, "{ \"role\": \"Global Reader\" }")).Error, StringComparison.Ordinal);

        // The published live object wins over the declaration.
        var resolver = new LiveReferenceResolver();
        resolver.Publish(plain.Id, new JsonObject { ["id"] = Guid1, ["isAssignableToRole"] = true }, Guid1, plain.PlannedId);
        Assert.Null((await Resolve(host, plain, "{ \"role\": \"Global Reader\" }", Context(host, resolver))).Error);

        // Neither declared nor live: Graph is asked for the published id; an unknown group is not role-assignable.
        var undeclared = await InstanceOf(host, Group());
        undeclared.Properties!.Remove("isAssignableToRole");
        string groupId = host.Directory.AddGroup("g", "G", g => g["isAssignableToRole"] = true)["id"]!.GetValue<string>();
        var viaGraph = new LiveReferenceResolver();
        viaGraph.Publish(undeclared.Id, new JsonObject { ["id"] = groupId }, groupId, undeclared.PlannedId);
        Assert.Null((await Resolve(host, undeclared, "{ \"role\": \"Global Reader\" }", Context(host, viaGraph))).Error);
        Assert.Contains(host.Fake.Requests, r => r.Path == "/groups/" + groupId && r.UrlParameters!["$select"].Contains("isAssignableToRole", StringComparison.Ordinal));

        var neverSeen = await InstanceOf(host, Group());
        neverSeen.Properties!.Remove("isAssignableToRole");
        Assert.Contains("not role-assignable", (await Resolve(host, neverSeen, "{ \"role\": \"Global Reader\" }")).Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveItem_ResolvesRolesAndUnits_FromTheTemplateBeforeGraph()
    {
        var host = NewHost();
        var alice = await InstanceOf(host, Alice());
        var resolver = new LiveReferenceResolver();
        resolver.Publish(ResourceIdentifier.Create(RoleDefinitionsProvider.ResourceType, "Orders Support"), new JsonObject { ["id"] = Guid1 }, Guid1);
        resolver.Publish(ResourceIdentifier.Create(AdministrativeUnitsProvider.ResourceType, "Orders AU"), new JsonObject { ["id"] = Guid2 }, Guid2);
        host.Fake.ClearRequests();

        var item = await Resolve(host, alice, "{ \"role\": \"Orders Support\", \"administrativeUnit\": \"Orders AU\" }", Context(host, resolver));

        Assert.Null(item.Error);
        Assert.Equal(RoleAssignmentsRules.TargetKey(Guid1, "/administrativeUnits/" + Guid2), item.OtherId);
        Assert.Empty(host.Fake.Requests);

        var byGuid = await Resolve(host, alice, "{ \"role\": \"Global Reader\", \"administrativeUnit\": \"" + Guid2 + "\" }");
        Assert.Equal(RoleAssignmentsRules.TargetKey(BuiltInRoles.GlobalReader, "/administrativeUnits/" + Guid2), byGuid.OtherId);
    }

    [Fact]
    public async Task ResolveItem_ARoleDefinitionIdAlone_IsNormalised_AndDefaultsToTheRootScope()
    {
        var host = NewHost();
        var alice = await InstanceOf(host, Alice());

        var item = await Resolve(host, alice, "{ \"roleDefinitionId\": \"" + BuiltInRoles.GlobalReader.ToUpperInvariant() + "\" }");

        Assert.Equal(RoleAssignmentsRules.TargetKey(BuiltInRoles.GlobalReader, "/"), item.OtherId);
        Assert.Equal("/", item.Extra!["directoryScopeId"]!.GetValue<string>());
    }

    // ---- reads ----

    [Fact]
    public async Task ReadTarget_ChecksTheRoleAndTheUnit_AndNeverReadsTargetSideEdges()
    {
        var host = NewHost();
        string unitId = host.Directory.Add(BuiltInRoles.UnitsPath, new JsonObject { ["displayName"] = "Orders AU" })["id"]!.GetValue<string>();
        var context = Context(host);
        var ct = ProviderHost.Ct;

        Assert.Same(EdgeTargetState.Missing, await Handler.ReadTargetAsync("not-a-composite", null, true, GraphApi.V1, context, ct));
        Assert.Same(EdgeTargetState.Missing, await Handler.ReadTargetAsync(RoleAssignmentsRules.TargetKey(Guid.NewGuid().ToString("D"), "/"), null, true, GraphApi.V1, context, ct));

        var builtIn = await Handler.ReadTargetAsync(RoleAssignmentsRules.TargetKey(BuiltInRoles.GlobalReader, "/"), null, true, GraphApi.V1, context, ct);
        Assert.True(builtIn.Exists);
        Assert.Null(builtIn.Edges);

        var scoped = await Handler.ReadTargetAsync(RoleAssignmentsRules.TargetKey(BuiltInRoles.GlobalReader, "/administrativeUnits/" + unitId), null, true, GraphApi.V1, context, ct);
        Assert.True(scoped.Exists);

        var missingUnit = await Handler.ReadTargetAsync(RoleAssignmentsRules.TargetKey(BuiltInRoles.GlobalReader, "/administrativeUnits/" + Guid.NewGuid()), null, true, GraphApi.V1, context, ct);
        Assert.Same(EdgeTargetState.Missing, missingUnit);

        host.Directory.Add(BuiltInRoles.DefinitionsPath, new JsonObject { ["id"] = Guid2, ["displayName"] = "Custom" });
        var custom = await Handler.ReadTargetAsync(RoleAssignmentsRules.TargetKey(Guid2, "/"), null, true, GraphApi.V1, context, ct);
        Assert.True(custom.Exists);
    }

    [Fact]
    public async Task ReadPrincipal_ListsTheAssignments_AndAnswersEmptyForOtherPrincipalTypes()
    {
        var host = NewHost();
        string userId = host.Directory.AddUser("alice@contoso.com", "Alice")["id"]!.GetValue<string>();
        var assignment = host.Directory.Add(BuiltInRoles.AssignmentsPath, new JsonObject { ["roleDefinitionId"] = BuiltInRoles.GlobalReader, ["principalId"] = userId, ["directoryScopeId"] = "/" });
        var context = Context(host);

        var user = await Handler.ReadPrincipalAsync(userId, "Microsoft.Entra/users", GraphApi.V1, context, ProviderHost.Ct);
        var app = await Handler.ReadPrincipalAsync(userId, "Microsoft.Entra/applications", GraphApi.V1, context, ProviderHost.Ct);

        var edge = Assert.Single(user.Edges);
        Assert.Equal(assignment["id"]!.GetValue<string>(), edge.Value!["id"]!.GetValue<string>());
        Assert.Empty(app.Edges);
    }

    // ---- records and identity ----

    [Fact]
    public async Task FromRecord_RebuildsWithAnId_LooksTheIdUp_AndDropsStaleOrMalformedRecords()
    {
        var host = NewHost();
        string userId = host.Directory.AddUser("alice@contoso.com", "Alice")["id"]!.GetValue<string>();
        string assignmentId = host.Directory.Add(BuiltInRoles.AssignmentsPath, new JsonObject { ["roleDefinitionId"] = BuiltInRoles.GlobalReader, ["principalId"] = userId, ["directoryScopeId"] = "/" })["id"]!.GetValue<string>();
        var context = Context(host);
        var ct = ProviderHost.Ct;

        var withId = await Handler.FromRecordAsync(new JsonObject { ["roleDefinitionId"] = BuiltInRoles.GlobalReader, ["directoryScopeId"] = "/", ["id"] = "recorded-1" }, userId, GraphApi.V1, context, ct);
        var lookedUp = await Handler.FromRecordAsync(new JsonObject { ["roleDefinitionId"] = BuiltInRoles.GlobalReader }, userId, GraphApi.V1, context, ct);
        var stale = await Handler.FromRecordAsync(new JsonObject { ["roleDefinitionId"] = BuiltInRoles.SecurityReader }, userId, GraphApi.V1, context, ct);

        Assert.Equal("recorded-1", withId!.Extra!["id"]!.GetValue<string>());
        Assert.Equal(assignmentId, lookedUp!.Extra!["id"]!.GetValue<string>());
        Assert.Null(stale);
        Assert.Null(await Handler.FromRecordAsync(null, userId, GraphApi.V1, context, ct));
        Assert.Null(await Handler.FromRecordAsync(JsonValue.Create(3), userId, GraphApi.V1, context, ct));
        Assert.Null(await Handler.FromRecordAsync(new JsonObject { ["roleDefinitionId"] = "not-a-guid" }, userId, GraphApi.V1, context, ct));
        Assert.Null(await Handler.FromRecordAsync(new JsonObject { ["directoryScopeId"] = "/" }, userId, GraphApi.V1, context, ct));
    }

    [Fact]
    public void Composite_FallsBackToTheExtras_AndRejectsEdgesWithoutOne()
    {
        var viaExtra = new Edge(EdgeKind.DirectoryRole, BuiltInRoles.GlobalReader, Guid1, null, new JsonObject { ["directoryScopeId"] = "/administrativeUnits/" + Guid2 });
        var broken = new Edge(EdgeKind.DirectoryRole, "not-a-guid", Guid1, null, null);

        Assert.Equal(RoleAssignmentsRules.Identity(Guid1, BuiltInRoles.GlobalReader, "/administrativeUnits/" + Guid2), Handler.IdentityOf(viaExtra));
        Assert.Equal([Guid1, BuiltInRoles.GlobalReader, Guid2], Handler.GraphIdsOf(viaExtra).ToArray());
        Assert.Contains("assigned to " + Guid1, Handler.Describe(viaExtra), StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => Handler.IdentityOf(broken));
        Assert.Throws<InvalidOperationException>(() => Handler.AddRequest(broken, GraphApi.V1));
    }

    [Fact]
    public void RequestsAndRecords_UseTheAppliedIdThenTheEdgeExtraId()
    {
        string key = RoleAssignmentsRules.TargetKey(BuiltInRoles.GlobalReader, "/");
        var edge = new Edge(EdgeKind.DirectoryRole, key, Guid1, null, null);
        var withExtra = edge with { Extra = new JsonObject { ["id"] = "extra-id" } };

        var add = Handler.AddRequest(edge, GraphApi.V1);
        Assert.Equal("POST", add.Method);
        Assert.Equal(RoleAssignmentsRules.AssignmentsPath, add.Path);
        Assert.Equal(Guid1, add.Body!["principalId"]!.GetValue<string>());

        Assert.EndsWith("/live-id", Handler.RemoveRequest(edge, new JsonObject { ["id"] = "live-id" }, GraphApi.V1).Path, StringComparison.Ordinal);
        Assert.EndsWith("/extra-id", Handler.RemoveRequest(withExtra, null, GraphApi.V1).Path, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => Handler.RemoveRequest(edge, null, GraphApi.V1));

        Assert.Equal("applied-id", ((JsonObject)Handler.RecordEntry(edge, new JsonObject { ["id"] = "applied-id" }))["id"]!.GetValue<string>());
        Assert.Equal("extra-id", ((JsonObject)Handler.RecordEntry(withExtra, null))["id"]!.GetValue<string>());
        Assert.False(((JsonObject)Handler.RecordEntry(edge, null)).ContainsKey("id"));
        Assert.Equal(RoleAssignmentsRules.NormalizeId(Guid1), ((JsonObject)Handler.RecordEntry(edge, null))["principalId"]!.GetValue<string>());
    }

    [Fact]
    public void Kind_AndPhase_AreThePartBDirectoryRole()
    {
        Assert.Equal(EdgeKind.DirectoryRole, Handler.Kind);
        Assert.Equal(EdgePhase.B, Handler.Phase);
    }
}
