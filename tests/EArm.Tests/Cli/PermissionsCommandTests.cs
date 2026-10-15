using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary><c>earm permissions</c> driven in-process over the samples and small purpose-built templates (offline: no az, no tenant).</summary>
[Collection(CliCollection.Name)]
public sealed class PermissionsCommandTests
{
    private const string KeyVaultName = "Key Vault secret read ('Key Vault Secrets User' or the get-secret access policy) on vault 'contoso-kv'";

    /// <summary>The sorted permission names of the orders sample in app mode (the Key Vault entry comes after the Graph ones).</summary>
    private static readonly string[] OrdersGraphNames =
    [
        "AdministrativeUnit.ReadWrite.All",
        "Application.Read.All",
        "Application.ReadWrite.All",
        "AppRoleAssignment.ReadWrite.All",
        "AuthenticationContext.ReadWrite.All",
        "DelegatedPermissionGrant.ReadWrite.All",
        "Directory.Read.All",
        "Group.ReadWrite.All",
        "GroupMember.ReadWrite.All",
        "LicenseAssignment.Read.All",
        "LicenseAssignment.ReadWrite.All",
        "Organization.Read.All",
        "Policy.Read.All",
        "Policy.ReadWrite.ConditionalAccess",
        "RoleManagement.ReadWrite.Directory",
        "User.ReadWrite.All",
    ];

    private static Task<CliResult> RunJsonAsync(string template, string parameters, params string[] extra)
    {
        string[] args = ["permissions", "-f", template, "-p", "@" + parameters, "-o", "json", .. extra];
        return CliRunner.RunAsync(args);
    }

    private static Task<CliResult> OrdersAsync(params string[] extra)
    {
        return RunJsonAsync(TestPaths.Sample("orders-api.entra.json"), TestPaths.Sample("orders-api.parameters.json"), extra);
    }

    private static JsonObject Entry(CliResult result, string name)
    {
        return result.Json["permissions"]!.AsArray().Select(n => n!.AsObject()).Single(p => p["name"]!.GetValue<string>() == name);
    }

    private static List<string> Strings(JsonNode? array)
    {
        return array!.AsArray().Select(n => n!.GetValue<string>()).ToList();
    }

    private static List<string> Names(CliResult result)
    {
        return result.Json["permissions"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()).ToList();
    }

    private static List<string> RoleGroups(CliResult result)
    {
        return result.Json["entraRoles"]!.AsArray().Select(r => string.Join('|', Strings(r!["anyOf"]))).ToList();
    }

    // ---- orders sample, application permissions ----

    [Fact]
    public async Task OrdersSample_AppMode_ListsTheExpectedPermissions()
    {
        var result = await OrdersAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.Equal("app", result.Json["mode"]!.GetValue<string>());
        Assert.Equal(16, result.Json["resourceCount"]!.GetValue<int>());
        Assert.Equal([.. OrdersGraphNames, KeyVaultName], Names(result));
        Assert.All(result.Json["permissions"]!.AsArray().Take(OrdersGraphNames.Length), p => Assert.Equal("graphApplication", p!["kind"]!.GetValue<string>()));
        Assert.Empty(result.Json["entraRoles"]!.AsArray());
    }

    [Fact]
    public async Task OrdersSample_AppMode_FoldsOwnedByIntoApplicationReadWriteAll()
    {
        var result = await OrdersAsync();

        // The application declares owners (OwnedBy would do) but ownerOf on a user needs All: only the broader grant is listed.
        Assert.DoesNotContain("Application.ReadWrite.OwnedBy", Names(result));
        var all = Entry(result, "Application.ReadWrite.All");
        var resources = Strings(all["resources"]);
        Assert.Contains("/providers/Microsoft.Entra/applications/contoso-prod-api", resources);
        Assert.Contains("/providers/Microsoft.Entra/applications/contoso-prod-api/federatedIdentityCredentials/github-0", resources);
        Assert.Contains("/providers/Microsoft.Entra/applications/contoso-prod-api/federatedIdentityCredentials/github-1", resources);
        Assert.Contains("/providers/Microsoft.Entra/applications/contoso-prod-api/passwordCredentials/ci-secret", resources);
        var reasons = Strings(all["reasons"]);
        Assert.Contains(reasons, r => r.StartsWith("Application.ReadWrite.OwnedBy would suffice for the applications this principal owns", StringComparison.Ordinal));
        Assert.Contains("edge: ownerOf (when a target is an application or service principal)", reasons);
    }

