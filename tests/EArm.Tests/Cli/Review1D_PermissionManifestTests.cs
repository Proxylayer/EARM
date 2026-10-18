using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Core.Types;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// Review 1D / G-03..G-06, G-17: the permission manifest no longer under-reports. Every expectation cites the Microsoft Learn page it comes from
/// (docs/permissions.md, "Review 1D corrections").
/// </summary>
public sealed class Review1D_PermissionManifestTests
{
    private const string UserId = "11111111-1111-1111-1111-111111111111";

    private static string Resource(string type, string name, string properties)
    {
        return "{ \"type\": \"" + type + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", \"properties\": " + properties + " }";
    }

    private static async Task<CliResult> PermissionsAsync(string testName, string resources, params string[] extra)
    {
        using var sandbox = new CliSandbox(testName);
        string template = sandbox.WriteTemplate("manifest.entra.json", "\"resources\": [ " + resources + " ]");
        string[] args = ["permissions", "-f", template, "-o", "json", .. extra];
        return await CliRunner.RunAsync(args);
    }

    private static List<string> Names(CliResult result)
    {
        return result.Json["permissions"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()).ToList();
    }

    private static JsonObject Entry(CliResult result, string name)
    {
        return result.Json["permissions"]!.AsArray().Select(n => n!.AsObject()).Single(p => p["name"]!.GetValue<string>() == name);
    }

    private static List<string> Strings(JsonNode? array)
    {
        return array!.AsArray().Select(n => n!.GetValue<string>()).ToList();
    }

    private static string Notes(CliResult result)
    {
        return string.Join("\n", Strings(result.Json["notes"]));
    }

