using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>
/// The tenant policy types against a real tenant. Written for the first real-tenant pass and not yet run:
/// <list type="bullet">
/// <item><see cref="It_AppPolicies_TokenLifetimeAndClaimsMappingRoundTrip"/> (tenant gate only): prefixed token lifetime and claims mapping policies,
/// assigned to a prefixed application and its service principal, then deleted; nothing tenant-wide.</item>
/// <item><see cref="It_AppManagement_CustomPolicyAndDefaultDescriptionUnderTheRestoreGuard"/> (<c>EARM_IT_ALLOW_APP_MANAGEMENT=1</c>): a DISABLED prefixed custom
/// policy assigned to a prefixed application, and the description (only) of the tenant default policy, snapshotted and restored.</item>
/// <item><see cref="It_CrossTenant_InboundTrustToggleAndPartnerUnderTheRestoreGuard"/> (<c>EARM_IT_ALLOW_CROSS_TENANT=1</c>): one inbound trust toggle of
/// the default configuration, snapshotted and restored, and a partner configuration for <c>EARM_IT_PARTNER_TENANT</c> only when that tenant has none.</item>
/// <item><see cref="It_GroupLifecycle_SelectedPolicyAndGroupEdge_OnlyInATenantWithoutAPolicy"/> (<c>EARM_IT_ALLOW_GROUP_LIFECYCLE=1</c>): a Selected
/// expiration policy and a prefixed Microsoft 365 group joining it, only when the tenant has no policy (an existing one is only compared); the policy
/// is deleted again.</item>
/// </list>
/// </summary>
[Collection(RealTenantCollection.Name)]
public sealed class TenantPolicyScenarioTests : IntegrationTestBase
{
    private const string TokenLifetimePolicies = "/policies/tokenLifetimePolicies";
    private const string ClaimsMappingPolicies = "/policies/claimsMappingPolicies";
    private const string AppManagementPolicies = "/policies/appManagementPolicies";
    private const string DefaultAppManagementPolicy = "/policies/defaultAppManagementPolicy";
    private const string CrossTenantDefault = "/policies/crossTenantAccessPolicy/default";
    private const string CrossTenantPartners = "/policies/crossTenantAccessPolicy/partners";
    private const string GroupLifecyclePolicies = "/groupLifecyclePolicies";

    public TenantPolicyScenarioTests(IntegrationFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task It_AppPolicies_TokenLifetimeAndClaimsMappingRoundTrip()
    {
        Fixture.Require(Needs.Tenant);
        var run = Fixture.NewRun("apppol", "app-policies.entra.json");
        try
        {
            AssertCreated(await Fixture.Cli.CreateAsync(run, Ct), "create");

            var tokenPolicy = await FindListedEventuallyAsync(TokenLifetimePolicies, Fixture.Name("tlp"));
            var claimsPolicy = await FindListedEventuallyAsync(ClaimsMappingPolicies, Fixture.Name("cmp"));
            var app = await ApplicationByNameAsync(Fixture.Name("policy-app"), "id", "appId");
            Assert.NotNull(app);
            var assigned = await EventuallyAsync(() => Graph.ListAsync(new GraphRequest($"/applications/{app.RequireId()}/tokenLifetimePolicies").WithSelect("id"), Ct), list => list.Count == 1);
            Assert.Equal(tokenPolicy.RequireId(), Assert.Single(assigned).RequireId());
            var sp = await Graph.GetObjectAsync(new GraphRequest($"/servicePrincipals(appId='{app.Text("appId")}')").WithSelect("id"), Ct);
            var mapped = await EventuallyAsync(() => Graph.ListAsync(new GraphRequest($"/servicePrincipals/{sp.RequireId()}/claimsMappingPolicies").WithSelect("id"), Ct), list => list.Count == 1);
            Assert.Equal(claimsPolicy.RequireId(), Assert.Single(mapped).RequireId());
            Observe($"token lifetime definition as served: {tokenPolicy["definition"]?.ToJsonString()}");

            await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "what-if after create");

            var longer = run.WithParameter("accessTokenLifetime", "04:00:00");
            AssertChange(await Fixture.Cli.WhatIfAsync(longer, Ct, "--exit-code-on-changes"), "/tokenLifetimePolicies/" + Fixture.Name("tlp"), "Modify", "what-if with a longer lifetime");
            AssertCreated(await Fixture.Cli.CreateAsync(longer, Ct), "create with a longer lifetime");
            await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(longer, Ct, "--exit-code-on-changes"), "what-if after the lifetime change");
        }
        finally
        {
            AssertCreated(await Fixture.Cli.DeleteAllAsync(run, Ct), "delete");
        }

