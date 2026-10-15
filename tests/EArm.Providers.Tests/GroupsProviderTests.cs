using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Engine.WhatIf;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>The groups provider: kind defaults, dynamic groups, licenses, immutables, destructive deletes and the soft-deleted Microsoft 365 group.</summary>
public sealed class GroupsProviderTests
{
    private const string Type = "Microsoft.Entra/groups";
    private const string SkuId = "11111111-2222-3333-4444-555555555555";

    private static string Group(string name, string properties)
    {
        return Templates.Resource(Type, name, properties);
    }

    private static void SeedSkus(ProviderHost host)
    {
        host.Directory.Add("/subscribedSkus", new JsonObject { ["skuId"] = SkuId, ["skuPartNumber"] = "ENTERPRISEPREMIUM", ["prepaidUnits"] = new JsonObject { ["enabled"] = 10 }, ["consumedUnits"] = 1 });
    }

    [Fact]
    public void Discovery_RegistersGroupsProvider()
    {
        var registry = ProviderDiscovery.CreateRegistry(EArm.Core.Types.ResourceTypeCatalog.Default);
        Assert.True(registry.TryGet(Type, out var provider));
        Assert.IsType<GroupsProvider>(provider);
        Assert.IsAssignableFrom<IDeleteWarningSource>(provider);
    }

