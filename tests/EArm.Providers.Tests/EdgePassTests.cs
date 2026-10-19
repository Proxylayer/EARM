using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Engine.WhatIf;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>The edge pass driven through the real providers: the T1–T9 lifecycle, batching, tolerated Graph answers, skipped / unsupported kinds and type discovery.</summary>
public sealed class EdgePassTests
{
    private const string App = """
        { "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "name": "test-app",
          "properties": { "displayName": "Test App", "owners": ["[reference('alice@contoso.com').id]"] } }
        """;

    private const string AppOwnedByBob = """
        { "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "name": "test-app",
          "properties": { "displayName": "Test App", "owners": ["[reference(resourceId('Microsoft.Entra/users', 'bob@contoso.com'), '2026-10-01-preview').id]"] } }
        """;

    private const string Sp = """
        { "type": "Microsoft.Entra/servicePrincipals", "apiVersion": "2026-10-01-preview", "name": "[reference('test-app').appId]",
          "properties": { }, "dependsOn": ["[resourceId('Microsoft.Entra/applications', 'test-app')]"] }
        """;

    private const string Au = """
        { "type": "Microsoft.Entra/administrativeUnits", "apiVersion": "2026-10-01-preview", "name": "Test AU", "properties": { "description": "AU" } }
        """;

    private const string Group2 = """
        { "type": "Microsoft.Entra/groups", "apiVersion": "2026-10-01-preview", "name": "test-group2", "properties": { "displayName": "Test Group 2" } }
        """;

    private const string MemberOfGroup = "\"[reference('test-group').id]\"";
    private const string MemberOfGroup2 = "\"[reference('test-group2').id]\"";
    private const string MemberOfAllStaff = "\"[reference(resourceId('Microsoft.Entra/groups', 'all-staff'), '2026-10-01-preview').id]\"";
    private const string MembersSpAndAlice = "\"[reference(resourceId('Microsoft.Entra/servicePrincipals', reference('test-app').appId)).id]\", \"[reference('alice@contoso.com').id]\"";
    private const string MembersAlice = "\"[reference('alice@contoso.com').id]\"";
    private const string ManagerBoss = "\"[reference(resourceId('Microsoft.Entra/users', 'boss@contoso.com'), '2026-10-01-preview').id]\"";

    private static string Alice(string memberOf, string manager = ManagerBoss, bool ownerOf = true)
    {
        return "{ \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"alice@contoso.com\", \"properties\": { \"displayName\": \"Alice\", \"memberOf\": [" + memberOf + "], "
            + (ownerOf ? "\"ownerOf\": [\"[reference('test-app').id]\"], " : string.Empty)
            + "\"administrativeUnits\": [\"[reference('Test AU').id]\"], \"manager\": " + manager + " } }";
    }

    private static string Group(string? members)
    {
        return "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"test-group\", \"properties\": { \"displayName\": \"Test Group\"" + (members is null ? string.Empty : ", \"members\": [" + members + "]") + " } }";
    }

    private static string Join(params string[] resources)
    {
        return string.Join(",", resources);
    }

    private static IReadOnlyList<FakeGraphRequest> EdgeWrites(Run run)
    {
        return run.Writes.Where(w => w.Path.Contains("$ref", StringComparison.Ordinal)).ToList();
    }

    /// <summary>The shared tenant of the T1–T9 lifecycle; <see cref="UpToAsync"/> replays the steps a test builds on.</summary>
    private sealed class EdgeTenant
    {
        public EdgeTenant()
        {
            AllStaffId = Host.Directory.AddGroup("all-staff", "All staff")["id"]!.GetValue<string>();
            BossId = Host.Directory.AddUser("boss@contoso.com", "Boss")["id"]!.GetValue<string>();
            BobId = Host.Directory.AddUser("bob@contoso.com", "Bob")["id"]!.GetValue<string>();
        }

        public ProviderHost Host { get; } = new();

        public string AllStaffId { get; }

        public string BossId { get; }

        public string BobId { get; }

