using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// Access packages over applications and SharePoint Online sites (docs/access-packages.md, "Resource kinds"): the catalog resource of a service principal
/// (<c>AadApplication</c>) and of a site URL (<c>SharePointOnline</c>), the role scopes that grant an app role or a site group by display name or by the role's
/// <c>originId</c>, NoChange on a rerun (a site URL regardless of case and trailing slash), removal, and the refusals.
/// </summary>
public sealed class AccessPackageResourceKindsTests
{
    private const string Root = FakeEntitlementManagement.Root;
    private const string CatalogName = "Orders Access";
    private const string PackageName = "Orders Approvers";
    private const string SiteUrl = "https://contoso.sharepoint.com/sites/Orders";
    private const string ApproverRoleId = "6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0001";
    private const string ReaderRoleId = "6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0002";

    private static (ProviderHost Host, string SpId) NewTenant(bool withAppRoles = true)
    {
        var host = new ProviderHost();
        string sp = host.Directory.AddServicePrincipal(System.Guid.NewGuid().ToString(),"Orders Portal", p =>
        {
            if (withAppRoles)
            {
                p["appRoles"] = new JsonArray(AppRole(ApproverRoleId, "Approver", "Orders.Approve"), AppRole(ReaderRoleId, "Reader", "Orders.Read"));
            }
        })["id"]!.GetValue<string>();
        host.Directory.EntitlementManagement.AddSharePointSite(SiteUrl, "Orders");
        return (host, sp);
    }

    private static JsonObject AppRole(string id, string displayName, string value)
    {
        return new JsonObject { ["id"] = id, ["displayName"] = displayName, ["value"] = value, ["isEnabled"] = true, ["allowedMemberTypes"] = new JsonArray("User") };
    }

    private static string Catalog()
    {
        return Templates.Resource(AccessPackageCatalogsProvider.ResourceType, CatalogName, "{ \"description\": \"Orders\" }");
    }

    private static string Package()
    {
        return Templates.Resource(AccessPackagesProvider.ResourceType, PackageName, "{ \"catalogId\": \"[reference('" + CatalogName + "').id]\" }");
    }

    private static string CatalogResource(int label, string originId, string originSystem)
    {
        return Templates.Resource(CatalogResourcesProvider.ResourceType, Guid(label), "{ \"catalogId\": \"[reference('" + CatalogName + "').id]\", \"originId\": \"" + originId + "\", \"originSystem\": \"" + originSystem + "\" }");
    }

    private static string RoleScope(int label, string originId, string originSystem, string role)
    {
        return Templates.Resource(ResourceRoleScopesProvider.ResourceType, Guid(label), "{ \"accessPackageId\": \"[reference('" + PackageName + "').id]\", \"resourceOriginId\": \"" + originId + "\", \"originSystem\": \"" + originSystem + "\", \"role\": \"" + role + "\" }");
    }

    private static string Guid(int n)
    {
        return AccessPackagesProviderTests.Guid(n);
    }

    private static string App(string spId, string role = "Approver")
    {
        return string.Join(", ", Catalog(), CatalogResource(1, spId, "AadApplication"), Package(), RoleScope(2, spId, "AadApplication", role));
    }

    private static string Site(string url = SiteUrl, string role = "Orders Members")
    {
        return string.Join(", ", Catalog(), CatalogResource(3, url, "SharePointOnline"), Package(), RoleScope(4, url, "SharePointOnline", role));
    }

    private static JsonObject RoleScopeBody(Run run)
    {
        return Check.Body(run.Writes.Single(w => w.Path.EndsWith("/resourceRoleScopes", StringComparison.Ordinal)));
    }

    // ---- applications ----

