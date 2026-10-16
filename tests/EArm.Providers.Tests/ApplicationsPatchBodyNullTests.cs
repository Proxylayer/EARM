using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Core.Types;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// How an application PATCH clears live-only sub-properties (the <c>x-earm.nullable</c> overlay of applications.json and
/// <see cref="JsonDiff.BuildPatchBody"/>), per the Graph probes F2 / F3 / G: <c>web.redirectUriSettings</c> and
/// <c>web.implicitGrantSettings</c> are never sent as <c>null</c>; the <c>redirectUris</c> collections are cleared with <c>[]</c>
/// (Graph refuses <c>null</c>); <c>api.requestedAccessTokenVersion</c> is cleared with <c>null</c>.
/// </summary>
public sealed class ApplicationsPatchBodyNullTests
{
    private const string Type = ApplicationsProvider.ResourceType;
    private const string Name = "Test-API";

    private static ResourceTypeDefinition Definition => ResourceTypeCatalog.Default.Find(Type)!;

    private static JsonObject J(string json)
    {
        return JsonNode.Parse(json)!.AsObject();
    }

    /// <summary>The PATCH body the provider base would build against the live view as given (no <c>ignoreLiveOnly</c> pruning).</summary>
    private static JsonObject PatchBody(string desired, string live)
    {
        var options = JsonDiffOptions.ForDefinition(Definition, existing: true);
        var desiredObject = J(desired);
        var liveObject = J(live);
        return JsonDiff.BuildPatchBody(desiredObject, liveObject, JsonDiff.Diff(desiredObject, liveObject, options), options);
    }

    [Fact]
    public void TheOverlay_MarksRedirectUriSettingsAndImplicitGrantSettingsNotNullable_AndRequestedAccessTokenVersionNullable()
    {
        var nullable = Definition.Nullable;

        Assert.False(nullable["web.redirectUriSettings"]);
        Assert.False(nullable["web.implicitGrantSettings"]);
        Assert.False(nullable.ContainsKey("api.requestedAccessTokenVersion"));
    }

    [Fact]
    public void ALiveOnlyRedirectUriSettings_IsNeverSentAsNull()
    {
        var body = PatchBody(
            """{ "web": { "redirectUris": ["https://new"] } }""",
            """{ "web": { "redirectUris": ["https://old"], "redirectUriSettings": [ { "uri": "https://old", "index": null } ] } }""");

        var web = Assert.IsType<JsonObject>(body["web"]);
        Assert.False(web.ContainsKey("redirectUriSettings"));
        Assert.Equal("""{"redirectUris":["https://new"]}""", web.ToJsonString());
    }

    [Theory]
    [InlineData("web", """{ "homePageUrl": "https://home" }""", """{ "homePageUrl": "https://old-home", "redirectUris": ["https://stale"] }""")]
    [InlineData("spa", """{ "futureProperty": 1 }""", """{ "redirectUris": ["https://stale-spa"] }""")]
    [InlineData("publicClient", """{ "futureProperty": 1 }""", """{ "redirectUris": ["https://stale-pc"] }""")]
    public void ALiveOnlyRedirectUrisCollection_IsClearedWithAnEmptyArray_NotNull(string parent, string desiredChild, string liveChild)
    {
        var body = PatchBody("{ \"" + parent + "\": " + desiredChild + " }", "{ \"" + parent + "\": " + liveChild + " }");

        var sent = Assert.IsType<JsonObject>(body[parent]);
        var uris = Assert.IsType<JsonArray>(sent["redirectUris"]);
        Assert.Empty(uris);
    }

    [Fact]
    public void ALiveOnlyRequestedAccessTokenVersion_IsClearedWithNull()
    {
        var body = PatchBody(
            """{ "api": { "oauth2PermissionScopes": [ { "id": "6a1e1c1e-0000-4000-8000-000000000001", "value": "read" } ] } }""",
            """{ "api": { "oauth2PermissionScopes": [], "requestedAccessTokenVersion": 2 } }""");

        var api = Assert.IsType<JsonObject>(body["api"]);
        Assert.True(api.ContainsKey("requestedAccessTokenVersion"));
        Assert.Null(api["requestedAccessTokenVersion"]);
    }

    [Fact]
    public void ALiveOnlyImplicitGrantSettings_IsStillNotSent()
    {
        var body = PatchBody(
            """{ "web": { "homePageUrl": "https://home" } }""",
            """{ "web": { "homePageUrl": "https://old", "implicitGrantSettings": { "enableAccessTokenIssuance": true, "enableIdTokenIssuance": false } } }""");

        Assert.Equal("""{"homePageUrl":"https://home"}""", body["web"]!.ToJsonString());
    }

    [Fact]
    public async Task EndToEnd_AStaleSpaRedirectUriIsClearedWithAnEmptyArray_ThenNoChange()
    {
        var host = new ProviderHost();
        host.Directory.AddApplication(Name, "Test API", configure: app =>
        {
            app["spa"] = new JsonObject { ["redirectUris"] = new JsonArray("https://stale-spa") };
        });
        string resource = Templates.Resource(Type, Name, """{ "displayName": "Test API", "spa": { "redirectUris": [] } }""");

        var run = await host.CreateAsync(host.Resources(resource));

        Check.Succeeded(run);
        var patch = Assert.Single(run.Writes);
        Assert.Equal("PATCH", patch.Method);
        Assert.Equal("""{"spa":{"redirectUris":[]}}""", patch.Body!.ToJsonString());
        Assert.DoesNotContain("null", patch.Body!.ToJsonString(), StringComparison.Ordinal);

        var rerun = await host.CreateAsync(host.Resources(resource));
        Assert.Equal(ChangeType.NoChange, rerun.Executed.Operations.Single().ChangeType);
        Check.NoWrites(rerun);
    }
}
