using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>The group lifecycle policy singleton and the groups' ensure-only <c>groupLifecyclePolicies</c> edge, end to end against the fake.</summary>
public sealed class GroupLifecyclePoliciesProviderTests
{
    private const string PolicyType = GroupLifecyclePoliciesProvider.ResourceType;
    private const string PolicyPath = FakeDirectory.GroupLifecyclePoliciesPath;

    private static string Policy(int days = 180, string managed = "Selected", string emails = "admin@contoso.com;ops@contoso.com", string name = "default")
    {
        return "{ \"type\": \"" + PolicyType + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", \"properties\": { \"groupLifetimeInDays\": " + days
            + ", \"managedGroupTypes\": \"" + managed + "\", \"alternateNotificationEmails\": \"" + emails + "\" } }";
    }

    private static string Team(string uniqueName = "team-x", bool unified = true)
    {
        string groupTypes = unified ? "[\"Unified\"]" : "[]";
        return "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + uniqueName + "\", \"properties\": { \"displayName\": \"" + uniqueName
            + "\", \"groupTypes\": " + groupTypes + ", \"mailEnabled\": " + (unified ? "true" : "false") + ", \"securityEnabled\": " + (unified ? "false" : "true")
            + ", \"groupLifecyclePolicies\": [\"[reference('default').id]\"] }, \"dependsOn\": [\"[resourceId('" + PolicyType + "', 'default')]\"] }";
    }

    private static IReadOnlyList<FakeGraphRequest> LifecycleActions(Run run)
    {
        return run.Writes.Where(w => w.Path.EndsWith("/addGroup", StringComparison.Ordinal) || w.Path.EndsWith("/removeGroup", StringComparison.Ordinal)).ToList();
    }

    [Fact]
    public void Discovery_PermissionsAndEdgeKind()
    {
        var host = new ProviderHost();
        var provider = Assert.IsType<GroupLifecyclePoliciesProvider>(host.Provider(PolicyType));
        Assert.Equal(["Directory.ReadWrite.All"], provider.RequiredPermissions(null));
        Assert.Empty(provider.ImmutableProperties);
        Assert.True(host.EdgeKinds.Contains(EdgeKindOf("GroupLifecyclePolicy")));
    }