    [Fact]
    public async Task OrdersSample_AppMode_EdgeDerivedEntriesNameTheirProperty()
    {
        var result = await OrdersAsync();

        var user = "/providers/Microsoft.Entra/users/bg-orders@contoso.com";
        Assert.Equal(["edge: administrativeUnits", "edge: members", "provider"], Strings(Entry(result, "AdministrativeUnit.ReadWrite.All")["reasons"]));
        Assert.Equal(["edge: memberOf", "edge: members", "provider"], Strings(Entry(result, "GroupMember.ReadWrite.All")["reasons"]));
        Assert.Equal(["edge: manager", "provider"], Strings(Entry(result, "User.ReadWrite.All")["reasons"]));
        Assert.Equal(["edge: appRoleAssignments", "provider"], Strings(Entry(result, "AppRoleAssignment.ReadWrite.All")["reasons"]));
        Assert.Equal(["edge: directoryRoles", "provider"], Strings(Entry(result, "RoleManagement.ReadWrite.Directory")["reasons"]));
        Assert.Equal(["edge: ownerOf (when a target is a group)", "provider"], Strings(Entry(result, "Group.ReadWrite.All")["reasons"]));
        Assert.Contains(user, Strings(Entry(result, "User.ReadWrite.All")["resources"]));
        Assert.Contains(user, Strings(Entry(result, "AdministrativeUnit.ReadWrite.All")["resources"]));

        var organization = Entry(result, "Organization.Read.All");
        Assert.Equal(["preflight: GET /organization"], Strings(organization["reasons"]));
        Assert.Equal(["edge: assignedLicenses", "provider"], Strings(Entry(result, "LicenseAssignment.ReadWrite.All")["reasons"]));
        Assert.Contains("(every deployment)", Strings(organization["resources"]));
    }

