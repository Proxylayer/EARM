using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>
/// Entitlement management against a real tenant (docs/access-packages.md): a catalog, a group in it, an access package, its role scope and an approval
/// policy, created, compared, repaired after drift, changed and deleted; and an Eligible Member role scope of a group a same-template PIM request onboards.
/// Needs <c>EARM_IT_ALLOW_ACCESS_PACKAGES=1</c> (and <c>EARM_IT_ALLOW_PIM=1</c> with a domain for the eligible role). The prefix sweep removes prefixed
/// catalogs with their contents (<see cref="AccessPackageCleanup"/>).
/// </summary>
[Collection(RealTenantCollection.Name)]
public sealed class AccessPackageScenarioTests : IntegrationTestBase
{
    private const string Root = "/identityGovernance/entitlementManagement";

    public AccessPackageScenarioTests(IntegrationFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task It_AccessPackages_CatalogPackageRoleScopeAndPolicyRoundTrip()
    {
        Fixture.Require(Needs.AccessPackages);
        var run = Fixture.NewRun("accesspkg", "access-packages.entra.json");
        string catalogName = Fixture.Name("catalog");
        string packageName = Fixture.Name("package");

        AssertCreated(await Fixture.Cli.CreateAsync(run, Ct), "create");

        var catalog = await FindOneEventuallyAsync(Root + "/catalogs", "displayName", catalogName, found => found is not null);
        Assert.NotNull(catalog);
        Assert.Equal("published", catalog.Text("state"));
        var package = await FindOneEventuallyAsync(Root + "/accessPackages", "displayName", packageName, found => found is not null);
        Assert.NotNull(package);
        string packageId = package.RequireId();
        var group = await GroupByNameAsync(Fixture.Name("apgrp"), "id");
        Assert.NotNull(group);

        var resources = await EventuallyAsync(() => Graph.ListAsync(new GraphRequest($"{Root}/catalogs/{catalog.RequireId()}/resources").WithFilter($"originId eq '{group.RequireId()}'"), Ct), list => list.Count == 1);
        Assert.Single(resources);

        var expanded = await EventuallyAsync(
            () => Graph.GetObjectAsync(new GraphRequest($"{Root}/accessPackages/{packageId}").WithExpand("resourceRoleScopes($expand=role,scope)"), Ct),
            found => found?["resourceRoleScopes"] is JsonArray { Count: 1 });
        var roleScope = Assert.Single(expanded!["resourceRoleScopes"]!.AsArray().OfType<JsonObject>());
        Assert.Equal("Member", roleScope.Nested("role")!.Text("displayName"));
        Assert.Equal(group.RequireId(), roleScope.Nested("scope")!.Text("originId"), ignoreCase: true);

        var policies = await EventuallyAsync(() => Graph.ListAsync(new GraphRequest(Root + "/assignmentPolicies").WithFilter($"accessPackage/id eq '{packageId}'"), Ct), list => list.Count == 1);
        var policy = Assert.Single(policies);
        Assert.Equal("Approval", policy.Text("displayName"));
        Assert.Equal("P30D", policy.Nested("expiration")!.Text("duration"));
        Assert.True((bool?)policy.Nested("requestApprovalSettings")!["isApprovalRequiredForAdd"] == true, "The policy does not require approval.");

        await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "what-if after create");

        // Drift: the package description is changed behind EARM's back.
        await Graph.PatchAsync(new GraphRequest($"{Root}/accessPackages/{packageId}").WithHint(RetryHint.ExpectReplicationLag), new JsonObject { ["description"] = "changed outside EARM" }, Ct);
        var driftPreview = await EventuallyAsync(() => Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), preview => preview.ExitCode == 5, seconds: 60);
        AssertChange(driftPreview, "/accessPackages/" + packageName, "Modify", "what-if after drift");
        AssertCreated(await Fixture.Cli.CreateAsync(run, Ct), "create after drift");
        await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "what-if after repair");

        // A template change: a shorter assignment duration is written with the merged PUT; the undeclared settings keep their values.
        var changed = run.WithParameter("duration", "P14D");
        AssertChange(await Fixture.Cli.WhatIfAsync(changed, Ct, "--exit-code-on-changes"), "/assignmentPolicies/Approval", "Modify", "what-if with another duration");
        AssertCreated(await Fixture.Cli.CreateAsync(changed, Ct), "create with another duration");
        var updated = await EventuallyAsync(
            () => Graph.GetObjectAsync(new GraphRequest($"{Root}/assignmentPolicies/{policy.RequireId()}"), Ct),
            found => found?.Nested("expiration")?.Text("duration") == "P14D");
        Assert.Equal("P14D", updated!.Nested("expiration")!.Text("duration"));
        Assert.Equal("allMemberUsers", updated.Text("allowedTargetScope"));
        await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(changed, Ct, "--exit-code-on-changes"), "what-if after the duration change");

        AssertExit(0, await Fixture.Cli.DeleteAllAsync(changed, Ct), "deployment delete");
        Assert.Null(await FindOneEventuallyAsync(Root + "/accessPackages", "displayName", packageName, found => found is null));
        Assert.Null(await FindOneEventuallyAsync(Root + "/catalogs", "displayName", catalogName, found => found is null));
    }

    [Fact]
    public async Task It_AccessPackages_ResourceRoleScope_EligibleMember()
    {
        Fixture.Require(Needs.Domain | Needs.Pim | Needs.AccessPackages);
        var run = Fixture.NewRun("accesspkgelig", "access-packages-eligible.entra.json").WithParameter("domain", Fixture.Domain);
        string packageName = Fixture.Name("package-elig");

        // One run: the PIM request onboards the group, the role scope is applied after it and waits for the catalog to offer Eligible Member. The
        // fallback (a PimGroupNotOnboarded warning, the next run creates it) is observed and allowed, but reported.
        var first = await Fixture.Cli.CreateAsync(run, Ct);
        AssertCreated(first, "create (onboards the group and grants the eligible role)");
        if (first.Diagnostics.Any(d => (string?)d["code"] == "PimGroupNotOnboarded"))
        {
            Observe("The eligible role scope was deferred: the catalog did not offer Eligible Member within the wait.");
            AssertCreated(await Fixture.Cli.CreateAsync(run, Ct), "create (the deferred eligible role scope)");
        }

        var package = await FindOneEventuallyAsync(Root + "/accessPackages", "displayName", packageName, found => found is not null);
        Assert.NotNull(package);
        var expanded = await EventuallyAsync(
            () => Graph.GetObjectAsync(new GraphRequest($"{Root}/accessPackages/{package.RequireId()}").WithExpand("resourceRoleScopes($expand=role,scope)"), Ct),
            found => found?["resourceRoleScopes"] is JsonArray { Count: 1 });
        var roleScope = Assert.Single(expanded!["resourceRoleScopes"]!.AsArray().OfType<JsonObject>());
        Assert.Equal("Eligible Member", roleScope.Nested("role")!.Text("displayName"));
        Observe("Eligible Member role originId: " + roleScope.Nested("role")!.Text("originId"));

        await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "what-if after create");
        AssertExit(0, await Fixture.Cli.DeleteAllAsync(run, Ct), "deployment delete");
    }
}
