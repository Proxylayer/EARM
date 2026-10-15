using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph.Tests.Infrastructure;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary>
/// Review finding Q-05: the PRODUCTION <see cref="HttpClient"/> (<see cref="HttpGraphClient.CreateHttpClient"/>) was only ever tested through fakes, so
/// <c>AllowAutoRedirect = true</c> survived mutation testing. These tests run it over a real socket against an in-process loopback server
/// (reached through the internal, test-only <see cref="GraphCloud.LoopbackForTests"/> cloud, which no environment variable or option can produce).
/// </summary>
public sealed class Review1B_HttpClientSettingsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Review1BRig RigFor(LoopbackServer server, Action<GraphOptions>? configure = null)
    {
        return new Review1BRig(http: HttpGraphClient.CreateHttpClient(), cloud: GraphCloud.LoopbackForTests(server.Port), configure: configure);
    }

    // ---- the handler settings ------------------------------------------------------------------------------------

    [Fact]
    public void TheProductionHandlerHasTheSecureSettings()
    {
        using var handler = HttpGraphClient.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Equal(DecompressionMethods.GZip | DecompressionMethods.Deflate, handler.AutomaticDecompression);
        Assert.Equal(TimeSpan.FromMinutes(5), handler.PooledConnectionLifetime);
        Assert.Equal(TimeSpan.FromSeconds(30), handler.ConnectTimeout);
    }

    [Fact]
    public void TheProductionClientLeavesTheTimeoutToTheTransport()
    {
        using var client = HttpGraphClient.CreateHttpClient();

        Assert.Equal(System.Threading.Timeout.InfiniteTimeSpan, client.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(120), new GraphOptions().Timeout);
    }

    // ---- over a real socket --------------------------------------------------------------------------------------

    [Fact]
    public async Task ARedirectIsNeverFollowedAndNothingIsSentToTheTarget()
    {
        using var target = new LoopbackServer(_ => LoopbackReply.Json("{\"value\":[]}"));
        using var origin = new LoopbackServer(_ => new LoopbackReply
        {
            Status = 302,
            Reason = "Found",
            Headers = [new("Location", $"http://127.0.0.1:{target.Port}/v1.0/stolen")],
        });
        using var rig = RigFor(origin);

        var error = await Assert.ThrowsAsync<GraphException>(() => rig.Client.GetAsync(new GraphRequest("/users"), Ct));

        Assert.Equal(HttpStatusCode.Found, error.Status);
        Assert.Equal("UnexpectedRedirect", error.Code);
        Assert.Contains("redirect", error.Message, StringComparison.OrdinalIgnoreCase);
        var sent = Assert.Single(origin.Requests);
        Assert.StartsWith("Bearer ", sent.Header("Authorization"), StringComparison.Ordinal);
        Assert.Empty(target.Requests);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task EveryRedirectStatusIsRefusedAndTheAuthorizationHeaderStaysOnTheOrigin(int status)
    {
        using var target = new LoopbackServer(_ => LoopbackReply.Json("{}"));
        using var origin = new LoopbackServer(_ => new LoopbackReply { Status = status, Reason = "Moved", Headers = [new("Location", $"http://127.0.0.1:{target.Port}/x")] });
        using var rig = RigFor(origin);

        await Assert.ThrowsAsync<GraphException>(() => rig.Client.PostAsync(new GraphRequest("/groups"), new JsonObject(), Ct));

        Assert.Single(origin.Requests);
        Assert.Empty(target.Requests);
    }

    [Fact]
    public async Task CookiesAreNeverStoredOrReplayed()
    {
        using var server = new LoopbackServer(_ => LoopbackReply.Json("{\"value\":[]}").With("Set-Cookie", "sid=abc123; Path=/"));
        using var rig = RigFor(server);

        await rig.Client.GetAsync(new GraphRequest("/users"), Ct);
        await rig.Client.GetAsync(new GraphRequest("/users"), Ct);

        Assert.Equal(2, server.Requests.Count);
        Assert.All(server.Requests, static r => Assert.Null(r.Header("Cookie")));
    }

    [Fact]
    public async Task GzipResponsesAreDecompressedAndAdvertised()
    {
        using var server = new LoopbackServer(_ => LoopbackReply.Gzip(Encoding.UTF8.GetBytes("{\"value\":[{\"id\":\"1\"}]}")));
        using var rig = RigFor(server);

        var node = await rig.Client.GetAsync(new GraphRequest("/users"), Ct);

        Assert.Equal("1", node!["value"]![0]!["id"]!.GetValue<string>());
        var acceptEncoding = Assert.Single(server.Requests).Header("Accept-Encoding");
        Assert.NotNull(acceptEncoding);
        Assert.Contains("gzip", acceptEncoding, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("deflate", acceptEncoding, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AGzipBombIsCutOffAtTheLimitAfterDecompression()
    {
        // 64 MiB of zeros compresses to about 64 KiB: tiny on the wire, huge once inflated.
        var bomb = new byte[64 * 1024 * 1024];
        using var server = new LoopbackServer(_ => LoopbackReply.Gzip(bomb));
        using var rig = RigFor(server, o => o.MaxResponseBytes = 1024 * 1024);

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => rig.Client.GetAsync(new GraphRequest("/users"), Ct));

        Assert.Equal(GraphTransportFailure.ResponseTooLarge, error.Failure);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task ARegularGzipResponseJustUnderTheLimitIsAccepted()
    {
        var json = Encoding.UTF8.GetBytes("{\"a\":\"" + new string('x', 100_000 - 8) + "\"}");
        using var server = new LoopbackServer(_ => LoopbackReply.Gzip(json));
        using var rig = RigFor(server, o => o.MaxResponseBytes = 100_000);

        var node = await rig.Client.GetAsync(new GraphRequest("/users"), Ct);

        Assert.NotNull(node);
    }

    [Fact]
    public async Task TheRequestCarriesTheUserAgentTheBearerTokenAndTheClientRequestId()
    {
        using var server = new LoopbackServer(_ => LoopbackReply.Json("{\"value\":[]}"));
        using var rig = RigFor(server, o => o.UserAgentVersion = "9.8.7");

        await rig.Client.GetAsync(new GraphRequest("/users").WithSelect("id"), Ct);

        var request = Assert.Single(server.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/v1.0/users?$select=id", request.Target);
        Assert.Equal("EARM/9.8.7", request.Header("User-Agent"));
        Assert.Equal("Bearer " + rig.Tokens.Current, request.Header("Authorization"));
        Assert.True(Guid.TryParse(request.Header("client-request-id"), out _));
        Assert.Equal("application/json", request.Header("Accept"));
    }

    [Fact]
    public async Task ThePerCallTimeoutIsEnforcedAndAGetIsRetriedThreeTimes()
    {
        using var server = new LoopbackServer(_ => new LoopbackReply { Hang = true });
        using var rig = RigFor(server, o => o.Timeout = TimeSpan.FromMilliseconds(300));

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => rig.Client.GetAsync(new GraphRequest("/users"), Ct));

        Assert.Equal(GraphTransportFailure.Timeout, error.Failure);
        Assert.False(error.Ambiguous);
        Assert.Equal(3, server.Requests.Count);
    }

    [Fact]
    public async Task APostThatTimesOutOverARealSocketIsSentOnceAndAmbiguous()
    {
        using var server = new LoopbackServer(_ => new LoopbackReply { Hang = true });
        using var rig = RigFor(server, o => o.Timeout = TimeSpan.FromMilliseconds(300));

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => rig.Client.PostAsync(new GraphRequest("/applications/a/addPassword"), new JsonObject(), Ct));

        Assert.Equal(GraphTransportFailure.Timeout, error.Failure);
        Assert.True(error.Ambiguous);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task APostWhoseConnectionIsResetAfterTheRequestWasReadIsSentOnceAndAmbiguous()
    {
        using var server = new LoopbackServer(_ => new LoopbackReply { CloseWithoutReply = true });
        using var rig = RigFor(server);

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => rig.Client.PostAsync(new GraphRequest("/applications/a/addPassword"), new JsonObject(), Ct));

        Assert.Equal(GraphTransportFailure.Network, error.Failure);
        Assert.True(error.Ambiguous);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task APostToAPortNobodyListensOnIsRetriedBecauseNothingWasSent()
    {
        var port = FreePort();
        using var rig = new Review1BRig(http: HttpGraphClient.CreateHttpClient(), cloud: GraphCloud.LoopbackForTests(port));

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => rig.Client.PostAsync(new GraphRequest("/groups"), new JsonObject(), Ct));

        Assert.Equal(GraphTransportFailure.Network, error.Failure);
        Assert.False(error.Ambiguous);
        Assert.Equal(2, rig.AllDelays.Count);
    }

    [Fact]
    public async Task AWriteCarriesItsJsonBodyAndContentType()
    {
        using var server = new LoopbackServer(_ => LoopbackReply.Json("{\"id\":\"g1\"}", 201, "Created"));
        using var rig = RigFor(server);

        var response = await rig.Client.PostAsync(new GraphRequest("/groups"), new JsonObject { ["displayName"] = "G" }, Ct);

        Assert.True(response.IsCreated);
        var request = Assert.Single(server.Requests);
        Assert.StartsWith("application/json", request.Header("Content-Type"), StringComparison.Ordinal);
        Assert.Equal("{\"displayName\":\"G\"}", Encoding.UTF8.GetString(request.Body));
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