        public string Group2Id { get; private set; } = string.Empty;

        public string AliceId => Host.IdOf("/users", "userPrincipalName", "alice@contoso.com");

        public string GroupId => Host.IdOf("/groups", "uniqueName", "test-group");

        public string AppId => Host.IdOf("/applications", "uniqueName", "test-app");

        public string SpId => Host.Directory.Objects("/servicePrincipals").Single()["id"]!.GetValue<string>();

        public string AuId => Host.IdOf("/directory/administrativeUnits", "displayName", "Test AU");

        public static string T1Template => Join(App, Sp, Alice(MemberOfGroup + ", " + MemberOfAllStaff), Group(MembersSpAndAlice), Au);

        public static string T2Template => Join(App, Sp, Alice(MemberOfGroup), Group(MembersSpAndAlice), Au);

        public static string T3Template => Join(App, Sp, Alice(MemberOfGroup), Group(MembersAlice), Au);

        public static string T4Template => Join(App, Sp, Alice(MemberOfGroup + ", " + MemberOfGroup2), Group(MembersAlice), Group2, Au);

        public static string T6Template => Join(App, Sp, Alice(MemberOfGroup + ", " + MemberOfGroup2, manager: "null"), Group(MembersAlice), Group2, Au);

        public static string T7Template => Join(AppOwnedByBob, Sp, Alice(MemberOfGroup + ", " + MemberOfGroup2, manager: "null", ownerOf: false), Group(MembersAlice), Group2, Au);

        public Task<Run> DeployAsync(string resources, DeploymentMode mode = DeploymentMode.Incremental)
        {
            return Host.CreateAsync(Host.Resources(resources), mode);
        }

        public Task<Run> WhatIfAsync(string resources, DeploymentMode mode = DeploymentMode.Incremental)
        {
            return Host.WhatIfAsync(Host.Resources(resources), mode);
        }

        /// <summary>Seeds group 2 with bob as an extra member (the T4 precondition).</summary>
        public async Task SeedGroup2Async()
        {
            Group2Id = Host.Directory.AddGroup("test-group2", "Test Group 2")["id"]!.GetValue<string>();
            await Host.Fake.PostAsync(new GraphRequest($"/groups/{Group2Id}/members/$ref"), new JsonObject { ["@odata.id"] = "https://graph.microsoft.com/v1.0/directoryObjects/" + BobId }, ProviderHost.Ct);
            Host.ClearRequests();
        }

        /// <summary>Replays the lifecycle up to and including step <paramref name="step"/> (1, 2, 3, 4, 6, 7), asserting each step succeeded.</summary>
        public static async Task<EdgeTenant> UpToAsync(int step)
        {
            var tenant = new EdgeTenant();
            var steps = new List<Func<Task<Run>>>
            {
                () => tenant.DeployAsync(T1Template),
                () => tenant.DeployAsync(T2Template),
                () => tenant.DeployAsync(T3Template),
                async () => { await tenant.SeedGroup2Async(); return await tenant.DeployAsync(T4Template); },
                () => tenant.DeployAsync(T6Template),
                () => tenant.DeployAsync(T7Template),
            };

            int[] numbers = [1, 2, 3, 4, 6, 7];
            for (int i = 0; i < steps.Count && numbers[i] <= step; i++)
            {
                var run = await steps[i]();
                Assert.True(run.Succeeded, $"replay of T{numbers[i]} failed: {run.Errors}");
            }

            return tenant;
        }
    }

    // ---- T1 .. T9 ----

