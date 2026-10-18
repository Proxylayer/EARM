using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// The permission manifest of a catalog resource that adds an application or a SharePoint Online site (Learn: entitlementmanagement-post-resourcerequests,
/// entitlement-management-delegate): the overlay's <c>rolesAllOfByValue</c> replaces the group roles, the application permission on the resource follows
/// <c>originSystem</c>, and no group narrowing (RoleManagement.ReadWrite.Directory) applies.
/// </summary>
public sealed class PermissionManifestResourceKindTests
{
    private const string SpId = "33333333-3333-3333-3333-333333333333";
    private const string SiteUrl = "https://contoso.sharepoint.com/sites/Orders";

    private static string Resource(string type, string name, string properties)
    {
        return "{ \"type\": \"" + type + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", \"properties\": " + properties + " }";
    }

    private static string Template(string originId, string originSystem)
    {
        return "\"resources\": [ "
            + Resource("Microsoft.Entra/entitlementManagement/catalogs", "C", "{ \"description\": \"c\" }") + ", "
            + Resource("Microsoft.Entra/entitlementManagement/catalogResources", "[guid('cr')]", "{ \"catalogId\": \"[reference('C').id]\", \"originId\": \"" + originId + "\", \"originSystem\": \"" + originSystem + "\" }")
            + " ]";
    }

    private static async Task<CliResult> PermissionsAsync(string testName, string body, params string[] extra)
    {
        using var sandbox = new CliSandbox(testName);
        string template = sandbox.WriteTemplate("manifest.entra.json", body);
        string[] args = ["permissions", "-f", template, "-o", "json", .. extra];
        var result = await CliRunner.RunAsync(args);
        Assert.Equal(0, result.ExitCode);
        return result;
    }

    private static List<string> Strings(JsonNode? array)
    {
        return array!.AsArray().Select(n => n!.GetValue<string>()).ToList();
    }

    private static JsonObject CatalogResourceRole(CliResult result)
    {
        return result.Json["entraRoles"]!.AsArray().Select(n => n!.AsObject()).Single(r => Strings(r["resources"]).Any(s => s.Contains("catalogResources/", StringComparison.Ordinal)));
    }

    private static List<List<string>> Parts(JsonObject role)
    {
        return role["allOf"]!.AsArray().Select(p => Strings(p)).ToList();
    }

    private static List<string> PermissionNames(CliResult result)
    {
        return result.Json["permissions"]!.AsArray().Select(n => n!["name"]!.GetValue<string>()).ToList();
    }

    [Fact]
    public async Task AnApplication_NeedsIdentityGovernanceAdministrator_AndApplicationReadWriteAll_NotTheGroupPermissions()
    {
        string body = Template(SpId, "AadApplication");
        var delegated = await PermissionsAsync(nameof(AnApplication_NeedsIdentityGovernanceAdministrator_AndApplicationReadWriteAll_NotTheGroupPermissions) + "-d", body, "--mode", "delegated");
        var app = await PermissionsAsync(nameof(AnApplication_NeedsIdentityGovernanceAdministrator_AndApplicationReadWriteAll_NotTheGroupPermissions) + "-a", body);

        // A single part needed "together" is a plain alternative: it folds into the catalog's own Identity Governance Administrator group.
        var role = CatalogResourceRole(delegated);
        Assert.Equal(["Identity Governance Administrator"], Strings(role["anyOf"]));
        Assert.Null(role["allOf"]);
        Assert.Contains("provider: originSystem AadApplication", Strings(role["reasons"]));
        var names = PermissionNames(app);
        Assert.Contains("Application.ReadWrite.All", names);
        Assert.DoesNotContain("Group.ReadWrite.All", names);
        Assert.DoesNotContain("RoleManagement.ReadWrite.Directory", names);
    }

    [Fact]
    public async Task ASite_NeedsIdentityGovernanceAdministratorTogetherWithSharePointAdministrator_AndSitesFullControlAll()
    {
        string body = Template(SiteUrl, "SharePointOnline");
        var delegated = await PermissionsAsync(nameof(ASite_NeedsIdentityGovernanceAdministratorTogetherWithSharePointAdministrator_AndSitesFullControlAll) + "-d", body, "--mode", "delegated");
        var app = await PermissionsAsync(nameof(ASite_NeedsIdentityGovernanceAdministratorTogetherWithSharePointAdministrator_AndSitesFullControlAll) + "-a", body);

        Assert.Equal([["Identity Governance Administrator"], ["SharePoint Administrator"]], Parts(CatalogResourceRole(delegated)));
        var names = PermissionNames(app);
        Assert.Contains("Sites.FullControl.All", names);
        Assert.DoesNotContain("Group.ReadWrite.All", names);
        Assert.DoesNotContain("RoleManagement.ReadWrite.Directory", names);
    }

    [Fact]
    public async Task AnOriginSystemFromAParameter_IsEvaluatedOffline_LikeALiteral()
    {
        string body = "\"parameters\": { \"origin\": { \"type\": \"string\", \"defaultValue\": \"SharePointOnline\" } }, " + Template(SiteUrl, "[parameters('origin')]");
        var delegated = await PermissionsAsync(nameof(AnOriginSystemFromAParameter_IsEvaluatedOffline_LikeALiteral) + "-d", body, "--mode", "delegated");
        var app = await PermissionsAsync(nameof(AnOriginSystemFromAParameter_IsEvaluatedOffline_LikeALiteral) + "-a", body);

        Assert.Equal([["Identity Governance Administrator"], ["SharePoint Administrator"]], Parts(CatalogResourceRole(delegated)));
        var names = PermissionNames(app);
        Assert.Contains("Sites.FullControl.All", names);
        Assert.DoesNotContain("RoleManagement.ReadWrite.Directory", names);
    }
}
