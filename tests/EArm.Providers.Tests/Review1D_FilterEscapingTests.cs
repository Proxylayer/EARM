using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Engine.Providers;
using EArm.Graph;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// Review 1D / Q-09: the apostrophe escaping of an OData string literal (<c>'</c> → <c>''</c>) was removed from five lookups without a single
/// test failing. Every lookup that puts a name into <c>$filter</c> is exercised with a name that contains an apostrophe, and the recorded request
/// must carry the doubled quote (the fake parses the literal back, so the object is found again on a rerun).
/// </summary>
public sealed class Review1D_FilterEscapingTests
{
    private static void AssertFilter(ProviderHost host, string pathStartsWith, string expected)
    {
        var filters = host.Fake.Requests
            .Where(r => r.Method == "GET" && r.Path.StartsWith(pathStartsWith, StringComparison.Ordinal) && r.UrlParameters is not null && r.UrlParameters.ContainsKey("$filter"))
            .Select(r => r.UrlParameters!["$filter"])
            .ToList();
        Assert.True(filters.Contains(expected), $"expected a filter '{expected}' on {pathStartsWith}; saw: " + string.Join(" | ", filters));
    }

    [Fact]
    public async Task ConditionalAccessPolicies_EscapeTheDisplayName()
    {
        var host = new ProviderHost();
        string template = Templates.Resource("Microsoft.Entra/conditionalAccess/policies", "CA-O'Brien MFA", """{ "conditions": { "users": { "includeGroups": ["aaaaaaaa-1111-2222-3333-444444444444"] }, "applications": { "includeApplications": ["All"] } }, "grantControls": { "builtInControls": ["mfa"] } }""");

        Check.Succeeded(await host.CreateAsync(host.Resources(template)));
        var rerun = await host.CreateAsync(host.Resources(template));

        AssertFilter(host, "/identity/conditionalAccess/policies", "displayName eq 'CA-O''Brien MFA'");
        Assert.Equal(ChangeType.NoChange, rerun.OpOfType("Microsoft.Entra/conditionalAccess/policies").ChangeType);
    }

    [Fact]
    public async Task NamedLocations_EscapeTheDisplayName()
    {
        var host = new ProviderHost();
        string template = Templates.Resource("Microsoft.Entra/conditionalAccess/namedLocations", "O'Brien offices", """{ "@odata.type": "#microsoft.graph.ipNamedLocation", "ipRanges": [ { "cidrAddress": "10.0.0.0/8" } ] }""");

        Check.Succeeded(await host.CreateAsync(host.Resources(template)));
        var rerun = await host.CreateAsync(host.Resources(template));

        AssertFilter(host, "/identity/conditionalAccess/namedLocations", "displayName eq 'O''Brien offices'");
        Assert.Equal(ChangeType.NoChange, rerun.OpOfType("Microsoft.Entra/conditionalAccess/namedLocations").ChangeType);
    }

    [Fact]
    public async Task RoleDefinitions_EscapeTheDisplayName()
    {
        var host = new ProviderHost();
        string template = Templates.Resource("Microsoft.Entra/roleDefinitions", "O'Brien Support", """{ "description": "x", "rolePermissions": [ { "allowedResourceActions": ["microsoft.directory/users/standard/read"] } ] }""");

        Check.Succeeded(await host.CreateAsync(host.Resources(template)));
        var rerun = await host.CreateAsync(host.Resources(template));

        AssertFilter(host, "/roleManagement/directory/roleDefinitions", "displayName eq 'O''Brien Support'");
        Assert.Equal(ChangeType.NoChange, rerun.OpOfType("Microsoft.Entra/roleDefinitions").ChangeType);
    }

