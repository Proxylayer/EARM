using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// <c>Microsoft.Entra/oauth2PermissionGrants</c>: the prepare / plan / read / create / update / delete branches the
/// happy-path scenarios in <see cref="AssignmentsAndGrantsTests"/> do not reach. Invalid declarations are fed straight
/// to the provider (the offline validators would stop most of them first), so each message is asserted as written.
/// </summary>
public sealed class PermissionGrantScenarioTests
{
    private const string GrantType = OAuth2PermissionGrantsProvider.ResourceType;
    private const string GrantName = "aaaaaaaa-0000-0000-0000-0000000000a1";
    private const string ScopeA = "7d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0003";
    private const string ScopeB = "8d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0004";
    private const string ScopeC = "9d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0005";

    private sealed record Tenant(ProviderHost Host, string ResourceId, string ClientId, string UserId, OAuth2PermissionGrantsProvider Provider);

    private static Tenant NewTenant()
    {
        var host = new ProviderHost();
        string resourceId = host.Directory.AddServicePrincipal(Guid.NewGuid().ToString("D"), "Orders API", sp => sp["oauth2PermissionScopes"] = new JsonArray(
            Scope(ScopeA, "orders.read", true),
            Scope(ScopeB, "orders.write", true),
            Scope(ScopeC, "orders.legacy", false),
            Scope(Guid.NewGuid().ToString("D"), "orders.old", false)))["id"]!.GetValue<string>();
        string clientId = host.Directory.AddServicePrincipal(Guid.NewGuid().ToString("D"), "Client")["id"]!.GetValue<string>();
        string userId = host.Directory.AddUser("carol@contoso.com", "Carol")["id"]!.GetValue<string>();
        return new Tenant(host, resourceId, clientId, userId, host.Provider<OAuth2PermissionGrantsProvider>(GrantType));
    }

    private static JsonObject Scope(string id, string value, bool enabled)
    {
        return new JsonObject { ["id"] = id, ["value"] = value, ["isEnabled"] = enabled };
    }

    private static string ValidGrant(Tenant t)
    {
        return Templates.Resource(GrantType, GrantName, "{ \"clientId\": \"" + t.ClientId + "\", \"resourceId\": \"" + t.ResourceId + "\", \"consentType\": \"AllPrincipals\", \"scope\": \"orders.read\" }");
    }

    private static Task<ResourceInstance> Instance(Tenant t, string propertiesJson, string? name = null)
    {
        return InstanceFactory.WithPropertiesAsync(t.Host, ValidGrant(t), propertiesJson, name);
    }

    private static string Props(Tenant t, string extra)
    {
        return "{ \"clientId\": \"" + t.ClientId + "\", \"resourceId\": \"" + t.ResourceId + "\"" + (extra.Length == 0 ? string.Empty : ", " + extra) + " }";
    }

    private static ResourceKey Key(Tenant t, string consentType = "AllPrincipals", string? principalId = null)
    {
        var values = new Dictionary<string, string> { ["clientId"] = t.ClientId, ["resourceId"] = t.ResourceId, ["consentType"] = consentType };
        if (principalId is not null)
        {
            values["principalId"] = principalId;
        }

        return new ResourceKey { Type = GrantType, Values = values };
    }

    private static DeleteTarget Entry(Tenant t, string graphId)
    {
        return new DeleteTarget
        {
            Id = ResourceIdentifier.Create(GrantType, GrantName),
            Type = GrantType,
            ApiVersion = ProviderHost.ApiVersion,
            GraphId = graphId,
            Key = new Dictionary<string, string> { ["clientId"] = t.ClientId, ["resourceId"] = t.ResourceId, ["consentType"] = "AllPrincipals" },
        };
    }

    private static string GrantsPath => "/oauth2PermissionGrants";

    // ---- prepare: invalid ----

