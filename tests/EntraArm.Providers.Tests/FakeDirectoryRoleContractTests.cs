using System;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EntraArm.Az;
using EntraArm.Providers.Tests.Infrastructure;
using EntraArm.TestSupport;
using Xunit;

namespace EntraArm.Providers.Tests;

/// <summary>Contract tests of the role behaviours <see cref="FakeDirectory"/> models, so the provider scenarios rest on a faithful fake.</summary>
public sealed class FakeDirectoryRoleContractTests
{
    private const string Assignments = BuiltInRoles.AssignmentsPath;
    private const string Definitions = BuiltInRoles.DefinitionsPath;

    private static async Task<GraphResponse> AssignAsync(FakeGraphClient client, string roleId, string principalId, string? scope = "/")
    {
        var body = new JsonObject { ["@odata.type"] = "#microsoft.graph.unifiedRoleAssignment", ["roleDefinitionId"] = roleId, ["principalId"] = principalId };
        if (scope is not null)
        {
            body["directoryScopeId"] = scope;
        }

        return await client.PostAsync(new GraphRequest(Assignments), body, ProviderHost.Ct);
    }

    private static string AddRole(FakeGraphClient client, bool builtIn = false)
    {
        return client.Directory.Add(Definitions, new JsonObject { ["displayName"] = "Role " + Guid.NewGuid().ToString("N")[..6], ["isBuiltIn"] = builtIn, ["rolePermissions"] = new JsonArray() })["id"]!.GetValue<string>();
    }

    [Fact]
    public async Task ACreatedAssignment_DefaultsTheScope_AndDoesNotStoreTheODataType()
    {
        var client = new FakeGraphClient();
        string roleId = AddRole(client);
        string userId = client.Directory.AddUser("a@contoso.com", "A")["id"]!.GetValue<string>();

        var response = await AssignAsync(client, roleId, userId, scope: null);

        Assert.Equal(HttpStatusCode.Created, response.Status);
        var stored = Assert.Single(client.Directory.Objects(Assignments));
        Assert.Equal("/", stored["directoryScopeId"]!.GetValue<string>());
        Assert.False(stored.ContainsKey("@odata.type"));
        Assert.NotNull(stored["id"]);
    }

    [Fact]
    public async Task ADuplicateAssignment_IsThe400Graph_Returns_AndIsClassifiedAlreadyExists()
    {
        var client = new FakeGraphClient();
        string roleId = AddRole(client);
        string userId = client.Directory.AddUser("a@contoso.com", "A")["id"]!.GetValue<string>();
        await AssignAsync(client, roleId, userId);

        var ex = await Assert.ThrowsAsync<GraphException>(() => AssignAsync(client, roleId, userId.ToUpperInvariant()));

        Assert.Equal(HttpStatusCode.BadRequest, ex.Status);
        Assert.True(ex.IsAlreadyExists);
        Assert.Contains("conflicting object", ex.GraphMessage, StringComparison.Ordinal);
        Assert.Single(client.Directory.Objects(Assignments));

        // The same role and principal at another scope is a different assignment.
        await AssignAsync(client, roleId, userId, "/administrativeUnits/3b9f9a6e-0d4c-4b1e-8c9c-2f4b7a6d1e10");
        Assert.Equal(2, client.Directory.Objects(Assignments).Count);
    }

    [Fact]
    public async Task ANonRoleAssignableGroup_Is400_ARoleAssignableOneIsAccepted()
    {
        var client = new FakeGraphClient();
        string roleId = AddRole(client);
        string plain = client.Directory.AddGroup("plain", "Plain")["id"]!.GetValue<string>();
        string assignable = client.Directory.AddGroup("assignable", "Assignable", g => g["isAssignableToRole"] = true)["id"]!.GetValue<string>();

        var ex = await Assert.ThrowsAsync<GraphException>(() => AssignAsync(client, roleId, plain));
        Assert.Equal(HttpStatusCode.BadRequest, ex.Status);
        Assert.Contains("role-assignable", ex.GraphMessage, StringComparison.Ordinal);
        Assert.False(ex.IsAlreadyExists);

        await AssignAsync(client, roleId, assignable);
        Assert.Single(client.Directory.Objects(Assignments));
    }

    [Fact]
    public async Task AnUnknownPrincipal_AndANonPrincipalObject_Are400()
    {
        var client = new FakeGraphClient();
        string roleId = AddRole(client);
        string appId = client.Directory.AddApplication("app", "App")["id"]!.GetValue<string>();

        var missing = await Assert.ThrowsAsync<GraphException>(() => AssignAsync(client, roleId, Guid.NewGuid().ToString("D")));
        Assert.Contains("PrincipalNotFound", missing.GraphMessage, StringComparison.Ordinal);

        var application = await Assert.ThrowsAsync<GraphException>(() => AssignAsync(client, roleId, appId));
        Assert.Contains("user, a group or a service principal", application.GraphMessage, StringComparison.Ordinal);

        var incomplete = await Assert.ThrowsAsync<GraphException>(() => client.PostAsync(new GraphRequest(Assignments), new JsonObject { ["principalId"] = Guid.NewGuid().ToString("D") }, ProviderHost.Ct));
        Assert.Equal(HttpStatusCode.BadRequest, incomplete.Status);
    }

