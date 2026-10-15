using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// Review 3I, finding 4: the fake tenant refuses what Graph refuses (a create without the properties Learn lists as required, a bogus CA state, a
/// named location without its <c>@odata.type</c>, a <c>$top</c> over 999, a <c>$select</c> of a property the type does not have, an upsert below the
/// <c>uniqueName</c> key) and answers like Graph does (default property sets without <c>$select</c>).
/// </summary>
public sealed class Review3I_FakeDirectoryStrictnessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject J(string json)
    {
        return (JsonObject)JsonNode.Parse(json)!;
    }

    private static async Task<GraphException> Rejected(FakeGraphClient client, string path, string json)
    {
        return await Assert.ThrowsAsync<GraphException>(() => client.PostAsync(new GraphRequest(path), J(json), Ct));
    }

    // ---- required properties ----

    [Theory]
    [InlineData("/groups")]
    [InlineData("/applications")]
    [InlineData("/users")]
    [InlineData("/identity/conditionalAccess/policies")]
    [InlineData("/directory/administrativeUnits")]
    [InlineData("/roleManagement/directory/roleDefinitions")]
    [InlineData("/identity/conditionalAccess/namedLocations")]
    public async Task AnEmptyObjectIsNotACreate(string collection)
    {
        var client = new FakeGraphClient();

        var error = await Rejected(client, collection, "{}");

        Assert.Equal(HttpStatusCode.BadRequest, error.Status);
        Assert.Equal(0, client.Directory.Count(collection));
    }

    [Fact]
    public async Task AnApplicationUpsertWithoutADisplayNameIsRefused()
    {
        var client = new FakeGraphClient();

        var error = await Assert.ThrowsAsync<GraphException>(() => client.PatchAsync(new GraphRequest("/applications(uniqueName='app')").WithPrefer("create-if-missing"), J("""{ "signInAudience": "AzureADMyOrg" }"""), Ct));

        Assert.Equal(HttpStatusCode.BadRequest, error.Status);
        Assert.Contains("displayName", error.GraphMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "displayName": "G", "mailEnabled": false, "securityEnabled": true }""", "mailNickname")]
    [InlineData("""{ "displayName": "G", "mailNickname": "g", "securityEnabled": true }""", "mailEnabled")]
    [InlineData("""{ "displayName": "G", "mailNickname": "g", "mailEnabled": false }""", "securityEnabled")]
    [InlineData("""{ "mailNickname": "g", "mailEnabled": false, "securityEnabled": true }""", "displayName")]
    public async Task AGroupNeedsDisplayNameMailEnabledMailNicknameAndSecurityEnabled(string body, string missing)
    {
        var client = new FakeGraphClient();

        var post = await Rejected(client, "/groups", body);
        var upsert = await Assert.ThrowsAsync<GraphException>(() => client.PatchAsync(new GraphRequest("/groups(uniqueName='g')").WithPrefer("create-if-missing"), J(body), Ct));

        Assert.Contains(missing, post.GraphMessage, StringComparison.Ordinal);
        Assert.Contains(missing, upsert.GraphMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AValidGroupIsCreated()
    {
        var client = new FakeGraphClient();

        var response = await client.PostAsync(new GraphRequest("/groups"), J("""{ "displayName": "G", "mailNickname": "g", "mailEnabled": false, "securityEnabled": true }"""), Ct);

        Assert.Equal(HttpStatusCode.Created, response.Status);
    }

    [Theory]
    [InlineData("""{ "userPrincipalName": "a@contoso.com", "displayName": "A", "mailNickname": "a", "passwordProfile": { "password": "x-1-Secret" } }""", "accountEnabled")]
    [InlineData("""{ "userPrincipalName": "a@contoso.com", "accountEnabled": true, "mailNickname": "a", "passwordProfile": { "password": "x-1-Secret" } }""", "displayName")]
    [InlineData("""{ "userPrincipalName": "a@contoso.com", "accountEnabled": true, "displayName": "A", "passwordProfile": { "password": "x-1-Secret" } }""", "mailNickname")]
    [InlineData("""{ "userPrincipalName": "a@contoso.com", "accountEnabled": true, "displayName": "A", "mailNickname": "a" }""", "passwordProfile")]
    [InlineData("""{ "userPrincipalName": "a@contoso.com", "accountEnabled": true, "displayName": "A", "mailNickname": "a", "passwordProfile": { "forceChangePasswordNextSignIn": true } }""", "passwordProfile.password")]
    public async Task AUserNeedsTheDocumentedProperties(string body, string missing)
    {
        var client = new FakeGraphClient();

        var error = await Rejected(client, "/users", body);

        Assert.Contains(missing, error.GraphMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AValidUserIsCreatedAndItsPasswordProfileIsNeverStoredOrReturned()
    {
        var client = new FakeGraphClient();

        var response = await client.PostAsync(new GraphRequest("/users"), J("""{ "userPrincipalName": "a@contoso.com", "accountEnabled": true, "displayName": "A", "mailNickname": "a", "passwordProfile": { "password": "x-1-Secret" } }"""), Ct);

        Assert.Equal(HttpStatusCode.Created, response.Status);
        Assert.Null(client.Directory.Find("/users", "userPrincipalName", "a@contoso.com")!["passwordProfile"]);
    }

    [Theory]
    [InlineData("""{ "displayName": "P", "state": "on" }""")]
    [InlineData("""{ "displayName": "P" }""")]
    public async Task AConditionalAccessPolicyNeedsAValidState(string body)
    {
        var client = new FakeGraphClient();

        var error = await Rejected(client, "/identity/conditionalAccess/policies", body);

        Assert.Equal(HttpStatusCode.BadRequest, error.Status);
    }

    [Theory]
    [InlineData("enabled")]
    [InlineData("Disabled")]
    [InlineData("enabledForReportingButNotEnforced")]
    public async Task EveryDocumentedPolicyStateIsAccepted(string state)
    {
        var client = new FakeGraphClient();

        var response = await client.PostAsync(new GraphRequest("/identity/conditionalAccess/policies"), J("""{ "displayName": "P", "state": "STATE" }""".Replace("STATE", state, StringComparison.Ordinal)), Ct);

        Assert.Equal(HttpStatusCode.Created, response.Status);
    }

    [Fact]
    public async Task APolicyPatchWithABogusStateIsRefused()
    {
        var client = new FakeGraphClient();
        var policy = client.Directory.Add("/identity/conditionalAccess/policies", J("""{ "displayName": "P", "state": "disabled" }"""));

        await Assert.ThrowsAsync<GraphException>(() => client.PatchAsync(new GraphRequest("/identity/conditionalAccess/policies/" + policy["id"]), J("""{ "state": "sometimes" }"""), Ct));
        await client.PatchAsync(new GraphRequest("/identity/conditionalAccess/policies/" + policy["id"]), J("""{ "state": "enabled" }"""), Ct);

        Assert.Equal("enabled", client.Directory.FindById(policy["id"]!.GetValue<string>())!["state"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{ "displayName": "N", "isTrusted": true, "ipRanges": [ { "@odata.type": "#microsoft.graph.iPv4CidrRange", "cidrAddress": "10.0.0.0/8" } ] }""")]
    [InlineData("""{ "@odata.type": "#microsoft.graph.ipNamedLocation", "displayName": "N", "isTrusted": true }""")]
    [InlineData("""{ "@odata.type": "#microsoft.graph.countryNamedLocation", "displayName": "N" }""")]
    [InlineData("""{ "@odata.type": "#microsoft.graph.somethingElse", "displayName": "N" }""")]
    public async Task ANamedLocationNeedsItsODataTypeAndTheTypesRequiredProperties(string body)
    {
        var client = new FakeGraphClient();

        await Rejected(client, "/identity/conditionalAccess/namedLocations", body);
    }

    [Fact]
    public async Task ValidNamedLocationsAreCreated()
    {
        var client = new FakeGraphClient();

        var ip = await client.PostAsync(new GraphRequest("/identity/conditionalAccess/namedLocations"), J("""{ "@odata.type": "#microsoft.graph.ipNamedLocation", "displayName": "N", "isTrusted": true, "ipRanges": [ { "@odata.type": "#microsoft.graph.iPv4CidrRange", "cidrAddress": "10.0.0.0/8" } ] }"""), Ct);
        var country = await client.PostAsync(new GraphRequest("/identity/conditionalAccess/namedLocations"), J("""{ "@odata.type": "#microsoft.graph.countryNamedLocation", "displayName": "C", "countriesAndRegions": ["DE"] }"""), Ct);

        Assert.Equal(HttpStatusCode.Created, ip.Status);
        Assert.Equal(HttpStatusCode.Created, country.Status);
    }

    // ---- $select and the default property sets ----

    private static FakeGraphClient WithUser(out JsonObject user)
    {
        var client = new FakeGraphClient();
        user = client.Directory.AddUser("alice@contoso.com", "Alice", u =>
        {
            u["givenName"] = "Alice";
            u["accountEnabled"] = true;
            u["department"] = "Eng";
            u["usageLocation"] = "DE";
        });
        return client;
    }

    [Fact]
    public async Task AUserWithoutSelectComesBackWithTheDocumentedDefaultPropertiesOnly()
    {
        var client = WithUser(out var user);

        var body = (JsonObject)(await client.GetAsync(new GraphRequest("/users/" + user["id"]), Ct))!;

        Assert.Equal("Alice", body["displayName"]!.GetValue<string>());
        Assert.Equal("alice@contoso.com", body["userPrincipalName"]!.GetValue<string>());
        Assert.False(body.ContainsKey("accountEnabled"));
        Assert.False(body.ContainsKey("department"));
        Assert.False(body.ContainsKey("mailNickname"));
        Assert.True(body.ContainsKey("id"));
    }

    [Fact]
    public async Task ASelectReturnsIdAndTheSelectedPropertiesTheObjectHolds()
    {
        var client = WithUser(out var user);

        var body = (JsonObject)(await client.GetAsync(new GraphRequest("/users/" + user["id"]).WithSelect("accountEnabled", "jobTitle", "city"), Ct))!;

        Assert.Equal(["accountEnabled", "id"], body.Select(p => p.Key).OrderBy(static k => k, StringComparer.Ordinal).ToArray());
        Assert.True(body["accountEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ASelectOfStarReturnsTheWholeObject()
    {
        var client = WithUser(out var user);

        var body = (JsonObject)(await client.GetAsync(new GraphRequest("/users/" + user["id"]).WithSelect("*"), Ct))!;

        Assert.True(body.ContainsKey("department"));
        Assert.True(body.ContainsKey("usageLocation"));
    }
    [Fact]
    public async Task ASelectOfAnUnknownPropertyIsA400OnAnEntityAndOnAnEmptyCollection()
    {
        var client = new FakeGraphClient();
        var group = client.Directory.AddGroup("g", "G");

        var entity = await Assert.ThrowsAsync<GraphException>(() => client.GetAsync(new GraphRequest("/groups/" + group["id"]).WithSelect("displayName", "noSuchProperty"), Ct));
        var empty = await Assert.ThrowsAsync<GraphException>(async () =>
        {
            await foreach (var _ in client.GetAllPagesAsync(new GraphRequest("/applications").WithSelect("bogus"), Ct))
            {
            }
        });

        Assert.Equal(HttpStatusCode.BadRequest, entity.Status);
        Assert.Contains("noSuchProperty", entity.GraphMessage, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, empty.Status);
    }

    [Fact]
    public async Task APropertyThatNeedsSelectIsNotReturnedByDefault()
    {
        var client = new FakeGraphClient();
        var group = client.Directory.AddGroup("g", "G", g => g["assignedLicenses"] = new JsonArray());

        var plain = (JsonObject)(await client.GetAsync(new GraphRequest("/groups/" + group["id"]), Ct))!;
        var selected = (JsonObject)(await client.GetAsync(new GraphRequest("/groups/" + group["id"]).WithSelect("assignedLicenses"), Ct))!;

        Assert.False(plain.ContainsKey("assignedLicenses"));
        Assert.True(plain.ContainsKey("displayName"));
        Assert.True(selected.ContainsKey("assignedLicenses"));
    }

    [Fact]
    public async Task ACollectionPageIsProjectedToo()
    {
        var client = WithUser(out _);
        client.Directory.AddUser("bob@contoso.com", "Bob", u => u["department"] = "Ops");

        var all = new List<JsonNode>();
        await foreach (var item in client.GetAllPagesAsync(new GraphRequest("/users").WithSelect("department"), Ct))
        {
            all.Add(item);
        }

        Assert.All(all, item => Assert.Equal(["department", "id"], ((JsonObject)item).Select(p => p.Key).OrderBy(static k => k, StringComparer.Ordinal).ToArray()));
    }

    [Fact]
    public async Task ExtensionAttributesMayBeSelectedWithoutBeingKnownProperties()
    {
        var client = new FakeGraphClient();
        var user = client.Directory.AddUser("ext@contoso.com", "Ext", u => u["extension_0123456789abcdef0123456789abcdef_costCenter"] = "42");

        var plain = (JsonObject)(await client.GetAsync(new GraphRequest("/users/" + user["id"]), Ct))!;
        var body = (JsonObject)(await client.GetAsync(new GraphRequest("/users/" + user["id"]).WithSelect("extension_0123456789abcdef0123456789abcdef_costCenter"), Ct))!;

        Assert.False(plain.ContainsKey("extension_0123456789abcdef0123456789abcdef_costCenter"));
        Assert.Equal("42", body["extension_0123456789abcdef0123456789abcdef_costCenter"]!.GetValue<string>());
    }
    [Fact]
    public async Task AnUntypedCollectionIsReturnedWhole()
    {
        var client = new FakeGraphClient();
        var policy = client.Directory.Add("/identity/conditionalAccess/policies", J("""{ "displayName": "P", "state": "disabled", "anything": 1 }"""));

        var body = (JsonObject)(await client.GetAsync(new GraphRequest("/identity/conditionalAccess/policies/" + policy["id"]), Ct))!;

        Assert.True(body.ContainsKey("anything"));
    }

    [Fact]
    public async Task ADeletedGroupIsProjectedByItsType()
    {
        var client = new FakeGraphClient();
        var group = client.Directory.AddGroup("g", "G", g => g["assignedLicenses"] = new JsonArray());
        client.Directory.SoftDelete(group["id"]!.GetValue<string>());

        var plain = new List<JsonNode>();
        await foreach (var item in client.GetAllPagesAsync(new GraphRequest("/directory/deletedItems/microsoft.graph.group"), Ct))
        {
            plain.Add(item);
        }

        var deleted = Assert.IsType<JsonObject>(Assert.Single(plain));
        Assert.False(deleted.ContainsKey("assignedLicenses"));
        Assert.True(deleted.ContainsKey("displayName"));
        Assert.True(deleted.ContainsKey("@odata.type"));
    }

    // ---- $top ----

    [Theory]
    [InlineData("/users")]
    [InlineData("/groups")]
    [InlineData("/applications")]
    [InlineData("/servicePrincipals")]
    [InlineData("/directory/administrativeUnits")]
    [InlineData("/directory/deletedItems/microsoft.graph.group")]
    public async Task ATopOver999IsRefusedOnDirectoryCollections(string path)
    {
        var client = new FakeGraphClient();

        var error = await Assert.ThrowsAsync<GraphException>(() => client.GetAsync(new GraphRequest(path).WithTop(1000), Ct));
        var ok = await client.GetAsync(new GraphRequest(path).WithTop(999), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, error.Status);
        Assert.NotNull(ok);
    }

    // ---- the uniqueName key is only an upsert key ----

    [Fact]
    public async Task ANavigationBelowTheUniqueNameKeyIsNotAGraphRoute()
    {
        var client = new FakeGraphClient();
        var app = client.Directory.AddApplication("app", "App");

        var bad = await Assert.ThrowsAsync<GraphException>(() => client.GetAsync(new GraphRequest("/applications(uniqueName='app')/federatedIdentityCredentials"), Ct));
        var byId = await client.GetAsync(new GraphRequest("/applications/" + app["id"] + "/federatedIdentityCredentials"), Ct);
        var byAppId = await client.GetAsync(new GraphRequest("/applications(appId='" + app["appId"] + "')/federatedIdentityCredentials"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.NotNull(byId);
        Assert.NotNull(byAppId);
    }
}
