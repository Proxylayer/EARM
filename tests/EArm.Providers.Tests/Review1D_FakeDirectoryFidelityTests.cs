using System;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// Review 1D / Q-12: the fake tenant is no longer more forgiving than Microsoft Graph. DELETE soft-deletes applications, Microsoft 365 groups,
/// users, service principals and administrative units (30-day key retention until restore or purge), advanced queries need
/// <c>ConsistencyLevel: eventual</c> (and <c>$count=true</c> for filters), a <c>$filter</c> operator Graph does not offer is a 400, and an undocumented
/// operation is a 405.
/// </summary>
public sealed class Review1D_FakeDirectoryFidelityTests
{
    private static System.Threading.CancellationToken Ct => ProviderHost.Ct;

    private static JsonNode J(string json) => JsonNode.Parse(json)!;

    // ---- soft delete ----

    [Fact]
    public async Task Delete_MovesApplicationsUsersServicePrincipalsAndUnitsToDeletedItems()
    {
        var client = new FakeGraphClient();
        var app = client.Directory.AddApplication("app", "App", Guid.NewGuid().ToString("D"));
        var user = client.Directory.AddUser("a@contoso.com", "A");
        var principal = client.Directory.AddServicePrincipal(Guid.NewGuid().ToString("D"), "SP");
        var unit = client.Directory.Add("/directory/administrativeUnits", new JsonObject { ["displayName"] = "AU" });

        await client.DeleteAsync(new GraphRequest("/applications/" + app["id"]), Ct);
        await client.DeleteAsync(new GraphRequest("/users/" + user["id"]), Ct);
        await client.DeleteAsync(new GraphRequest("/servicePrincipals/" + principal["id"]), Ct);
        await client.DeleteAsync(new GraphRequest("/directory/administrativeUnits/" + unit["id"]), Ct);

        var deleted = client.Directory.Objects("/directory/deletedItems");
        Assert.Equal(4, deleted.Count);
        Assert.Equal(
            ["#microsoft.graph.administrativeUnit", "#microsoft.graph.application", "#microsoft.graph.servicePrincipal", "#microsoft.graph.user"],
            deleted.Select(d => d["@odata.type"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray());
        Assert.All(deleted, d => Assert.NotNull(d["deletedDateTime"]));
        Assert.Empty(client.Directory.Objects("/applications"));
        Assert.Empty(client.Directory.Objects("/users"));
    }

    [Fact]
    public async Task Delete_SecurityAndMicrosoft365GroupsAreSoftDeleted_DistributionListsArePermanent()
    {
        // Review 3I (intentional change): Learn group-delete: "both Microsoft 365 and security groups are moved to a temporary container ... This does not apply
        // to Distribution groups which are permanently deleted immediately".
        var client = new FakeGraphClient();
        var unified = client.Directory.AddGroup("team", "Team", g => { g["groupTypes"] = new JsonArray("Unified"); g["mailEnabled"] = true; });
        var security = client.Directory.AddGroup("sec", "Sec");
        var distribution = client.Directory.AddGroup("dl", "DL", g => { g["mailEnabled"] = true; g["securityEnabled"] = false; });

        await client.DeleteAsync(new GraphRequest("/groups/" + unified["id"]), Ct);
        await client.DeleteAsync(new GraphRequest("/groups/" + security["id"]), Ct);
        await client.DeleteAsync(new GraphRequest("/groups/" + distribution["id"]), Ct);

        var deleted = client.Directory.Objects("/directory/deletedItems");
        Assert.Equal(2, deleted.Count);
        Assert.Contains(deleted, d => d["id"]!.GetValue<string>() == unified["id"]!.GetValue<string>());
        Assert.Contains(deleted, d => d["id"]!.GetValue<string>() == security["id"]!.GetValue<string>());
        Assert.Null(client.Directory.FindById(distribution["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Delete_OfAnApplication_AlsoSoftDeletesItsServicePrincipal()
    {
        var client = new FakeGraphClient();
        string appId = Guid.NewGuid().ToString("D");
        var app = client.Directory.AddApplication("app", "App", appId);
        client.Directory.AddServicePrincipal(appId, "App");

        await client.DeleteAsync(new GraphRequest("/applications/" + app["id"]), Ct);

        Assert.Empty(client.Directory.Objects("/servicePrincipals"));
        Assert.Equal(2, client.Directory.Objects("/directory/deletedItems").Count);
    }

    [Fact]
    public async Task ADeletedKey_ConflictsOnCreate_UntilRestoredOrPurged()
    {
        var client = new FakeGraphClient();
        var app = client.Directory.AddApplication("app", "App");
        var user = client.Directory.AddUser("a@contoso.com", "A");
        await client.DeleteAsync(new GraphRequest("/applications/" + app["id"]), Ct);
        await client.DeleteAsync(new GraphRequest("/users/" + user["id"]), Ct);

        var appConflict = await Assert.ThrowsAsync<GraphException>(() => client.PatchAsync(new GraphRequest("/applications(uniqueName='app')").WithPrefer("create-if-missing"), J("""{ "displayName": "New" }"""), Ct));
        var userConflict = await Assert.ThrowsAsync<GraphException>(() => client.PostAsync(new GraphRequest("/users"), J("""{ "userPrincipalName": "a@contoso.com", "displayName": "New", "accountEnabled": true, "mailNickname": "a", "passwordProfile": { "password": "Xx-1-secret-value" } }"""), Ct));

        Assert.True(appConflict.IsAlreadyExists);
        Assert.True(userConflict.IsAlreadyExists);

        // Purge frees the application name; restore brings the user back.
        await client.DeleteAsync(new GraphRequest("/directory/deletedItems/" + app["id"]), Ct);
        var created = await client.PatchAsync(new GraphRequest("/applications(uniqueName='app')").WithPrefer("create-if-missing"), J("""{ "displayName": "New" }"""), Ct);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        await client.PostAsync(new GraphRequest("/directory/deletedItems/" + user["id"] + "/restore"), null, Ct);
        Assert.NotNull(client.Directory.Find("/users", "userPrincipalName", "a@contoso.com"));
    }

    [Fact]
    public async Task AMicrosoft365GroupNickname_IsHeldByTheDeletedGroup()
    {
        var client = new FakeGraphClient();
        var team = client.Directory.AddGroup("team-a", "Team", g => { g["groupTypes"] = new JsonArray("Unified"); g["mailNickname"] = "team"; });
        await client.DeleteAsync(new GraphRequest("/groups/" + team["id"]), Ct);

        var conflict = await Assert.ThrowsAsync<GraphException>(() => client.PatchAsync(new GraphRequest("/groups(uniqueName='team-b')").WithPrefer("create-if-missing"), J("""{ "displayName": "B", "groupTypes": ["Unified"], "mailNickname": "team", "mailEnabled": true, "securityEnabled": false }"""), Ct));

        Assert.True(conflict.IsAlreadyExists);
    }

    [Fact]
    public async Task Restore_Conflicts_WhenTheKeyWasTakenInTheMeantime()
    {
        var client = new FakeGraphClient();
        var app = client.Directory.AddApplication("app", "App");
        await client.DeleteAsync(new GraphRequest("/applications/" + app["id"]), Ct);
        client.Directory.AddApplication("app", "Squatter"); // direct seeding bypasses the conflict check, like a restore race

        var error = await Assert.ThrowsAsync<GraphException>(() => client.PostAsync(new GraphRequest("/directory/deletedItems/" + app["id"] + "/restore"), null, Ct));

        Assert.Equal(HttpStatusCode.BadRequest, error.Status);
    }

    [Fact]
    public async Task DeletedItems_ExpireAfterThirtyDays()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var client = new FakeGraphClient(new FakeDirectory { Clock = clock });
        var app = client.Directory.AddApplication("app", "App");
        await client.DeleteAsync(new GraphRequest("/applications/" + app["id"]), Ct);
        Assert.Single(client.Directory.Objects("/directory/deletedItems"));

        clock.Now = clock.Now.AddDays(31);
        var created = await client.PatchAsync(new GraphRequest("/applications(uniqueName='app')").WithPrefer("create-if-missing"), J("""{ "displayName": "New" }"""), Ct);

        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.Empty(client.Directory.Objects("/directory/deletedItems"));
    }

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow()
        {
            return Now;
        }
    }

    [Fact]
    public async Task ListingDeletedItemsWithoutAType_IsNotSupported()
    {
        var client = new FakeGraphClient();

        var error = await Assert.ThrowsAsync<GraphException>(() => client.GetAsync(new GraphRequest("/directory/deletedItems"), Ct));

        Assert.Equal(HttpStatusCode.BadRequest, error.Status);
    }

    // ---- advanced queries ----

    [Theory]
    [InlineData("$count")]
    [InlineData("$search")]
    [InlineData("ne")]
    [InlineData("endswith")]
    [InlineData("not")]
    public async Task AdvancedQueries_NeedTheConsistencyLevelHeader(string kind)
    {
        var client = new FakeGraphClient();
        client.Directory.AddUser("alice@contoso.com", "Alice");
        var plain = new GraphRequest("/users");
        var request = kind switch
        {
            "$count" => plain.WithCount(),
            "$search" => plain.WithSearch("\"displayName:Ali\""),
            "ne" => plain.WithFilter("displayName ne 'x'").WithCount(),
            "endswith" => plain.WithFilter("endswith(userPrincipalName,'contoso.com')").WithCount(),
            _ => plain.WithFilter("not(displayName eq 'x')").WithCount(),
        };

        var refused = await Assert.ThrowsAsync<GraphException>(() => client.GetAsync(request, Ct));
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Equal("Request_UnsupportedQuery", refused.Code);

        var accepted = await client.GetAsync(request.WithConsistencyLevelEventual(), Ct);
        Assert.NotNull(accepted);
    }

    [Fact]
    public async Task AnAdvancedFilter_AlsoNeedsCountTrue()
    {
        var client = new FakeGraphClient();
        client.Directory.AddUser("alice@contoso.com", "Alice");
        client.Directory.AddUser("bob@contoso.com", "Bob");

        var refused = await Assert.ThrowsAsync<GraphException>(() => client.GetAsync(new GraphRequest("/users").WithFilter("displayName ne 'Alice'").WithConsistencyLevelEventual(), Ct));
        var page = (JsonObject)(await client.GetAsync(new GraphRequest("/users").WithFilter("displayName ne 'Alice'").WithCount().WithConsistencyLevelEventual(), Ct))!;

        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Equal("Bob", Assert.Single(page["value"]!.AsArray())!["displayName"]!.GetValue<string>());
    }

    [Fact]
    public async Task PlainEqualityFilters_NeedNoHeader_OnAnyCollection()
    {
        var client = new FakeGraphClient();
        client.Directory.AddUser("alice@contoso.com", "Alice");
        client.Directory.Add("/identity/conditionalAccess/policies", new JsonObject { ["displayName"] = "P" });

        var users = (JsonObject)(await client.GetAsync(new GraphRequest("/users").WithFilter("displayName eq 'Alice'"), Ct))!;
        var policies = (JsonObject)(await client.GetAsync(new GraphRequest("/identity/conditionalAccess/policies").WithFilter("displayName eq 'P'").WithCount(), Ct))!;

        Assert.Single(users["value"]!.AsArray());
        Assert.Single(policies["value"]!.AsArray());
    }

    [Theory]
    [InlineData("displayName gt 'a'")]
    [InlineData("displayName eq 'a' or displayName eq 'b'")]
    [InlineData("contains(displayName,'a')")]
    [InlineData("tags/any(t:t eq 'x')")]
    public async Task UnsupportedFilterOperators_AreBadRequests(string filter)
    {
        var client = new FakeGraphClient();

        var error = await Assert.ThrowsAsync<GraphException>(() => client.GetAsync(new GraphRequest("/groups").WithFilter(filter).WithConsistencyLevelEventual().WithCount(), Ct));

        Assert.Equal(HttpStatusCode.BadRequest, error.Status);
    }

    // ---- undocumented operations ----

    [Fact]
    public async Task UndocumentedOperations_AreMethodNotAllowed()
    {
        var client = new FakeGraphClient();
        var app = client.Directory.AddApplication("app", "App");
        var principal = client.Directory.AddServicePrincipal(Guid.NewGuid().ToString("D"), "Resource");
        var user = client.Directory.AddUser("a@contoso.com", "A");
        var assignment = (await client.PostAsync(
            new GraphRequest("/servicePrincipals/" + principal["id"] + "/appRoleAssignedTo"),
            J("{ \"principalId\": \"" + user["id"] + "\", \"resourceId\": \"" + principal["id"] + "\", \"appRoleId\": \"00000000-0000-0000-0000-000000000000\" }"),
            Ct)).Body!;

        var putEntity = await Assert.ThrowsAsync<GraphException>(() => client.PutAsync(new GraphRequest("/applications/" + app["id"]), J("{}"), Ct));
        var patchAssignment = await Assert.ThrowsAsync<GraphException>(() => client.PatchAsync(new GraphRequest("/servicePrincipals/" + principal["id"] + "/appRoleAssignedTo/" + assignment["id"]), J("{}"), Ct));
        var postEntity = await Assert.ThrowsAsync<GraphException>(() => client.PostAsync(new GraphRequest("/applications/" + app["id"]), J("{}"), Ct));

        Assert.All([putEntity, patchAssignment, postEntity], e => Assert.Equal(HttpStatusCode.MethodNotAllowed, e.Status));
    }

    [Fact]
    public async Task DocumentedOperations_StillWork()
    {
        var client = new FakeGraphClient();
        var app = client.Directory.AddApplication("app", "App");
        var principal = client.Directory.AddServicePrincipal(Guid.NewGuid().ToString("D"), "Resource");
        var user = client.Directory.AddUser("a@contoso.com", "A");
        var created = (await client.PostAsync(
            new GraphRequest("/servicePrincipals/" + principal["id"] + "/appRoleAssignedTo"),
            J("{ \"principalId\": \"" + user["id"] + "\", \"resourceId\": \"" + principal["id"] + "\", \"appRoleId\": \"00000000-0000-0000-0000-000000000000\" }"),
            Ct)).Body!;

        // GET /servicePrincipals/{id}/appRoleAssignedTo/{id} is documented (Learn: approleassignment-get).
        var read = await client.GetAsync(new GraphRequest("/servicePrincipals/" + principal["id"] + "/appRoleAssignedTo/" + created["id"]), Ct);
        await client.PatchAsync(new GraphRequest("/applications/" + app["id"]), J("""{ "displayName": "Renamed" }"""), Ct);
        await client.DeleteAsync(new GraphRequest("/servicePrincipals/" + principal["id"] + "/appRoleAssignedTo/" + created["id"]), Ct);

        Assert.NotNull(read);
        Assert.Equal("Renamed", client.Directory.Find("/applications", "uniqueName", "app")!["displayName"]!.GetValue<string>());
    }

    // ---- binds ----

    [Fact]
    public async Task OwnersAndMembersBind_InACreateBody_BecomeReferences()
    {
        var client = new FakeGraphClient();
        var owner = client.Directory.AddUser("owner@contoso.com", "Owner");

        var response = await client.PatchAsync(
            new GraphRequest("/groups(uniqueName='g')").WithPrefer("create-if-missing"),
            J("{ \"displayName\": \"G\", \"mailEnabled\": false, \"securityEnabled\": true, \"mailNickname\": \"g\", \"owners@odata.bind\": [\"https://graph.microsoft.com/v1.0/directoryObjects/" + owner["id"] + "\"] }"),
            Ct);

        var group = client.Directory.Find("/groups", "uniqueName", "g")!;
        Assert.Equal(HttpStatusCode.Created, response.Status);
        Assert.Equal([owner["id"]!.GetValue<string>()], client.Directory.References("/groups/" + group["id"] + "/owners").ToArray());
        Assert.False(group.ContainsKey("owners@odata.bind"));
    }
}