    [Fact]
    public async Task T1_Create_AddsEveryEdgeOnce_FromBothSides()
    {
        var tenant = new EdgeTenant();

        var run = await tenant.DeployAsync(EdgeTenant.T1Template);

        Check.Succeeded(run);
        var edgeWrites = EdgeWrites(run);
        Assert.Equal(6, edgeWrites.Count);
        Assert.All(edgeWrites, w => Assert.True(w.Method is "POST" or "PUT"));
        Assert.Equal(2, edgeWrites.Count(w => w.Path == $"/groups/{tenant.GroupId}/members/$ref"));
        Assert.Equal(1, edgeWrites.Count(w => w.Path == $"/applications/{tenant.AppId}/owners/$ref"));
        Assert.Contains(edgeWrites, w => w.Path == $"/groups/{tenant.AllStaffId}/members/$ref");
        Assert.Contains(edgeWrites, w => w.Path == $"/directory/administrativeUnits/{tenant.AuId}/members/$ref");
        Assert.Contains(edgeWrites, w => w.Method == "PUT" && w.Path == $"/users/{tenant.AliceId}/manager/$ref");
        Assert.Equal(2, tenant.Host.Directory.References($"/groups/{tenant.GroupId}/members").Count);

        var groupChange = run.Change("test-group");
        var membersDelta = Assert.Single(groupChange.Deltas, d => d.Path.ToString() == "properties.members");
        Assert.Equal(PropertyChangeType.Array, membersDelta.Kind);
        Assert.Equal(2, membersDelta.Children!.Count);
        Assert.Contains(run.Change("alice@contoso.com").Deltas, d => d.Path.ToString() == "properties.memberOf");
    }

    [Fact]
    public async Task T1b_Rerun_IssuesNoEdgeWrites()
    {
        var tenant = await EdgeTenant.UpToAsync(1);

        var run = await tenant.DeployAsync(EdgeTenant.T1Template);

        Check.Succeeded(run);
        Assert.Empty(EdgeWrites(run));
    }

    [Fact]
    public async Task T2_UndeclaringAMemberOf_IsEnsureOnly_TheMembershipStays()
    {
        var tenant = await EdgeTenant.UpToAsync(1);

        var whatIf = await tenant.WhatIfAsync(EdgeTenant.T2Template);
        var run = await tenant.DeployAsync(EdgeTenant.T2Template);

        Assert.DoesNotContain(whatIf.Change("alice@contoso.com").Deltas.SelectMany(d => d.Children ?? []), c => c.Kind == PropertyChangeType.Delete);
        Check.Succeeded(run);
        Assert.Empty(EdgeWrites(run));
        Assert.Contains(tenant.AliceId, tenant.Host.Directory.References($"/groups/{tenant.AllStaffId}/members"));
    }

    [Fact]
    public async Task T3_AuthoritativeMembers_HaveReplaceSemantics()
    {
        var tenant = await EdgeTenant.UpToAsync(2);

        var run = await tenant.DeployAsync(EdgeTenant.T3Template);

        Check.Succeeded(run);
        var write = Assert.Single(EdgeWrites(run));
        Assert.Equal("DELETE", write.Method);
        Assert.Equal($"/groups/{tenant.GroupId}/members/{tenant.SpId}/$ref", write.Path);
        Assert.Single(tenant.Host.Directory.References($"/groups/{tenant.GroupId}/members"));
    }

    [Fact]
    public async Task T4_MemberOfATargetWithoutDeclaredMembers_AddsOnly_AndLeavesExtrasAlone()
    {
        var tenant = await EdgeTenant.UpToAsync(3);
        await tenant.SeedGroup2Async();

        var run = await tenant.DeployAsync(EdgeTenant.T4Template);

        Check.Succeeded(run);
        var write = Assert.Single(EdgeWrites(run));
        Assert.Equal("POST", write.Method);
        Assert.Equal($"/groups/{tenant.Group2Id}/members/$ref", write.Path);
        Assert.Contains(tenant.BobId, tenant.Host.Directory.References($"/groups/{tenant.Group2Id}/members"));
    }