    [Fact]
    public async Task Create_PostsThePolicyWithoutAnId_AndTheRerunIsNoChange()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(Policy()));

        Check.Succeeded(run);
        var post = Assert.Single(run.Writes);
        Assert.Equal("POST", post.Method);
        Assert.Equal(PolicyPath, post.Path);
        Assert.Equal("{\"groupLifetimeInDays\":180,\"managedGroupTypes\":\"Selected\",\"alternateNotificationEmails\":\"admin@contoso.com;ops@contoso.com\"}", post.Body!.ToJsonString());
        Assert.Single(host.Directory.Objects(PolicyPath));

        var rerun = await host.CreateAsync(host.Resources(Policy(emails: "OPS@contoso.com; admin@contoso.com")));
        Assert.All(rerun.Executed.Operations, o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
        Check.NoWrites(rerun);
    }

    [Fact]
    public async Task WhatIf_OfANewPolicy_IsACreateWithoutWrites()
    {
        var host = new ProviderHost();

        var whatIf = await host.WhatIfAsync(host.Resources(Policy()));

        var row = Assert.Single(whatIf.Plan.Changes);
        Assert.Equal(ChangeType.Create, row.Type);
        Assert.False(row.HasError);
        Check.NoWrites(whatIf);
    }

    [Fact]
    public async Task ALifetimeChange_IsAModify_WhosePatchCarriesOnlyTheChangedProperty()
    {
        var host = new ProviderHost();
        await host.CreateAsync(host.Resources(Policy()));
        string id = host.Directory.Objects(PolicyPath).Single()["id"]!.GetValue<string>();

        var whatIf = await host.WhatIfAsync(host.Resources(Policy(days: 365)));
        Assert.Equal(ChangeType.Modify, Assert.Single(whatIf.Plan.Changes).Type);
        Check.NoWrites(whatIf);

        var run = await host.CreateAsync(host.Resources(Policy(days: 365)));

        Check.Succeeded(run);
        var patch = Assert.Single(run.Writes);
        Assert.Equal("PATCH", patch.Method);
        Assert.Equal(PolicyPath + "/" + id, patch.Path);
        Assert.Equal("{\"groupLifetimeInDays\":365}", patch.Body!.ToJsonString());
        Assert.Equal(365, host.Directory.FindById(id)!["groupLifetimeInDays"]!.GetValue<int>());
    }

    [Fact]
    public async Task AnExistingPolicy_IsAdopted_WhateverItsId()
    {
        var host = new ProviderHost();
        host.Directory.Add(PolicyPath, new JsonObject { ["groupLifetimeInDays"] = 180, ["managedGroupTypes"] = "All", ["alternateNotificationEmails"] = "admin@contoso.com;ops@contoso.com" });

        var run = await host.CreateAsync(host.Resources(Policy(managed: "All")));

        Check.Succeeded(run);
        Assert.All(run.Executed.Operations, o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
        Check.NoWrites(run);
    }

    [Fact]
    public async Task TwoLivePolicies_AreAnAmbiguousMatch()
    {
        var host = new ProviderHost();
        host.Directory.Add(PolicyPath, new JsonObject { ["groupLifetimeInDays"] = 180, ["managedGroupTypes"] = "All" });
        host.Directory.Add(PolicyPath, new JsonObject { ["groupLifetimeInDays"] = 365, ["managedGroupTypes"] = "None" });
        var key = new ResourceKey { Type = PolicyType, Values = new Dictionary<string, string> { ["id"] = "default" } };

        var ex = await Assert.ThrowsAsync<ProviderException>(() => host.Provider(PolicyType).ReadAsync(key, host.Fake, ProviderHost.Ct));

        Assert.Equal(ProviderException.AmbiguousMatch, ex.Code);
        Assert.All(host.Directory.Objects(PolicyPath).Select(o => o["id"]!.GetValue<string>()), id => Assert.Contains(id, ex.Message, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{ \"groupLifetimeInDays\": 0, \"managedGroupTypes\": \"All\" }", "groupLifetimeInDays")]
    [InlineData("{ \"groupLifetimeInDays\": 180, \"managedGroupTypes\": \"Some\" }", "managedGroupTypes 'Some'")]
    public async Task InvalidDeclarations_AreUnsupportedRows(string properties, string expected)
    {
        var host = new ProviderHost();
        var provider = host.Provider<GroupLifecyclePoliciesProvider>(PolicyType);
        var desired = await InstanceFactory.WithPropertiesAsync(host, Policy(), properties);

        var change = provider.Plan(desired, null, PlanOptions.Default);

        Assert.True(change.HasError);
        Assert.Contains(expected, change.UnsupportedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_RemovesThePolicy_WithTheRiskListed()
    {
        var host = new ProviderHost();
        await host.CreateAsync(host.Resources(Policy()));
        var document = host.Resources(Policy());
        var plan = await host.Planner().PlanAsync(document, DeploymentMode.Incremental, PlanMode.WhatIf, ProviderHost.Ct);
        var listed = Assert.Single(TemplateDeleter.FindDeclaredObjects(plan));

        var result = await host.DeleteDeclaredAsync(document);

        Assert.Contains("Microsoft 365 groups stop expiring", listed.Risk, StringComparison.Ordinal);
        Assert.True(result.Succeeded);
        Assert.Empty(host.Directory.Objects(PolicyPath));
    }

    [Fact]
    public void CompleteMode_ListsNoOrphans()
    {
        var provider = new ProviderHost().Provider<GroupLifecyclePoliciesProvider>(PolicyType);
        Assert.Equal(OrphanScope.Collection, provider.OrphanScope);
    }

    // ---- the groupLifecyclePolicies edge ----

    [Fact]
    public async Task AGroupDeclaringThePolicy_IsAddedOnce_AndTheRerunWritesNothing()
    {
        var host = new ProviderHost();
        string template = Policy() + ", " + Team();

        var whatIf = await host.WhatIfAsync(host.Resources(template));
        Assert.DoesNotContain(whatIf.Plan.Changes, c => c.HasError);
        Check.NoWrites(whatIf);

        var run = await host.CreateAsync(host.Resources(template));

        Check.Succeeded(run);
        string policyId = host.Directory.Objects(PolicyPath).Single()["id"]!.GetValue<string>();
        string groupId = host.IdOf("/groups", "uniqueName", "team-x");
        var add = Assert.Single(LifecycleActions(run));
        Assert.Equal(PolicyPath + "/" + policyId + "/addGroup", add.Path);
        Assert.Equal("{\"groupId\":\"" + groupId + "\"}", add.Body!.ToJsonString());
        Assert.Equal([groupId], host.Directory.LifecyclePolicyGroups(policyId));

        var rerun = await host.CreateAsync(host.Resources(template));
        Check.Succeeded(rerun);
        Assert.Empty(LifecycleActions(rerun));
    }

    [Fact]
    public async Task UndeclaringTheEdge_NeverRemovesTheGroup()
    {
        var host = new ProviderHost();
        await host.CreateAsync(host.Resources(Policy() + ", " + Team()));
        string policyId = host.Directory.Objects(PolicyPath).Single()["id"]!.GetValue<string>();
        string team = Team().Replace(", \"groupLifecyclePolicies\": [\"[reference('default').id]\"]", string.Empty, StringComparison.Ordinal);

        var run = await host.CreateAsync(host.Resources(Policy() + ", " + team));

        Check.Succeeded(run);
        Assert.Empty(LifecycleActions(run));
        Assert.Single(host.Directory.LifecyclePolicyGroups(policyId));
    }

    [Theory]
    [InlineData("All")]
    [InlineData("None")]
    public async Task APolicyThatIsNotSelected_IsAnEdgeValidationError_WithoutActions(string managed)
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(Policy(managed: managed) + ", " + Team()));

        Assert.False(run.Succeeded);
        var team = run.Op("team-x");
        Assert.Equal(EngineDiagnosticCodes.EdgeValidationFailed, team.ErrorCode);
        Assert.Contains("managedGroupTypes '" + managed + "'", team.Error, StringComparison.Ordinal);
        Assert.Empty(LifecycleActions(run));
    }

    [Fact]
    public async Task ASecurityGroup_CannotJoinThePolicy()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(Policy() + ", " + Team("sec-x", unified: false)));

        Assert.False(run.Succeeded);
        Assert.Contains("only Microsoft 365 groups", run.Op("sec-x").Error, StringComparison.Ordinal);
        Assert.Empty(LifecycleActions(run));
    }

    private static Engine.Edges.EdgeKind EdgeKindOf(string name)
    {
        return Enum.Parse<Engine.Edges.EdgeKind>(name);
    }
}