    [Fact]
    public async Task AnApplication_IsAddedByItsServicePrincipal_AndAnAppRoleIsGrantedByDisplayName_ARerunIsNoChange()
    {
        var (host, sp) = NewTenant();
        var document = host.Resources(App(sp));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var request = Check.Body(run.Writes.Single(w => w.Path == Root + "/resourceRequests"));
        Assert.Equal("adminAdd", request["requestType"]!.GetValue<string>());
        Assert.Equal(sp, request["resource"]!["originId"]!.GetValue<string>());
        Assert.Equal("AadApplication", request["resource"]!["originSystem"]!.GetValue<string>());

        var scope = RoleScopeBody(run);
        Assert.Equal(ApproverRoleId, scope["role"]!["originId"]!.GetValue<string>());
        Assert.Equal("AadApplication", scope["role"]!["originSystem"]!.GetValue<string>());
        Assert.NotNull(scope["role"]!["id"]);
        Assert.Equal(sp, scope["scope"]!["originId"]!.GetValue<string>());
        Assert.Equal("AadApplication", scope["scope"]!["originSystem"]!.GetValue<string>());

        var rerun = await host.CreateAsync(document);
        Assert.All(rerun.Executed.Operations, o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
        Check.NoWrites(rerun);
    }

    [Fact]
    public async Task AnAppRole_NamedByItsId_IsGranted_AndARerunIsNoChange()
    {
        var (host, sp) = NewTenant();
        var document = host.Resources(App(sp, role: ReaderRoleId));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        Assert.Equal(ReaderRoleId, RoleScopeBody(run)["role"]!["originId"]!.GetValue<string>());
        var rerun = await host.CreateAsync(document);
        Assert.All(rerun.Executed.Operations, o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
    }

    [Fact]
    public async Task ARoleScopeWithoutOriginSystem_TakesTheCatalogResourcesOwn()
    {
        var (host, sp) = NewTenant();
        string scope = Templates.Resource(ResourceRoleScopesProvider.ResourceType, Guid(2), "{ \"accessPackageId\": \"[reference('" + PackageName + "').id]\", \"resourceOriginId\": \"" + sp + "\", \"role\": \"Reader\" }");
        var document = host.Resources(string.Join(", ", Catalog(), CatalogResource(1, sp, "AadApplication"), Package(), scope));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var body = RoleScopeBody(run);
        Assert.Equal(ReaderRoleId, body["role"]!["originId"]!.GetValue<string>());
        Assert.Equal("AadApplication", body["scope"]!["originSystem"]!.GetValue<string>());
        var rerun = await host.CreateAsync(document);
        Assert.All(rerun.Executed.Operations, o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
    }

    [Fact]
    public async Task AnApplicationWithoutAppRoles_OffersDefaultAccess_WithTheZeroRoleOriginId()
    {
        var (host, sp) = NewTenant(withAppRoles: false);

        var run = await host.CreateAsync(host.Resources(App(sp, role: "Default Access")));

        Check.Succeeded(run);
        var role = RoleScopeBody(run)["role"]!.AsObject();
        Assert.Equal(System.Guid.Empty.ToString(), role["originId"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnUnknownAppRole_IsAccessPackageRoleNotAvailable_NamingTheOfferedRoles()
    {
        var (host, sp) = NewTenant();

        var run = await host.CreateAsync(host.Resources(App(sp, role: "Auditor")));

        var op = run.Op(Guid(2));
        Assert.Equal(EngineDiagnosticCodes.AccessPackageRoleNotAvailable, op.ErrorCode);
        Assert.Contains("application " + sp, op.Error, StringComparison.Ordinal);
        Assert.Contains("Approver, Reader", op.Error, StringComparison.Ordinal);
        Assert.Contains("app role", op.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAppRoleNamedEligible_IsNotTreatedAsAPimForGroupsRole()
    {
        var (host, sp) = NewTenant();

        var run = await host.CreateAsync(host.Resources(App(sp, role: "Eligible Approver")));

        Assert.Equal(EngineDiagnosticCodes.AccessPackageRoleNotAvailable, run.Op(Guid(2)).ErrorCode);
    }

    [Fact]
    public async Task AnApplicationTheCallerMayNotAdd_IsCatalogResourceAccessDenied_NamingTheApplicationRoles()
    {
        var (host, sp) = NewTenant();
        host.Directory.EntitlementManagement.CallerMayAddApplications = false;

        var run = await host.CreateAsync(host.Resources(Catalog() + ", " + CatalogResource(1, sp, "AadApplication")));

        var op = run.Op(Guid(1));
        Assert.Equal(EngineDiagnosticCodes.CatalogResourceAccessDenied, op.ErrorCode);
        Assert.Contains("Cloud Application Administrator", op.Error, StringComparison.Ordinal);
        Assert.Contains("Application.ReadWrite.All", op.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServicePrincipalOfTheTemplate_IsCreatedBeforeItsCatalogResource_AndTheRoleScopeAfterIt()
    {
        var host = new ProviderHost();
        const string app = """
            { "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "name": "orders-portal",
              "properties": { "displayName": "Orders Portal", "appRoles": [ { "id": "6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0001", "value": "Orders.Approve", "displayName": "Approver", "description": "Approve orders", "isEnabled": true, "allowedMemberTypes": ["User"] } ] } }
            """;
        const string sp = """
            { "type": "Microsoft.Entra/servicePrincipals", "apiVersion": "2026-10-01-preview", "name": "[reference('orders-portal').appId]", "properties": { } }
            """;
        const string spId = "[reference(resourceId('Microsoft.Entra/servicePrincipals', reference('orders-portal').appId)).id]";

        var run = await host.CreateAsync(host.Resources(string.Join(", ", app, sp, App(spId))));

        Check.Succeeded(run);
        var paths = run.Writes.Select(w => w.Path).ToList();
        int spWrite = paths.FindIndex(p => p.StartsWith("/servicePrincipals", StringComparison.Ordinal));
        int request = paths.IndexOf(Root + "/resourceRequests");
        Assert.True(spWrite >= 0 && spWrite < request, string.Join(", ", paths));
        Assert.True(run.OpOfType(CatalogResourcesProvider.ResourceType).Batch < run.Op(Guid(2)).Batch);
        Assert.Equal(ApproverRoleId, RoleScopeBody(run)["role"]!["originId"]!.GetValue<string>());
    }

    // ---- SharePoint Online sites ----

    [Fact]
    public async Task ASite_IsAddedByItsUrl_AndASiteGroupIsGrantedByDisplayName_ARerunIsNoChange()
    {
        var (host, _) = NewTenant();
        var document = host.Resources(Site());

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var request = Check.Body(run.Writes.Single(w => w.Path == Root + "/resourceRequests"));
        Assert.Equal(SiteUrl, request["resource"]!["originId"]!.GetValue<string>());
        Assert.Equal("SharePointOnline", request["resource"]!["originSystem"]!.GetValue<string>());
        var scope = RoleScopeBody(run);
        Assert.Equal("4", scope["role"]!["originId"]!.GetValue<string>());
        Assert.Equal(SiteUrl, scope["scope"]!["originId"]!.GetValue<string>());
        Assert.Equal("SharePointOnline", scope["scope"]!["originSystem"]!.GetValue<string>());

        var rerun = await host.CreateAsync(document);
        Assert.All(rerun.Executed.Operations, o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
        Check.NoWrites(rerun);
    }

    [Fact]
    public async Task ASiteUrl_WithATrailingSlashAndOtherCasing_IsRequestedWithoutTheSlash_AndIsNoChangeOnARerun()
    {
        var (host, _) = NewTenant();
        const string declared = "HTTPS://contoso.sharepoint.com/sites/orders/";
        var document = host.Resources(Site(declared, role: "Orders Visitors"));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var request = Check.Body(run.Writes.Single(w => w.Path == Root + "/resourceRequests"));
        Assert.Equal("HTTPS://contoso.sharepoint.com/sites/orders", request["resource"]!["originId"]!.GetValue<string>());
        Assert.Equal("5", RoleScopeBody(run)["role"]!["originId"]!.GetValue<string>());

        var rerun = await host.CreateAsync(host.Resources(Site(declared, role: "Orders Visitors")));
        Assert.All(rerun.Executed.Operations, o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
        Check.NoWrites(rerun);
    }

    [Fact]
    public async Task ASiteAlreadyInTheCatalog_WithATrailingSlash_IsFound_AndTheRoleScopeNamesItAsGraphKeepsIt()
    {
        var (host, _) = NewTenant();
        Check.Succeeded(await host.CreateAsync(host.Resources(Catalog())));
        var em = host.Directory.EntitlementManagement;
        em.AddCatalogResource(em.Catalogs.Single()["id"]!.GetValue<string>(), SiteUrl + "/", "SharePointOnline", "Orders");
        host.ClearRequests();

        var run = await host.CreateAsync(host.Resources(Site(role: "4")));

        Check.Succeeded(run);
        Assert.Equal(ChangeType.NoChange, run.Op(Guid(3)).ChangeType);
        Assert.DoesNotContain(run.Writes, w => w.Path == Root + "/resourceRequests");
        var scope = RoleScopeBody(run);
        Assert.Equal(SiteUrl + "/", scope["scope"]!["originId"]!.GetValue<string>());
        Assert.Equal("4", scope["role"]!["originId"]!.GetValue<string>());

        var rerun = await host.CreateAsync(host.Resources(Site(role: "4")));
        Assert.All(rerun.Executed.Operations, o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
    }

    [Fact]
    public async Task ASiteTheCallerMayNotAdd_IsCatalogResourceAccessDenied_NamingSharePointAdministrator()
    {
        var (host, _) = NewTenant();
        host.Directory.EntitlementManagement.CallerMayAddSites = false;

        var run = await host.CreateAsync(host.Resources(Catalog() + ", " + CatalogResource(3, SiteUrl, "SharePointOnline")));

        var op = run.Op(Guid(3));
        Assert.Equal(EngineDiagnosticCodes.CatalogResourceAccessDenied, op.ErrorCode);
        Assert.Contains("SharePoint Administrator", op.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownSiteGroup_IsAccessPackageRoleNotAvailable_WithTheNamingHint()
    {
        var (host, _) = NewTenant();

        var run = await host.CreateAsync(host.Resources(Site(role: "Members")));

        var op = run.Op(Guid(4));
        Assert.Equal(EngineDiagnosticCodes.AccessPackageRoleNotAvailable, op.ErrorCode);
        Assert.Contains("Orders Owners, Orders Members, Orders Visitors", op.Error, StringComparison.Ordinal);
        Assert.Contains("full display name", op.Error, StringComparison.Ordinal);
    }

    // ---- removal ----

    [Fact]
    public async Task DeploymentDelete_RemovesTheRoleScopesAndTheApplicationAndSiteResources()
    {
        var (host, sp) = NewTenant();
        string resources = string.Join(", ", Catalog(), CatalogResource(1, sp, "AadApplication"), CatalogResource(3, SiteUrl, "SharePointOnline"), Package(),
            RoleScope(2, sp, "AadApplication", "Approver"), RoleScope(4, SiteUrl, "SharePointOnline", "Orders Members"));
        var document = host.Resources(resources);
        Check.Succeeded(await host.CreateAsync(document));
        var em = host.Directory.EntitlementManagement;
        Assert.Equal(2, em.Resources.Count);
        Assert.Equal(2, em.RoleScopes.Count);

        var deletion = await host.DeleteDeclaredAsync(document);

        Assert.True(deletion.Succeeded, string.Join("; ", deletion.Outcomes.Select(o => o.Error)));
        Assert.Empty(em.RoleScopes);
        Assert.Empty(em.Resources);
        Assert.Equal(2, host.Fake.Writes.Count(w => w.Path == Root + "/resourceRequests" && Check.Body(w)["requestType"]!.GetValue<string>() == "adminRemove"));
    }

    // ---- origins ----

    [Theory]
    [InlineData("https://contoso.sharepoint.com/sites/A/", "HTTPS://CONTOSO.sharepoint.com/sites/a", true)]
    [InlineData(" https://contoso.sharepoint.com/sites/A ", "https://contoso.sharepoint.com/sites/A", true)]
    [InlineData("https://contoso.sharepoint.com/sites/A", "https://contoso.sharepoint.com/sites/B", false)]
    [InlineData("8AB659D0-3839-427D-8C54-5AE92F0B3E2E", "8ab659d0-3839-427d-8c54-5ae92f0b3e2e", true)]
    public void SameId_IgnoresCaseAndASiteUrlsTrailingSlash(string left, string right, bool same)
    {
        Assert.Equal(same, AccessPackageResourceOrigin.SameId(left, right));
    }

    [Theory]
    [InlineData(null, "AadGroup")]
    [InlineData("aadapplication", "AadApplication")]
    [InlineData("SHAREPOINTONLINE", "SharePointOnline")]
    [InlineData("AzureResources", null)]
    public void Canonical_IsTheGraphCasingOfASupportedOrigin(string? declared, string? expected)
    {
        Assert.Equal(expected, AccessPackageResourceOrigin.Canonical(declared));
    }

    [Fact]
    public void FindRole_PrefersTheDisplayName_ThenTheRolesOriginId()
    {
        JsonObject[] roles =
        [
            new() { ["displayName"] = "4", ["originId"] = "9" },
            new() { ["displayName"] = "Orders Members", ["originId"] = "4" },
        ];

        Assert.Equal("9", AccessPackageResourceOrigin.FindRole(roles, "4")!["originId"]!.GetValue<string>());
        Assert.Equal("4", AccessPackageResourceOrigin.FindRole(roles, " orders members ")!["originId"]!.GetValue<string>());
        Assert.Equal("Orders Members", AccessPackageResourceOrigin.FindRole([roles[1]], "4")!["displayName"]!.GetValue<string>());
        Assert.Null(AccessPackageResourceOrigin.FindRole(roles, "Visitors"));
    }

    [Theory]
    [InlineData("AadGroup", new[] { "EntitlementManagement.ReadWrite.All", "Group.ReadWrite.All" })]
    [InlineData("AadApplication", new[] { "EntitlementManagement.ReadWrite.All", "Application.ReadWrite.All" })]
    [InlineData("SharePointOnline", new[] { "EntitlementManagement.ReadWrite.All", "Sites.FullControl.All" })]
    [InlineData("[parameters('origin')]", new[] { "EntitlementManagement.ReadWrite.All", "Group.ReadWrite.All", "Application.ReadWrite.All", "Sites.FullControl.All" })]
    public async Task RequiredPermissions_FollowTheDeclaredOriginSystem(string originSystem, string[] expected)
    {
        var host = new ProviderHost();
        var document = host.Resources(Catalog() + ", " + CatalogResource(1, "8ab659d0-3839-427d-8c54-5ae92f0b3e2e", originSystem), parameters: "\"origin\": { \"type\": \"string\", \"defaultValue\": \"AadGroup\" }");
        var instance = (await host.ExpandAsync(document)).Instances.Single(i => i.Type == CatalogResourcesProvider.ResourceType);

        Assert.Equal(expected, host.Registry.Get(CatalogResourcesProvider.ResourceType).RequiredPermissions(instance));
    }
}