    [Fact]
    public async Task T5_MemberOfADynamicGroup_IsEdgeValidationFailed_WithoutWrites()
    {
        var tenant = await EdgeTenant.UpToAsync(4);
        tenant.Host.Directory.AddGroup("dyn-group", "Dynamic", g =>
        {
            g["groupTypes"] = new JsonArray("DynamicMembership");
            g["membershipRule"] = "(user.department -eq \"x\")";
            g["membershipRuleProcessingState"] = "On";
        });
        string template = Join(App, Sp, Alice(MemberOfGroup + ", " + MemberOfGroup2 + ", \"[reference(resourceId('Microsoft.Entra/groups', 'dyn-group'), '2026-10-01-preview').id]\""), Group(MembersAlice), Group2, Au);

        var run = await tenant.DeployAsync(template);

        Assert.False(run.Succeeded);
        var alice = run.Op("alice@contoso.com");
        Assert.Equal(OperationStatus.Failed, alice.Status);
        Assert.Equal(EngineDiagnosticCodes.EdgeValidationFailed, alice.ErrorCode);
        Assert.Empty(EdgeWrites(run));
    }

    [Fact]
    public async Task T6_ManagerNull_DeletesTheManagerReference()
    {
        var tenant = await EdgeTenant.UpToAsync(4);

        var run = await tenant.DeployAsync(EdgeTenant.T6Template);

        Check.Succeeded(run);
        var write = Assert.Single(EdgeWrites(run));
        Assert.Equal("DELETE", write.Method);
        Assert.Equal($"/users/{tenant.AliceId}/manager/$ref", write.Path);
    }

    [Fact]
    public async Task T7_OwnersReplace_RemovingTheSignedInPrincipal_Warns()
    {
        var tenant = await EdgeTenant.UpToAsync(6);
        tenant.Host.Directory.SignedInUserId = tenant.AliceId;

        var run = await tenant.DeployAsync(EdgeTenant.T7Template);

        Check.Succeeded(run);
        Check.Diagnostic(run, EngineDiagnosticCodes.SignedInPrincipalRemoved);
        var writes = EdgeWrites(run);
        Assert.Equal(1, writes.Count(w => w.Method == "DELETE" && w.Path == $"/applications/{tenant.AppId}/owners/{tenant.AliceId}/$ref"));
        Assert.Equal(1, writes.Count(w => w.Method == "POST" && w.Path == $"/applications/{tenant.AppId}/owners/$ref"));
    }

    [Fact]
    public async Task T8_WhatIf_OnAFreshTenant_ShowsEdgeDeltas_AndWritesNothing()
    {
        var tenant = new EdgeTenant();

        var run = await tenant.WhatIfAsync(EdgeTenant.T1Template);

        Check.NoWrites(run);
        var groupRow = run.Change("test-group");
        var aliceRow = run.Change("alice@contoso.com");
        var members = Assert.Single(groupRow.Deltas, d => d.Path.ToString() == "properties.members");
        Assert.Equal(PropertyChangeType.Array, members.Kind);
        Assert.NotEmpty(members.Children!);
        Assert.Contains(aliceRow.Deltas, d => d.Path.ToString() == "properties.memberOf");
        Assert.Contains(aliceRow.Deltas, d => d.Path.ToString() == "properties.manager");
    }

    [Fact]
    public async Task T8b_WhatIf_AgainstTheDeployedTenant_PlansNoRemovalOfAnUndeclaredMemberOf()
    {
        var tenant = await EdgeTenant.UpToAsync(7);
        string template = Join(AppOwnedByBob, Sp, Alice(MemberOfGroup, manager: "null", ownerOf: false), Group(MembersAlice), Group2, Au);

        var run = await tenant.WhatIfAsync(template);

        Check.NoWrites(run);
        var aliceRow = WhatIfJson.Build(run.Plan)["changes"]!.AsArray().First(c => c!["resourceId"]!.GetValue<string>().EndsWith("alice@contoso.com", StringComparison.Ordinal))!;
        var deltas = aliceRow["delta"]?.AsArray() ?? [];
        Assert.DoesNotContain(deltas.SelectMany(d => d!["children"]?.AsArray() ?? []), c => c!["propertyChangeType"]!.GetValue<string>() == "Delete");
    }

    // ---- beyond the lifecycle ----

