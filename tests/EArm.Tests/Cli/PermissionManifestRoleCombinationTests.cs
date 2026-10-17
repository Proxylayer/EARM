using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Engine.Permissions;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// The permission manifest's roles needed together (catalog resources: a catalog owner role AND a role that changes the group), the read-only
/// permissions of languageVersion 2.0 <c>"existing": true</c> resources, and the PIM for Groups role alternatives (Learn:
/// privilegedaccessgroup-post-eligibilityschedulerequests, entitlement-management-delegate).
/// </summary>
[Collection(CliCollection.Name)]
public sealed class PermissionManifestRoleCombinationTests
{
    private const string GroupId = "22222222-2222-2222-2222-222222222222";

    private static string Resource(string type, string name, string properties)
    {
        return "{ \"type\": \"" + type + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", \"properties\": " + properties + " }";
    }

    private static string Catalog => Resource("Microsoft.Entra/entitlementManagement/catalogs", "C", "{ \"description\": \"c\" }");

    private static string Group(string name, string assignable)
    {
        string flag = assignable.Length == 0 ? string.Empty : ", \"isAssignableToRole\": " + assignable;
        return Resource("Microsoft.Entra/groups", name, "{ \"displayName\": \"" + name + "\", \"mailEnabled\": false, \"mailNickname\": \"" + name + "\", \"securityEnabled\": true" + flag + " }");
    }