    [Fact]
    public async Task OwnersOnAnApplication_NeedDirectoryReadAll_AndTheNoteSaysOwnedByAloneCannotAddAnotherOwner()
    {
        var result = await PermissionsAsync(nameof(OwnersOnAnApplication_NeedDirectoryReadAll_AndTheNoteSaysOwnedByAloneCannotAddAnotherOwner), Resource("Microsoft.Entra/applications", "app", "{ \"displayName\": \"A\", \"owners\": [\"" + UserId + "\"] }"));

        Assert.Equal(["Application.ReadWrite.OwnedBy", "Directory.Read.All", "Organization.Read.All"], Names(result));
        Assert.Equal(["edge: owners"], Strings(Entry(result, "Directory.Read.All")["reasons"]));
        Assert.Contains("isn't sufficient to add another owner", Notes(result), StringComparison.Ordinal);
        Assert.Contains("Application.ReadWrite.All", Notes(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OwnersOnAGroup_NeedDirectoryReadAllToo()
    {
        var result = await PermissionsAsync(nameof(OwnersOnAGroup_NeedDirectoryReadAllToo), Resource("Microsoft.Entra/groups", "g", "{ \"displayName\": \"G\", \"mailEnabled\": false, \"securityEnabled\": true, \"owners\": [\"" + UserId + "\"] }"));

        Assert.Contains("Directory.Read.All", Names(result));
        Assert.Contains("GroupMember.ReadWrite.All", Names(result));
        Assert.DoesNotContain("isn't sufficient to add another owner", Notes(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GroupMembers_MayBeServicePrincipals_SoApplicationReadWriteAllIsListed()
    {
        var result = await PermissionsAsync(nameof(GroupMembers_MayBeServicePrincipals_SoApplicationReadWriteAllIsListed), Resource("Microsoft.Entra/groups", "g", "{ \"displayName\": \"G\", \"mailEnabled\": false, \"securityEnabled\": true, \"members\": [\"" + UserId + "\"] }"));

        Assert.Equal(["Application.ReadWrite.All", "Directory.Read.All", "Group.ReadWrite.All", "GroupMember.ReadWrite.All", "Organization.Read.All"], Names(result));
        Assert.Contains("edge: members (when a member is a service principal)", Strings(Entry(result, "Application.ReadWrite.All")["reasons"]));
    }

    [Fact]
    public async Task UserMemberOf_InAppMode_NeedsDirectoryReadAll()
    {
        var result = await PermissionsAsync(nameof(UserMemberOf_InAppMode_NeedsDirectoryReadAll), Resource("Microsoft.Entra/users", "u@contoso.com", "{ \"displayName\": \"U\", \"memberOf\": [\"" + UserId + "\"] }"));

        Assert.Contains("Directory.Read.All", Names(result));
        Assert.Contains("edge: memberOf", Strings(Entry(result, "Directory.Read.All")["reasons"]));
    }

    [Fact]
    public async Task OwnerOfOnAUser_IsExplainedAsUnsupportedWithApplicationPermissions_AndOnlyInAppMode()
    {
        string user = Resource("Microsoft.Entra/users", "u@contoso.com", "{ \"displayName\": \"U\", \"ownerOf\": [\"" + UserId + "\"] }");

        var app = await PermissionsAsync(nameof(OwnerOfOnAUser_IsExplainedAsUnsupportedWithApplicationPermissions_AndOnlyInAppMode) + "A", user);
        var delegated = await PermissionsAsync(nameof(OwnerOfOnAUser_IsExplainedAsUnsupportedWithApplicationPermissions_AndOnlyInAppMode) + "D", user, "--mode", "delegated");

        Assert.Contains("ownedObjects", Notes(app), StringComparison.Ordinal);
        Assert.Contains("does not support with application permissions", Notes(app), StringComparison.Ordinal);
        Assert.DoesNotContain("ownedObjects", Notes(delegated), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManagerOnAUser_NotesThatTheReadHasNoApplicationPermission()
    {
        var result = await PermissionsAsync(nameof(ManagerOnAUser_NotesThatTheReadHasNoApplicationPermission), Resource("Microsoft.Entra/users", "u@contoso.com", "{ \"displayName\": \"U\", \"manager\": \"" + UserId + "\" }"));

        Assert.Contains("GET /users/{id}/manager", Notes(result), StringComparison.Ordinal);
        Assert.Contains("User.ReadWrite.All", Names(result));
    }

    [Fact]
    public async Task Users_MentionTheRestorePermission()
    {
        var result = await PermissionsAsync(nameof(Users_MentionTheRestorePermission), Resource("Microsoft.Entra/users", "u@contoso.com", "{ \"displayName\": \"U\" }"));

        Assert.Contains("User.DeleteRestore.All", Notes(result), StringComparison.Ordinal);
        Assert.Contains("SoftDeleteProbeSkipped", Notes(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticationStrengthsAndContexts_UseTheLeastPrivilegedPermissionsOfTheirLearnPages()
    {
        var result = await PermissionsAsync(
            nameof(AuthenticationStrengthsAndContexts_UseTheLeastPrivilegedPermissionsOfTheirLearnPages),
            Resource("Microsoft.Entra/conditionalAccess/authenticationStrengths", "Strength", "{ \"allowedCombinations\": [\"fido2\"] }") + ", " + Resource("Microsoft.Entra/conditionalAccess/authenticationContextClassReferences", "c1", "{ \"displayName\": \"Context\" }"));

        var names = Names(result);
        Assert.Contains("Policy.ReadWrite.ConditionalAccess", names);
        Assert.Contains("AuthenticationContext.ReadWrite.All", names);
        Assert.DoesNotContain("Policy.ReadWrite.AuthenticationMethod", names);
    }

    [Fact]
    public async Task Licenses_UseLicenseAssignmentPermissions_NotOrganizationReadAll()
    {
        string properties = "{ \"displayName\": \"U\", \"usageLocation\": \"GB\", \"assignedLicenses\": [ { \"skuId\": \"6fd2c87f-b296-42f0-b197-1e91e994b900\" } ] }";
        var app = await PermissionsAsync(nameof(Licenses_UseLicenseAssignmentPermissions_NotOrganizationReadAll) + "A", Resource("Microsoft.Entra/users", "u@contoso.com", properties));
        var delegated = await PermissionsAsync(nameof(Licenses_UseLicenseAssignmentPermissions_NotOrganizationReadAll) + "D", Resource("Microsoft.Entra/users", "u@contoso.com", properties), "--mode", "delegated");

        Assert.Equal(["LicenseAssignment.Read.All", "LicenseAssignment.ReadWrite.All", "Organization.Read.All", "User.ReadWrite.All"], Names(app));
        Assert.Equal(["preflight: GET /organization"], Strings(Entry(app, "Organization.Read.All")["reasons"]));
        Assert.Equal(["LicenseAssignment.Read.All", "LicenseAssignment.ReadWrite.All", "User.ReadWrite.All"], Names(delegated));
    }

    [Fact]
    public async Task StandaloneRoleAssignments_NeedDirectoryReadAll_AndTheGroupReadForTheRoleAssignableCheck()
    {
        var result = await PermissionsAsync(
            nameof(StandaloneRoleAssignments_NeedDirectoryReadAll_AndTheGroupReadForTheRoleAssignableCheck),
            Resource("Microsoft.Entra/roleAssignments", "11111111-2222-3333-4444-555555555555", "{ \"principalId\": \"" + UserId + "\", \"role\": \"Global Reader\" }"));

        Assert.Contains("Directory.Read.All", Names(result));
        Assert.Contains("Group.Read.All", Names(result));
        Assert.Contains("lookup: role-assignable group check", Strings(Entry(result, "Group.Read.All")["reasons"]));
    }

    [Fact]
    public async Task AppRoleAssignments_NeedDirectoryReadAll_ToResolvePrincipalKinds()
    {
        var result = await PermissionsAsync(
            nameof(AppRoleAssignments_NeedDirectoryReadAll_ToResolvePrincipalKinds),
            Resource("Microsoft.Entra/users", "u@contoso.com", "{ \"displayName\": \"U\", \"appRoleAssignments\": [ { \"resourceAppId\": \"00000003-0000-0000-c000-000000000000\", \"appRole\": \"User.Read.All\" } ] }"));

        Assert.Contains("Directory.Read.All", Names(result));
        Assert.Contains("AppRoleAssignment.ReadWrite.All", Names(result));
    }

    [Fact]
    public void TheOverlays_CarryTheReviewedPermissions()
    {
        var catalog = ResourceTypeCatalog.Default;

        Assert.Equal(["Policy.ReadWrite.ConditionalAccess", "Policy.Read.All"], catalog.Find("Microsoft.Entra/conditionalAccess/authenticationStrengths")!.Permissions.Application);
        Assert.Equal(["AuthenticationContext.ReadWrite.All"], catalog.Find("Microsoft.Entra/conditionalAccess/authenticationContextClassReferences")!.Permissions.Application);
        Assert.Contains("Directory.Read.All", catalog.Find("Microsoft.Entra/roleAssignments")!.Permissions.Application);
        Assert.Contains("Directory.Read.All", catalog.Find("Microsoft.Entra/appRoleAssignments")!.Permissions.Application);
        // Policy.ReadWrite.AuthenticationMethod belongs to the authentication methods policy only (not to authentication strengths).
        Assert.Equal(["Microsoft.Entra/authenticationMethodConfigurations"], catalog.Types.Where(t => t.Permissions.Application.Contains("Policy.ReadWrite.AuthenticationMethod")).Select(t => t.Type).ToArray());
        Assert.Equal(["Policy.ReadWrite.AuthenticationMethod"], catalog.Find("Microsoft.Entra/authenticationMethodConfigurations")!.Permissions.Application);
        Assert.Equal(["Authentication Policy Administrator"], catalog.Find("Microsoft.Entra/authenticationMethodConfigurations")!.Permissions.Roles);
    }
}
