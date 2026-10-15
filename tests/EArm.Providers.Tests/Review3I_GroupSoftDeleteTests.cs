using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Engine.Providers;
using EArm.Engine.State;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// Review 3I, finding 3. Learn group-delete (https://learn.microsoft.com/en-us/graph/api/group-delete): "both Microsoft 365 and security groups are moved to a
/// temporary container and can be restored within 30 days ... This doesn't apply to Distribution groups". The create probes the recycle bin for every
/// group kind it can create, restores on request, and only the adopt-only kinds are reported as permanently deleted.
/// </summary>
public sealed class Review3I_GroupSoftDeleteTests
{
    private const string Type = "Microsoft.Entra/groups";

    private static string Group(string name, string properties)
    {
        return Templates.Resource(Type, name, properties);
    }

    private static JsonObject SeedDeletedSecurityGroup(ProviderHost host, string uniqueName, string? nickname = null)
    {
        return host.Directory.AddDeleted("/groups", new JsonObject
        {
            ["uniqueName"] = uniqueName,
            ["displayName"] = "Old " + uniqueName,
            ["mailNickname"] = nickname ?? uniqueName,
            ["groupTypes"] = new JsonArray(),
            ["mailEnabled"] = false,
            ["securityEnabled"] = true,
        });
    }

    [Fact]
    public async Task ASoftDeletedSecurityGroupIsAConflictUnlessRestored()
    {
        var host = new ProviderHost();
        var deleted = SeedDeletedSecurityGroup(host, "sec-team");
        var document = host.Resources(Group("sec-team", """{ "displayName": "Security Team" }"""));

        var run = await host.CreateAsync(document);

        Assert.False(run.Succeeded);
        Assert.Equal(ProviderException.SoftDeletedConflict, run.OpOfType(Type).ErrorCode);
        Assert.Contains("soft-deleted security group", run.OpOfType(Type).Error, StringComparison.Ordinal);
        Check.NoWrites(run);

        host.OnSoftDeletedConflict = SoftDeletedConflict.Restore;
        var restore = await host.CreateAsync(document);

        Check.Succeeded(restore);
        Assert.Equal("POST", restore.Writes[0].Method);
        Assert.Equal("/directory/deletedItems/" + deleted["id"]!.GetValue<string>() + "/restore", restore.Writes[0].Path);
        Assert.Contains(restore.Writes.Skip(1), w => w.Method == "PATCH");
        Assert.Equal(0, host.Directory.Count("/directory/deletedItems"));
        Assert.Equal("Security Team", host.Directory.Find("/groups", "uniqueName", "sec-team")!["displayName"]!.GetValue<string>());
        Assert.Equal(deleted["id"]!.GetValue<string>(), restore.OpOfType(Type).GraphId);
    }