    [Fact]
    public async Task G1_SecurityGroup_GetsTheKindDefaults_AndADerivedMailNickname()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(Group("Harness_Grp One!!", """{ "displayName": "Grp One" }""")));

        Check.Succeeded(run);
        var created = host.Directory.Find("/groups", "uniqueName", "Harness_Grp One!!");
        Assert.NotNull(created);
        Assert.False(created["mailEnabled"]!.GetValue<bool>());
        Assert.True(created["securityEnabled"]!.GetValue<bool>());
        Assert.Empty(created["groupTypes"]!.AsArray());
        Assert.Equal("harness_grp-one-", created["mailNickname"]!.GetValue<string>());
        var upsert = Assert.Single(run.Writes);
        Assert.Equal("PATCH", upsert.Method);
        Assert.StartsWith("/groups(uniqueName='Harness_Grp", upsert.Path, StringComparison.Ordinal);
        Assert.True(upsert.HasHeader("Prefer", "create-if-missing"));
        Assert.Contains("Group.ReadWrite.All", run.Plan.RequiredPermissions);
        Assert.DoesNotContain("GroupMember.ReadWrite.All", run.Plan.RequiredPermissions);
        // Review 3I (intentional change): a security group is soft-deleted too, so its create probes the recycle bin (by uniqueName).
        Assert.Contains(run.Requests, r => r.Method == "GET" && r.Path.StartsWith("/directory/deletedItems/microsoft.graph.group", StringComparison.Ordinal));
    }

    [Fact]
    public async Task G2_Microsoft365Group_GetsMailEnabledDefaults_AndProbesTheRecycleBin()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(Group("m365-team", """{ "displayName": "M365 Team", "groupTypes": ["Unified"] }""")));

        Check.Succeeded(run);
        var created = host.Directory.Find("/groups", "uniqueName", "m365-team")!;
        Assert.True(created["mailEnabled"]!.GetValue<bool>());
        Assert.False(created["securityEnabled"]!.GetValue<bool>());
        var probe = Assert.Single(run.Requests, r => r.Path.StartsWith("/directory/deletedItems/microsoft.graph.group", StringComparison.Ordinal));
        Assert.Equal("mailNickname eq 'm365-team'", probe.UrlParameters!["$filter"]);
    }

    [Fact]
    public async Task G3_DynamicGroup_InjectsProcessingOn_AndRequiresARule()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(Group("dyn-sales", """{ "displayName": "Sales (dynamic)", "groupTypes": ["DynamicMembership"], "membershipRule": "(user.department -eq \"Sales\")" }""")));
        Check.Succeeded(run);
        Assert.Equal("On", host.Directory.Find("/groups", "uniqueName", "dyn-sales")!["membershipRuleProcessingState"]!.GetValue<string>());

        var noRule = await new ProviderHost().CreateAsync(host.Resources(Group("dyn-norule", """{ "displayName": "No rule", "groupTypes": ["DynamicMembership"] }""")));
        Assert.False(noRule.Succeeded);
        Assert.Contains("membershipRule", noRule.OpOfType(Type).Error, StringComparison.Ordinal);
        Check.NoWrites(noRule);
    }

    [Fact]
    public async Task G4_MailEnabledSecurityGroup_CannotBeCreated()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(Group("mes", """{ "displayName": "MES", "mailEnabled": true, "securityEnabled": true }""")));

        Assert.False(run.Succeeded);
        Assert.Contains("mail-enabled security", run.OpOfType(Type).Error, StringComparison.Ordinal);
        Check.NoWrites(run);
    }

    [Fact]
    public async Task G5_MembershipRuleChange_PausesProcessingFirst()
    {
        var host = new ProviderHost();
        var toggle = host.Directory.AddGroup("dyn-toggle", "Dyn toggle", g =>
        {
            g["groupTypes"] = new JsonArray("DynamicMembership");
            g["membershipRule"] = "old";
            g["membershipRuleProcessingState"] = "On";
        });
        string toggleId = toggle["id"]!.GetValue<string>();

        var run = await host.CreateAsync(host.Resources(Group("dyn-toggle", """{ "displayName": "Dyn toggle", "groupTypes": ["DynamicMembership"], "membershipRule": "new" }""")));

        Check.Succeeded(run);
        var patches = run.Writes.Where(w => w.Method == "PATCH" && w.Path == $"/groups/{toggleId}").ToList();
        Assert.Equal(2, patches.Count);
        Assert.Equal("""{"membershipRuleProcessingState":"Paused"}""", patches[0].Body!.ToJsonString());
        Assert.Equal("new", patches[1].Body!["membershipRule"]!.GetValue<string>());
        Assert.Equal("On", patches[1].Body!["membershipRuleProcessingState"]!.GetValue<string>());
    }

    [Fact]
    public async Task G6_AssignedLicenses_AreAddedAndRemovedThroughAssignLicense()
    {
        var host = new ProviderHost();
        SeedSkus(host);
        var licensed = host.Resources(Group("licensed", """{ "displayName": "Licensed", "assignedLicenses": [{ "skuPartNumber": "ENTERPRISEPREMIUM", "disabledPlans": [] }] }"""));

        var whatIf = await host.WhatIfAsync(licensed);
        Check.NoWrites(whatIf);
        var row = whatIf.ChangeOfType(Type);
        Assert.Contains(row.Deltas, d => d.Path.ToString() == "properties.assignedLicenses");

        var run = await host.CreateAsync(licensed);
        Check.Succeeded(run);
        string licensedId = host.IdOf("/groups", "uniqueName", "licensed");
        var assign = Assert.Single(run.Writes, w => w.Path == $"/groups/{licensedId}/assignLicense");
        Assert.Equal(SkuId, assign.Body!["addLicenses"]![0]!["skuId"]!.GetValue<string>());
        Assert.Empty(assign.Body!["removeLicenses"]!.AsArray());
        Assert.Contains("LicenseAssignment.ReadWrite.All", run.Plan.RequiredPermissions);
        Assert.Contains("LicenseAssignment.Read.All", run.Plan.RequiredPermissions);
        Assert.DoesNotContain("Organization.Read.All", run.Plan.RequiredPermissions);

        var rerun = await host.CreateAsync(licensed);
        Check.Succeeded(rerun);
        Assert.DoesNotContain(rerun.Writes, w => w.Path.EndsWith("/assignLicense", StringComparison.Ordinal));

        var removed = await host.CreateAsync(host.Resources(Group("licensed", """{ "displayName": "Licensed", "assignedLicenses": [] }""")));
        Check.Succeeded(removed);
        var unassign = Assert.Single(removed.Writes, w => w.Path == $"/groups/{licensedId}/assignLicense");
        Assert.Equal(SkuId, unassign.Body!["removeLicenses"]![0]!.GetValue<string>());
        Assert.Empty(unassign.Body!["addLicenses"]!.AsArray());
    }

    [Fact]
    public async Task G7_ImmutableChanges_AreErrorsWithoutWrites()
    {
        var host = new ProviderHost();
        host.Directory.AddGroup("imm", "Imm", g => g["mailNickname"] = "imm-old");

        var run = await host.CreateAsync(host.Resources(Group("imm", """{ "displayName": "Imm", "mailNickname": "imm-new" }""")));
        Assert.False(run.Succeeded);
        Assert.Equal(EngineDiagnosticCodes.ImmutablePropertyChanged, run.OpOfType(Type).ErrorCode);
        Check.NoWrites(run);

        var unified = new ProviderHost();
        unified.Directory.AddGroup("unified-imm", "U", g =>
        {
            g["groupTypes"] = new JsonArray("Unified");
            g["mailEnabled"] = true;
            g["securityEnabled"] = false;
        });
        var dropUnified = await unified.CreateAsync(unified.Resources(Group("unified-imm", """{ "displayName": "U", "groupTypes": [] }""")));
        Assert.False(dropUnified.Succeeded);
        Assert.Contains("'Unified'", dropUnified.OpOfType(Type).Error, StringComparison.Ordinal);
        Check.NoWrites(dropUnified);
    }

    [Fact]
    public async Task G8_CompleteMode_DeletesTheOrphanSecurityGroup_IntoTheRecycleBin_WithoutADestructiveDeleteWarning()
    {
        var host = new ProviderHost();
        var first = await host.CreateAsync(host.Resources(Group("orphan", """{ "displayName": "Orphan" }""")));
        Check.Succeeded(first);

        var whatIf = await host.WhatIfAsync(host.Resources(Group("other", """{ "displayName": "Other" }""")), DeploymentMode.Complete);
        // Review 3I (intentional change): security groups wait 30 days in the recycle bin (Learn group-delete), so the delete is not flagged as permanent.
        Assert.DoesNotContain(whatIf.Plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.DestructiveDelete);
        Check.NoWrites(whatIf);

        var run = await host.CreateAsync(host.Resources(Group("other", """{ "displayName": "Other" }""")), DeploymentMode.Complete);

        Check.Succeeded(run);
        Assert.Equal(1, run.Executed.DeletedCount);
        Assert.DoesNotContain(run.Plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.DestructiveDelete);
        Assert.Null(host.Directory.Find("/groups", "uniqueName", "orphan"));
        Assert.Contains(host.Directory.Objects("/directory/deletedItems"), o => o["uniqueName"]!.GetValue<string>() == "orphan");
    }

    [Fact]
    public async Task G9_DynamicGroupWithLiteralMembers_IsAnOfflineValidationError()
    {
        var host = new ProviderHost();

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => host.WhatIfAsync(host.Resources(Group("dyn-members", """{ "displayName": "x", "groupTypes": ["DynamicMembership"], "membershipRule": "r", "members": ["00000000-0000-0000-0000-000000000001"] }"""))));

        Assert.Contains(ex.Diagnostics, d => d.Code == "DynamicMembershipConflict");
    }

    [Fact]
    public async Task SoftDeletedMicrosoft365Group_IsAConflict_UnlessRestored()
    {
        var host = new ProviderHost();
        var deleted = host.Directory.AddDeleted("/groups", new JsonObject
        {
            ["uniqueName"] = "m365-team",
            ["displayName"] = "Old team",
            ["mailNickname"] = "m365-team",
            ["groupTypes"] = new JsonArray("Unified"),
            ["mailEnabled"] = true,
            ["securityEnabled"] = false,
        });
        var document = host.Resources(Group("m365-team", """{ "displayName": "M365 Team", "groupTypes": ["Unified"] }"""));

        var run = await host.CreateAsync(document);
        Assert.False(run.Succeeded);
        Assert.Equal(ProviderException.SoftDeletedConflict, run.OpOfType(Type).ErrorCode);
        Check.NoWrites(run);

        host.OnSoftDeletedConflict = SoftDeletedConflict.Restore;
        var restore = await host.CreateAsync(document);

        Check.Succeeded(restore);
        Assert.Equal("POST", restore.Writes[0].Method);
        Assert.Equal("/directory/deletedItems/" + deleted["id"]!.GetValue<string>() + "/restore", restore.Writes[0].Path);
        Assert.Contains(restore.Writes.Skip(1), w => w.Method == "PATCH");
        Assert.Equal(0, host.Directory.Count("/directory/deletedItems"));
        Assert.Equal("M365 Team", host.Directory.Find("/groups", "uniqueName", "m365-team")!["displayName"]!.GetValue<string>());
        Assert.Equal(deleted["id"]!.GetValue<string>(), restore.OpOfType(Type).GraphId);
    }
}
