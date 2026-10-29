using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph.Tests.Infrastructure;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary>
/// <see cref="ArmClient"/>: resource path and api-version validation before any token or URL exists, the Resource Manager origin from the cloud table, the
/// <c>.default</c> token of that origin, paging over <c>nextLink</c> that stays on the origin, ARM's error shape, retries, size limits, a <c>PATCH</c> that is
/// never abandoned once sent, and no token or body in the log.
/// </summary>
public sealed class ArmClientTests
{
    private const string ApiVersion = "2020-10-01";
    private const string PolicyPath = "/subscriptions/00000000-0000-0000-0000-000000000001/providers/Microsoft.Authorization/roleManagementPolicies/p1";
    private const string ListPath = "/subscriptions/00000000-0000-0000-0000-000000000001/providers/Microsoft.Authorization/roleManagementPolicies";
    private const string Origin = "https://management.azure.com";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Harness : IDisposable
    {
        private readonly HttpClient _http;

        public Harness(HttpMessageHandler handler, GraphCloud? cloud = null, TimeSpan? timeout = null)
        {
            Handler = handler as ScriptedHttpHandler;
            Tokens = new FakeTokenProvider();
            Delays = new RecordingDelayProvider();
            Log = new LogSink();
            _http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
            Client = new ArmClient(_http, Tokens, new RetryPolicy(new RetryOptions(), Delays, new TestLogger<RetryPolicy>(Log)), cloud ?? GraphCloud.Public, timeout ?? TimeSpan.FromSeconds(5), new TestLogger<ArmClient>(Log), userAgentVersion: "9.9.9-test");
        }

        public ScriptedHttpHandler? Handler { get; }

        public FakeTokenProvider Tokens { get; }

        public RecordingDelayProvider Delays { get; }

        public LogSink Log { get; }

        public ArmClient Client { get; }

        public void Dispose()
        {
            _http.Dispose();
        }
    }