        Assert.Null(await FindListedAsync(TokenLifetimePolicies, Fixture.Name("tlp")));
        Assert.Null(await FindListedAsync(ClaimsMappingPolicies, Fixture.Name("cmp")));
    }

    [Fact]
    public async Task It_AppManagement_CustomPolicyAndDefaultDescriptionUnderTheRestoreGuard()
    {
        Fixture.Require(Needs.AppManagement);
        var guard = new TenantPolicyRestoreGuard(Graph, Fixture.RunDirectory, DefaultAppManagementPolicy, "defaultAppManagementPolicy");
        var log = new List<string>();
        var snapshot = await guard.SnapshotAsync(Ct);
        string original = snapshot.Text("description") ?? string.Empty;
        var run = Fixture.NewRun("appmgmt", "app-management.entra.json").WithParameter("defaultDescription", original);
        try
        {
            // The snapshot's description deployed as it is: the default policy is NoChange (the custom policy and the application are creates).
            var preview = await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes");
            Assert.Equal("NoChange", preview.ChangeTypeOf("/defaultAppManagementPolicy/default"));

            var changed = run.WithParameter("defaultDescription", original + " (" + Fixture.Prefix + ")");
            AssertCreated(await Fixture.Cli.CreateAsync(changed, Ct), "create");
            var live = await EventuallyAsync(() => Graph.GetObjectAsync(new GraphRequest(DefaultAppManagementPolicy), Ct), found => found.Text("description")?.EndsWith("(" + Fixture.Prefix + ")", StringComparison.Ordinal) == true, seconds: 60);
            Assert.Equal(snapshot["isEnabled"]?.ToJsonString(), live?["isEnabled"]?.ToJsonString());

            var policy = await FindListedEventuallyAsync(AppManagementPolicies, Fixture.Name("amp"));
            Assert.False((bool?)policy["isEnabled"] ?? true, "The test policy must stay disabled.");
            var app = await ApplicationByNameAsync(Fixture.Name("amp-app"), "id");
            var assigned = await EventuallyAsync(() => Graph.ListAsync(new GraphRequest($"/applications/{app.RequireId()}/appManagementPolicies").WithSelect("id"), Ct), list => list.Count == 1);
            Assert.Equal(policy.RequireId(), Assert.Single(assigned).RequireId());
            await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(changed, Ct, "--exit-code-on-changes"), "what-if after create");

            // deployment delete keeps the default policy (NotDeletable) and removes the custom one.
            var deleted = await Fixture.Cli.DeleteAllAsync(changed, Ct);
            AssertCreated(deleted, "delete");
            Assert.Contains(deleted.Operations, o => ((string?)o["resourceId"])?.EndsWith("/defaultAppManagementPolicy/default", StringComparison.Ordinal) == true && (string?)o["errorCode"] == "NotDeletable");
        }
        finally
        {
            bool restored = await guard.RestoreAsync(log, Ct);
            log.ForEach(Observe);
            Assert.True(restored, "The tenant default app management policy was not restored: " + string.Join(" | ", log));
        }
    }

    [Fact]
    public async Task It_CrossTenant_InboundTrustToggleAndPartnerUnderTheRestoreGuard()
    {
        Fixture.Require(Needs.CrossTenant);
        string? partner = Fixture.Settings.PartnerTenant;
        Assert.SkipWhen(partner is null, $"{IntegrationSettings.PartnerTenantVariable} is not set (the tenant id of a real other tenant).");
        Assert.SkipWhen(await Graph.GetObjectAsync(new GraphRequest($"{CrossTenantPartners}/{partner}"), Ct) is not null, $"The tenant already has a partner configuration for {partner}; the test never touches an existing one.");
        var guard = new TenantPolicyRestoreGuard(Graph, Fixture.RunDirectory, CrossTenantDefault, "crossTenantAccessDefault", "isServiceDefault", "automaticUserConsentSettings");
        var log = new List<string>();
        var snapshot = await guard.SnapshotAsync(Ct);
        bool hybrid = (bool?)snapshot["inboundTrust"]?["isHybridAzureADJoinedDeviceAccepted"] ?? false;
        var run = Fixture.NewRun("xtenant", "cross-tenant.entra.json").WithParameter("partnerTenantId", partner!);
        try
        {
            var same = run.WithParameter("hybridJoinAccepted", hybrid ? "true" : "false");
            Assert.Equal("NoChange", (await Fixture.Cli.WhatIfAsync(same, Ct, "--exit-code-on-changes")).ChangeTypeOf("/crossTenantAccessPolicy/default"));

            var toggled = run.WithParameter("hybridJoinAccepted", hybrid ? "false" : "true");
            AssertCreated(await Fixture.Cli.CreateAsync(toggled, Ct), "create");
            var live = await EventuallyAsync(() => Graph.GetObjectAsync(new GraphRequest(CrossTenantDefault), Ct), found => (bool?)found?["inboundTrust"]?["isHybridAzureADJoinedDeviceAccepted"] == !hybrid, seconds: 60);
            Assert.Equal(snapshot["inboundTrust"]?["isMfaAccepted"]?.ToJsonString(), live?["inboundTrust"]?["isMfaAccepted"]?.ToJsonString());
            Assert.Equal(snapshot["b2bCollaborationInbound"]?.ToJsonString(), live?["b2bCollaborationInbound"]?.ToJsonString());

            var partnerLive = await EventuallyAsync(() => Graph.GetObjectAsync(new GraphRequest($"{CrossTenantPartners}/{partner}"), Ct), found => found is not null);
            Assert.True((bool?)partnerLive?["inboundTrust"]?["isMfaAccepted"] == true, "The partner configuration does not trust MFA.");
            await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(toggled, Ct, "--exit-code-on-changes"), "what-if after create");
        }
        finally
        {
            try
            {
                await Graph.DeleteAsync(new GraphRequest($"{CrossTenantPartners}/{partner}"), Ct);
                log.Add($"cross-tenant: deleted the partner configuration of {partner}");
            }
            catch (GraphException ex) when (ex.IsNotFound)
            {
            }

            bool restored = await guard.RestoreAsync(log, Ct);
            log.ForEach(Observe);
            Assert.True(restored, "The default cross-tenant access configuration was not restored: " + string.Join(" | ", log));
        }
    }

    [Fact]
    public async Task It_GroupLifecycle_SelectedPolicyAndGroupEdge_OnlyInATenantWithoutAPolicy()
    {
        Fixture.Require(Needs.GroupLifecycle);
        var existing = await Graph.ListAsync(new GraphRequest(GroupLifecyclePolicies), Ct);
        var run = Fixture.NewRun("glp", "group-lifecycle.entra.json");
        if (existing.Count > 0)
        {
            // An existing tenant policy is never changed: the template is only compared with it (the policy row is NoChange or Modify, never Create).
            var preview = await Fixture.Cli.WhatIfAsync(run.WithParameter("groupLifetimeInDays", ((int?)existing[0]["groupLifetimeInDays"] ?? 180).ToString(System.Globalization.CultureInfo.InvariantCulture)), Ct);
            Assert.NotEqual("Create", preview.ChangeTypeOf("/groupLifecyclePolicies/default"));
            Assert.Skip($"The tenant already has a group expiration policy ({existing[0].RequireId()}, managedGroupTypes {existing[0].Text("managedGroupTypes")}); only the what-if ran.");
        }

        try
        {
            AssertCreated(await Fixture.Cli.CreateAsync(run, Ct), "create");
            var policy = await EventuallyAsync(() => Graph.ListAsync(new GraphRequest(GroupLifecyclePolicies), Ct), list => list.Count == 1);
            Assert.Equal("Selected", Assert.Single(policy).Text("managedGroupTypes"));
            var group = await GroupByNameAsync(Fixture.Name("glp-team"), "id");
            Assert.NotNull(group);
            var governing = await EventuallyAsync(() => Graph.ListAsync(new GraphRequest($"/groups/{group.RequireId()}/groupLifecyclePolicies"), Ct), list => list.Count == 1, seconds: 90);
            Assert.Equal(policy[0].RequireId(), Assert.Single(governing).RequireId());
            await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "what-if after create");
        }
        finally
        {
            AssertCreated(await Fixture.Cli.DeleteAllAsync(run, Ct), "delete");
        }

        Assert.Empty(await EventuallyAsync(() => Graph.ListAsync(new GraphRequest(GroupLifecyclePolicies), Ct), list => list.Count == 0));
    }

    private async Task<JsonObject?> FindListedAsync(string collection, string displayName)
    {
        var all = await Graph.ListAsync(new GraphRequest(collection), Ct);
        return all.SingleOrDefault(p => string.Equals(p.Text("displayName"), displayName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<JsonObject> FindListedEventuallyAsync(string collection, string displayName)
    {
        var found = await EventuallyAsync(() => FindListedAsync(collection, displayName), policy => policy is not null);
        Assert.True(found is not null, $"{collection}: no policy named '{displayName}' became visible.");
        return found;
    }
}
