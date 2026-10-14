using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Az;
using EntraArm.TestSupport;
using Xunit;
using GraphApi = EntraArm.Az.GraphApi;

namespace EntraArm.Tests.Engine;

/// <summary>
/// The behaviour the provider work items rely on from <see cref="FakeGraphClient"/> / <see cref="FakeDirectory"/>:
/// routing, filters, upserts, pagination, references, actions, batches, error shapes and the failure script.
/// </summary>
public sealed class FakeGraphClientContractTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject J(string json)
    {
        return JsonNode.Parse(json)!.AsObject();
    }

    private static async Task<List<JsonObject>> AllAsync(FakeGraphClient client, GraphRequest request)
    {
        var items = new List<JsonObject>();
        await foreach (var item in client.GetAllPagesAsync(request, Ct))
        {
            items.Add(item.AsObject());
        }

        return items;
    }

    // ---- routing ----

    [Theory]
    [InlineData("it's")]
    [InlineData("a b")]
    [InlineData("ünï-cødé")]
    [InlineData("plain")]
    public async Task AlternateKey_RoutesThroughEscapedValues(string uniqueName)
    {
        var client = new FakeGraphClient();
        var app = client.Directory.AddApplication(uniqueName, "App");

        var found = await client.GetAsync(new GraphRequest(GraphPath.AlternateKey("/applications", "uniqueName", uniqueName)), Ct);
        var missing = await client.GetAsync(new GraphRequest(GraphPath.AlternateKey("/applications", "uniqueName", uniqueName + "-nope")), Ct);

        Assert.Equal(app["id"]!.GetValue<string>(), found!["id"]!.GetValue<string>());
        Assert.Null(missing);
        if (uniqueName.Contains('\'', StringComparison.Ordinal))
        {
            Assert.Contains("(uniqueName='it''s')", client.Requests[0].Path);
        }
    }

    [Fact]
    public async Task EntitiesRouteByIdOrUpn_AndDirectoryObjectsSpanCollections()
    {
        var client = new FakeGraphClient();
        var user = client.Directory.AddUser("alice@contoso.com", "Alice");
        var group = client.Directory.AddGroup("g", "G");
        string userId = user["id"]!.GetValue<string>();

        Assert.Equal("Alice", (await client.GetAsync(new GraphRequest("/users/" + userId), Ct))!["displayName"]!.GetValue<string>());
        Assert.Equal("Alice", (await client.GetAsync(new GraphRequest("/users/alice@contoso.com"), Ct))!["displayName"]!.GetValue<string>());
        Assert.Equal("Alice", (await client.GetAsync(new GraphRequest("/users/" + userId.ToUpperInvariant()), Ct))!["displayName"]!.GetValue<string>());
        var directoryObject = await client.GetAsync(new GraphRequest("/directoryObjects/" + group["id"]), Ct);
        Assert.Equal("#microsoft.graph.group", directoryObject!["@odata.type"]!.GetValue<string>());
        Assert.Null(await client.GetAsync(new GraphRequest("/groups/" + userId), Ct));
        Assert.Null(await client.GetAsync(new GraphRequest("/unknownCollection/x"), Ct));
        Assert.Equal(group["id"]!.GetValue<string>(), client.Directory.FindById(group["id"]!.GetValue<string>())!["id"]!.GetValue<string>());
    }

    // ---- filters ----

    [Fact]
    public async Task Filter_SupportsEqWithQuotedValues_AndConjunctions()
    {
        var client = new FakeGraphClient();
        client.Directory.AddGroup("a", "a b");
        client.Directory.AddGroup("b", "a b", g => g["mailEnabled"] = true);
        client.Directory.AddGroup("c", "it's");

        var both = await AllAsync(client, new GraphRequest("/groups").WithFilter("displayName eq 'a b'"));
        var one = await AllAsync(client, new GraphRequest("/groups").WithFilter("displayName eq 'a b' and mailEnabled eq false"));
        var escaped = await AllAsync(client, new GraphRequest("/groups").WithFilter("displayName eq 'it''s'"));
        var none = await AllAsync(client, new GraphRequest("/groups").WithFilter("displayName eq 'zzz'"));

        Assert.Equal(2, both.Count);
        Assert.Equal("a", Assert.Single(one)["uniqueName"]!.GetValue<string>());
        Assert.Equal("c", Assert.Single(escaped)["uniqueName"]!.GetValue<string>());
        Assert.Empty(none);
        Assert.Equal("displayName eq 'a b' and mailEnabled eq false", client.Requests[1].UrlParameters!["$filter"]);
    }

    [Fact]
    public async Task Filter_UnsupportedOperators_AreBadRequests()
    {
        var client = new FakeGraphClient();

        var error = await Assert.ThrowsAsync<GraphException>(() => AllAsync(client, new GraphRequest("/groups").WithFilter("startsWith(displayName, 'a')")));

        Assert.Equal(HttpStatusCode.BadRequest, error.Status);
    }

    // ---- upserts ----

    [Fact]
    public async Task PreferCreateIfMissing_CreatesOnFirstPatch_AndUpdatesAfterwards()
    {
        var client = new FakeGraphClient();
        var request = new GraphRequest("/groups(uniqueName='g')").WithPrefer("create-if-missing");

        var created = await client.PatchAsync(request, J("{\"displayName\":\"G\",\"mailEnabled\":false,\"securityEnabled\":true}"), Ct);
        var updated = await client.PatchAsync(request, J("{\"displayName\":\"G2\"}"), Ct);
        var live = client.Directory.Find("/groups", "uniqueName", "g")!;

        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.True(created.IsCreated);
        Assert.Equal("g", created.Body!["uniqueName"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(created.Body["id"]!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.NoContent, updated.Status);
        Assert.Null(updated.Body);
        Assert.Equal("G2", live["displayName"]!.GetValue<string>());
        Assert.Equal(1, client.Directory.Count("/groups"));
        Assert.True(client.Requests[0].HasHeader("Prefer", "create-if-missing"));
    }

    [Fact]
    public async Task PatchWithoutPrefer_OnAMissingObject_Is404()
    {
        var client = new FakeGraphClient();

        var error = await Assert.ThrowsAsync<GraphException>(() => client.PatchAsync(new GraphRequest("/groups(uniqueName='g')"), J("{\"displayName\":\"G\"}"), Ct));

        Assert.True(error.IsNotFound);
        Assert.Equal("Request_ResourceNotFound", error.Code);
    }

    [Fact]
    public async Task IdUpsert_ForAuthenticationContextClassReferences()
    {
        var client = new FakeGraphClient();

        var created = await client.PatchAsync(new GraphRequest("/identity/conditionalAccess/authenticationContextClassReferences/c10").WithPrefer("create-if-missing"), J("{\"displayName\":\"Step-up\"}"), Ct);

        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.Equal("c10", created.Body!["id"]!.GetValue<string>());
        Assert.Equal("c10", Assert.Single(client.Directory.Objects("/identity/conditionalAccess/authenticationContextClassReferences"))["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Patch_RemovesNullMembers_AndRejectsUniqueNameChanges()
    {
        var client = new FakeGraphClient();
        var group = client.Directory.AddGroup("g", "G", g => g["description"] = "d");
        string path = "/groups/" + group["id"];

        await client.PatchAsync(new GraphRequest(path), J("{\"description\":null,\"DisplayName\":\"New\"}"), Ct);
        var live = client.Directory.Find("/groups", "uniqueName", "g")!;
        var error = await Assert.ThrowsAsync<GraphException>(() => client.PatchAsync(new GraphRequest(path), J("{\"uniqueName\":\"other\"}"), Ct));

        Assert.False(live.ContainsKey("description"));
        Assert.Equal("New", live["displayName"]!.GetValue<string>());
        Assert.False(live.ContainsKey("DisplayName"), "case-insensitive merge keeps the stored key");
        Assert.Contains("immutable", error.GraphMessage);
    }

    // ---- pagination ----

    [Fact]
    public async Task Pagination_FollowsNextLinks_WithPageSizeOne()
    {
        var client = new FakeGraphClient { PageSize = 1 };
        client.Directory.AddUser("a@contoso.com", "A");
        client.Directory.AddUser("b@contoso.com", "B");
        client.Directory.AddUser("c@contoso.com", "C");

        var all = await AllAsync(client, new GraphRequest("/users").WithSelect("id,userPrincipalName"));
        var firstPage = (await client.GetAsync(new GraphRequest("/users"), Ct))!.AsObject();

        Assert.Equal(["a@contoso.com", "b@contoso.com", "c@contoso.com"], all.Select(u => u["userPrincipalName"]!.GetValue<string>()).ToList());
        var requests = client.Requests.Take(3).ToList();
        Assert.All(requests, r => Assert.Equal("/users", r.Path));
        Assert.Null(requests[0].UrlParameters?.GetValueOrDefault("$skiptoken"));
        Assert.Equal("1", requests[1].UrlParameters!["$skiptoken"]);
        Assert.Equal("2", requests[2].UrlParameters!["$skiptoken"]);
        Assert.Equal("id,userPrincipalName", requests[2].UrlParameters!["$select"]);
        Assert.Single(firstPage["value"]!.AsArray());
        Assert.StartsWith("https://graph.microsoft.com/v1.0/users?$skiptoken=1", firstPage["@odata.nextLink"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pagination_HonoursTop()
    {
        var client = new FakeGraphClient();
        client.Directory.AddUser("a@contoso.com", "A");
        client.Directory.AddUser("b@contoso.com", "B");

        var page = (await client.GetAsync(new GraphRequest("/users").WithTop(1), Ct))!.AsObject();

        Assert.Single(page["value"]!.AsArray());
        Assert.NotNull(page["@odata.nextLink"]);
    }

    // ---- references ----

    [Fact]
    public async Task RefNavigations_AddListAndRemoveMembers()
    {
        var client = new FakeGraphClient();
        var group = client.Directory.AddGroup("g", "G");
        var user = client.Directory.AddUser("alice@contoso.com", "Alice");
        string g = group["id"]!.GetValue<string>();
        string u = user["id"]!.GetValue<string>();
        var add = new GraphRequest("/groups/" + g + "/members/$ref");
        var body = J("{\"@odata.id\":\"https://graph.microsoft.com/v1.0/directoryObjects/" + u + "\"}");

        var added = await client.PostAsync(add, body, Ct);
        var duplicate = await Assert.ThrowsAsync<GraphException>(() => client.PostAsync(add, body, Ct));
        var members = await AllAsync(client, new GraphRequest("/groups/" + g + "/members"));
        await client.DeleteAsync(new GraphRequest("/groups/" + g + "/members/" + u + "/$ref"), Ct);
        var gone = await Assert.ThrowsAsync<GraphException>(() => client.DeleteAsync(new GraphRequest("/groups/" + g + "/members/" + u + "/$ref"), Ct));

        Assert.Equal(HttpStatusCode.NoContent, added.Status);
        Assert.True(duplicate.IsAlreadyExists);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.Status);
        var member = Assert.Single(members);
        Assert.Equal(u, member["id"]!.GetValue<string>());
        Assert.Equal("#microsoft.graph.user", member["@odata.type"]!.GetValue<string>());
        Assert.True(gone.IsNotFound);
        Assert.Empty(client.Directory.References("/groups/" + g + "/members"));
        // Every call is logged, including the two that failed.
        Assert.Equal(4, client.Writes.Count);
    }

    [Fact]
    public async Task RefNavigations_PostWithoutOdataId_IsBadRequest_AndGetWithRefIsRejected()
    {
        var client = new FakeGraphClient();
        var group = client.Directory.AddGroup("g", "G");

        var bad = await Assert.ThrowsAsync<GraphException>(() => client.PostAsync(new GraphRequest("/groups/" + group["id"] + "/members/$ref"), J("{}"), Ct));
        var notRef = await Assert.ThrowsAsync<GraphException>(() => client.PostAsync(new GraphRequest("/groups/" + group["id"] + "/members"), J("{\"@odata.id\":\"x\"}"), Ct));

        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.Equal(HttpStatusCode.BadRequest, notRef.Status);
    }

    [Fact]
    public async Task Manager_IsPutDeleteAndRead()
    {
        var client = new FakeGraphClient();
        var user = client.Directory.AddUser("alice@contoso.com", "Alice");
        var boss = client.Directory.AddUser("boss@contoso.com", "Boss");
        string u = user["id"]!.GetValue<string>();
        string b = boss["id"]!.GetValue<string>();

        Assert.Null(await client.GetAsync(new GraphRequest("/users/" + u + "/manager").WithSelect("id"), Ct));
        var put = await client.PutAsync(new GraphRequest("/users/" + u + "/manager/$ref"), J("{\"@odata.id\":\"https://graph.microsoft.com/v1.0/users/" + b + "\"}"), Ct);
        var manager = await client.GetAsync(new GraphRequest("/users/" + u + "/manager").WithSelect("id"), Ct);
        var references = client.Directory.References("/users/" + u + "/manager");
        await client.DeleteAsync(new GraphRequest("/users/" + u + "/manager/$ref"), Ct);
        var removedTwice = await Assert.ThrowsAsync<GraphException>(() => client.DeleteAsync(new GraphRequest("/users/" + u + "/manager/$ref"), Ct));

        Assert.Equal(HttpStatusCode.NoContent, put.Status);
        Assert.Equal(b, manager!["id"]!.GetValue<string>());
        Assert.Equal("#microsoft.graph.user", manager["@odata.type"]!.GetValue<string>());
        Assert.Equal([b], references);
        Assert.True(removedTwice.IsNotFound);
        Assert.Null(await client.GetAsync(new GraphRequest("/users/" + u + "/manager"), Ct));
    }

    [Fact]
    public async Task DeletingAnObject_RemovesItsReferencesAndChildCollections()
    {
        var client = new FakeGraphClient();
        var group = client.Directory.AddGroup("g", "G");
        var user = client.Directory.AddUser("alice@contoso.com", "Alice");
        var app = client.Directory.AddApplication("app", "App");
        string g = group["id"]!.GetValue<string>();
        string u = user["id"]!.GetValue<string>();
        await client.PostAsync(new GraphRequest("/groups/" + g + "/members/$ref"), J("{\"@odata.id\":\"https://graph.microsoft.com/v1.0/directoryObjects/" + u + "\"}"), Ct);
        await client.PostAsync(new GraphRequest("/applications/" + app["id"] + "/federatedIdentityCredentials"), J("{\"name\":\"gh\",\"issuer\":\"i\",\"subject\":\"s\"}"), Ct);

        await client.DeleteAsync(new GraphRequest("/users/" + u), Ct);
        await client.DeleteAsync(new GraphRequest("/applications/" + app["id"]), Ct);

        Assert.Empty(client.Directory.References("/groups/" + g + "/members"));
        Assert.Equal(0, client.Directory.Count("/applications/" + app["id"] + "/federatedIdentityCredentials"));
        Assert.Null(client.Directory.FindById(u));
        Assert.True((await Assert.ThrowsAsync<GraphException>(() => client.DeleteAsync(new GraphRequest("/users/" + u), Ct))).IsNotFound);
    }

    // ---- actions ----

    [Fact]
    public async Task AddPassword_ReturnsTheSecretOnce_AndRemovePasswordDropsIt()
    {
        var client = new FakeGraphClient();
        var app = client.Directory.AddApplication("app", "App");
        string path = "/applications/" + app["id"];

        var added = await client.PostAsync(new GraphRequest(path + "/addPassword"), J("{\"passwordCredential\":{\"displayName\":\"ci\",\"endDateTime\":\"2027-10-01T00:00:00Z\",\"rotationTag\":\"q4\"}}"), Ct);
        var stored = Assert.Single((await client.GetAsync(new GraphRequest(path).WithSelect("id", "passwordCredentials"), Ct))!["passwordCredentials"]!.AsArray());
        await client.PostAsync(new GraphRequest(path + "/removePassword"), J("{\"keyId\":\"" + added.Body!["keyId"] + "\"}"), Ct);
        var after = (await client.GetAsync(new GraphRequest(path), Ct))!["passwordCredentials"]!.AsArray();

        Assert.Equal(HttpStatusCode.OK, added.Status);
        Assert.StartsWith("fake-secret-", added.Body["secretText"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("ci", added.Body["displayName"]!.GetValue<string>());
        Assert.Equal("q4", stored!["rotationTag"]!.GetValue<string>());
        Assert.False(stored.AsObject().ContainsKey("secretText"), "Graph never returns the secret on read");
        Assert.Equal(added.Body["keyId"]!.GetValue<string>(), stored["keyId"]!.GetValue<string>());
        Assert.Empty(after);
    }

    [Fact]
    public async Task AssignLicense_AddsAndRemoves()
    {
        var client = new FakeGraphClient();
        var user = client.Directory.AddUser("alice@contoso.com", "Alice");
        string path = "/users/" + user["id"] + "/assignLicense";

        var added = await client.PostAsync(new GraphRequest(path), J("{\"addLicenses\":[{\"skuId\":\"11111111-1111-1111-1111-111111111111\",\"disabledPlans\":[]}],\"removeLicenses\":[]}"), Ct);
        await client.PostAsync(new GraphRequest(path), J("{\"addLicenses\":[],\"removeLicenses\":[\"11111111-1111-1111-1111-111111111111\"]}"), Ct);

        Assert.Single(added.Body!["assignedLicenses"]!.AsArray());
        Assert.Empty(client.Directory.Find("/users", "userPrincipalName", "alice@contoso.com")!["assignedLicenses"]!.AsArray());
    }

    // ---- batch ----

    [Fact]
    public async Task Batch_ReturnsOneResponsePerRequest_InOrder_WithErrorsInline()
    {
        var client = new FakeGraphClient();
        var group = client.Directory.AddGroup("g", "G");
        var user = client.Directory.AddUser("alice@contoso.com", "Alice");
        string g = group["id"]!.GetValue<string>();
        var requests = new List<BatchRequest>
        {
            new("POST", "/groups/" + g + "/members/$ref", J("{\"@odata.id\":\"https://graph.microsoft.com/v1.0/directoryObjects/" + user["id"] + "\"}")),
            new("GET", "/groups/missing"),
            new("PATCH", "/groups/" + g, J("{\"description\":\"d\"}"), Id: "custom"),
            new("GET", "/groups/" + g),
        };

        var responses = await client.BatchAsync(requests, GraphApi.V1, RetryHint.Default, Ct);

        Assert.Equal(4, responses.Count);
        Assert.Equal(["1", "2", "custom", "4"], responses.Select(r => r.Id).ToList());
        Assert.True(responses[0].Succeeded);
        Assert.Equal(HttpStatusCode.NoContent, responses[0].Status);
        Assert.False(responses[1].Succeeded);
        Assert.True(responses[1].Error!.IsNotFound);
        Assert.Equal(HttpStatusCode.NotFound, responses[1].Status);
        Assert.True(responses[2].Succeeded);
        Assert.Equal("d", responses[3].Body!["description"]!.GetValue<string>());
        Assert.Single(client.Requests, r => r.Path == "/$batch");
        Assert.Equal([user["id"]!.GetValue<string>()], client.Directory.References("/groups/" + g + "/members"));
        Assert.Contains(client.Writes, w => w.Path == "/$batch");
    }

    // ---- error shapes ----

    [Fact]
    public async Task NotFound_IsNullOnGet_UnlessReplicationLagIsExpected()
    {
        var client = new FakeGraphClient();

        Assert.Null(await client.GetAsync(new GraphRequest("/applications/missing"), Ct));
        var error = await Assert.ThrowsAsync<GraphException>(() => client.GetAsync(new GraphRequest("/applications/missing", hint: RetryHint.ExpectReplicationLag), Ct));

        Assert.True(error.IsNotFound);
        Assert.True(error.IsReplication);
        Assert.Equal(RetryHint.ExpectReplicationLag, error.Hint);
        Assert.Equal("Request_ResourceNotFound", error.Code);
        Assert.Contains("does not exist or one of its queried reference-property objects are not present", error.GraphMessage);
    }

    [Theory]
    [InlineData("/applications", "{\"uniqueName\":\"dup\",\"displayName\":\"A\"}")]
    [InlineData("/groups", "{\"uniqueName\":\"dup\",\"displayName\":\"G\"}")]
    [InlineData("/users", "{\"userPrincipalName\":\"dup@contoso.com\",\"displayName\":\"U\"}")]
    public async Task DuplicateKeys_AreAlreadyExistsErrors(string collection, string body)
    {
        var client = new FakeGraphClient();
        var first = await client.PostAsync(new GraphRequest(collection), J(body), Ct);

        var error = await Assert.ThrowsAsync<GraphException>(() => client.PostAsync(new GraphRequest(collection), J(body), Ct));

        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.True(error.IsAlreadyExists);
        Assert.False(error.IsReplication);
        Assert.Equal(HttpStatusCode.BadRequest, error.Status);
        Assert.Contains("already exists", error.GraphMessage);
        Assert.Equal(1, client.Directory.Count(collection));
    }

    [Fact]
    public async Task ServicePrincipalCreate_WithUnknownAppId_IsAReplicationFlavouredBadRequest()
    {
        var client = new FakeGraphClient();
        var body = J("{\"appId\":\"11111111-1111-1111-1111-111111111111\"}");

        var terminal = await Assert.ThrowsAsync<GraphException>(() => client.PostAsync(new GraphRequest("/servicePrincipals"), body, Ct));
        var retryable = await Assert.ThrowsAsync<GraphException>(() => client.PostAsync(new GraphRequest("/servicePrincipals", hint: RetryHint.ExpectReplicationLag), body, Ct));

        Assert.Equal(HttpStatusCode.BadRequest, terminal.Status);
        Assert.Contains("does not reference a valid application object", terminal.GraphMessage);
        Assert.False(terminal.IsReplication);
        Assert.False(terminal.IsAlreadyExists);
        Assert.True(retryable.IsReplication);

        client.Directory.AddApplication("app", "App", "11111111-1111-1111-1111-111111111111", a => a["appRoles"] = new JsonArray(J("{\"id\":\"r\"}")));
        var created = await client.PostAsync(new GraphRequest("/servicePrincipals"), body, Ct);

        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.Equal("App", created.Body!["displayName"]!.GetValue<string>());
        Assert.Single(created.Body["appRoles"]!.AsArray());
        Assert.Equal("Application", created.Body["servicePrincipalType"]!.GetValue<string>());
    }

    [Fact]
    public async Task UserCreate_RequiresAUpn_AndNeverStoresThePasswordProfile()
    {
        var client = new FakeGraphClient();

        var error = await Assert.ThrowsAsync<GraphException>(() => client.PostAsync(new GraphRequest("/users"), J("{\"displayName\":\"x\"}"), Ct));
        var created = await client.PostAsync(new GraphRequest("/users"), J("{\"userPrincipalName\":\"a@contoso.com\",\"displayName\":\"A\",\"passwordProfile\":{\"password\":\"p\"}}"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, error.Status);
        Assert.False(created.Body!.AsObject().ContainsKey("passwordProfile"));
        Assert.False(client.Directory.Find("/users", "userPrincipalName", "a@contoso.com")!.ContainsKey("passwordProfile"));
    }

    // ---- failure script ----

    [Fact]
    public async Task FailureScript_FiresTheRequestedNumberOfTimes_ThenExpires()
    {
        var client = new FakeGraphClient();
        client.Directory.AddGroup("g", "G");
        client.Failures.Throttle("/groups", times: 2, retryAfter: TimeSpan.FromSeconds(3));
        client.Failures.Forbid("/users");

        var first = await Assert.ThrowsAsync<GraphException>(() => client.GetAsync(new GraphRequest("/groups(uniqueName='g')"), Ct));
        var second = await Assert.ThrowsAsync<GraphException>(() => client.GetAsync(new GraphRequest("/groups(uniqueName='g')"), Ct));
        var third = await client.GetAsync(new GraphRequest("/groups(uniqueName='g')"), Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, first.Status);
        Assert.True(first.IsTransient);
        Assert.Equal(TimeSpan.FromSeconds(3), first.RetryAfter);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.Status);
        Assert.NotNull(third);
        Assert.Equal(1, client.Failures.PendingRules);
        Assert.True((await Assert.ThrowsAsync<GraphException>(() => client.GetAsync(new GraphRequest("/users/x"), Ct))).IsForbidden);
        Assert.True((await Assert.ThrowsAsync<GraphException>(() => client.GetAsync(new GraphRequest("/users/y"), Ct))).IsForbidden);
        Assert.Equal(1, client.Failures.PendingRules);
        Assert.Equal(5, client.Requests.Count);
    }

    [Fact]
    public async Task FailureScript_MatchesMethodAndPathFragment_AndAzFailuresAreAzCliExceptions()
    {
        var client = new FakeGraphClient();
        client.Directory.AddGroup("g", "G");
        client.Failures.ServerError("/groups", times: 1, method: "PATCH");
        client.Failures.AzFailure("/users", times: 1);

        Assert.NotNull(await client.GetAsync(new GraphRequest("/groups(uniqueName='g')"), Ct));
        var patch = await Assert.ThrowsAsync<GraphException>(() => client.PatchAsync(new GraphRequest("/groups(uniqueName='g')"), J("{\"description\":\"x\"}"), Ct));
        var az = await Assert.ThrowsAsync<AzCliException>(() => client.GetAsync(new GraphRequest("/users/x"), Ct));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, patch.Status);
        Assert.Equal(AzCliFailure.Transient, az.Failure);
        Assert.True(az.IsRetryable);
    }

    // ---- request log ----

    [Fact]
    public async Task RequestLog_CapturesMethodPathParametersHeadersBodyAndHint()
    {
        var client = new FakeGraphClient();
        var request = new GraphRequest("/groups", GraphApi.Beta, hint: RetryHint.ExpectReplicationLag).WithHeader("Prefer", "return=representation").WithConsistencyLevelEventual();

        await client.PostAsync(request, J("{\"uniqueName\":\"g\",\"displayName\":\"G\"}"), Ct);
        await client.GetAsync(new GraphRequest("/groups").WithSelect("id").WithCount(), Ct);

        var post = client.Requests[0];
        Assert.Equal("POST", post.Method);
        Assert.Equal("/groups", post.Path);
        Assert.Equal(GraphApi.Beta, post.Api);
        Assert.Equal(RetryHint.ExpectReplicationLag, post.Hint);
        Assert.True(post.IsWrite);
        Assert.True(post.HasHeader("prefer", "return=representation"));
        Assert.True(post.HasHeader("ConsistencyLevel", "eventual"));
        Assert.Equal("G", post.Body!["displayName"]!.GetValue<string>());
        Assert.Equal("POST /groups", post.ToString());

        var get = client.Requests[1];
        Assert.False(get.IsWrite);
        Assert.Equal("GET /groups?$select=id&$count=true", get.ToString());
        Assert.Single(client.Writes);

        client.ClearRequests();
        Assert.Empty(client.Requests);
    }

    [Fact]
    public void Snapshots_AreCopies()
    {
        var directory = new FakeDirectory();
        directory.AddGroup("g", "G");

        var snapshot = directory.Objects("/groups")[0];
        snapshot["displayName"] = "tampered";

        Assert.Equal("G", directory.Find("/groups", "uniqueName", "g")!["displayName"]!.GetValue<string>());
        Assert.Equal("11111111-1111-1111-1111-111111111111", directory.TenantId);
        Assert.Equal(directory.TenantId, directory.Objects("/organization")[0]["id"]!.GetValue<string>());
    }
}
