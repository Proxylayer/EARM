using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>
/// Access packages over an application and a SharePoint Online site against a real tenant (docs/access-packages.md, "Resource kinds"): a template service
/// principal added to a catalog (<c>AadApplication</c>) with an app role granted by display name, then by app role id; and an existing site
/// (<c>SharePointOnline</c>) with one of its groups. Both need <c>EARM_IT_ALLOW_ACCESS_PACKAGES=1</c>; the site test also needs
/// <c>EARM_IT_SP_SITE_URL</c> and <c>EARM_IT_SP_SITE_ROLE</c> (and a user sign-in: Graph lists sites in a catalog only to delegated callers).
/// </summary>
[Collection(RealTenantCollection.Name)]
public sealed class AccessPackageResourceKindScenarioTests : IntegrationTestBase
{
    private const string Root = "/identityGovernance/entitlementManagement";

    public AccessPackageResourceKindScenarioTests(IntegrationFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task It_AccessPackages_ApplicationAppRole_RoundTrip()
    {
        Fixture.Require(Needs.AccessPackages);
        var run = Fixture.NewRun("accesspkgapp", "access-packages-app.entra.json");
        string packageName = Fixture.Name("package-app");

        AssertCreated(await Fixture.Cli.CreateAsync(run, Ct), "create");

        var package = await FindOneEventuallyAsync(Root + "/accessPackages", "displayName", packageName, found => found is not null);
        Assert.NotNull(package);
        string packageId = package.RequireId();
        var roleScope = await SingleRoleScopeAsync(packageId, "Approver");
        Assert.Equal("AadApplication", roleScope.Nested("role")!.Text("originSystem"));
        Assert.Equal("AadApplication", roleScope.Nested("scope")!.Text("originSystem"));
        await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "what-if after create");

        // The same app role named by its id: the role scope is found again (no change).
        var app = await FindOneEventuallyAsync("/applications", "displayName", Fixture.Name("apapp"), found => found is not null);
        Assert.NotNull(app);
        string readerRoleId = app["appRoles"]!.AsArray().OfType<JsonObject>().Single(r => r.Text("value") == "It.Read").RequireId();
        var byId = run.WithParameter("appRole", readerRoleId);
        AssertCreated(await Fixture.Cli.CreateAsync(byId, Ct), "create with the Reader role by id");
        await SingleRoleScopeAsync(packageId, "Reader", allowOthers: true);
        await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(byId, Ct, "--exit-code-on-changes"), "what-if with the role by id");

        AssertExit(0, await Fixture.Cli.DeleteAllAsync(byId, Ct), "deployment delete");
        Assert.Null(await FindOneEventuallyAsync(Root + "/accessPackages", "displayName", packageName, found => found is null));
    }

    [Fact]
    public async Task It_AccessPackages_SharePointSiteGroup_RoundTrip()
    {
        Fixture.Require(Needs.AccessPackages | Needs.SharePointSite);
        var run = Fixture.NewRun("accesspkgsite", "access-packages-site.entra.json")
            .WithParameter("siteUrl", Fixture.Settings.SharePointSiteUrl!)
            .WithParameter("siteRole", Fixture.Settings.SharePointSiteRole!);
        string packageName = Fixture.Name("package-site");

        AssertCreated(await Fixture.Cli.CreateAsync(run, Ct), "create");

        var package = await FindOneEventuallyAsync(Root + "/accessPackages", "displayName", packageName, found => found is not null);
        Assert.NotNull(package);
        var roleScope = await SingleRoleScopeAsync(package.RequireId(), Fixture.Settings.SharePointSiteRole!);
        Assert.Equal("SharePointOnline", roleScope.Nested("scope")!.Text("originSystem"));
        Assert.Equal(Fixture.Settings.SharePointSiteUrl!.TrimEnd('/'), roleScope.Nested("scope")!.Text("originId")!.TrimEnd('/'), ignoreCase: true);
        await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "what-if after create");

        // A trailing slash on the URL names the same site: still no change.
        var slashed = run.WithParameter("siteUrl", Fixture.Settings.SharePointSiteUrl!.TrimEnd('/') + "/");
        await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(slashed, Ct, "--exit-code-on-changes"), "what-if with a trailing slash");

        AssertExit(0, await Fixture.Cli.DeleteAllAsync(run, Ct), "deployment delete");
        Assert.Null(await FindOneEventuallyAsync(Root + "/accessPackages", "displayName", packageName, found => found is null));
    }

    private async Task<JsonObject> SingleRoleScopeAsync(string packageId, string roleName, bool allowOthers = false)
    {
        var expanded = await EventuallyAsync(
            () => Graph.GetObjectAsync(new GraphRequest($"{Root}/accessPackages/{packageId}").WithExpand("resourceRoleScopes($expand=role,scope)"), Ct),
            found => found?["resourceRoleScopes"] is JsonArray scopes && scopes.OfType<JsonObject>().Any(s => string.Equals(s.Nested("role")?.Text("displayName"), roleName, StringComparison.OrdinalIgnoreCase)));
        var scopes = expanded!["resourceRoleScopes"]!.AsArray().OfType<JsonObject>().ToList();
        if (!allowOthers)
        {
            Assert.Single(scopes);
        }

        return scopes.Single(s => string.Equals(s.Nested("role")!.Text("displayName"), roleName, StringComparison.OrdinalIgnoreCase));
    }
}