    [Fact]
    public async Task ServicePrincipalMember_OmittedByV1Members_IsReadThroughTheTypeCast_AndTheNextWhatIfIsNoChange()
    {
        // Real tenant (probe h): v1.0 GET /groups/{id}/members leaves service principal members out; the fake models that.
        var tenant = await EdgeTenant.UpToAsync(1);
        var v1Members = await tenant.Host.Fake.GetAsync(new GraphRequest($"/groups/{tenant.GroupId}/members").WithSelect("id"), ProviderHost.Ct);
        Assert.DoesNotContain(tenant.SpId, v1Members!["value"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()));
        Assert.Contains(tenant.SpId, tenant.Host.Directory.References($"/groups/{tenant.GroupId}/members"));

        var whatIf = await tenant.WhatIfAsync(EdgeTenant.T1Template);

        Check.NoWrites(whatIf);
        var groupRow = whatIf.Change("test-group");
        Assert.Equal(ChangeType.NoChange, groupRow.Type);
        Assert.DoesNotContain(groupRow.Deltas, d => d.Path.ToString() == "properties.members");
        Assert.Contains(whatIf.Requests, r => r.Method == "GET" && r.Path == $"/groups/{tenant.GroupId}/members/microsoft.graph.servicePrincipal");
    }

    [Fact]
    public async Task MoreThanTwentyMembers_GoThroughBatch()
    {
        var host = new ProviderHost();
        var ids = Enumerable.Range(0, 25).Select(i => host.Directory.AddUser($"m{i}@contoso.com", $"M{i}")["id"]!.GetValue<string>()).ToList();
        string members = string.Join(", ", ids.Select(id => "\"" + id + "\""));

        var run = await host.CreateAsync(host.Resources(Group(members)));

        Check.Succeeded(run);
        Assert.Contains(run.Writes, w => w.Path == "/$batch");
        string groupId = host.IdOf("/groups", "uniqueName", "test-group");
        Assert.Equal(25, host.Directory.References($"/groups/{groupId}/members").Count);
        Assert.Equal(25, run.Writes.Count(w => w.Path == $"/groups/{groupId}/members/$ref"));
    }