    /// <summary>Signals when a request arrives and answers only when released (honouring the token it was given), so a test can act while the request is in flight.</summary>
    private sealed class GatedHandler : HttpMessageHandler
    {
        private int _requests;

        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Requests => Volatile.Read(ref _requests);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            Arrived.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("{\"id\":\"applied\"}", new UTF8Encoding(false), "application/json"),
            };
        }
    }

    private static string ArmError(string code, string message)
    {
        return "{\"error\":{\"code\":\"" + code + "\",\"message\":\"" + message + "\"}}";
    }

    private static async Task<(List<JsonObject> Items, Exception? Error)> DrainAsync(IAsyncEnumerable<JsonObject> source)
    {
        var items = new List<JsonObject>();
        try
        {
            await foreach (var item in source)
            {
                items.Add(item);
            }

            return (items, null);
        }
        catch (Exception ex)
        {
            return (items, ex);
        }
    }

    private static List<string> Ids(IEnumerable<JsonObject> items)
    {
        return [.. items.Select(static i => i["id"]!.GetValue<string>())];
    }

    // ---- URL building ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("public", "https://management.azure.com")]
    [InlineData("usgov", "https://management.usgovcloudapi.net")]
    [InlineData("usgovdod", "https://management.usgovcloudapi.net")]
    [InlineData("china", "https://management.chinacloudapi.cn")]
    public void TheUrlIsTheCloudsOriginThePathAndTheApiVersion(string cloud, string origin)
    {
        using var harness = new Harness(new ScriptedHttpHandler(), GraphCloud.Parse(cloud));

        Assert.Equal(origin + PolicyPath + "?api-version=" + ApiVersion, harness.Client.BuildUri(PolicyPath, ApiVersion).AbsoluteUri);
    }

    [Fact]
    public void AFilterIsAppendedPercentEncodedAsAQueryValue()
    {
        using var harness = new Harness(new ScriptedHttpHandler());

        var uri = harness.Client.BuildUri(ListPath, ApiVersion, "roleDefinitionId eq '/x' & a=b#c");

        Assert.StartsWith(Origin + ListPath + "?api-version=" + ApiVersion + "&$filter=", uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("?api-version=" + ApiVersion + "&$filter=" + Uri.EscapeDataString("roleDefinitionId eq '/x' & a=b#c"), uri.Query);
        Assert.Empty(uri.Fragment);
        Assert.Equal(ListPath, uri.AbsolutePath);
    }

    [Fact]
    public void TheLoopbackTestCloudUsesPlainHttpOnItsPort()
    {
        using var harness = new Harness(new ScriptedHttpHandler(), GraphCloud.LoopbackForTests(1234));

        Assert.Equal("http://127.0.0.1:1234" + PolicyPath + "?api-version=" + ApiVersion, harness.Client.BuildUri(PolicyPath, ApiVersion).AbsoluteUri);
        Assert.Equal("http://127.0.0.1:1234/next", harness.Client.ParseNextLink("http://127.0.0.1:1234/next").AbsoluteUri);
        Assert.Equal(GraphTransportFailure.UnsafeUrl, Assert.Throws<GraphTransportException>(() => harness.Client.ParseNextLink("https://127.0.0.1:1234/next")).Failure);
        Assert.Equal(GraphTransportFailure.UnsafeUrl, Assert.Throws<GraphTransportException>(() => harness.Client.ParseNextLink("http://127.0.0.1:1235/next")).Failure);
        Assert.Equal(GraphTransportFailure.UnsafeUrl, Assert.Throws<GraphTransportException>(() => harness.Client.ParseNextLink("http://localhost:1234/next")).Failure);
    }

    [Theory]
    [InlineData("/subscriptions/s1", true)]
    [InlineData(PolicyPath, true)]
    [InlineData("/providers/Microsoft.Management/managementGroups/mg-1/providers/Microsoft.Authorization/roleManagementPolicies", true)]
    [InlineData("/a-._~!$&'()*+,;=:@b", true)]
    [InlineData("subscriptions/s1", false)]
    [InlineData("//evil.example/x", false)]
    [InlineData("/http://evil.example/x", false)]
    [InlineData("/https://evil.example/x", false)]
    [InlineData("/subscriptions/s1?api-version=1", false)]
    [InlineData("/subscriptions/s1#frag", false)]
    [InlineData("/subscriptions\\s1", false)]
    [InlineData("/subscriptions/s%2F1", false)]
    [InlineData("/subscriptions/s 1", false)]
    [InlineData("/subscriptions/é", false)]
    [InlineData("/subscriptions/./s1", false)]
    [InlineData("/subscriptions/../s1", false)]
    [InlineData("/subscriptions//s1", false)]
    [InlineData("/subscriptions/s1/", false)]
    [InlineData("/.", false)]
    [InlineData("/..", false)]
    [InlineData("/", false)]
    [InlineData("/subscriptions/s1\n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ResourcePathsFollowTheDocumentedRules(string? path, bool valid)
    {
        Assert.Equal(valid, ArmClient.IsValidResourcePath(path));
    }

    [Fact]
    public void ResourcePathsAreLimitedTo2048Characters()
    {
        Assert.True(ArmClient.IsValidResourcePath("/" + new string('a', ArmClient.MaxResourcePathLength - 1)));
        Assert.False(ArmClient.IsValidResourcePath("/" + new string('a', ArmClient.MaxResourcePathLength)));
    }

    [Theory]
    [InlineData("2020-10-01", true)]
    [InlineData("2024-02-01-preview", true)]
    [InlineData("7.4", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("2020 10 01", false)]
    [InlineData("2020-10-01&x=1", false)]
    [InlineData("2020-10-01#x", false)]
    [InlineData("../x", false)]
    [InlineData("2020_10_01", false)]
    public void ApiVersionsFollowTheDocumentedRules(string? apiVersion, bool valid)
    {
        Assert.Equal(valid, ArmClient.IsValidApiVersion(apiVersion));
    }

    [Fact]
    public void ApiVersionsAreLimitedTo64Characters()
    {
        Assert.True(ArmClient.IsValidApiVersion(new string('1', 64)));
        Assert.False(ArmClient.IsValidApiVersion(new string('1', 65)));
    }

    public static TheoryData<string, string> RefusedRequests => new()
    {
        { "subscriptions/s1", ApiVersion },
        { "//evil.example/x", ApiVersion },
        { "/http://evil.example/x", ApiVersion },
        { "/subscriptions/s1?x=1", ApiVersion },
        { "/subscriptions/s1#x", ApiVersion },
        { "/subscriptions\\s1", ApiVersion },
        { "/subscriptions/s%2F1", ApiVersion },
        { "/subscriptions/s 1", ApiVersion },
        { "/subscriptions/é", ApiVersion },
        { "/subscriptions/./s1", ApiVersion },
        { "/subscriptions/../s1", ApiVersion },
        { "/subscriptions//s1", ApiVersion },
        { "/" + new string('a', ArmClient.MaxResourcePathLength), ApiVersion },
        { PolicyPath, "2020-10-01&evil=1" },
        { PolicyPath, "" },
    };

    [Theory]
    [MemberData(nameof(RefusedRequests))]
    public async Task InvalidRequestsNeverReachTheTokenProviderOrTheNetwork(string path, string apiVersion)
    {
        var handler = new ScriptedHttpHandler();
        using var harness = new Harness(handler);

        Assert.Throws<ArgumentException>(() => harness.Client.BuildUri(path, apiVersion));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Client.GetAsync(path, apiVersion, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Client.PatchAsync(path, apiVersion, new JsonObject { ["a"] = 1 }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await DrainOrThrowAsync(harness.Client.ListAsync(path, apiVersion, cancellationToken: Ct)));

        Assert.Empty(handler.Requests);
        Assert.Empty(harness.Tokens.Scopes);
    }

    private static async Task DrainOrThrowAsync(IAsyncEnumerable<JsonObject> source)
    {
        await foreach (var unused in source)
        {
            _ = unused;
        }
    }

    [Fact]
    public void ARefusedPathIsNotEchoedInFull()
    {
        using var harness = new Harness(new ScriptedHttpHandler());
        var path = "/subscriptions/" + new string('a', 100) + "/x y";

        var error = Assert.Throws<ArgumentException>(() => harness.Client.BuildUri(path, ApiVersion));

        Assert.DoesNotContain(path, error.Message, StringComparison.Ordinal);
        Assert.Contains("...", error.Message, StringComparison.Ordinal);
    }

    // ---- GET -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AResourceIsReadWithAResourceManagerScopedBearerTokenAndTheArmHeaders()
    {
        var handler = new ScriptedHttpHandler(ScriptedResponse.Json("{\"id\":\"" + PolicyPath + "\",\"properties\":{\"rules\":[]}}"));
        using var harness = new Harness(handler);

        var resource = await harness.Client.GetAsync(PolicyPath, ApiVersion, Ct);

        Assert.NotNull(resource);
        Assert.Equal(PolicyPath, resource["id"]!.GetValue<string>());
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(Origin + PolicyPath + "?api-version=" + ApiVersion, request.Uri.AbsoluteUri);
        Assert.Equal("Bearer token-1", request.Header("Authorization"));
        Assert.Equal("application/json", request.Header("Accept"));
        Assert.Equal("EARM/9.9.9-test", request.Header("User-Agent"));
        Assert.True(Guid.TryParse(request.Header("x-ms-client-request-id"), out _));
        Assert.Null(request.Body);
        Assert.Equal(["https://management.azure.com/.default"], harness.Tokens.Scopes);
    }

    [Fact]
    public async Task EachRequestCarriesItsOwnClientRequestId()
    {
        var handler = new ScriptedHttpHandler(ScriptedResponse.Json("{}"), ScriptedResponse.Json("{}"));
        using var harness = new Harness(handler);

        await harness.Client.GetAsync(PolicyPath, ApiVersion, Ct);
        await harness.Client.GetAsync(PolicyPath, ApiVersion, Ct);

        Assert.NotEqual(handler.Requests[0].Header("x-ms-client-request-id"), handler.Requests[1].Header("x-ms-client-request-id"));
    }

    [Theory]
    [InlineData("usgov", "https://management.usgovcloudapi.net/.default", "management.usgovcloudapi.net")]
    [InlineData("usgovdod", "https://management.usgovcloudapi.net/.default", "management.usgovcloudapi.net")]
    [InlineData("china", "https://management.chinacloudapi.cn/.default", "management.chinacloudapi.cn")]
    public async Task SovereignCloudsAskForTheirOwnResourceManagerScope(string cloud, string scope, string host)
    {
        var handler = new ScriptedHttpHandler(ScriptedResponse.Json("{}"));
        using var harness = new Harness(handler, GraphCloud.Parse(cloud));

        await harness.Client.GetAsync(PolicyPath, ApiVersion, Ct);

        Assert.Equal([scope], harness.Tokens.Scopes);
        Assert.Equal(host, Assert.Single(handler.Requests).Uri.Host);
    }

    [Fact]
    public async Task AnEmptySuccessfulAnswerIsNull()
    {
        var handler = new ScriptedHttpHandler(ScriptedResponse.NoContent(), ScriptedResponse.Json(string.Empty), ScriptedResponse.Json("  \r\n "));
        using var harness = new Harness(handler);

        Assert.Null(await harness.Client.GetAsync(PolicyPath, ApiVersion, Ct));
        Assert.Null(await harness.Client.GetAsync(PolicyPath, ApiVersion, Ct));
        Assert.Null(await harness.Client.GetAsync(PolicyPath, ApiVersion, Ct));
    }

    [Theory]
    [InlineData("<html>Sup3r-Secret-Body</html>")]
    [InlineData("{\"Sup3r-Secret-Body\":")]
    [InlineData("[\"Sup3r-Secret-Body\"]")]
    [InlineData("\"Sup3r-Secret-Body\"")]
    [InlineData("12345")]
    public async Task AnAnswerThatIsNotAJsonObjectIsInvalidAndEchoesNothing(string body)
    {
        var handler = new ScriptedHttpHandler(ScriptedResponse.Json(body));
        using var harness = new Harness(handler);

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => harness.Client.GetAsync(PolicyPath, ApiVersion, Ct));

        Assert.Equal(GraphTransportFailure.InvalidResponse, error.Failure);
        Assert.DoesNotContain(body, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Sup3r-Secret-Body", error.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    // ---- errors and retries --------------------------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "AuthorizationFailed")]
    [InlineData(HttpStatusCode.NotFound, "ResourceNotFound")]
    [InlineData(HttpStatusCode.BadRequest, "InvalidRequestContent")]
    public async Task ArmErrorsSurfaceAsGraphExceptionsWithTheirCodeMessageAndRequestId(HttpStatusCode status, string code)
    {
        var handler = new ScriptedHttpHandler(new ScriptedResponse { Status = status, Body = ArmError(code, "The client does not have authorization.") }.WithHeader("x-ms-request-id", "arm-req-42"));
        using var harness = new Harness(handler);

        var error = await Assert.ThrowsAsync<GraphException>(() => harness.Client.GetAsync(PolicyPath, ApiVersion, Ct));

        Assert.Equal(status, error.Status);
        Assert.Equal(code, error.Code);
        Assert.Equal("The client does not have authorization.", error.GraphMessage);
        Assert.Equal("arm-req-42", error.RequestId);
        Assert.Equal(Assert.Single(handler.Requests).Header("x-ms-client-request-id"), error.ClientRequestId);
        Assert.False(error.IsAmbiguous);
    }

    [Fact]
    public async Task ThrottlingIsRetriedHonouringRetryAfter()
    {
        var handler = new ScriptedHttpHandler(
            new ScriptedResponse { Status = HttpStatusCode.TooManyRequests, Body = ArmError("TooManyRequests", "slow down") }.WithHeader("Retry-After", "7"),
            ScriptedResponse.Json("{\"id\":\"ok\"}"));
        using var harness = new Harness(handler);

        var resource = await harness.Client.GetAsync(PolicyPath, ApiVersion, Ct);

        Assert.Equal("ok", resource!["id"]!.GetValue<string>());
        Assert.Equal([TimeSpan.FromSeconds(7)], harness.Delays.Delays);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task AServerErrorToAGetIsRetried()
    {
        var handler = new ScriptedHttpHandler(
            new ScriptedResponse { Status = HttpStatusCode.ServiceUnavailable, Body = ArmError("ServiceUnavailable", "busy") },
            new ScriptedResponse { Status = HttpStatusCode.InternalServerError, Body = ArmError("InternalServerError", "oops") },
            ScriptedResponse.Json("{\"id\":\"ok\"}"));
        using var harness = new Harness(handler);

        var resource = await harness.Client.GetAsync(PolicyPath, ApiVersion, Ct);

        Assert.Equal("ok", resource!["id"]!.GetValue<string>());
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(2, harness.Delays.Delays.Count);
    }

    [Fact]
    public async Task AGetWhoseServerErrorsPersistIsNotAmbiguous()
    {
        var handler = new ScriptedHttpHandler();
        handler.Fallback = _ => new ScriptedResponse { Status = HttpStatusCode.ServiceUnavailable, Body = ArmError("ServiceUnavailable", "busy") };
        using var harness = new Harness(handler);

        var error = await Assert.ThrowsAsync<GraphException>(() => harness.Client.GetAsync(PolicyPath, ApiVersion, Ct));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.Status);
        Assert.False(error.IsAmbiguous);
        Assert.Equal(new RetryOptions().ServerErrorMaxRetries + 1, handler.Requests.Count);
    }

    [Fact]
    public async Task AnExpiredTokenIsInvalidatedForTheArmScopeAndRetriedOnce()
    {
        var handler = new ScriptedHttpHandler(
            new ScriptedResponse { Status = HttpStatusCode.Unauthorized, Body = ArmError("ExpiredAuthenticationToken", "expired") },
            ScriptedResponse.Json("{\"id\":\"ok\"}"));
        using var harness = new Harness(handler);

        var resource = await harness.Client.GetAsync(PolicyPath, ApiVersion, Ct);

        Assert.Equal("ok", resource!["id"]!.GetValue<string>());
        Assert.Equal(1, harness.Tokens.Invalidations);
        Assert.Equal("Bearer token-1", handler.Requests[0].Header("Authorization"));
        Assert.Equal("Bearer token-2", handler.Requests[1].Header("Authorization"));
        Assert.Equal(["https://management.azure.com/.default", "https://management.azure.com/.default"], harness.Tokens.Scopes);
    }

    [Fact]
    public async Task ASecond401IsTerminal()
    {
        var handler = new ScriptedHttpHandler();
        handler.Fallback = _ => new ScriptedResponse { Status = HttpStatusCode.Unauthorized, Body = ArmError("InvalidAuthenticationToken", "bad") };
        using var harness = new Harness(handler);

        var error = await Assert.ThrowsAsync<GraphException>(() => harness.Client.GetAsync(PolicyPath, ApiVersion, Ct));

        Assert.Equal(HttpStatusCode.Unauthorized, error.Status);
        Assert.Equal(1, harness.Tokens.Invalidations);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    public async Task ARedirectIsAnErrorAndIsNotFollowed(HttpStatusCode status)
    {
        var handler = new ScriptedHttpHandler(new ScriptedResponse { Status = status }.WithHeader("Location", "https://evil.example/steal"));
        using var harness = new Harness(handler);

        var error = await Assert.ThrowsAsync<GraphException>(() => harness.Client.GetAsync(PolicyPath, ApiVersion, Ct));

        Assert.Equal("UnexpectedRedirect", error.Code);
        Assert.Equal(status, error.Status);
        Assert.DoesNotContain("evil.example", error.Message, StringComparison.Ordinal);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("management.azure.com", request.Uri.Host);
    }

    [Fact]
    public async Task AFailingTokenProviderStopsBeforeAnythingIsSent()
    {
        var handler = new ScriptedHttpHandler();
        using var harness = new Harness(handler);
        harness.Tokens.Failure = new GraphTransportException(GraphTransportFailure.NotAuthenticated, "Not signed in.");

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => harness.Client.GetAsync(PolicyPath, ApiVersion, Ct));

        Assert.True(error.IsNotAuthenticated);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AResponseOverTheLimitIsRefused()
    {
        var handler = new ScriptedHttpHandler(ScriptedResponse.Json("\"" + new string('x', (int)ArmClient.MaxResponseBytes) + "\""));
        using var harness = new Harness(handler);

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => harness.Client.GetAsync(PolicyPath, ApiVersion, Ct));

        Assert.Equal(GraphTransportFailure.ResponseTooLarge, error.Failure);
        Assert.False(error.Ambiguous);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AResponseOverTheLimitToAPatchIsAmbiguous()
    {
        var handler = new ScriptedHttpHandler(ScriptedResponse.Json("\"" + new string('x', (int)ArmClient.MaxResponseBytes) + "\""));
        using var harness = new Harness(handler);

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => harness.Client.PatchAsync(PolicyPath, ApiVersion, new JsonObject { ["a"] = 1 }, Ct));

        Assert.Equal(GraphTransportFailure.ResponseTooLarge, error.Failure);
        Assert.True(error.Ambiguous);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task APatchBodyOverTheLimitIsRefusedBeforeAnythingIsSent()
    {
        var handler = new ScriptedHttpHandler();
        using var harness = new Harness(handler);
        var body = new JsonObject { ["a"] = new string('x', (int)ArmClient.MaxRequestBodyBytes) };

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => harness.Client.PatchAsync(PolicyPath, ApiVersion, body, Ct));

        Assert.Equal(GraphTransportFailure.RequestTooLarge, error.Failure);
        Assert.False(error.Ambiguous);
        Assert.Empty(handler.Requests);
        Assert.Empty(harness.Tokens.Scopes);
    }

    [Fact]
    public async Task NeitherTheTokenNorAnyBodyReachesTheLog()
    {
        const string secretOut = "Sup3r-S3cret-Request-Value";
        const string secretIn = "Sup3r-S3cret-Response-Value";
        var handler = new ScriptedHttpHandler(
            new ScriptedResponse { Status = HttpStatusCode.ServiceUnavailable, Body = "{\"error\":{\"code\":\"Busy\",\"message\":\"busy\"},\"extra\":\"" + secretIn + "\"}" },
            ScriptedResponse.Json("{\"value\":\"" + secretIn + "\"}"),
            ScriptedResponse.Json("{\"value\":[{\"id\":\"" + secretIn + "\"}]}"));
        using var harness = new Harness(handler);

        await harness.Client.PatchAsync(PolicyPath, ApiVersion, new JsonObject { ["secret"] = secretOut }, Ct);
        await DrainOrThrowAsync(harness.Client.ListAsync(ListPath, ApiVersion, cancellationToken: Ct));

        Assert.Contains(secretOut, handler.Requests[0].Body!, StringComparison.Ordinal);
        Assert.NotEmpty(harness.Log.Entries);
        Assert.DoesNotContain(secretOut, harness.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(secretIn, harness.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("token-1", harness.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", harness.Log.AllText, StringComparison.Ordinal);
    }

    // ---- PATCH ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task APatchSendsTheBodyAsJsonAndReturnsTheAnswer()
    {
        var handler = new ScriptedHttpHandler(ScriptedResponse.Json("{\"id\":\"" + PolicyPath + "\",\"properties\":{\"enabled\":true}}"));
        using var harness = new Harness(handler);
        var body = new JsonObject { ["properties"] = new JsonObject { ["enabled"] = true, ["note"] = "café <b>" } };

        var answer = await harness.Client.PatchAsync(PolicyPath, ApiVersion, body, Ct);

        Assert.True(answer!["properties"]!["enabled"]!.GetValue<bool>());
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal(Origin + PolicyPath + "?api-version=" + ApiVersion, request.Uri.AbsoluteUri);
        Assert.Equal("{\"properties\":{\"enabled\":true,\"note\":\"café <b>\"}}", request.Body);
        Assert.StartsWith("application/json", request.Header("Content-Type"), StringComparison.Ordinal);
        Assert.Equal("Bearer token-1", request.Header("Authorization"));
        Assert.Equal(["https://management.azure.com/.default"], harness.Tokens.Scopes);
    }

    [Fact]
    public async Task APatchWithAnEmptyAnswerReturnsNull()
    {
        var handler = new ScriptedHttpHandler(ScriptedResponse.NoContent());
        using var harness = new Harness(handler);

        Assert.Null(await harness.Client.PatchAsync(PolicyPath, ApiVersion, new JsonObject(), Ct));
    }

    [Fact]
    public async Task APatchCancelledBeforeItIsSentSendsNothing()
    {
        var handler = new ScriptedHttpHandler(ScriptedResponse.Json("{}"));
        using var harness = new Harness(handler);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Client.PatchAsync(PolicyPath, ApiVersion, new JsonObject { ["a"] = 1 }, cts.Token));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task APatchInFlightIsNotAbandonedWhenTheCallerCancels()
    {
        var handler = new GatedHandler();
        using var harness = new Harness(handler);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pending = harness.Client.PatchAsync(PolicyPath, ApiVersion, new JsonObject { ["a"] = 1 }, cts.Token);

        await handler.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cts.CancelAsync();
        handler.Release.SetResult();
        var answer = await pending.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal("applied", answer!["id"]!.GetValue<string>());
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task AGetInFlightIsCancelledWhenTheCallerCancels()
    {
        var handler = new GatedHandler();
        using var harness = new Harness(handler);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pending = harness.Client.GetAsync(PolicyPath, ApiVersion, cts.Token);

        await handler.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Equal(1, handler.Requests);
        Assert.Empty(harness.Delays.Delays);
    }

    [Fact]
    public async Task APatchThatTimesOutIsAnAmbiguousTimeout()
    {
        var handler = new ScriptedHttpHandler();
        handler.Fallback = _ => new ScriptedResponse { Delay = TimeSpan.FromSeconds(30) };
        using var harness = new Harness(handler, timeout: TimeSpan.FromMilliseconds(50));

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => harness.Client.PatchAsync(PolicyPath, ApiVersion, new JsonObject { ["a"] = 1 }, Ct));

        Assert.Equal(GraphTransportFailure.Timeout, error.Failure);
        Assert.True(error.Ambiguous);
        Assert.Contains("did not answer within", error.Message, StringComparison.Ordinal);
        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, static r => Assert.Equal(HttpMethod.Patch, r.Method));
    }

    [Fact]
    public async Task AGetThatTimesOutIsATimeoutThatIsNotAmbiguous()
    {
        var handler = new ScriptedHttpHandler();
        handler.Fallback = _ => new ScriptedResponse { Delay = TimeSpan.FromSeconds(30) };
        using var harness = new Harness(handler, timeout: TimeSpan.FromMilliseconds(50));

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => harness.Client.GetAsync(PolicyPath, ApiVersion, Ct));

        Assert.Equal(GraphTransportFailure.Timeout, error.Failure);
        Assert.False(error.Ambiguous);
        Assert.Equal(new RetryOptions().TransportFailureMaxAttempts, handler.Requests.Count);
    }

    [Fact]
    public async Task AServerErrorToAPatchIsMarkedAmbiguous()
    {
        var handler = new ScriptedHttpHandler();
        handler.Fallback = _ => new ScriptedResponse { Status = HttpStatusCode.InternalServerError, Body = ArmError("InternalServerError", "oops") };
        using var harness = new Harness(handler);

        var error = await Assert.ThrowsAsync<GraphException>(() => harness.Client.PatchAsync(PolicyPath, ApiVersion, new JsonObject { ["a"] = 1 }, Ct));

        Assert.Equal(HttpStatusCode.InternalServerError, error.Status);
        Assert.True(error.IsAmbiguous);
        Assert.All(handler.Requests, static r => Assert.Equal(HttpMethod.Patch, r.Method));
    }

    [Fact]
    public async Task AClientErrorToAPatchIsNotAmbiguous()
    {
        var handler = new ScriptedHttpHandler(new ScriptedResponse { Status = HttpStatusCode.BadRequest, Body = ArmError("InvalidRequestContent", "bad") });
        using var harness = new Harness(handler);

        var error = await Assert.ThrowsAsync<GraphException>(() => harness.Client.PatchAsync(PolicyPath, ApiVersion, new JsonObject { ["a"] = 1 }, Ct));

        Assert.False(error.IsAmbiguous);
        Assert.Single(handler.Requests);
    }

    // ---- collections ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACollectionYieldsEveryValueAcrossPagesFollowingNextLink()
    {
        const string next = Origin + ListPath + "?api-version=" + ApiVersion + "&$skiptoken=abc";
        var handler = new ScriptedHttpHandler(
            ScriptedResponse.Json("{\"value\":[{\"id\":\"a\"},{\"id\":\"b\"}],\"nextLink\":\"" + next + "\"}"),
            ScriptedResponse.Json("{\"value\":[null,{\"id\":\"c\"}],\"nextLink\":null}"));
        using var harness = new Harness(handler);

        var (items, error) = await DrainAsync(harness.Client.ListAsync(ListPath, ApiVersion, "atScope()", Ct));

        Assert.Null(error);
        Assert.Equal(["a", "b", "c"], Ids(items));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("atScope()", handler.Requests[0].QueryValue("$filter"));
        Assert.Equal(ApiVersion, handler.Requests[0].QueryValue("api-version"));
        Assert.Equal(next, handler.Requests[1].Uri.AbsoluteUri);
        Assert.All(handler.Requests, static r => Assert.Equal("Bearer token-1", r.Header("Authorization")));
    }

    [Fact]
    public async Task AnEmptyNextLinkEndsTheCollection()
    {
        var handler = new ScriptedHttpHandler(ScriptedResponse.Json("{\"value\":[{\"id\":\"a\"}],\"nextLink\":\"\"}"));
        using var harness = new Harness(handler);

        var (items, error) = await DrainAsync(harness.Client.ListAsync(ListPath, ApiVersion, cancellationToken: Ct));

        Assert.Null(error);
        Assert.Equal(["a"], Ids(items));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("https://evil.example" + ListPath + "?api-version=2020-10-01")]
    [InlineData("https://management.azure.com.evil.example" + ListPath)]
    [InlineData("https://management.usgovcloudapi.net" + ListPath)]
    [InlineData("http://management.azure.com" + ListPath)]
    [InlineData("https://user:pass@management.azure.com" + ListPath)]
    [InlineData("https://management.azure.com:8443" + ListPath)]
    [InlineData("https://management.azure.com" + ListPath + "?api-version=2020-10-01#frag")]
    [InlineData("https://management.azure.com/subscriptions/s1/../s2/providers/x")]
    [InlineData("https://management.azure.com/subscriptions/s1/%2e%2e/s2/providers/x")]
    [InlineData("https://127.0.0.1" + ListPath)]
    [InlineData("ftp://management.azure.com" + ListPath)]
    public async Task ANextLinkOffTheResourceManagerOriginIsRefusedAfterTheFirstPage(string nextLink)
    {
        var handler = new ScriptedHttpHandler(ScriptedResponse.Json("{\"value\":[{\"id\":\"a\"}],\"nextLink\":\"" + nextLink + "\"}"));
        handler.Fallback = _ => ScriptedResponse.Json("{\"value\":[{\"id\":\"leaked\"}]}");
        using var harness = new Harness(handler);

        var (items, error) = await DrainAsync(harness.Client.ListAsync(ListPath, ApiVersion, cancellationToken: Ct));

        var transport = Assert.IsType<GraphTransportException>(error);
        Assert.Equal(GraphTransportFailure.UnsafeUrl, transport.Failure);
        Assert.DoesNotContain("pass", transport.Message, StringComparison.Ordinal);
        Assert.Equal(["a"], Ids(items));
        Assert.Single(handler.Requests);
        Assert.Single(harness.Tokens.Scopes);
    }

    [Fact]
    public async Task ANextLinkThatWasAlreadyFollowedStopsTheCollection()
    {
        const string second = Origin + ListPath + "?api-version=" + ApiVersion + "&$skiptoken=2";
        var handler = new ScriptedHttpHandler(
            ScriptedResponse.Json("{\"value\":[{\"id\":\"a\"}],\"nextLink\":\"" + second + "\"}"),
            ScriptedResponse.Json("{\"value\":[{\"id\":\"b\"}],\"nextLink\":\"" + Origin + ListPath + "?api-version=" + ApiVersion + "\"}"));
        handler.Fallback = _ => ScriptedResponse.Json("{\"value\":[{\"id\":\"again\"}]}");
        using var harness = new Harness(handler);

        var (items, error) = await DrainAsync(harness.Client.ListAsync(ListPath, ApiVersion, cancellationToken: Ct));

        var transport = Assert.IsType<GraphTransportException>(error);
        Assert.Equal(GraphTransportFailure.InvalidResponse, transport.Failure);
        Assert.Contains("already followed", transport.Message, StringComparison.Ordinal);
        Assert.Equal(["a", "b"], Ids(items));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("{\"id\":\"x\"}")]
    [InlineData("{\"value\":{\"id\":\"x\"}}")]
    [InlineData("{\"value\":null}")]
    [InlineData("")]
    public async Task APageWithoutAValueArrayIsInvalid(string body)
    {
        var handler = new ScriptedHttpHandler(ScriptedResponse.Json(body));
        using var harness = new Harness(handler);

        var (items, error) = await DrainAsync(harness.Client.ListAsync(ListPath, ApiVersion, cancellationToken: Ct));

        var transport = Assert.IsType<GraphTransportException>(error);
        Assert.Equal(GraphTransportFailure.InvalidResponse, transport.Failure);
        Assert.Contains("'value' array", transport.Message, StringComparison.Ordinal);
        Assert.Empty(items);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("\"x\"")]
    [InlineData("[]")]
    public async Task AnItemThatIsNotAnObjectStopsTheCollection(string item)
    {
        var handler = new ScriptedHttpHandler(ScriptedResponse.Json("{\"value\":[{\"id\":\"a\"}," + item + ",{\"id\":\"b\"}]}"));
        using var harness = new Harness(handler);

        var (items, error) = await DrainAsync(harness.Client.ListAsync(ListPath, ApiVersion, cancellationToken: Ct));

        var transport = Assert.IsType<GraphTransportException>(error);
        Assert.Equal(GraphTransportFailure.InvalidResponse, transport.Failure);
        Assert.Contains("not a JSON object", transport.Message, StringComparison.Ordinal);
        Assert.Equal(["a"], Ids(items));
    }

    [Fact]
    public async Task AnErrorOnALaterPageSurfacesAfterTheEarlierItems()
    {
        const string next = Origin + ListPath + "?api-version=" + ApiVersion + "&$skiptoken=2";
        var handler = new ScriptedHttpHandler(
            ScriptedResponse.Json("{\"value\":[{\"id\":\"a\"}],\"nextLink\":\"" + next + "\"}"),
            new ScriptedResponse { Status = HttpStatusCode.Forbidden, Body = ArmError("AuthorizationFailed", "no") });
        using var harness = new Harness(handler);

        var (items, error) = await DrainAsync(harness.Client.ListAsync(ListPath, ApiVersion, cancellationToken: Ct));

        Assert.Equal("AuthorizationFailed", Assert.IsType<GraphException>(error).Code);
        Assert.Equal(["a"], Ids(items));
    }
}