    [Fact]
    public async Task TheSecurityGroupCreateProbesTheRecycleBinByUniqueName()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(Group("probe-me", """{ "displayName": "Probe" }""")));

        Check.Succeeded(run);
        var probe = Assert.Single(run.Requests, r => r.Path.StartsWith("/directory/deletedItems/microsoft.graph.group", StringComparison.Ordinal));
        Assert.Equal("uniqueName eq 'probe-me'", probe.UrlParameters!["$filter"]);
    }

    [Fact]
    public async Task ADynamicSecurityGroupIsProbedToo()
    {
        var host = new ProviderHost();
        SeedDeletedSecurityGroup(host, "dyn");

        var run = await host.CreateAsync(host.Resources(Group("dyn", """{ "displayName": "Dyn", "groupTypes": ["DynamicMembership"], "membershipRule": "user.department -eq \"x\"" }""")));

        Assert.Equal(ProviderException.SoftDeletedConflict, run.OpOfType(Type).ErrorCode);
    }

    [Fact]
    public async Task ADeletedSecurityGroupThatOnlySharesTheMailNicknameIsNotAConflict()
    {
        // mailNickname is reserved for Microsoft 365 groups only: a security group may reuse the alias of a deleted one.
        var host = new ProviderHost();
        SeedDeletedSecurityGroup(host, "old-name", nickname: "shared");

        var run = await host.CreateAsync(host.Resources(Group("new-name", """{ "displayName": "New", "mailNickname": "shared" }""")));

        Check.Succeeded(run);
        Assert.NotNull(host.Directory.Find("/groups", "uniqueName", "new-name"));
    }

    [Fact]
    public async Task AnUnreadableRecycleBinDoesNotBlockASecurityGroupCreate()
    {
        var host = new ProviderHost();
        host.Fake.Failures.Forbid("/directory/deletedItems", "GET");

        var run = await host.CreateAsync(host.Resources(Group("no-probe", """{ "displayName": "No probe" }""")));

        Check.Succeeded(run);
        Assert.Contains(run.Executed.Diagnostics, d => d.Code == EArm.Engine.EngineDiagnosticCodes.SoftDeleteProbeSkipped);
    }

    [Fact]
    public async Task DeletingASecurityGroupSendsItToTheRecycleBinWhereItKeepsItsKey()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(Group("bin-me", """{ "displayName": "Bin" }"""))));

        var deletion = await host.DeleteAllAsync();

        Assert.True(deletion.Succeeded);
        Assert.Null(host.Directory.Find("/groups", "uniqueName", "bin-me"));
        Assert.Contains(host.Directory.Objects("/directory/deletedItems"), o => o["uniqueName"]!.GetValue<string>() == "bin-me");

        var again = await host.CreateAsync(host.Resources(Group("bin-me", """{ "displayName": "Bin" }""")));
        Assert.Equal(ProviderException.SoftDeletedConflict, again.OpOfType(Type).ErrorCode);
    }

    [Fact]
    public void OnlyTheAdoptOnlyKindsAreReportedAsPermanentlyDeleted()
    {
        var provider = new ProviderHost().Provider<GroupsProvider>(Type);
        StateEntry Entry(string kind) => new()
        {
            Id = EArm.Core.ResourceIdentifier.Create(Type, "g"),
            Type = Type,
            ApiVersion = ProviderHost.ApiVersion,
            GraphId = "g1",
            Extra = new JsonObject { ["kind"] = kind },
        };

        Assert.Null(provider.DescribeDeleteRisk(Entry("security"), null));
        Assert.Null(provider.DescribeDeleteRisk(Entry("microsoft365"), null));
        Assert.NotNull(provider.DescribeDeleteRisk(Entry("distribution"), null));
        Assert.NotNull(provider.DescribeDeleteRisk(Entry("mailEnabledSecurity"), null));

        var distribution = new JsonObject { ["mailEnabled"] = true, ["securityEnabled"] = false, ["groupTypes"] = new JsonArray() };
        var security = new JsonObject { ["mailEnabled"] = false, ["securityEnabled"] = true, ["groupTypes"] = new JsonArray() };
        Assert.Contains("distribution list", provider.DescribeDeleteRisk(Entry("security"), new ExistingResource("g1", distribution, distribution))!, StringComparison.Ordinal);
        Assert.Null(provider.DescribeDeleteRisk(Entry("distribution"), new ExistingResource("g1", security, security)));
    }

    [Fact]
    public async Task ADistributionListIsDeletedImmediatelyByTheFake()
    {
        var host = new ProviderHost();
        var distribution = host.Directory.AddGroup("dl", "DL", g => { g["mailEnabled"] = true; g["securityEnabled"] = false; });

        await host.Fake.DeleteAsync(new EArm.Graph.GraphRequest("/groups/" + distribution["id"]), ProviderHost.Ct);

        Assert.Equal(0, host.Directory.Count("/directory/deletedItems"));
        Assert.Null(host.Directory.FindById(distribution["id"]!.GetValue<string>()));
    }
}
