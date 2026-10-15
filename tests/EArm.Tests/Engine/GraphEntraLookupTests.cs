using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Az;
using EArm.Engine.Planning;
using EArm.Expressions.Evaluation;
using EArm.TestSupport;
using Xunit;

namespace EArm.Tests.Engine;

/// <summary>The engine's <see cref="IEntraLookup"/> over <see cref="FakeGraphClient"/> (WI-16): service principal first, application fallback, caching, messages, filters.</summary>
public sealed class GraphEntraLookupTests
{
    private const string AppId = "aaaaaaaa-1111-2222-3333-444444444444";

    private static JsonArray Members(params (string Id, string Value)[] members)
    {
        var array = new JsonArray();
        foreach (var (id, value) in members)
        {
            array.Add(new JsonObject { ["id"] = id, ["value"] = value });
        }

        return array;
    }

    private static string Id(EntraLookupResult result)
    {
        return Assert.IsType<EntraLookupResult.Found>(result).Id;
    }

    private static string Reason(EntraLookupResult result)
    {
        return Assert.IsType<EntraLookupResult.NotFound>(result).Reason;
    }

    [Fact]
    public async Task AppRolesAndScopes_AreReadFromTheServicePrincipal_OnceAndCached()
    {
        var host = new EngineHost();
        host.Directory.AddServicePrincipal(AppId, "Orders API", sp =>
        {
            sp["appRoles"] = Members(("role-admin", "Orders.Admin"), ("role-read", "Orders.Read"));
            sp["oauth2PermissionScopes"] = Members(("scope-read", "Orders.Read"));
        });
        var lookup = new GraphEntraLookup(host.Graph);

        Assert.Equal("role-admin", Id(await lookup.FindAppRoleIdAsync(AppId, "Orders.Admin")));
        Assert.Equal("role-read", Id(await lookup.FindAppRoleIdAsync(AppId, "orders.read")));
        Assert.Equal("scope-read", Id(await lookup.FindPermissionScopeIdAsync(AppId, "Orders.Read")));
        Assert.Contains("declares no app role 'Orders.Write' (available: Orders.Admin, Orders.Read)", Reason(await lookup.FindAppRoleIdAsync(AppId, "Orders.Write")));
        Assert.Contains("the service principal for appId '" + AppId + "'", Reason(await lookup.FindPermissionScopeIdAsync(AppId, "Nope")));

        var request = Assert.Single(host.Fake.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/servicePrincipals(appId='" + AppId + "')", request.Path);
        Assert.Equal("id,appId,appRoles,oauth2PermissionScopes", request.UrlParameters!["$select"]);
    }

    [Fact]
    public async Task ExactCaseWins_OverACaseInsensitiveMatch()
    {
        var host = new EngineHost();
        host.Directory.AddServicePrincipal(AppId, "Orders API", sp => sp["appRoles"] = Members(("role-lower", "orders.read"), ("role-exact", "Orders.Read")));
        var lookup = new GraphEntraLookup(host.Graph);

        Assert.Equal("role-exact", Id(await lookup.FindAppRoleIdAsync(AppId, "Orders.Read")));
        Assert.Equal("role-lower", Id(await lookup.FindAppRoleIdAsync(AppId, "ORDERS.READ")));
    }

    [Fact]
    public async Task WithoutAServicePrincipal_TheApplicationObjectAnswers()
    {
        var host = new EngineHost();
        host.Directory.AddApplication("orders-api", "Orders API", AppId, app =>
        {
            app["appRoles"] = Members(("role-admin", "Orders.Admin"));
            app["api"] = new JsonObject { ["oauth2PermissionScopes"] = Members(("scope-read", "Orders.Read")) };
        });
        var lookup = new GraphEntraLookup(host.Graph);

        Assert.Equal("role-admin", Id(await lookup.FindAppRoleIdAsync(AppId, "Orders.Admin")));
        Assert.Equal("scope-read", Id(await lookup.FindPermissionScopeIdAsync(AppId, "Orders.Read")));
        Assert.Contains("the application for appId '" + AppId + "' declares no permission scope 'X' (available: Orders.Read)", Reason(await lookup.FindPermissionScopeIdAsync(AppId, "X")));

        Assert.Equal(2, host.Fake.Requests.Count);
        Assert.Equal("/servicePrincipals(appId='" + AppId + "')", host.Fake.Requests[0].Path);
        Assert.Equal("/applications(appId='" + AppId + "')", host.Fake.Requests[1].Path);
        Assert.Equal("id,appId,appRoles,api", host.Fake.Requests[1].UrlParameters!["$select"]);
    }

    [Fact]
    public async Task NothingDeclared_IsSaidSo()
    {
        var host = new EngineHost();
        host.Directory.AddApplication("bare", "Bare", AppId);
        var lookup = new GraphEntraLookup(host.Graph);

        Assert.Contains("declares no app role 'X' (it declares none)", Reason(await lookup.FindAppRoleIdAsync(AppId, "X")));
    }

    [Fact]
    public async Task UnknownAppId_IsNotFound_AndNonGuidsNeverHitGraph()
    {
        var host = new EngineHost();
        var lookup = new GraphEntraLookup(host.Graph);

        Assert.Contains("no service principal or application with appId '" + AppId + "' exists in the tenant", Reason(await lookup.FindAppRoleIdAsync(AppId, "X")));
        Assert.Contains("no service principal or application with appId", Reason(await lookup.FindPermissionScopeIdAsync(AppId, "Y")));
        Assert.Equal(2, host.Fake.Requests.Count);

        Assert.Contains("'orders-api' is not an application id (GUID)", Reason(await lookup.FindAppRoleIdAsync("orders-api", "X")));
        Assert.Equal(2, host.Fake.Requests.Count);
    }

    [Fact]
    public async Task RoleDefinitions_AreFilteredByDisplayName_WithQuotesDoubled_AndCached()
    {
        var host = new EngineHost();
        host.Directory.Add("/roleManagement/directory/roleDefinitions", new JsonObject { ["id"] = "RD1", ["displayName"] = "Orders Support", ["isBuiltIn"] = false });
        host.Directory.Add("/roleManagement/directory/roleDefinitions", new JsonObject { ["id"] = "RD2", ["displayName"] = "It's Complicated", ["isBuiltIn"] = false });
        host.Directory.Add("/roleManagement/directory/roleDefinitions", new JsonObject { ["id"] = "RD3", ["displayName"] = "Twice" });
        host.Directory.Add("/roleManagement/directory/roleDefinitions", new JsonObject { ["id"] = "RD4", ["displayName"] = "Twice" });
        var lookup = new GraphEntraLookup(host.Graph);

        Assert.Equal("RD1", Id(await lookup.FindRoleDefinitionIdAsync("Orders Support")));
        Assert.Equal("RD1", Id(await lookup.FindRoleDefinitionIdAsync("Orders Support")));
        Assert.Equal("RD2", Id(await lookup.FindRoleDefinitionIdAsync("It's Complicated")));
        Assert.Contains("no role definition named 'Nope' exists in the tenant", Reason(await lookup.FindRoleDefinitionIdAsync("Nope")));
        string twice = Reason(await lookup.FindRoleDefinitionIdAsync("Twice"));
        Assert.Contains("2 role definitions are named 'Twice' (RD3, RD4); pass the id instead", twice);

        var requests = host.Fake.Requests;
        Assert.Equal(4, requests.Count);
        Assert.All(requests, r => Assert.Equal("/roleManagement/directory/roleDefinitions", r.Path));
        Assert.Equal("displayName eq 'Orders Support'", requests[0].UrlParameters!["$filter"]);
        Assert.Equal("id,displayName,isBuiltIn", requests[0].UrlParameters!["$select"]);
        Assert.Equal("displayName eq 'It''s Complicated'", requests[1].UrlParameters!["$filter"]);
    }

    [Fact]
    public async Task FailedReads_AreNotCached_AndGoThroughTheRetryPolicy()
    {
        var host = new EngineHost();
        host.Directory.AddServicePrincipal(AppId, "Orders API", sp => sp["appRoles"] = Members(("role-admin", "Orders.Admin")));
        host.Fake.Failures.ServerError("/servicePrincipals", times: 1);
        var uncached = new GraphEntraLookup(host.Graph);

        await Assert.ThrowsAsync<GraphException>(() => uncached.FindAppRoleIdAsync(AppId, "Orders.Admin"));
        Assert.Equal("role-admin", Id(await uncached.FindAppRoleIdAsync(AppId, "Orders.Admin")));
        Assert.Equal(2, host.Fake.Requests.Count);

        host.Fake.ClearRequests();
        host.Fake.Failures.ServerError("/servicePrincipals", times: 2);
        var retried = new GraphEntraLookup(host.Graph, host.Retry);

        Assert.Equal("role-admin", Id(await retried.FindAppRoleIdAsync(AppId, "Orders.Admin")));
        Assert.Equal(3, host.Fake.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], host.Delays.Delays);
    }

    [Fact]
    public async Task ConcurrentCallers_ShareOneRead()
    {
        var host = new EngineHost();
        host.Directory.AddServicePrincipal(AppId, "Orders API", sp => sp["appRoles"] = Members(("role-admin", "Orders.Admin")));
        var lookup = new GraphEntraLookup(host.Graph);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => lookup.FindAppRoleIdAsync(AppId, "Orders.Admin"))));

        Assert.All(results, r => Assert.Equal("role-admin", Id(r)));
        Assert.Single(host.Fake.Requests);
    }
}
