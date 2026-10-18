using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>The default cross-tenant access configuration (a resettable singleton) and the partner configurations keyed by tenant id.</summary>
public sealed class CrossTenantAccessProviderTests
{
    private const string DefaultType = CrossTenantAccessPolicyProvider.ResourceType;
    private const string PartnerType = CrossTenantAccessPartnersProvider.ResourceType;
    private const string PartnerTenant = "2f0a1f3e-5b6c-4d7e-8f90-a1b2c3d4e5f6";

    private static string Default(string properties, string name = "default")
    {
        return "{ \"type\": \"" + DefaultType + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", \"properties\": " + properties + " }";
    }

    private static string Partner(string properties = "{ \"inboundTrust\": { \"isMfaAccepted\": true, \"isCompliantDeviceAccepted\": false, \"isHybridAzureADJoinedDeviceAccepted\": false } }", string tenantId = PartnerTenant)
    {
        return "{ \"type\": \"" + PartnerType + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + tenantId + "\", \"properties\": " + properties + " }";
    }

    // ---- default ----

    [Fact]
    public async Task Default_DeclaringTheServiceDefaults_IsNoChange()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(Default("{ \"b2bCollaborationInbound\": { \"usersAndGroups\": { \"accessType\": \"allowed\", \"targets\": [ { \"target\": \"AllUsers\", \"targetType\": \"user\" } ] } } }")));

        Check.Succeeded(run);
        Assert.All(run.Executed.Operations, o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
        Check.NoWrites(run);
        Assert.Equal("GET " + FakeDirectory.CrossTenantDefaultPath, run.Requests[0].ToString());
    }

    [Fact]
    public async Task Default_AChangedSetting_IsOnePatch_CarryingTheWholeObjectWithItsUndeclaredMembers()
    {
        var host = new ProviderHost();
        string blockInbound = Default("{ \"b2bCollaborationInbound\": { \"usersAndGroups\": { \"accessType\": \"blocked\", \"targets\": [ { \"target\": \"AllUsers\", \"targetType\": \"user\" } ] } } }");

        var whatIf = await host.WhatIfAsync(host.Resources(blockInbound));
        Assert.Equal(ChangeType.Modify, Assert.Single(whatIf.Plan.Changes).Type);
        Check.NoWrites(whatIf);

        var run = await host.CreateAsync(host.Resources(blockInbound));

        Check.Succeeded(run);
        var patch = Assert.Single(run.Writes);
        Assert.Equal("PATCH", patch.Method);
        Assert.Equal(FakeDirectory.CrossTenantDefaultPath, patch.Path);
        var inbound = patch.Body!["b2bCollaborationInbound"]!;
        Assert.Equal("blocked", inbound["usersAndGroups"]!["accessType"]!.GetValue<string>());
        Assert.Equal("allowed", inbound["applications"]!["accessType"]!.GetValue<string>());
        Assert.Single(patch.Body!.AsObject());
        Assert.Equal("allowed", host.Directory.CrossTenantDefault["b2bCollaborationInbound"]!["applications"]!["accessType"]!.GetValue<string>());

        var rerun = await host.CreateAsync(host.Resources(blockInbound));
        Check.NoWrites(rerun);
    }

    [Theory]
    [InlineData("{ \"isServiceDefault\": true }", "isServiceDefault")]
    [InlineData("{ \"automaticUserConsentSettings\": { \"inboundAllowed\": true } }", "automaticUserConsentSettings")]
    public async Task Default_ReadOnlyProperties_AreUnsupportedRows(string properties, string expected)
    {
        var host = new ProviderHost();
        var desired = await InstanceFactory.WithPropertiesAsync(host, Default("{ }"), properties);

        var change = host.Provider(DefaultType).Plan(desired, await host.Provider(DefaultType).ReadAsync(host.Provider(DefaultType).ResolveKey(desired), host.Fake, ProviderHost.Ct), PlanOptions.Default);

        Assert.True(change.HasError);
        Assert.Contains(expected, change.UnsupportedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Default_Delete_ResetsToTheSystemDefault_WithTheRiskListed()
    {
        var host = new ProviderHost();
        await host.CreateAsync(host.Resources(Default("{ \"inboundTrust\": { \"isMfaAccepted\": true } }")));
        Assert.False(host.Directory.CrossTenantDefault["isServiceDefault"]!.GetValue<bool>());
        var document = host.Resources(Default("{ \"inboundTrust\": { \"isMfaAccepted\": true } }"));
        var plan = await host.Planner().PlanAsync(document, DeploymentMode.Incremental, PlanMode.WhatIf, ProviderHost.Ct);
        var listed = Assert.Single(TemplateDeleter.FindDeclaredObjects(plan));

        var result = await host.DeleteDeclaredAsync(document);

        Assert.Null(listed.Refusal);
        Assert.Contains("Microsoft service defaults", listed.Risk, StringComparison.Ordinal);
        Assert.True(result.Succeeded);
        Assert.Contains(host.Fake.Writes, w => w.Method == "POST" && w.Path == FakeDirectory.CrossTenantDefaultPath + "/resetToSystemDefault");
        Assert.True(host.Directory.CrossTenantDefault["isServiceDefault"]!.GetValue<bool>());
        Assert.False(host.Directory.CrossTenantDefault["inboundTrust"]!["isMfaAccepted"]!.GetValue<bool>());
    }

    // ---- partners ----

    [Fact]
    public async Task Partner_Create_PostsTheTenantId_AndTheRerunIsNoChange()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(Partner()));

        Check.Succeeded(run);
        var post = Assert.Single(run.Writes);
        Assert.Equal("POST", post.Method);
        Assert.Equal(FakeDirectory.CrossTenantPartnersPath, post.Path);
        Assert.Equal(PartnerTenant, post.Body!["tenantId"]!.GetValue<string>());
        Assert.Equal("GET " + FakeDirectory.CrossTenantPartnersPath + "/" + PartnerTenant, run.Requests[0].ToString());
        Assert.Equal(PartnerTenant, run.GraphIdOf("/providers/Microsoft.Entra/policies/crossTenantAccessPolicy/partners/" + PartnerTenant));

        var rerun = await host.CreateAsync(host.Resources(Partner()));
        Assert.All(rerun.Executed.Operations, o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
        Check.NoWrites(rerun);
    }

    [Fact]
    public async Task Partner_AChangedSetting_IsAPatchWithoutTheKey()
    {
        var host = new ProviderHost();
        host.Directory.AddCrossTenantPartner(PartnerTenant, p => p["inboundTrust"] = new JsonObject { ["isMfaAccepted"] = false, ["isCompliantDeviceAccepted"] = true, ["isHybridAzureADJoinedDeviceAccepted"] = false });

        var run = await host.CreateAsync(host.Resources(Partner("{ \"inboundTrust\": { \"isMfaAccepted\": true }, \"isServiceProvider\": true }")));

        Check.Succeeded(run);
        var patch = Assert.Single(run.Writes);
        Assert.Equal("PATCH", patch.Method);
        Assert.Equal(FakeDirectory.CrossTenantPartnersPath + "/" + PartnerTenant, patch.Path);
        Assert.Null(patch.Body!["tenantId"]);
        Assert.True(patch.Body!["inboundTrust"]!["isMfaAccepted"]!.GetValue<bool>());
        Assert.True(patch.Body!["inboundTrust"]!["isCompliantDeviceAccepted"]!.GetValue<bool>());
        Assert.True(patch.Body!["isServiceProvider"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("{ \"isInMultiTenantOrganization\": true }", "isInMultiTenantOrganization")]
    public async Task Partner_ReadOnlyProperties_AreUnsupportedRows(string properties, string expected)
    {
        var host = new ProviderHost();
        var desired = await InstanceFactory.WithPropertiesAsync(host, Partner(), properties);

        var change = host.Provider(PartnerType).Plan(desired, null, PlanOptions.Default);

        Assert.True(change.HasError);
        Assert.Contains(expected, change.UnsupportedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Partner_Delete_RemovesIt_AndAnIdentitySynchronizationPolicyIsExplained()
    {
        var host = new ProviderHost();
        host.Directory.AddCrossTenantPartner(PartnerTenant);
        var result = await host.DeleteDeclaredAsync(host.Resources(Partner()));
        Assert.True(result.Succeeded);
        Assert.Empty(host.Directory.Objects(FakeDirectory.CrossTenantPartnersPath));

        var synced = new ProviderHost();
        synced.Directory.AddCrossTenantPartner(PartnerTenant);
        synced.Directory.AddPartnerIdentitySynchronization(PartnerTenant);
        var refused = await synced.DeleteDeclaredAsync(synced.Resources(Partner()));
        Assert.False(refused.Succeeded);
        Assert.Contains("identitySynchronization", Assert.Single(refused.Outcomes).Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Partner_CompleteMode_ListsUndeclaredPartnersByTenantId()
    {
        var host = new ProviderHost();
        const string other = "9a8b7c6d-5e4f-4a3b-9c2d-1e0f9a8b7c6d";
        host.Directory.AddCrossTenantPartner(PartnerTenant);
        host.Directory.AddCrossTenantPartner(other);

        var candidates = await host.Provider<CrossTenantAccessPartnersProvider>(PartnerType).ListOrphanCandidatesAsync(new OrphanQuery(), host.Fake, ProviderHost.Ct);

        Assert.Equal([PartnerTenant, other], candidates.Select(c => c.Name).Order(StringComparer.Ordinal).ToArray());
    }
}