    private static string CatalogResource(string name, string originId)
    {
        return Resource("Microsoft.Entra/entitlementManagement/catalogResources", "[guid('" + name + "')]", "{ \"catalogId\": \"[reference('C').id]\", \"originId\": \"" + originId + "\" }");
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

    private static Task<CliResult> ArrayTemplateAsync(string testName, string resources, params string[] extra)
    {
        return PermissionsAsync(testName, "\"resources\": [ " + resources + " ]", extra);
    }

    private static List<JsonObject> Roles(CliResult result)
    {
        return result.Json["entraRoles"]!.AsArray().Select(n => n!.AsObject()).ToList();
    }

    private static List<string> Strings(JsonNode? array)
    {
        return array!.AsArray().Select(n => n!.GetValue<string>()).ToList();
    }

    private static List<List<string>> Parts(JsonObject role)
    {
        return role["allOf"]!.AsArray().Select(p => Strings(p)).ToList();
    }

    private static JsonObject RoleOf(CliResult result, string resourceFragment)
    {
        return Roles(result).Single(r => Strings(r["resources"]).Any(s => s.Contains(resourceFragment, StringComparison.Ordinal)));
    }

    private static JsonObject? Permission(CliResult result, string name)
    {
        return result.Json["permissions"]!.AsArray().Select(n => n!.AsObject()).SingleOrDefault(p => p["name"]!.GetValue<string>() == name);
    }

    // ---- roles needed together ----

    [Fact]
    public async Task ACatalogResourceOfARoleAssignableGroup_NeedsIdentityGovernanceAdministratorTogetherWithPrivilegedRoleAdministrator()
    {
        var result = await ArrayTemplateAsync(nameof(ACatalogResourceOfARoleAssignableGroup_NeedsIdentityGovernanceAdministratorTogetherWithPrivilegedRoleAdministrator),
            string.Join(", ", Catalog, Group("ra", "true"), CatalogResource("cr", "[reference('ra').id]")), "--mode", "delegated");

        var role = RoleOf(result, "catalogResources/");
        Assert.Empty(Strings(role["anyOf"]));
        Assert.Equal([["Identity Governance Administrator"], ["Privileged Role Administrator"]], Parts(role));
        Assert.Equal(["provider: the group is role-assignable"], Strings(role["reasons"]));
        Assert.Contains("'together'", string.Join("\n", Strings(result.Json["notes"])), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACatalogResourceOfAnOrdinaryGroup_NeedsIdentityGovernanceAdministratorTogetherWithAGroupRole_NotPrivilegedRoleAdministrator()
    {
        var result = await ArrayTemplateAsync(nameof(ACatalogResourceOfAnOrdinaryGroup_NeedsIdentityGovernanceAdministratorTogetherWithAGroupRole_NotPrivilegedRoleAdministrator),
            string.Join(", ", Catalog, Group("plain", string.Empty), CatalogResource("cr", "[reference('plain').id]")), "--mode", "delegated");

        var role = RoleOf(result, "catalogResources/");
        Assert.Equal([["Identity Governance Administrator"], ["Groups Administrator", "Intune Administrator", "User Administrator"]], Parts(role));
        Assert.Equal(["provider: the group is not role-assignable"], Strings(role["reasons"]));
    }

    [Fact]
    public async Task ACatalogResourceOfAnExistingGroup_KeepsEveryAlternative_AndAnAppNeedsRoleManagementWhenTheGroupIsRoleAssignable()
    {
        string resources = string.Join(", ", Catalog, CatalogResource("cr", GroupId));
        var delegated = await ArrayTemplateAsync(nameof(ACatalogResourceOfAnExistingGroup_KeepsEveryAlternative_AndAnAppNeedsRoleManagementWhenTheGroupIsRoleAssignable) + "-d", resources, "--mode", "delegated");
        var app = await ArrayTemplateAsync(nameof(ACatalogResourceOfAnExistingGroup_KeepsEveryAlternative_AndAnAppNeedsRoleManagementWhenTheGroupIsRoleAssignable) + "-a", resources);

        var role = RoleOf(delegated, "catalogResources/");
        Assert.Equal([["Identity Governance Administrator"], ["Groups Administrator", "Intune Administrator", "Privileged Role Administrator", "User Administrator"]], Parts(role));
        Assert.Equal(["provider: when the group is role-assignable"], Strings(Permission(app, "RoleManagement.ReadWrite.Directory")!["reasons"]));
    }

    [Fact]
    public async Task AnAppAddingAnOrdinaryTemplateGroup_DoesNotNeedRoleManagement()
    {
        var result = await ArrayTemplateAsync(nameof(AnAppAddingAnOrdinaryTemplateGroup_DoesNotNeedRoleManagement),
            string.Join(", ", Catalog, Group("plain", "false"), CatalogResource("cr", "[reference('plain').id]")));

        Assert.Null(Permission(result, "RoleManagement.ReadWrite.Directory"));
    }

    [Fact]
    public async Task TheTableOutput_WritesATogetherLine_WithAlternativesInParentheses()
    {
        using var sandbox = new CliSandbox(nameof(TheTableOutput_WritesATogetherLine_WithAlternativesInParentheses));
        string template = sandbox.WriteTemplate("manifest.entra.json", "\"resources\": [ " + string.Join(", ", Catalog, Group("plain", string.Empty), CatalogResource("cr", "[reference('plain').id]")) + " ]");
        var result = await CliRunner.RunAsync("permissions", "-f", template, "--mode", "delegated");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("  together: Identity Governance Administrator + (Groups Administrator | Intune Administrator | User Administrator)", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void RoleLine_JoinsAlternativesWithBars_AndTogetherPartsWithPlus()
    {
        Assert.Equal("A | B", EArm.Cli.Commands.PermissionsCommand.RoleLine(new RoleRequirement(["A", "B"], [], [])));
        Assert.Equal("together: A + (B | C)", EArm.Cli.Commands.PermissionsCommand.RoleLine(new RoleRequirement([], [], []) { AllOf = [["A"], ["B", "C"]] }));
    }

    // ---- existing resources ----

    [Fact]
    public async Task AnExistingResource_NeedsOnlyTheReadVariant_AndNoEntraRole()
    {
        string body = "\"languageVersion\": \"2.0\", \"resources\": { \"admins\": { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"admins\", \"existing\": true } }";
        var app = await PermissionsAsync(nameof(AnExistingResource_NeedsOnlyTheReadVariant_AndNoEntraRole) + "-a", body);
        var delegated = await PermissionsAsync(nameof(AnExistingResource_NeedsOnlyTheReadVariant_AndNoEntraRole) + "-d", body, "--mode", "delegated");

        Assert.Null(Permission(app, "Group.ReadWrite.All"));
        Assert.Null(Permission(app, "GroupMember.ReadWrite.All"));
        Assert.NotNull(Permission(app, "Group.Read.All"));
        Assert.Equal(["lookup: existing resource (\"existing\": true, read only)"], Strings(Permission(app, "Group.Read.All")!["reasons"]));
        Assert.Empty(Roles(delegated));
        Assert.Contains("\"existing\": true", string.Join("\n", Strings(app.Json["notes"])), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExistingResource_ReadPermission_FoldsIntoTheWriteGrantOfADeployedResourceOfTheSameType()
    {
        string body = "\"languageVersion\": \"2.0\", \"resources\": { "
            + "\"admins\": { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"admins\", \"existing\": true }, "
            + "\"team\": " + Group("team", string.Empty) + " }";
        var result = await PermissionsAsync(nameof(AnExistingResource_ReadPermission_FoldsIntoTheWriteGrantOfADeployedResourceOfTheSameType), body);

        Assert.NotNull(Permission(result, "Group.ReadWrite.All"));
        Assert.Null(Permission(result, "Group.Read.All"));
    }

    [Fact]
    public async Task AWhatIfRunOfATemplateWithOnlyAnExistingResource_RequestsNoWriteScope()
    {
        string body = "\"languageVersion\": \"2.0\", \"resources\": { \"admins\": { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"admins\", \"existing\": true } }";
        var result = await PermissionsAsync(nameof(AWhatIfRunOfATemplateWithOnlyAnExistingResource_RequestsNoWriteScope), body, "--verb", "create");

        Assert.DoesNotContain(Strings(result.Json["graphScopes"]), s => s.Contains(".ReadWrite.", StringComparison.Ordinal));
    }

    // ---- PIM for Groups role alternatives ----

    [Theory]
    [InlineData("Microsoft.Entra/privilegedAccess/groupEligibilityScheduleRequests")]
    [InlineData("Microsoft.Entra/privilegedAccess/groupAssignmentScheduleRequests")]
    public void TheGroupPimOverlays_ListUserAdministratorAndDirectoryWriters_AsAlternatives(string type)
    {
        var definition = EArm.Core.Types.ResourceTypeCatalog.Default.Find(type)!;

        Assert.Equal(
            ["Privileged Role Administrator", "Identity Governance Administrator", "Groups Administrator", "User Administrator", "Directory Writers"],
            definition.Permissions.Roles);
    }

    [Fact]
    public void TheGroupPolicyOnboardingCondition_ListsTheSameAlternatives()
    {
        var definition = EArm.Core.Types.ResourceTypeCatalog.Default.Find("Microsoft.Entra/roleManagementPolicies")!;

        Assert.Equal(
            ["Privileged Role Administrator", "Identity Governance Administrator", "Groups Administrator", "User Administrator", "Directory Writers"],
            Strings(definition.Permissions.Raw!["conditional"]!["onboarding"]!["roles"]));
    }
}