    [Theory]
    [InlineData("\"consentType\": \"AllPrincipals\", \"scope\": \"orders.read\", \"extra\": 1", "is not a property of a permission grant")]
    [InlineData("\"consentType\": \"Everyone\", \"scope\": \"orders.read\"", "consentType must be")]
    [InlineData("\"consentType\": \"AllPrincipals\", \"principalId\": \"PRINCIPAL\", \"scope\": \"orders.read\"", "principalId must be omitted")]
    [InlineData("\"consentType\": \"Principal\", \"scope\": \"orders.read\"", "is required when consentType is Principal")]
    [InlineData("\"consentType\": \"Principal\", \"principalId\": \"not-a-guid\", \"scope\": \"orders.read\"", "is not an object id (GUID)")]
    [InlineData("\"consentType\": \"AllPrincipals\", \"scopes\": \"orders.read\"", "scopes must be an array")]
    [InlineData("\"consentType\": \"AllPrincipals\", \"scopes\": [\"orders.read\", 5]", "scopes entries must be permission values")]
    [InlineData("\"consentType\": \"AllPrincipals\", \"scopes\": [\"orders.read\", \"  \"]", "scopes entries must be permission values")]
    [InlineData("\"consentType\": \"AllPrincipals\", \"scope\": \"orders.read\", \"scopes\": [\"orders.write\"]", "list different permission values")]
    [InlineData("\"consentType\": \"AllPrincipals\", \"scope\": \"   \"", "must list at least one permission value")]
    [InlineData("\"consentType\": \"AllPrincipals\", \"scope\": \"orders.read nope\"", "is not delegated permission")]
    [InlineData("\"consentType\": \"AllPrincipals\", \"scope\": \"nope also-nope\"", "are not delegated permissions")]
    [InlineData("\"consentType\": \"AllPrincipals\", \"scope\": \"orders.read orders.legacy\"", "is disabled on service principal")]
    [InlineData("\"consentType\": \"AllPrincipals\", \"scope\": \"orders.legacy orders.old\"", "are disabled on service principal")]
    public async Task Prepare_RejectsInvalidDeclarations_WithAnInvalidGrantProviderException(string extra, string expected)
    {
        var t = NewTenant();
        var instance = await Instance(t, Props(t, extra.Replace("PRINCIPAL", t.UserId, StringComparison.Ordinal)));

        var ex = await Assert.ThrowsAsync<ProviderException>(() => t.Provider.PrepareAsync(instance, t.Host.Fake, ProviderHost.Ct));

        Assert.Equal(OAuth2PermissionGrantsProvider.InvalidGrant, ex.Code);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prepare_RejectsAResourceThatIsNotAGuid_OrDoesNotExist()
    {
        var t = NewTenant();
        var notGuid = await InstanceFactory.WithPropertiesAsync(t.Host, ValidGrant(t), "{ \"clientId\": \"" + t.ClientId + "\", \"resourceId\": \"nope\", \"consentType\": \"AllPrincipals\", \"scope\": \"orders.read\" }");
        var missing = await InstanceFactory.WithPropertiesAsync(t.Host, ValidGrant(t), "{ \"clientId\": \"" + t.ClientId + "\", \"resourceId\": \"" + Guid.NewGuid() + "\", \"consentType\": \"AllPrincipals\", \"scope\": \"orders.read\" }");

        var first = await Assert.ThrowsAsync<ProviderException>(() => t.Provider.PrepareAsync(notGuid, t.Host.Fake, ProviderHost.Ct));
        var second = await Assert.ThrowsAsync<ProviderException>(() => t.Provider.PrepareAsync(missing, t.Host.Fake, ProviderHost.Ct));

        Assert.Contains("is not an object id (GUID)", first.Message, StringComparison.Ordinal);
        Assert.Contains("does not exist", second.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prepare_NullAndUnevaluatedInstances_AreGuarded()
    {
        var t = NewTenant();
        var instance = await Instance(t, "{ }");
        instance.Properties = null;

        await Assert.ThrowsAsync<ArgumentNullException>(() => t.Provider.PrepareAsync(null!, t.Host.Fake, ProviderHost.Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => t.Provider.PrepareAsync(instance, null!, ProviderHost.Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => t.Provider.PrepareAsync(instance, t.Host.Fake, ProviderHost.Ct));
    }

    // ---- prepare: valid sugar ----

    [Fact]
    public async Task Prepare_MergesScopesIntoAnOrderedDeduplicatedScope_AndNormalisesTheConsentType()
    {
        var t = NewTenant();
        var instance = await Instance(t, Props(t, "\"consentType\": \"allprincipals\", \"scopes\": [\"orders.write orders.read\", \"orders.read\"]"));

        await t.Provider.PrepareAsync(instance, t.Host.Fake, ProviderHost.Ct);

        var properties = instance.Properties!;
        Assert.False(properties.ContainsKey("scopes"));
        Assert.Equal("AllPrincipals", properties["consentType"]!.GetValue<string>());
        Assert.Null(properties["principalId"]);
        Assert.True(properties.ContainsKey("principalId"));
        Assert.Equal(new[] { "orders.read", "orders.write" }, properties["scope"]!.GetValue<string>().Split(' ').Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Prepare_AnEquivalentScopeAndScopes_IsAccepted_AndAnUnknownScopesEntryStaysAPlaceholder()
    {
        var t = NewTenant();
        var same = await Instance(t, Props(t, "\"consentType\": \"AllPrincipals\", \"scope\": \"orders.write orders.read\", \"scopes\": [\"orders.read\", \"orders.write\"]"));
        await t.Provider.PrepareAsync(same, t.Host.Fake, ProviderHost.Ct);
        Assert.False(same.Properties!.ContainsKey("scopes"));

        var unknown = await Instance(t, Props(t, "\"consentType\": \"AllPrincipals\", \"scopes\": [\"orders.read\", null]"));
        unknown.Properties!["scopes"] = new JsonArray(JsonValue.Create("orders.read"), UnknownJson.Create("reference('x').scope", UnknownOrigins.Reference));
        await t.Provider.PrepareAsync(unknown, t.Host.Fake, ProviderHost.Ct);
        Assert.True(UnknownJson.IsUnknown(unknown.Properties["scope"]));

        var wholeUnknown = await Instance(t, Props(t, "\"consentType\": \"AllPrincipals\""));
        wholeUnknown.Properties!["scopes"] = UnknownJson.Create("reference('y').scopes", UnknownOrigins.Reference);
        await t.Provider.PrepareAsync(wholeUnknown, t.Host.Fake, ProviderHost.Ct);
        Assert.True(UnknownJson.IsUnknown(wholeUnknown.Properties["scope"]));
    }

    [Fact]
    public async Task Prepare_APrincipalConsent_KeepsThePrincipal()
    {
        var t = NewTenant();
        var instance = await Instance(t, Props(t, "\"consentType\": \"Principal\", \"principalId\": \"" + t.UserId + "\", \"scope\": \"orders.read\""));

        await t.Provider.PrepareAsync(instance, t.Host.Fake, ProviderHost.Ct);

        Assert.Equal(t.UserId, instance.Properties!["principalId"]!.GetValue<string>());
    }

    // ---- plan ----

    [Fact]
    public async Task Plan_ANonGuidName_AndAnUnknownProperty_AreUnsupportedRows()
    {
        var t = NewTenant();
        var named = await Instance(t, Props(t, "\"consentType\": \"AllPrincipals\", \"scope\": \"orders.read\""), name: "not-a-guid");
        var extra = await Instance(t, Props(t, "\"consentType\": \"AllPrincipals\", \"scope\": \"orders.read\", \"extra\": true"));

        var badName = t.Provider.Plan(named, null, PlanOptions.Default);
        var badProperty = t.Provider.Plan(extra, null, PlanOptions.Default);

        Assert.Contains("is not a GUID", badName.UnsupportedReason, StringComparison.Ordinal);
        Assert.Contains("is not a property of a permission grant", badProperty.UnsupportedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_BeforePrepare_LetsScopesStandInForScope()
    {
        var t = NewTenant();
        var instance = await Instance(t, Props(t, "\"consentType\": \"AllPrincipals\", \"scopes\": [\"orders.read\"]"));

        var change = t.Provider.Plan(instance, null, PlanOptions.Default);

        Assert.False(change.HasError, change.UnsupportedReason);
        Assert.True(UnknownJson.IsUnknown(instance.Properties!["scope"]));
    }

    // ---- read ----

    [Fact]
    public async Task Read_FindsTheGrantByKey_AndPrincipalGrantsAreMatchedByPrincipal()
    {
        var t = NewTenant();
        var all = t.Host.Directory.Add(GrantsPath, new JsonObject { ["clientId"] = t.ClientId, ["resourceId"] = t.ResourceId, ["consentType"] = "AllPrincipals", ["principalId"] = null, ["scope"] = "orders.read" });
        var principal = t.Host.Directory.Add(GrantsPath, new JsonObject { ["clientId"] = t.ClientId, ["resourceId"] = t.ResourceId, ["consentType"] = "Principal", ["principalId"] = t.UserId, ["scope"] = "orders.write" });
        string allId = all["id"]!.GetValue<string>();
        string principalGrantId = principal["id"]!.GetValue<string>();

        var allPrincipals = await t.Provider.ReadAsync(Key(t), t.Host.Fake, ProviderHost.Ct);
        var byPrincipal = await t.Provider.ReadAsync(Key(t, "Principal", t.UserId), t.Host.Fake, ProviderHost.Ct);
        var otherPrincipal = await t.Provider.ReadAsync(Key(t, "Principal", Guid.NewGuid().ToString("D")), t.Host.Fake, ProviderHost.Ct);

        Assert.Equal(allId, allPrincipals!.GraphId);
        Assert.Equal(principalGrantId, byPrincipal!.GraphId);
        Assert.Null(otherPrincipal);
    }

    [Fact]
    public async Task Read_AKeyWithoutTheCompositeProperties_Throws()
    {
        var t = NewTenant();
        var key = new ResourceKey { Type = GrantType, Values = new Dictionary<string, string> { ["clientId"] = t.ClientId } };

        var ex = await Assert.ThrowsAsync<ProviderException>(() => t.Provider.ReadAsync(key, t.Host.Fake, ProviderHost.Ct));

        Assert.Equal(EngineDiagnosticCodes.ResourceKeyUnresolved, ex.Code);
    }

    [Fact]
    public async Task Read_QuotesInAKeyValue_AreDoubledInTheFilter()
    {
        var t = NewTenant();
        var key = Key(t, "AllPrincipals") with { Values = new Dictionary<string, string> { ["clientId"] = "o'brien", ["resourceId"] = t.ResourceId, ["consentType"] = "AllPrincipals" } };

        var result = await t.Provider.ReadAsync(key, t.Host.Fake, ProviderHost.Ct);

        Assert.Null(result);
        var request = t.Host.Fake.Requests.Last(r => r.Method == "GET");
        Assert.Contains("clientId eq 'o''brien'", request.UrlParameters!["$filter"], StringComparison.Ordinal);
    }

    // ---- create ----

    [Fact]
    public async Task Create_WithoutAScope_Throws_AndAnUnevaluatedInstanceIsGuarded()
    {
        var t = NewTenant();
        var instance = await Instance(t, Props(t, "\"consentType\": \"AllPrincipals\""));

        var ex = await Assert.ThrowsAsync<ProviderException>(() => t.Provider.CreateAsync(instance, t.Host.Fake, ProviderHost.Ct));
        instance.Properties = null;

        Assert.Contains("scope is required", ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => t.Provider.CreateAsync(instance, t.Host.Fake, ProviderHost.Ct));
    }

    [Fact]
    public async Task Create_WhenGraphAnswersAlreadyExists_AdoptsTheGrantAndAlignsItsScope()
    {
        var t = NewTenant();
        var existing = t.Host.Directory.Add(GrantsPath, new JsonObject { ["clientId"] = t.ClientId, ["resourceId"] = t.ResourceId, ["consentType"] = "AllPrincipals", ["principalId"] = null, ["scope"] = "orders.read" });
        string id = existing["id"]!.GetValue<string>();
        var instance = await Instance(t, Props(t, "\"consentType\": \"AllPrincipals\", \"scope\": \"orders.read orders.write\""));
        t.Host.Fake.Failures.AlreadyExists(GrantsPath);

        var applied = await t.Provider.CreateAsync(instance, t.Host.Fake, ProviderHost.Ct);

        Assert.Equal(id, applied.GraphId);
        var patch = Assert.Single(t.Host.Fake.Writes, w => w.Method == "PATCH");
        Assert.Equal("orders.read orders.write", Check.Body(patch)["scope"]!.GetValue<string>());
        Assert.Equal("orders.read orders.write", t.Host.Directory.FindById(id)!["scope"]!.GetValue<string>());
    }

    [Fact]
    public async Task Create_WhenGraphAnswersAlreadyExistsWithTheSameScope_DoesNotPatch()
    {
        var t = NewTenant();
        t.Host.Directory.Add(GrantsPath, new JsonObject { ["clientId"] = t.ClientId, ["resourceId"] = t.ResourceId, ["consentType"] = "AllPrincipals", ["principalId"] = null, ["scope"] = "orders.write orders.read" });
        var instance = await Instance(t, Props(t, "\"consentType\": \"AllPrincipals\", \"scope\": \"orders.read orders.write\""));
        t.Host.Fake.Failures.AlreadyExists(GrantsPath);

        await t.Provider.CreateAsync(instance, t.Host.Fake, ProviderHost.Ct);

        Assert.DoesNotContain(t.Host.Fake.Writes, w => w.Method == "PATCH");
    }

    [Fact]
    public async Task Create_WhenGraphAnswersAlreadyExistsButTheGrantIsNotThere_Throws()
    {
        var t = NewTenant();
        var instance = await Instance(t, Props(t, "\"consentType\": \"AllPrincipals\", \"scope\": \"orders.read\""));
        t.Host.Fake.Failures.AlreadyExists(GrantsPath);

        var ex = await Assert.ThrowsAsync<ProviderException>(() => t.Provider.CreateAsync(instance, t.Host.Fake, ProviderHost.Ct));

        Assert.Equal(EngineDiagnosticCodes.ResourceApplyFailed, ex.Code);
        Assert.Contains("could not be read back", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_WhenTheAdoptedGrantVanishesAfterThePatch_SurfacesTheReplicationLagNotFound()
    {
        var t = NewTenant();
        var existing = t.Host.Directory.Add(GrantsPath, new JsonObject { ["clientId"] = t.ClientId, ["resourceId"] = t.ResourceId, ["consentType"] = "AllPrincipals", ["principalId"] = null, ["scope"] = "orders.read" });
        var instance = await Instance(t, Props(t, "\"consentType\": \"AllPrincipals\", \"scope\": \"orders.write\""));
        t.Host.Fake.Failures.AlreadyExists(GrantsPath);
        t.Host.Fake.Failures.Add("GET", GrantsPath + "/" + existing["id"]!.GetValue<string>(), request => new GraphException(HttpStatusCode.NotFound, "Request_ResourceNotFound", "gone", request.Hint));

        var ex = await Assert.ThrowsAsync<GraphException>(() => t.Provider.CreateAsync(instance, t.Host.Fake, ProviderHost.Ct));

        Assert.True(ex.IsNotFound);
        Assert.True(ex.IsReplication, "the read-back runs under the replication-lag hint, so the policy would retry it");
    }

    [Fact]
    public async Task Create_ABodylessResponse_IsReadBackByKey()
    {
        var t = NewTenant();
        var instance = await Instance(t, Props(t, "\"consentType\": \"AllPrincipals\", \"scope\": \"orders.read\""));
        var graph = new BodylessPostClient(t.Host.Fake);

        var applied = await t.Provider.CreateAsync(instance, graph, ProviderHost.Ct);

        Assert.Equal(t.Host.Directory.Objects(GrantsPath).Single()["id"]!.GetValue<string>(), applied.GraphId);
    }

    [Fact]
    public async Task Create_ABodylessResponse_WhoseGrantCannotBeFound_Throws()
    {
        var t = NewTenant();
        var instance = await Instance(t, Props(t, "\"consentType\": \"AllPrincipals\", \"scope\": \"orders.read\""));
        var graph = new BodylessPostClient(t.Host.Fake, createIt: false);

        var ex = await Assert.ThrowsAsync<ProviderException>(() => t.Provider.CreateAsync(instance, graph, ProviderHost.Ct));

        Assert.Contains("returned no object", ex.Message, StringComparison.Ordinal);
    }

    // ---- update ----

    [Fact]
    public async Task Update_PatchesTheScope_AndFailsWithoutOneOrWhenTheGrantVanishes()
    {
        var t = NewTenant();
        var existing = t.Host.Directory.Add(GrantsPath, new JsonObject { ["clientId"] = t.ClientId, ["resourceId"] = t.ResourceId, ["consentType"] = "AllPrincipals", ["principalId"] = null, ["scope"] = "orders.read" });
        string id = existing["id"]!.GetValue<string>();
        var current = new ExistingResource(id, existing, existing);
        var instance = await Instance(t, Props(t, "\"consentType\": \"AllPrincipals\", \"scope\": \"orders.read orders.write\""));
        var change = t.Provider.Plan(instance, current, PlanOptions.Default);

        var applied = await t.Provider.UpdateAsync(instance, current, change, t.Host.Fake, ProviderHost.Ct);

        Assert.Equal(id, applied.GraphId);
        Assert.Equal("orders.read orders.write", t.Host.Directory.FindById(id)!["scope"]!.GetValue<string>());

        var noScope = await Instance(t, Props(t, "\"consentType\": \"AllPrincipals\""));
        var missingScope = await Assert.ThrowsAsync<ProviderException>(() => t.Provider.UpdateAsync(noScope, current, change, t.Host.Fake, ProviderHost.Ct));
        Assert.Contains("scope is the only property that can change", missingScope.Message, StringComparison.Ordinal);

        t.Host.Fake.Failures.Add("GET", GrantsPath + "/" + id, request => new GraphException(HttpStatusCode.NotFound, "Request_ResourceNotFound", "gone", request.Hint));
        var vanished = await Assert.ThrowsAsync<ProviderException>(() => t.Provider.UpdateAsync(instance, current, change, t.Host.Fake, ProviderHost.Ct));
        Assert.Contains("vanished after the update", vanished.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<ArgumentNullException>(() => t.Provider.UpdateAsync(instance, null!, change, t.Host.Fake, ProviderHost.Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => t.Provider.UpdateAsync(instance, current, null!, t.Host.Fake, ProviderHost.Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => t.Provider.UpdateAsync(instance, current, change, null!, ProviderHost.Ct));
    }

    // ---- delete ----

    [Fact]
    public async Task Delete_RemovesTheGrant_ToleratesAMissingOne_AndPropagatesOtherErrors()
    {
        var t = NewTenant();
        var existing = t.Host.Directory.Add(GrantsPath, new JsonObject { ["clientId"] = t.ClientId, ["resourceId"] = t.ResourceId, ["consentType"] = "AllPrincipals", ["principalId"] = null, ["scope"] = "orders.read" });
        string id = existing["id"]!.GetValue<string>();

        await t.Provider.DeleteAsync(Entry(t, id).ResourceKey, Entry(t, id).Live, t.Host.Fake, ProviderHost.Ct);
        Assert.Empty(t.Host.Directory.Objects(GrantsPath));

        await t.Provider.DeleteAsync(Entry(t, id).ResourceKey, Entry(t, id).Live, t.Host.Fake, ProviderHost.Ct);

        t.Host.Fake.Failures.Forbid(GrantsPath, "DELETE");
        var ex = await Assert.ThrowsAsync<GraphException>(() => t.Provider.DeleteAsync(Entry(t, id).ResourceKey, Entry(t, id).Live, t.Host.Fake, ProviderHost.Ct));
        Assert.True(ex.IsForbidden);
        await Assert.ThrowsAsync<ArgumentNullException>(() => t.Provider.DeleteAsync(null!, null!, t.Host.Fake, ProviderHost.Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => t.Provider.DeleteAsync(Entry(t, id).ResourceKey, Entry(t, id).Live, null!, ProviderHost.Ct));
    }
}