    [Fact]
    public async Task OrdersSample_AppMode_LookupFunctionsAreListedUnderTheirReasons()
    {
        var result = await OrdersAsync();

        var application = Entry(result, "Application.Read.All");
        var reasons = Strings(application["reasons"]);
        Assert.Contains("lookup: appRole()", reasons);
        Assert.Contains("lookup: permissionScope()", reasons);
        Assert.Contains("lookup: existing resource", reasons);
        Assert.Contains("reference(Microsoft.Entra/servicePrincipals, apiVersion)", Strings(application["resources"]));

        // roleTemplate() needs RoleManagement.Read.Directory, which RoleManagement.ReadWrite.Directory already covers: folded away.
        Assert.DoesNotContain("RoleManagement.Read.Directory", Names(result));
        Assert.Contains(Strings(Entry(result, "RoleManagement.ReadWrite.Directory")["resources"]), r => r.Contains("roleAssignments", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OrdersSample_AppMode_KeyVaultReferenceIsAnAzureRbacEntry()
    {
        var result = await OrdersAsync();

        var vault = Entry(result, KeyVaultName);
        Assert.Equal("azureRbac", vault["kind"]!.GetValue<string>());
        Assert.Equal(["parameter 'initialPassword'"], Strings(vault["resources"]));
        Assert.Equal(["GET https://contoso-kv.vault.azure.net/secrets/bg-initial"], Strings(vault["reasons"]));
        Assert.Equal(KeyVaultName, Names(result).Last());
    }

    // ---- orders sample, delegated ----

    [Fact]
    public async Task OrdersSample_DelegatedMode_ListsTheSameScopes_AndRoleGroupsAsAlternatives()
    {
        var result = await OrdersAsync("--mode", "delegated");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("delegated", result.Json["mode"]!.GetValue<string>());
        // The preflight read of /organization is an application-mode requirement only (and licenses no longer need Organization.Read.All).
        string[] delegatedGraphNames = OrdersGraphNames.Where(n => n != "Organization.Read.All").ToArray();
        Assert.Equal([.. delegatedGraphNames, KeyVaultName], Names(result));
        Assert.All(result.Json["permissions"]!.AsArray().Take(delegatedGraphNames.Length), p => Assert.Equal("graphDelegated", p!["kind"]!.GetValue<string>()));
        Assert.Equal("azureRbac", Entry(result, KeyVaultName)["kind"]!.GetValue<string>());

        string[] expectedGroups =
        [
            "Application Administrator|Application Developer|Cloud Application Administrator",
            "Application Administrator|Cloud Application Administrator",
            "Authentication Policy Administrator",
            "Conditional Access Administrator",
            "Conditional Access Administrator|Security Administrator",
            "Groups Administrator",
            "License Administrator",
            "Privileged Role Administrator",
            "User Administrator",
        ];
        Assert.Equal(expectedGroups, RoleGroups(result));

        // Every group's roles are alternatives (sorted, no duplicates) and name the resources and reasons that need them.
        foreach (var group in result.Json["entraRoles"]!.AsArray())
        {
            var anyOf = Strings(group!["anyOf"]);
            Assert.Equal(anyOf.Order(StringComparer.OrdinalIgnoreCase).ToList(), anyOf);
            Assert.Equal(anyOf.Count, anyOf.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.NotEmpty(Strings(group["resources"]));
            Assert.NotEmpty(Strings(group["reasons"]));
        }
    }

    [Fact]
    public async Task OrdersSample_DelegatedMode_EdgeRolesAreGroupedByTheirAlternatives()
    {
        var result = await OrdersAsync("--mode", "delegated");

        var groups = result.Json["entraRoles"]!.AsArray().ToDictionary(g => string.Join('|', Strings(g!["anyOf"])), g => g!.AsObject());
        var groupsAdministrator = groups["Groups Administrator"];
        Assert.Contains("edge: memberOf", Strings(groupsAdministrator["reasons"]));
        Assert.Contains("edge: members", Strings(groupsAdministrator["reasons"]));
        Assert.Equal(["condition: assignedLicenses", "edge: assignedLicenses"], Strings(groups["License Administrator"]["reasons"]));
        Assert.Equal(["edge: manager", "provider"], Strings(groups["User Administrator"]["reasons"]));
        Assert.Contains("edge: appRoleAssignments", Strings(groups["Application Administrator|Cloud Application Administrator"]["reasons"]));
        Assert.Contains("edge: directoryRoles", Strings(groups["Privileged Role Administrator"]["reasons"]));
    }

    [Fact]
    public async Task OrdersSample_NestedAndFlatForms_YieldIdenticalSets()
    {
        foreach (var mode in new[] { "app", "delegated" })
        {
            var nested = await RunJsonAsync(TestPaths.Sample("orders-api.entra.json"), TestPaths.Sample("orders-api.parameters.json"), "--mode", mode);
            var flat = await RunJsonAsync(TestPaths.Sample("orders-api.flat.entra.json"), TestPaths.Sample("orders-api.parameters.json"), "--mode", mode);

            Assert.Equal(0, nested.ExitCode);
            Assert.Equal(0, flat.ExitCode);
            Assert.Equal(nested.Json["resourceCount"]!.GetValue<int>(), flat.Json["resourceCount"]!.GetValue<int>());
            Assert.Equal(nested.Json["permissions"]!.ToJsonString(), flat.Json["permissions"]!.ToJsonString());
            Assert.Equal(nested.Json["entraRoles"]!.ToJsonString(), flat.Json["entraRoles"]!.ToJsonString());
            Assert.Equal(nested.Json["notes"]!.ToJsonString(), flat.Json["notes"]!.ToJsonString());
        }
    }

    // ---- other samples ----

    [Fact]
    public async Task AppWithSpAndSecretSample_NeedsApplicationReadWriteAllAndTheOrganizationRead()
    {
        string template = TestPaths.Sample("app-with-sp-and-secret.entra.json");
        string parameters = TestPaths.Sample("app-with-sp-and-secret.parameters.json");

        var app = await RunJsonAsync(template, parameters);
        var delegated = await RunJsonAsync(template, parameters, "--mode", "delegated");

        Assert.Equal(0, app.ExitCode);
        Assert.Equal(4, app.Json["resourceCount"]!.GetValue<int>());
        Assert.Equal(["Application.ReadWrite.All", "Organization.Read.All"], Names(app));
        Assert.Equal(["Application.ReadWrite.All", "Organization.Read.All"], Names(delegated));
        Assert.Equal(
            ["Application Administrator|Application Developer|Cloud Application Administrator", "Application Administrator|Cloud Application Administrator"],
            RoleGroups(delegated));
        Assert.Equal(["lookup: tenant()"], Strings(Entry(delegated, "Organization.Read.All")["reasons"]));
    }

    [Fact]
    public async Task TenantSecuritySample_ListsPolicyGroupAndRoleManagementPermissions()
    {
        string template = TestPaths.Sample("tenant-security.entra.json");
        string parameters = TestPaths.Sample("tenant-security.parameters.json");

        var app = await RunJsonAsync(template, parameters);
        var delegated = await RunJsonAsync(template, parameters, "--mode", "delegated");

        // Review 1D: authentication strengths need Policy.ReadWrite.ConditionalAccess (Learn), authentication contexts AuthenticationContext.ReadWrite.All,
        // group members of unknown kind may be service principals (Application.ReadWrite.All) and every edge resolves principals (Directory.Read.All).
        string[] common =
        [
            "AdministrativeUnit.ReadWrite.All",
            "Application.Read.All",
            "Application.ReadWrite.All",
            "AuthenticationContext.ReadWrite.All",
            "Directory.Read.All",
            "Group.ReadWrite.All",
            "GroupMember.ReadWrite.All",
            "Policy.Read.All",
            "Policy.ReadWrite.ConditionalAccess",
            "RoleManagement.ReadWrite.Directory",
        ];
        Assert.Equal(10, app.Json["resourceCount"]!.GetValue<int>());
        Assert.Equal(["AdministrativeUnit.ReadWrite.All", "Application.Read.All", "Application.ReadWrite.All", "AuthenticationContext.ReadWrite.All", "Directory.Read.All", "Group.ReadWrite.All", "GroupMember.ReadWrite.All", "Organization.Read.All", "Policy.Read.All", "Policy.ReadWrite.ConditionalAccess", "RoleManagement.ReadWrite.Directory"], Names(app));

        // The preflight read of /organization is an application-mode requirement only.
        Assert.Equal(common, Names(delegated));
        Assert.Equal(
            [
                "Authentication Policy Administrator",
                "Conditional Access Administrator",
                "Conditional Access Administrator|Security Administrator",
                "Groups Administrator",
                "Privileged Role Administrator",
            ],
            RoleGroups(delegated));
    }

    // ---- purpose-built templates ----

    private const string OwnedApplication =
        "{ \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"owned-app\", \"properties\": { \"displayName\": \"Owned\", \"owners\": [\"11111111-1111-1111-1111-111111111111\"] } }";

    private const string UnownedApplication =
        "{ \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"unowned-app\", \"properties\": { \"displayName\": \"Unowned\" } }";

    [Fact]
    public async Task ApplicationWithOwners_NeedsOwnedByInAppMode_AndAllInDelegatedMode()
    {
        using var sandbox = new CliSandbox(nameof(ApplicationWithOwners_NeedsOwnedByInAppMode_AndAllInDelegatedMode));
        string template = sandbox.WriteTemplate("owned.entra.json", "\"resources\": [ " + OwnedApplication + " ]");

        var app = await CliRunner.RunAsync("permissions", "-f", template, "-o", "json");
        var delegated = await CliRunner.RunAsync("permissions", "-f", template, "-o", "json", "--mode", "delegated");

        // Declaring owners adds the edge pass' Directory.Read.All (Learn: application-post-owners).
        Assert.Equal(["Application.ReadWrite.OwnedBy", "Directory.Read.All", "Organization.Read.All"], Names(app));
        Assert.Equal(["Application.ReadWrite.All", "Directory.Read.All"], Names(delegated));
        Assert.Equal(["Application Administrator|Application Developer|Cloud Application Administrator"], RoleGroups(delegated));
    }

    [Fact]
    public async Task ApplicationWithoutOwners_NeedsAllInBothModes()
    {
        using var sandbox = new CliSandbox(nameof(ApplicationWithoutOwners_NeedsAllInBothModes));
        string template = sandbox.WriteTemplate("unowned.entra.json", "\"resources\": [ " + UnownedApplication + " ]");

        var app = await CliRunner.RunAsync("permissions", "-f", template, "-o", "json");
        var delegated = await CliRunner.RunAsync("permissions", "-f", template, "-o", "json", "--mode", "delegated");

        Assert.Equal(["Application.ReadWrite.All", "Organization.Read.All"], Names(app));
        Assert.Equal(["Application.ReadWrite.All"], Names(delegated));
    }

    [Fact]
    public async Task OwnedAndUnownedApplications_ListOnlyTheBroaderGrant_NamingBothResources()
    {
        using var sandbox = new CliSandbox(nameof(OwnedAndUnownedApplications_ListOnlyTheBroaderGrant_NamingBothResources));
        string template = sandbox.WriteTemplate("mixed.entra.json", "\"resources\": [ " + OwnedApplication + ", " + UnownedApplication + " ]");

        var app = await CliRunner.RunAsync("permissions", "-f", template, "-o", "json");

        Assert.Equal(["Application.ReadWrite.All", "Directory.Read.All", "Organization.Read.All"], Names(app));
        var all = Entry(app, "Application.ReadWrite.All");
        Assert.Equal(
            ["/providers/Microsoft.Entra/applications/owned-app", "/providers/Microsoft.Entra/applications/unowned-app"],
            Strings(all["resources"]));
        Assert.Contains(Strings(all["reasons"]), r => r.StartsWith("Application.ReadWrite.OwnedBy would suffice", StringComparison.Ordinal));
    }

    private const string RoleAssignableGroup =
        "{ \"condition\": \"[parameters('enabled')]\", \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"admins\", " +
        "\"properties\": { \"displayName\": \"Admins\", \"mailEnabled\": false, \"mailNickname\": \"admins\", \"securityEnabled\": true, \"isAssignableToRole\": true } }";

    [Fact]
    public async Task ResourceWithConditionFalse_IsExcluded_AndCounted()
    {
        using var sandbox = new CliSandbox(nameof(ResourceWithConditionFalse_IsExcluded_AndCounted));
        string template = sandbox.WriteTemplate(
            "condition.entra.json",
            "\"parameters\": { \"enabled\": { \"type\": \"bool\", \"defaultValue\": false } }, \"resources\": [ " + UnownedApplication + ", " + RoleAssignableGroup + " ]");

        var off = await CliRunner.RunAsync("permissions", "-f", template, "-o", "json");
        var on = await CliRunner.RunAsync("permissions", "-f", template, "-o", "json", "-p", "enabled=true");

        Assert.Equal(0, off.ExitCode);
        Assert.Equal(1, off.Json["resourceCount"]!.GetValue<int>());
        Assert.DoesNotContain("RoleManagement.ReadWrite.Directory", Names(off));
        Assert.DoesNotContain("Group.ReadWrite.All", Names(off));
        Assert.DoesNotContain(off.Json["permissions"]!.AsArray(), p => Strings(p!["resources"]).Any(r => r.Contains("/groups/admins", StringComparison.Ordinal)));
        Assert.Contains("1 resource(s) with a condition that evaluates to false offline are not included.", Strings(off.Json["notes"]));

        Assert.Equal(2, on.Json["resourceCount"]!.GetValue<int>());
        Assert.Contains("RoleManagement.ReadWrite.Directory", Names(on));
        Assert.Contains("Group.ReadWrite.All", Names(on));
        Assert.Contains("/providers/Microsoft.Entra/groups/admins", Strings(Entry(on, "RoleManagement.ReadWrite.Directory")["resources"]));
        Assert.DoesNotContain(Strings(on.Json["notes"]), n => n.Contains("condition that evaluates to false", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OrdersSample_WithEnvDev_LeavesOutTheConditionalAccessPolicy()
    {
        var prod = await OrdersAsync();
        var dev = await OrdersAsync("-p", "env=dev");

        Assert.Equal(0, dev.ExitCode);
        Assert.Equal(16, prod.Json["resourceCount"]!.GetValue<int>());
        Assert.Equal(15, dev.Json["resourceCount"]!.GetValue<int>());
        Assert.Contains(prod.Json["permissions"]!.AsArray(), p => Strings(p!["resources"]).Any(r => r.Contains("/policies/CA-Orders-Admins-MFA", StringComparison.Ordinal)));
        Assert.DoesNotContain(dev.Json["permissions"]!.AsArray(), p => Strings(p!["resources"]).Any(r => r.Contains("/policies/", StringComparison.Ordinal)));
        Assert.Contains("1 resource(s) with a condition that evaluates to false offline are not included.", Strings(dev.Json["notes"]));
    }

    [Fact]
    public async Task LookupOnlyTemplate_ListsTheReadPermissionsOfTheLookups()
    {
        using var sandbox = new CliSandbox(nameof(LookupOnlyTemplate_ListsTheReadPermissionsOfTheLookups));
        string description = "[format('{0}{1}{2}', roleTemplate('Global Reader'), appRole('00000003-0000-0000-c000-000000000000', 'User.Read.All'), permissionScope('00000003-0000-0000-c000-000000000000', 'User.Read'))]";
        string template = sandbox.WriteTemplate(
            "lookups.entra.json",
            "\"resources\": [ { \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"lookup-app\", \"properties\": { \"displayName\": \"L\", \"owners\": [\"11111111-1111-1111-1111-111111111111\"], \"description\": \"" + description + "\" } } ]");

        var result = await CliRunner.RunAsync("permissions", "-f", template, "-o", "json");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["Application.Read.All", "Application.ReadWrite.OwnedBy", "Directory.Read.All", "Organization.Read.All", "RoleManagement.Read.Directory"], Names(result));
        Assert.Equal(["lookup: appRole()", "lookup: permissionScope()"], Strings(Entry(result, "Application.Read.All")["reasons"]));
        Assert.Equal(["lookup: roleTemplate() (custom role names)"], Strings(Entry(result, "RoleManagement.Read.Directory")["reasons"]));
        Assert.Equal(["(expression lookups)"], Strings(Entry(result, "RoleManagement.Read.Directory")["resources"]));
    }

    [Fact]
    public async Task ExistingResourceLookup_NeedsTheReadVariant_UnlessAWritePermissionCoversIt()
    {
        using var sandbox = new CliSandbox(nameof(ExistingResourceLookup_NeedsTheReadVariant_UnlessAWritePermissionCoversIt));
        string lookups = "[format('{0}{1}', reference(resourceId('Microsoft.Entra/users', 'x@contoso.com'), '2026-10-01-preview').displayName, reference(resourceId('Microsoft.Entra/groups', 'some-group'), '2026-10-01-preview').displayName)]";
        string template = sandbox.WriteTemplate(
            "existing.entra.json",
            "\"resources\": [ { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"plain-group\", \"properties\": { \"displayName\": \"Plain\", \"mailEnabled\": false, \"mailNickname\": \"plain\", \"securityEnabled\": true, \"description\": \"" + lookups + "\" } } ]");

        var result = await CliRunner.RunAsync("permissions", "-f", template, "-o", "json");

        Assert.Equal(0, result.ExitCode);
        // The groups overlay lists GroupMember.ReadWrite.All too, so a group lookup also needs GroupMember.Read.All (nothing here covers it).
        Assert.Equal(["Group.ReadWrite.All", "GroupMember.Read.All", "Organization.Read.All", "User.Read.All"], Names(result));
        Assert.DoesNotContain("Group.Read.All", Names(result));
        Assert.Equal(["reference(Microsoft.Entra/users, apiVersion)"], Strings(Entry(result, "User.Read.All")["resources"]));
    }

    [Fact]
    public async Task GroupEdges_AddGroupMemberPermissionsAndTheirRoles()
    {
        using var sandbox = new CliSandbox(nameof(GroupEdges_AddGroupMemberPermissionsAndTheirRoles));
        string template = sandbox.WriteTemplate(
            "edges.entra.json",
            "\"resources\": [ { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"empty-members\", \"properties\": { \"displayName\": \"E\", \"mailEnabled\": false, \"mailNickname\": \"e\", \"securityEnabled\": true, \"members\": [] } } ]");

        var app = await CliRunner.RunAsync("permissions", "-f", template, "-o", "json");
        var delegated = await CliRunner.RunAsync("permissions", "-f", template, "-o", "json", "--mode", "delegated");

        // An empty array still acts (it removes edges), so the edge permission is required.
        Assert.Contains("GroupMember.ReadWrite.All", Names(app));
        Assert.Contains("edge: members", Strings(Entry(app, "GroupMember.ReadWrite.All")["reasons"]));
        Assert.Equal(["Groups Administrator"], RoleGroups(delegated));
    }

    // ---- failures ----

    [Fact]
    public async Task BrokenTemplate_ExitsTwo_AndListsNoPermissions()
    {
        var json = await CliRunner.RunAsync("permissions", "-f", TestPaths.InvalidFixture("unknown-type.entra.json"), "-o", "json");
        var table = await CliRunner.RunAsync("permissions", "-f", TestPaths.InvalidFixture("bad-guid-name.entra.json"));

        Assert.Equal(2, json.ExitCode);
        Assert.Equal("Failed", json.Json["status"]!.GetValue<string>());
        Assert.Contains(json.Diagnostics, d => d!["code"]!.GetValue<string>() == "UnknownResourceType");
        Assert.Null(json.Json["permissions"]);
        Assert.Equal(2, table.ExitCode);
        Assert.DoesNotContain("Permissions required by", table.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TemplateThatCannotBeLoaded_ExitsOne()
    {
        using var sandbox = new CliSandbox(nameof(TemplateThatCannotBeLoaded_ExitsOne));

        var missing = await CliRunner.RunAsync("permissions", "-f", System.IO.Path.Combine(sandbox.TemplatesDir, "nope.entra.json"), "-o", "json");
        var notJson = await CliRunner.RunAsync("permissions", "-f", TestPaths.InvalidFixture("not-json.entra.json"), "-o", "json");

        Assert.Equal(1, missing.ExitCode);
        Assert.Contains(missing.Diagnostics, d => d!["code"]!.GetValue<string>() == "FileNotFound");
        Assert.Equal(1, notJson.ExitCode);
        Assert.Contains(notJson.Diagnostics, d => d!["code"]!.GetValue<string>() == "InvalidJson");
    }

    [Fact]
    public async Task MalformedArguments_ExitOne()
    {
        var token = await CliRunner.RunAsync("permissions", "-f", TestPaths.Sample("orders-api.entra.json"), "-p", "notatoken", "-o", "json");
        var mode = await CliRunner.RunAsync("permissions", "-f", TestPaths.Sample("orders-api.entra.json"), "--mode", "both");
        var noTemplate = await CliRunner.RunAsync("permissions");

        Assert.Equal(1, token.ExitCode);
        Assert.Contains(token.Diagnostics, d => d!["code"]!.GetValue<string>() == "InvalidArgument");
        Assert.Equal(1, mode.ExitCode);
        Assert.Contains("app", mode.StandardError, StringComparison.Ordinal);
        Assert.Contains("delegated", mode.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, noTemplate.ExitCode);
        Assert.Contains("--template-file", noTemplate.StandardError, StringComparison.Ordinal);
    }

    // ---- output shapes ----

    [Fact]
    public async Task JsonOutput_HasTheDocumentedShape()
    {
        var result = await OrdersAsync();

        Assert.Equal(["template", "mode", "resourceCount", "permissions", "entraRoles", "notes"], result.Json.Select(p => p.Key).ToList());
        Assert.Equal(TestPaths.Sample("orders-api.entra.json"), result.Json["template"]!.GetValue<string>());
        foreach (var permission in result.Json["permissions"]!.AsArray())
        {
            Assert.Equal(["name", "kind", "resources", "reasons"], permission!.AsObject().Select(p => p.Key).ToList());
            Assert.Contains(permission["kind"]!.GetValue<string>(), new[] { "graphApplication", "graphDelegated", "azureRbac" });
            Assert.NotEmpty(Strings(permission["resources"]));
            Assert.NotEmpty(Strings(permission["reasons"]));
        }

        var delegated = await OrdersAsync("--mode", "delegated");
        foreach (var role in delegated.Json["entraRoles"]!.AsArray())
        {
            Assert.Equal(["anyOf", "resources", "reasons"], role!.AsObject().Select(p => p.Key).ToList());
        }

        Assert.All(Strings(result.Json["notes"]), n => Assert.False(string.IsNullOrWhiteSpace(n)));
    }

    [Fact]
    public async Task NoneOutput_PrintsNothing_AndExitsZero()
    {
        var result = await CliRunner.RunAsync("permissions", "-f", TestPaths.Sample("orders-api.entra.json"), "-p", "@" + TestPaths.Sample("orders-api.parameters.json"), "-o", "none");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task TableOutput_ListsPermissionsRolesAndNotes()
    {
        string template = TestPaths.Sample("orders-api.entra.json");
        string parameters = "@" + TestPaths.Sample("orders-api.parameters.json");

        var app = await CliRunner.RunAsync("permissions", "-f", template, "-p", parameters);
        var delegated = await CliRunner.RunAsync("permissions", "-f", template, "-p", parameters, "--mode", "delegated");

        Assert.Equal(0, app.ExitCode);
        Assert.Contains("(mode: app, 16 resource(s))", app.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Microsoft Graph application permissions", app.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("  Application.ReadWrite.All", app.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Azure (signed-in identity)", app.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("parameter 'initialPassword'", app.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("note: ", app.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Entra roles", app.StandardOutput, StringComparison.Ordinal);

        Assert.Equal(0, delegated.ExitCode);
        Assert.Contains("(mode: delegated, 16 resource(s))", delegated.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Microsoft Graph delegated permissions", delegated.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Entra roles (the signed-in user needs at least one role of each group)", delegated.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("  Application Administrator | Cloud Application Administrator", delegated.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Permissions_NeverSendsARequestOrAsksForAToken()
    {
        using var sandbox = new CliSandbox(nameof(Permissions_NeverSendsARequestOrAsksForAToken));
        sandbox.UseNotLoggedIn();

        string[] args = ["permissions", "-f", TestPaths.Sample("orders-api.entra.json"), "-p", "@" + TestPaths.Sample("orders-api.parameters.json"), "-o", "json"];
        var result = await CliRunner.RunAsync(args);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(sandbox.RecordedCommands());
        Assert.Empty(sandbox.Tokens.RequestedScopes);
    }
}