    [Fact]
    public async Task AlreadyExistsOnAdd_IsIdempotentSuccess()
    {
        var host = new ProviderHost();
        string groupId = host.Directory.AddGroup("existing", "Existing")["id"]!.GetValue<string>();
        host.Fake.Failures.Add("POST", "/members/$ref", r => new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", "One or more added object references already exist for the following modified properties: 'members'.", r.Hint));
        string template = Templates.Resource("Microsoft.Entra/users", "carol@contoso.com", "{ \"displayName\": \"Carol\", \"memberOf\": [\"" + groupId + "\"] }");

        var run = await host.CreateAsync(host.Resources(template));

        Check.Succeeded(run);
        Assert.Equal(OperationStatus.Succeeded, run.Op("carol").Status);
        Assert.Single(run.Writes, w => w.Path == $"/groups/{groupId}/members/$ref");
    }

    [Fact]
    public async Task NotFoundOnRemove_CountsAsRemoved()
    {
        var host = new ProviderHost();
        string carolId = host.Directory.AddUser("carol@contoso.com", "Carol")["id"]!.GetValue<string>();
        var first = await host.CreateAsync(host.Resources(Group("\"" + carolId + "\"")));
        Check.Succeeded(first);
        string groupId = host.IdOf("/groups", "uniqueName", "test-group");
        host.Fake.Failures.Add("DELETE", $"/members/{carolId}/$ref", r => new GraphException(HttpStatusCode.NotFound, "Request_ResourceNotFound", "Resource not found.", r.Hint));

        // The target side is authoritative: in Complete mode an empty members list removes carol, and the 404 counts as removed.
        var run = await host.CreateAsync(host.Resources(Group(string.Empty)), DeploymentMode.Complete);

        Check.Succeeded(run);
        Assert.Single(run.Writes, w => w.Method == "DELETE" && w.Path == $"/groups/{groupId}/members/{carolId}/$ref");
    }

    [Fact]
    public async Task FailFast_SkipsTheEdgePass_AndReportsEdgePassSkipped()
    {
        var host = new ProviderHost();
        string groupId = host.Directory.AddGroup("existing", "Existing")["id"]!.GetValue<string>();
        const string brokenApp = """
            { "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "name": "broken",
              "properties": { "displayName": "Broken", "appRoles": [ { "value": "dup", "displayName": "a", "description": "a", "allowedMemberTypes": ["User"] } ],
                "api": { "oauth2PermissionScopes": [ { "value": "dup", "type": "User", "adminConsentDisplayName": "a", "adminConsentDescription": "a" } ] } } }
            """;
        const string sp = """
            { "type": "Microsoft.Entra/servicePrincipals", "apiVersion": "2026-10-01-preview", "name": "[reference('broken').appId]", "properties": { }, "dependsOn": ["[resourceId('Microsoft.Entra/applications', 'broken')]"] }
            """;
        string user = Templates.Resource("Microsoft.Entra/users", "carol@contoso.com", "{ \"displayName\": \"Carol\", \"memberOf\": [\"" + groupId + "\"] }");

        var run = await host.CreateAsync(host.Resources(Join(brokenApp, sp, user)));

        Assert.Equal(DeploymentStatus.Failed, run.Executed.Status);
        Assert.Equal(OperationStatus.Failed, run.Op("broken").Status);
        Assert.Equal(OperationStatus.Skipped, run.OpOfType(ServicePrincipalsProvider.ResourceType).Status);
        Check.Diagnostic(run, EngineDiagnosticCodes.EdgePassSkipped);
        Assert.Empty(EdgeWrites(run));
    }

    [Fact]
    public async Task OwnerOf_AnUndeclaredTarget_DiscoversTheCollectionThroughDirectoryObjects()
    {
        var host = new ProviderHost();
        string appId = host.Directory.AddApplication("brownfield", "Brownfield")["id"]!.GetValue<string>();
        string template = Templates.Resource("Microsoft.Entra/users", "carol@contoso.com", "{ \"displayName\": \"Carol\", \"ownerOf\": [\"" + appId + "\"] }");

        var run = await host.CreateAsync(host.Resources(template));

        Check.Succeeded(run);
        Assert.Contains(run.Requests, r => r.Method == "GET" && r.Path == "/directoryObjects/" + appId);
        var write = Assert.Single(EdgeWrites(run));
        Assert.Equal($"/applications/{appId}/owners/$ref", write.Path);
    }

    [Fact]
    public async Task OwnerRemoval_WithoutASignedInUser_RaisesNoWarning()
    {
        var host = new ProviderHost();
        string aliceId = host.Directory.AddUser("alice@contoso.com", "Alice")["id"]!.GetValue<string>();
        string bobId = host.Directory.AddUser("bob@contoso.com", "Bob")["id"]!.GetValue<string>();
        string appId = host.Directory.AddApplication("test-app", "Test App")["id"]!.GetValue<string>();
        await host.Fake.PostAsync(new GraphRequest($"/applications/{appId}/owners/$ref"), new JsonObject { ["@odata.id"] = "https://graph.microsoft.com/v1.0/directoryObjects/" + aliceId }, ProviderHost.Ct);
        host.ClearRequests();
        Assert.Null(host.Directory.SignedInUserId);
        string template = Templates.Resource("Microsoft.Entra/applications", "test-app", "{ \"displayName\": \"Test App\", \"owners\": [\"" + bobId + "\"] }");

        var run = await host.CreateAsync(host.Resources(template));

        Check.Succeeded(run);
        Check.NoDiagnostic(run, EngineDiagnosticCodes.SignedInPrincipalRemoved);
        Assert.Contains(EdgeWrites(run), w => w.Method == "DELETE" && w.Path == $"/applications/{appId}/owners/{aliceId}/$ref");
        Assert.Contains(EdgeWrites(run), w => w.Method == "POST" && w.Path == $"/applications/{appId}/owners/$ref");
        Assert.Equal([bobId], host.Directory.References($"/applications/{appId}/owners"));
    }
}