    [Fact]
    public async Task AdministrativeUnits_EscapeTheDisplayName_OnReadAndOnTheSoftDeleteProbe()
    {
        var host = new ProviderHost();
        string template = Templates.Resource("Microsoft.Entra/administrativeUnits", "O'Brien AU", """{ "description": "x" }""");

        Check.Succeeded(await host.CreateAsync(host.Resources(template)));
        var rerun = await host.CreateAsync(host.Resources(template));

        AssertFilter(host, "/directory/administrativeUnits", "displayName eq 'O''Brien AU'");
        AssertFilter(host, "/directory/deletedItems/microsoft.graph.administrativeUnit", "displayName eq 'O''Brien AU'");
        Assert.Equal(ChangeType.NoChange, rerun.OpOfType("Microsoft.Entra/administrativeUnits").ChangeType);
    }

    [Fact]
    public async Task Applications_EscapeTheUniqueName_InTheSoftDeleteProbe()
    {
        var host = new ProviderHost();

        Check.Succeeded(await host.CreateAsync(host.Resources(Templates.Resource(ApplicationsProvider.ResourceType, "o'brien-app", """{ "displayName": "O'Brien" }"""))));

        AssertFilter(host, "/directory/deletedItems/microsoft.graph.application", "uniqueName eq 'o''brien-app'");
        Assert.NotNull(host.Directory.Find("/applications", "uniqueName", "o'brien-app"));
    }

    [Fact]
    public async Task Users_EscapeTheUserPrincipalName_InTheSoftDeleteProbe()
    {
        var host = new ProviderHost();

        Check.Succeeded(await host.CreateAsync(host.Resources(Templates.Resource(UsersProvider.ResourceType, "o'brien@contoso.com", """{ "displayName": "O'Brien" }"""))));

        AssertFilter(host, "/directory/deletedItems/microsoft.graph.user", "userPrincipalName eq 'o''brien@contoso.com'");
    }

    [Fact]
    public async Task Groups_EscapeTheMailNickname_InTheSoftDeleteProbe()
    {
        var host = new ProviderHost();

        Check.Succeeded(await host.CreateAsync(host.Resources(Templates.Resource("Microsoft.Entra/groups", "o-brien-team", """{ "displayName": "O'Brien", "groupTypes": ["Unified"], "mailEnabled": true, "mailNickname": "o'brien" }"""))));

        AssertFilter(host, "/directory/deletedItems/microsoft.graph.group", "mailNickname eq 'o''brien'");
    }

    [Fact]
    public async Task Applications_EscapeTheKeyInTheAlternateKeyPath()
    {
        var host = new ProviderHost();

        Check.Succeeded(await host.CreateAsync(host.Resources(Templates.Resource(ApplicationsProvider.ResourceType, "o'brien-app", """{ "displayName": "O'Brien" }"""))));

        Assert.Contains(host.Fake.Requests, r => r.Path == "/applications(uniqueName='o''brien-app')");
    }

    [Fact]
    public async Task TheSharedLookups_EscapeRoleDefinitionAndAdministrativeUnitNames()
    {
        var host = new ProviderHost();
        host.Directory.Add("/roleManagement/directory/roleDefinitions", new JsonObject { ["displayName"] = "O'Role", ["isBuiltIn"] = false });
        host.Directory.Add("/directory/administrativeUnits", new JsonObject { ["displayName"] = "O'Unit" });

        var roles = await RoleAssignmentsLookup.FindRoleDefinitionsByNameAsync(host.Fake, "O'Role", GraphApi.V1, RetryHint.Default, ProviderHost.Ct);
        var units = await RoleAssignmentsLookup.FindAdministrativeUnitsByNameAsync(host.Fake, "O'Unit", GraphApi.V1, RetryHint.Default, ProviderHost.Ct);

        Assert.Single(roles);
        Assert.Single(units);
        AssertFilter(host, "/roleManagement/directory/roleDefinitions", "displayName eq 'O''Role'");
        AssertFilter(host, "/directory/administrativeUnits", "displayName eq 'O''Unit'");
    }

    [Fact]
    public void TheSharedEscapeHelper_DoublesEveryApostrophe()
    {
        Assert.Equal("a''b''''c", ProviderSupport.EscapeLiteral("a'b''c"));
        Assert.Equal("plain", ProviderSupport.EscapeLiteral("plain"));
        Assert.Equal(string.Empty, ProviderSupport.EscapeLiteral(string.Empty));
    }
}
