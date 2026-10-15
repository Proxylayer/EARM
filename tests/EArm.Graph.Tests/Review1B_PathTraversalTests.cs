using System;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph.Tests.Infrastructure;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary>
/// Review findings S-09 and I-07 (path part): <see cref="Uri"/> collapses <c>.</c> / <c>..</c> segments (even percent-encoded), so
/// <c>/v1.0/x/../beta/y</c> became <c>/beta/y</c> and a user called <c>..</c> produced <c>GET /v1.0/</c>. Key values are refused, and the client fails closed
/// when the path it was given is not the path <see cref="Uri"/> would send.
/// </summary>
public sealed class Review1B_PathTraversalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- GraphPath -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void ADotSegmentCannotBeAPathSegment(string value)
    {
        var error = Assert.Throws<ArgumentException>(() => GraphPath.Segment(value));
        Assert.Contains("path segment", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("...", "...")]
    [InlineData("a..b", "a..b")]
    [InlineData(".a", ".a")]
    [InlineData("a.", "a.")]
    [InlineData("..x", "..x")]
    [InlineData(" ..", "%20..")]
    [InlineData("%2E%2E", "%252E%252E")]
    public void OnlyAWholeDotSegmentIsRefused(string value, string expected)
    {
        Assert.Equal(expected, GraphPath.Segment(value));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    public void AKeyValueOfDotsIsSafeBecauseTheQuotesKeepItInsideOneSegment(string value)
    {
        Assert.Equal("users(userPrincipalName='" + value + "')", GraphPath.AlternateKey("users", "userPrincipalName", value));
        Assert.Equal(value, GraphPath.KeyValue(value));
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\tb")]
    [InlineData("a\0b")]
    [InlineData("a\u007Fb")]
    [InlineData("a\u0085b")]
    public void ControlCharactersAreRefusedInSegmentsAndKeyValues(string value)
    {
        Assert.Throws<ArgumentException>(() => GraphPath.Segment(value));
        Assert.Throws<ArgumentException>(() => GraphPath.KeyValue(value));
        Assert.Throws<ArgumentException>(() => GraphPath.AlternateKey("users", "mail", value));
    }

    // ---- the client fails closed ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("/x/../beta/y")]
    [InlineData("/users/..")]
    [InlineData("/users/../me")]
    [InlineData("/users/%2E%2E/me")]
    [InlineData("/users/%2e%2e/me")]
    [InlineData("/users/%2E./me")]
    [InlineData("/users/./me")]
    [InlineData("/users/%2e/me")]
    [InlineData("/..")]
    [InlineData("/../../beta/users")]
    [InlineData("/users/a/../../../beta/x")]
    public async Task ADotSegmentInTheRequestPathIsRefusedBeforeAnythingIsSent(string path)
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.Json("{}")));

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => harness.Client.GetAsync(new GraphRequest(path), Ct));

        Assert.Equal(GraphTransportFailure.UnsafeUrl, error.Failure);
        Assert.Empty(harness.Handler.Requests);
        Assert.Empty(harness.Tokens.Scopes);
    }

    [Theory]
    [InlineData("/users/..;/x", "/v1.0/users/..;/x")]
    [InlineData("/users/%41b", "/v1.0/users/Ab")]
    [InlineData("/users/a%2Fb", "/v1.0/users/a%2Fb")]
    [InlineData("/users/%E6%97%A5", "/v1.0/users/%E6%97%A5")]
    [InlineData("/applications(uniqueName='..')", "/v1.0/applications(uniqueName='..')")]
    [InlineData("/users/a...b", "/v1.0/users/a...b")]
    public async Task LegitimatePathsWithDotsOrEscapesStillGoThrough(string path, string expectedAbsolutePath)
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.Json("{}")));

        await harness.Client.GetAsync(new GraphRequest(path), Ct);

        Assert.Equal(expectedAbsolutePath, Assert.Single(harness.Handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task APathThatWouldHopFromV1ToBetaIsRefusedForEveryVerb()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.Json("{}")));
        var request = new GraphRequest("/me/../../beta/users");

        await Assert.ThrowsAsync<GraphTransportException>(() => harness.Client.PostAsync(request, new JsonObject(), Ct));
        await Assert.ThrowsAsync<GraphTransportException>(() => harness.Client.PatchAsync(request, new JsonObject(), Ct));
        await Assert.ThrowsAsync<GraphTransportException>(() => harness.Client.PutAsync(request, new JsonObject(), Ct));
        await Assert.ThrowsAsync<GraphTransportException>(() => harness.Client.DeleteAsync(request, Ct));
        await Assert.ThrowsAsync<GraphTransportException>(async () =>
        {
            await foreach (var _ in harness.Client.GetAllPagesAsync(request, Ct))
            {
            }
        });

        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task ANextLinkWithADotSegmentIsRefused()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(
            ScriptedResponse.Json("{\"value\":[{\"id\":\"1\"}],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/users/../beta/applications?$skiptoken=x\"}"),
            ScriptedResponse.Json("{\"value\":[]}")));

        var error = await Assert.ThrowsAsync<GraphTransportException>(async () =>
        {
            await foreach (var _ in harness.Client.GetAllPagesAsync(new GraphRequest("/users"), Ct))
            {
            }
        });

        Assert.Equal(GraphTransportFailure.UnsafeUrl, error.Failure);
        Assert.Single(harness.Handler.Requests);
    }

    [Theory]
    [InlineData("/users/../../beta/me")]
    [InlineData("/users/%2E%2E/me")]
    [InlineData("/users/./me")]
    public async Task ADotSegmentInABatchSubRequestIsRefused(string path)
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.Json("{\"responses\":[]}")));

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => harness.Client.BatchAsync([new BatchRequest("GET", path)], GraphApi.V1, RetryHint.Default, Ct));

        Assert.Equal(GraphTransportFailure.UnsafeUrl, error.Failure);
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public void GraphUrlBuildKeepsTheApiVersionOfTheRequest()
    {
        var v1 = GraphUrl.Build(GraphCloud.Public, new GraphRequest("/users/u1"));
        var beta = GraphUrl.Build(GraphCloud.Public, new GraphRequest("/users/u1", GraphApi.Beta));

        Assert.Equal("/v1.0/users/u1", v1.AbsolutePath);
        Assert.Equal("/beta/users/u1", beta.AbsolutePath);
    }

    [Fact]
    public void HasDotSegmentSeesRawAndEncodedDotsButNotNamesThatContainDots()
    {
        Assert.True(GraphUrl.HasDotSegment("/a/../b"));
        Assert.True(GraphUrl.HasDotSegment("/a/%2e%2E/b"));
        Assert.True(GraphUrl.HasDotSegment("/."));
        Assert.False(GraphUrl.HasDotSegment("/a/b..c/d"));
        Assert.False(GraphUrl.HasDotSegment("/a/.../d"));
        Assert.False(GraphUrl.HasDotSegment("/a/%252E%252E/d"));
    }

    [Fact]
    public void ThePathCheckCoversEveryKindOfRewriteUriCanDo()
    {
        foreach (var url in new[] { "https://graph.microsoft.com/v1.0/a/../b", "https://graph.microsoft.com/v1.0/a/%2E%2E/b", "https://graph.microsoft.com/v1.0/a/./b" })
        {
            var uri = new Uri(url);
            var error = Assert.Throws<GraphTransportException>(() => GraphUrl.EnsurePathIntact(uri, "test"));
            Assert.Equal(GraphTransportFailure.UnsafeUrl, error.Failure);
        }

        // Escape normalisation alone is not a rewrite.
        GraphUrl.EnsurePathIntact(new Uri("https://graph.microsoft.com/v1.0/%41/%7e/%2Fx"), "test");
    }

    [Fact]
    public void TheLoopbackCloudIsNotReachableThroughTheProductSurface()
    {
        Assert.DoesNotContain(GraphCloud.All, static c => c.LoopbackPort != 0);
        Assert.All(new[] { "loopback-test", "http://127.0.0.1:1", "127.0.0.1" }, static value => Assert.Throws<GraphConfigurationException>(() => GraphCloud.Parse(value)));
        Assert.StartsWith("https://", GraphCloud.Public.GraphOrigin, StringComparison.Ordinal);
        var loopback = GraphCloud.LoopbackForTests(1234);
        Assert.Equal("http://127.0.0.1:1234", loopback.GraphOrigin);
        // The https-only allow-list still refuses a plain-http or other-port URL for the loopback cloud, and the loopback URL for every real cloud.
        Assert.NotNull(GraphUrl.Check(GraphCloud.Public, new Uri("http://127.0.0.1:1234/v1.0/x")));
        Assert.NotNull(GraphUrl.Check(loopback, new Uri("http://127.0.0.1:1235/v1.0/x")));
        Assert.NotNull(GraphUrl.Check(loopback, new Uri("https://127.0.0.1:1234/v1.0/x")));
        Assert.NotNull(GraphUrl.Check(loopback, new Uri("http://localhost:1234/v1.0/x")));
        Assert.Null(GraphUrl.Check(loopback, new Uri("http://127.0.0.1:1234/v1.0/x")));
        Assert.True(GraphCloud.All.All(static c => c.LoopbackPort == 0));
    }
}