    [Fact]
    public async Task DeletingARoleDefinition_IsRefusedWhileAssigned_AndForBuiltIns_ButNotOtherwise()
    {
        var client = new FakeGraphClient();
        string custom = AddRole(client);
        string builtIn = AddRole(client, builtIn: true);
        string free = AddRole(client);
        string userId = client.Directory.AddUser("a@contoso.com", "A")["id"]!.GetValue<string>();
        var assignment = await AssignAsync(client, custom, userId);

        var inUse = await Assert.ThrowsAsync<GraphException>(() => client.DeleteAsync(new GraphRequest($"{Definitions}/{custom}"), ProviderHost.Ct));
        Assert.Equal(HttpStatusCode.BadRequest, inUse.Status);
        Assert.Contains("referenced by one or more role assignments", inUse.GraphMessage, StringComparison.Ordinal);

        var protectedRole = await Assert.ThrowsAsync<GraphException>(() => client.DeleteAsync(new GraphRequest($"{Definitions}/{builtIn}"), ProviderHost.Ct));
        Assert.Contains("Built-in role definitions cannot be deleted", protectedRole.GraphMessage, StringComparison.Ordinal);

        await client.DeleteAsync(new GraphRequest($"{Definitions}/{free}"), ProviderHost.Ct);
        Assert.Null(client.Directory.FindById(free));

        await client.DeleteAsync(new GraphRequest($"{Assignments}/{assignment.Body!["id"]!.GetValue<string>()}"), ProviderHost.Ct);
        await client.DeleteAsync(new GraphRequest($"{Definitions}/{custom}"), ProviderHost.Ct);
        Assert.Null(client.Directory.FindById(custom));
        Assert.NotNull(client.Directory.FindById(builtIn));
    }

    [Fact]
    public async Task ARoleDefinitionCreatedThroughTheApi_IsCustom()
    {
        var client = new FakeGraphClient();

        var response = await client.PostAsync(new GraphRequest(Definitions), new JsonObject { ["displayName"] = "Mine", ["rolePermissions"] = new JsonArray() }, ProviderHost.Ct);

        Assert.False(response.Body!["isBuiltIn"]!.GetValue<bool>());
    }

    [Fact]
    public async Task DeletingAPrincipal_CascadesItsRoleAssignments()
    {
        var client = new FakeGraphClient();
        string roleId = AddRole(client);
        string alice = client.Directory.AddUser("a@contoso.com", "A")["id"]!.GetValue<string>();
        string bob = client.Directory.AddUser("b@contoso.com", "B")["id"]!.GetValue<string>();
        await AssignAsync(client, roleId, alice);
        await AssignAsync(client, roleId, bob);

        await client.DeleteAsync(new GraphRequest("/users/" + alice), ProviderHost.Ct);

        var remaining = Assert.Single(client.Directory.Objects(Assignments));
        Assert.Equal(bob, remaining["principalId"]!.GetValue<string>());
    }

    [Fact]
    public async Task SoftDeletingAPrincipal_CascadesItsRoleAssignmentsToo()
    {
        var client = new FakeGraphClient();
        string roleId = AddRole(client);
        string alice = client.Directory.AddUser("a@contoso.com", "A")["id"]!.GetValue<string>();
        await AssignAsync(client, roleId, alice);

        client.Directory.SoftDelete(alice);

        Assert.Empty(client.Directory.Objects(Assignments));
        Assert.Equal(1, client.Directory.Count("/directory/deletedItems"));
    }

    [Fact]
    public async Task AssignmentsAreListedByPrincipalFilter()
    {
        var client = new FakeGraphClient();
        string roleId = AddRole(client);
        string alice = client.Directory.AddUser("a@contoso.com", "A")["id"]!.GetValue<string>();
        string bob = client.Directory.AddUser("b@contoso.com", "B")["id"]!.GetValue<string>();
        await AssignAsync(client, roleId, alice);
        await AssignAsync(client, roleId, bob);

        var page = await client.GetAsync(new GraphRequest(Assignments).WithFilter($"principalId eq '{alice}'"), ProviderHost.Ct);

        var items = Assert.IsType<JsonArray>(page!["value"]);
        Assert.Equal(alice, Assert.Single(items)!["principalId"]!.GetValue<string>());
    }
}
