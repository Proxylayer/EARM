using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph.Tests.Infrastructure;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary>How <see cref="HttpGraphClient"/> builds the HTTP request: URL, query encoding, headers, body.</summary>
public sealed class HttpGraphClientRequestTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ScriptedResponse EmptyPage()
    {
        return ScriptedResponse.Json("{\"value\":[]}");
    }

    public static TheoryData<string> FilterValues =>
    [
        "displayName eq 'Orders AU'",
        "displayName eq 'it''s a test'",
        "mailNickname eq 'a&b=c'",
        "displayName eq '50% off, 100%25 sure'",
        "displayName eq 'ünï 日本語 Ωmega'",
        "displayName eq 'smile 😀 pair'",
        "displayName eq 'x+y;z#frag?q=1\\\\path'",
        "displayName eq '\"quoted\"' and startswith(userPrincipalName,'a~b._-c')",
        "id in ('a','b') and (x ne null)",
        "tab\tnew\nline and  double  space",
    ];

    [Theory]
    [MemberData(nameof(FilterValues))]
    public async Task FilterValuesRoundTripThroughPercentEncoding(string filter)
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(EmptyPage()));

        await harness.Client.GetAsync(new GraphRequest("/users").WithFilter(filter).WithSelect("id", "displayName").WithTop(5), Ct);

        var request = Assert.Single(harness.Handler.Requests);
        Assert.Equal(filter, request.QueryValue("$filter"));
        Assert.Equal("id,displayName", request.QueryValue("$select"));
        Assert.Equal("5", request.QueryValue("$top"));

        // Only RFC 3986 unreserved characters (plus the '$' of option names and the structural '&' '=') appear raw in the query.
        var raw = request.Uri.Query[1..];
        foreach (var c in raw)
        {
            Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '%' or '&' or '=' or '$', $"raw query character '{c}' should have been percent-encoded: {raw}");
        }

        Assert.StartsWith("$filter=", raw, StringComparison.Ordinal);
        Assert.Equal(3, raw.Split('&').Length);
    }

    [Fact]
    public async Task ALoneSurrogateInAFilterBecomesTheReplacementCharacter()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(EmptyPage()));

        await harness.Client.GetAsync(new GraphRequest("/users").WithFilter("displayName eq 'a\uD800b'"), Ct);

        Assert.Equal("displayName eq 'a�b'", Assert.Single(harness.Handler.Requests).QueryValue("$filter"));
    }

    [Fact]
    public async Task TheUrlIsTheCloudOriginPlusApiVersionPlusPath()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(EmptyPage(), EmptyPage()));

        await harness.Client.GetAsync(new GraphRequest("/users"), Ct);
        await harness.Client.GetAsync(new GraphRequest("/applications", GraphApi.Beta), Ct);

        Assert.Equal("https://graph.microsoft.com/v1.0/users", harness.Handler.Requests[0].Uri.AbsoluteUri);
        Assert.Equal("https://graph.microsoft.com/beta/applications", harness.Handler.Requests[1].Uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("usgov", "graph.microsoft.us")]
    [InlineData("usgovdod", "dod-graph.microsoft.us")]
    [InlineData("china", "microsoftgraph.chinacloudapi.cn")]
    public async Task SovereignCloudsUseTheirOwnGraphHost(string cloud, string host)
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(EmptyPage()), cloud: GraphCloud.Parse(cloud));

        await harness.Client.GetAsync(new GraphRequest("/users"), Ct);

        Assert.Equal($"https://{host}/v1.0/users", Assert.Single(harness.Handler.Requests).Uri.AbsoluteUri);
        Assert.Equal($"https://{host}/.default", Assert.Single(harness.Tokens.Scopes));
    }

    [Theory]
    [InlineData("applications(uniqueName='it''s')", "/v1.0/applications(uniqueName='it''s')")]
    [InlineData("applications(uniqueName='a%20b')", "/v1.0/applications(uniqueName='a%20b')")]
    [InlineData("users/user_contoso.example%23EXT%23@fabrikam.example", "/v1.0/users/user_contoso.example%23EXT%23@fabrikam.example")]
    [InlineData("groups/g%2F1/members/$ref", "/v1.0/groups/g%2F1/members/$ref")]
    public async Task AlternateKeyAndEscapedPathsReachTheWireUnchanged(string path, string expectedPath)
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(EmptyPage()));

        await harness.Client.GetAsync(new GraphRequest(path), Ct);

        Assert.Equal(expectedPath, Assert.Single(harness.Handler.Requests).Uri.AbsolutePath);
    }

    [Theory]
    [InlineData("/../../other")]
    [InlineData("/..")]
    [InlineData("/%2e%2e/%2e%2e/oauth2/v2.0/token")]
    public async Task APathThatClimbsOutOfTheApiRootIsRefusedBeforeAnythingIsSent(string path)
    {
        using var harness = new ClientHarness();

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => harness.Client.GetAsync(new GraphRequest(path), Ct));

        Assert.Equal(GraphTransportFailure.UnsafeUrl, error.Failure);
        Assert.Empty(harness.Handler.Requests);
        Assert.Empty(harness.Tokens.Scopes);
    }

    [Fact]
    public async Task EveryRequestCarriesBearerAcceptUserAgentAndAClientRequestId()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(EmptyPage()), options => options.UserAgentVersion = "9.8.7");

        await harness.Client.GetAsync(new GraphRequest("/users"), Ct);

        var request = Assert.Single(harness.Handler.Requests);
        Assert.Equal("Bearer token-1", request.Header("Authorization"));
        Assert.Equal("application/json", request.Header("Accept"));
        Assert.Equal("EARM/9.8.7", request.Header("User-Agent"));
        Assert.True(Guid.TryParse(request.Header("client-request-id"), out _));
        Assert.Equal("true", request.Header("return-client-request-id"));
        Assert.Equal(["https://graph.microsoft.com/.default"], harness.Tokens.Scopes);
    }

    [Fact]
    public async Task PreferAndConsistencyLevelHeadersComeFromTheRequest()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(EmptyPage(), EmptyPage()));
        var request = new GraphRequest("/users").WithPrefer("odata.maxpagesize=5").WithPrefer("return=representation").WithConsistencyLevelEventual().WithCount();

        await harness.Client.GetAsync(request, Ct);
        await harness.Client.GetAsync(new GraphRequest("/users"), Ct);

        var first = harness.Handler.Requests[0];
        Assert.Equal("odata.maxpagesize=5, return=representation", first.Header("Prefer"));
        Assert.Equal("eventual", first.Header("ConsistencyLevel"));
        Assert.Equal("true", first.QueryValue("$count"));
        var second = harness.Handler.Requests[1];
        Assert.Null(second.Header("Prefer"));
        Assert.Null(second.Header("ConsistencyLevel"));
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("authorization")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Cookie")]
    public async Task ACallerCannotSupplyCredentialHeaders(string header)
    {
        using var harness = new ClientHarness();

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Client.GetAsync(new GraphRequest("/users").WithHeader(header, "Bearer stolen"), Ct));

        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task AHeaderValueWithALineBreakIsRefused()
    {
        using var harness = new ClientHarness();

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Client.GetAsync(new GraphRequest("/users").WithHeader("Prefer", "a\r\nX-Injected: 1"), Ct));

        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task AGetNeverCarriesABodyOrContentHeaders()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(EmptyPage(), ScriptedResponse.NoContent()));

        await harness.Client.GetAsync(new GraphRequest("/users").WithHeader("Content-Type", "application/json"), Ct);
        await harness.Client.DeleteAsync(new GraphRequest("/users/u1"), Ct);

        foreach (var request in harness.Handler.Requests)
        {
            Assert.Null(request.Body);
            Assert.Null(request.Header("Content-Type"));
            Assert.Null(request.Header("Content-Length"));
        }
    }

    [Fact]
    public async Task PostPatchAndPutSendTheJsonBodyAsUtf8WithAJsonContentType()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(
            ScriptedResponse.Json("{\"id\":\"1\"}", HttpStatusCode.Created),
            ScriptedResponse.NoContent(),
            ScriptedResponse.NoContent()));
        var body = new JsonObject { ["displayName"] = "Ünï 日本 😀", ["quote"] = "he said \"hi\"", ["lt"] = "<a&b>" };

        await harness.Client.PostAsync(new GraphRequest("/groups"), body, Ct);
        await harness.Client.PatchAsync(new GraphRequest("/groups/g1"), body, Ct);
        await harness.Client.PutAsync(new GraphRequest("/users/u1/manager/$ref"), body, Ct);

        Assert.Equal([HttpMethod.Post, HttpMethod.Patch, HttpMethod.Put], harness.Handler.Requests.Select(static r => r.Method));
        foreach (var request in harness.Handler.Requests)
        {
            Assert.StartsWith("application/json", request.Header("Content-Type"), StringComparison.Ordinal);
            Assert.Contains("charset=utf-8", request.Header("Content-Type"), StringComparison.OrdinalIgnoreCase);
            var parsed = JsonNode.Parse(request.Body!)!;
            Assert.Equal("Ünï 日本 😀", parsed["displayName"]!.GetValue<string>());
            Assert.Equal("he said \"hi\"", parsed["quote"]!.GetValue<string>());
            Assert.Equal("<a&b>", parsed["lt"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task APostWithoutABodySendsNoContentButStillPosts()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.NoContent()));

        var response = await harness.Client.PostAsync(new GraphRequest("/applications/a1/removePassword"), null, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.Status);
        Assert.Null(Assert.Single(harness.Handler.Requests).Body);
    }

    [Fact]
    public async Task ACallerSuppliedContentTypeWinsOverTheJsonDefault()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.NoContent()));

        await harness.Client.PatchAsync(new GraphRequest("/users/u1/photo/$value").WithHeader("Content-Type", "application/merge-patch+json"), new JsonObject { ["a"] = 1 }, Ct);

        Assert.StartsWith("application/merge-patch+json", Assert.Single(harness.Handler.Requests).Header("Content-Type"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SovereignCloudsRewriteODataIdReferencesToTheirOwnHost()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.NoContent()), cloud: GraphCloud.UsGov);
        var body = new JsonObject { ["@odata.id"] = "https://graph.microsoft.com/v1.0/directoryObjects/o1", ["other"] = "https://graph.microsoft.com/v1.0/keep" };

        await harness.Client.PostAsync(new GraphRequest("/groups/g1/members/$ref"), body, Ct);

        var sent = JsonNode.Parse(Assert.Single(harness.Handler.Requests).Body!)!;
        Assert.Equal("https://graph.microsoft.us/v1.0/directoryObjects/o1", sent["@odata.id"]!.GetValue<string>());
        Assert.Equal("https://graph.microsoft.com/v1.0/keep", sent["other"]!.GetValue<string>());
        Assert.Equal("https://graph.microsoft.com/v1.0/directoryObjects/o1", body["@odata.id"]!.GetValue<string>());
    }

    [Fact]
    public async Task APublicCloudBodyIsSentAsIs()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.NoContent()));

        await harness.Client.PostAsync(new GraphRequest("/groups/g1/members/$ref"), new JsonObject { ["@odata.id"] = "https://graph.microsoft.com/v1.0/directoryObjects/o1" }, Ct);

        Assert.Contains("https://graph.microsoft.com/v1.0/directoryObjects/o1", Assert.Single(harness.Handler.Requests).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTokenProviderIsAskedForEveryRequestSoItsCacheDecides()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(EmptyPage(), EmptyPage(), EmptyPage()));

        for (var i = 0; i < 3; i++)
        {
            await harness.Client.GetAsync(new GraphRequest("/users"), Ct);
        }

        Assert.Equal(3, harness.Tokens.Scopes.Count);
        Assert.Equal(0, harness.Tokens.Invalidations);
    }
}
