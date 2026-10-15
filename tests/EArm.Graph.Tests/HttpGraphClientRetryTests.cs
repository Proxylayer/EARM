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

/// <summary>Retry behaviour of <see cref="HttpGraphClient"/> over real statuses and headers (delays are recorded, never waited).</summary>
public sealed class HttpGraphClientRetryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ScriptedResponse Ok()
    {
        return ScriptedResponse.Json("{\"value\":[]}");
    }

    private static ScriptedResponse Throttled(string? retryAfter)
    {
        var response = ScriptedResponse.Error(HttpStatusCode.TooManyRequests, "TooManyRequests", "Too many requests.");
        return retryAfter is null ? response : response.WithHeader("Retry-After", retryAfter);
    }

    [Theory]
    [InlineData("3", 3)]
    [InlineData("0", 2)]
    [InlineData("120", 120)]
    [InlineData("99999", 120)]
    [InlineData(null, 2)]
    [InlineData("garbage", 2)]
    public async Task ThrottlingHonoursARetryAfterInSeconds(string? header, double expectedSeconds)
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(Throttled(header), Ok()));

        await harness.Client.GetAsync(new GraphRequest("/users"), Ct);

        Assert.Equal(2, harness.Handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(expectedSeconds)], harness.Delays.Delays);
    }

    [Fact]
    public async Task ThrottlingHonoursARetryAfterHttpDate()
    {
        var when = harnessNow().AddSeconds(42).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        using var harness = new ClientHarness(new ScriptedHttpHandler(Throttled(when), Ok()));

        await harness.Client.GetAsync(new GraphRequest("/users"), Ct);

        Assert.Equal([TimeSpan.FromSeconds(42)], harness.Delays.Delays);

        static DateTimeOffset harnessNow()
        {
            return new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        }
    }

    [Fact]
    public async Task APastRetryAfterDateRetriesAtOnceWithTheBackoffFloorOfZero()
    {
        var past = new DateTimeOffset(2026, 10, 14, 11, 0, 0, TimeSpan.Zero).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        using var harness = new ClientHarness(new ScriptedHttpHandler(Throttled(past), Ok()));

        await harness.Client.GetAsync(new GraphRequest("/users"), Ct);

        Assert.Equal([TimeSpan.FromSeconds(2)], harness.Delays.Delays);
    }

    [Fact]
    public async Task ThrottlingBacksOffExponentiallyAndGivesUpAfterEightRetries()
    {
        using var harness = new ClientHarness();
        harness.Handler.Fallback = _ => Throttled(null);

        var error = await Assert.ThrowsAsync<GraphException>(() => harness.Client.GetAsync(new GraphRequest("/users"), Ct));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.Status);
        Assert.Equal(9, harness.Handler.Requests.Count);
        Assert.Equal([2d, 4d, 8d, 16d, 32d, 60d, 60d, 60d], harness.Delays.Delays.Select(static d => d.TotalSeconds));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task ServerErrorsAreRetriedWithExponentialBackoff(HttpStatusCode status)
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(
            ScriptedResponse.Error(status, "UnknownError", "boom"),
            ScriptedResponse.Error(status, "UnknownError", "boom"),
            Ok()));

        await harness.Client.GetAsync(new GraphRequest("/users"), Ct);

        Assert.Equal(3, harness.Handler.Requests.Count);
        Assert.Equal([1d, 2d], harness.Delays.Delays.Select(static d => d.TotalSeconds));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task ARetryAfterOnA503Or504ReplacesTheBackoff(HttpStatusCode status)
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(
            ScriptedResponse.Error(status, "ServiceUnavailable", "later").WithHeader("Retry-After", "17"),
            Ok()));

        await harness.Client.GetAsync(new GraphRequest("/users"), Ct);

        Assert.Equal([TimeSpan.FromSeconds(17)], harness.Delays.Delays);
    }

    [Fact]
    public async Task ServerErrorsGiveUpAfterFiveRetries()
    {
        using var harness = new ClientHarness();
        harness.Handler.Fallback = _ => ScriptedResponse.Error(HttpStatusCode.BadGateway, "BadGateway", "bad");

        var error = await Assert.ThrowsAsync<GraphException>(() => harness.Client.GetAsync(new GraphRequest("/users"), Ct));

        Assert.Equal(HttpStatusCode.BadGateway, error.Status);
        Assert.Equal(6, harness.Handler.Requests.Count);
    }

    [Fact]
    public async Task A401RefreshesTheTokenOnceAndRetries()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(
            ScriptedResponse.Error(HttpStatusCode.Unauthorized, "InvalidAuthenticationToken", "Access token has expired."),
            Ok()));

        await harness.Client.GetAsync(new GraphRequest("/users"), Ct);

        Assert.Equal(1, harness.Tokens.Invalidations);
        Assert.Equal(["Bearer token-1", "Bearer token-2"], harness.Handler.Requests.Select(static r => r.Header("Authorization")));
        Assert.Empty(harness.Delays.Delays);
    }

    [Fact]
    public async Task ASecond401AfterTheRefreshIsTerminal()
    {
        using var harness = new ClientHarness();
        harness.Handler.Fallback = _ => ScriptedResponse.Error(HttpStatusCode.Unauthorized, "InvalidAuthenticationToken", "still bad");

        var error = await Assert.ThrowsAsync<GraphException>(() => harness.Client.GetAsync(new GraphRequest("/users"), Ct));

        Assert.True(error.IsUnauthorized);
        Assert.Equal(2, harness.Handler.Requests.Count);
        Assert.Equal(1, harness.Tokens.Invalidations);
    }

    [Fact]
    public async Task HintNoneNeverRetriesAndNeverRefreshes()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(
            ScriptedResponse.Error(HttpStatusCode.Unauthorized, "InvalidAuthenticationToken", "bad"),
            Ok()));

        await Assert.ThrowsAsync<GraphException>(() => harness.Client.GetAsync(new GraphRequest("/users").WithHint(RetryHint.None), Ct));

        Assert.Single(harness.Handler.Requests);
        Assert.Equal(0, harness.Tokens.Invalidations);
    }

    [Fact]
    public async Task AlreadyExistsIsNeverRetried()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.Error(HttpStatusCode.BadRequest, "Request_BadRequest", "Another object with the same value for property uniqueName already exists.")));

        var error = await Assert.ThrowsAsync<GraphException>(() => harness.Client.PostAsync(new GraphRequest("/groups").WithHint(RetryHint.ExpectReplicationLag), new JsonObject(), Ct));

        Assert.True(error.IsAlreadyExists);
        Assert.Single(harness.Handler.Requests);
    }

    [Fact]
    public async Task ReplicationLagRetriesEveryFiveSecondsAndSucceeds()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(
            ScriptedResponse.Error(HttpStatusCode.BadRequest, "Request_BadRequest", "The source resource object 'ServicePrincipal_1' don't exist."),
            ScriptedResponse.Error(HttpStatusCode.NotFound, "Request_ResourceNotFound", "Resource does not exist or one of its queried reference-property objects are not present."),
            ScriptedResponse.Json("{\"id\":\"ok\"}", HttpStatusCode.Created)));

        var response = await harness.Client.PostAsync(new GraphRequest("/servicePrincipals/s1/appRoleAssignedTo").WithHint(RetryHint.ExpectReplicationLag), new JsonObject(), Ct);

        Assert.True(response.IsCreated);
        Assert.Equal([5d, 5d], harness.Delays.Delays.Select(static d => d.TotalSeconds));
    }

    [Fact]
    public async Task NetworkFailuresAreRetriedThreeTimesInTotal()
    {
        using var harness = new ClientHarness();
        harness.Handler.Fallback = _ => new ScriptedResponse { Throw = new HttpRequestException("Name or service not known") };

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => harness.Client.GetAsync(new GraphRequest("/users"), Ct));

        Assert.Equal(GraphTransportFailure.Network, error.Failure);
        Assert.True(error.IsRetryable);
        Assert.Contains("graph.microsoft.com", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, harness.Handler.Requests.Count);
        Assert.Equal([1d, 2d], harness.Delays.Delays.Select(static d => d.TotalSeconds));
    }

    [Fact]
    public async Task ANetworkBlipIsRetriedAndRecovers()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(new ScriptedResponse { Throw = new HttpRequestException("reset") }, Ok()));

        await harness.Client.GetAsync(new GraphRequest("/users"), Ct);

        Assert.Equal(2, harness.Handler.Requests.Count);
    }

    [Fact]
    public async Task ACallThatExceedsTheTimeoutIsATimeoutFailureThatIsRetried()
    {
        using var harness = new ClientHarness(configure: options => options.Timeout = TimeSpan.FromMilliseconds(50));
        harness.Handler.Fallback = _ => new ScriptedResponse { Delay = TimeSpan.FromSeconds(30) };

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => harness.Client.GetAsync(new GraphRequest("/users"), Ct));

        Assert.Equal(GraphTransportFailure.Timeout, error.Failure);
        Assert.Contains("did not answer within", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, harness.Handler.Requests.Count);
    }

    [Fact]
    public async Task CallerCancellationIsNotATimeoutAndStopsAtOnce()
    {
        using var harness = new ClientHarness();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Handler.Fallback = _ =>
        {
            arrived.TrySetResult();
            return new ScriptedResponse { Delay = TimeSpan.FromSeconds(30) };
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pending = harness.Client.GetAsync(new GraphRequest("/users"), cts.Token);

        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Single(harness.Handler.Requests);
        Assert.Empty(harness.Delays.Delays);
    }

    [Fact]
    public async Task AFailingTokenProviderIsAuthenticationFailureThatIsNotRetried()
    {
        using var harness = new ClientHarness();
        harness.Tokens.Failure = new GraphTransportException(GraphTransportFailure.NotAuthenticated, "Not signed in.");

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => harness.Client.GetAsync(new GraphRequest("/users"), Ct));

        Assert.True(error.IsNotAuthenticated);
        Assert.Empty(harness.Handler.Requests);
        Assert.Single(harness.Tokens.Scopes);
    }

    [Fact]
    public async Task ConcurrencyIsBoundedByMaxParallel()
    {
        using var harness = new ClientHarness(configure: options => options.MaxParallel = 2);
        harness.Handler.Fallback = _ => new ScriptedResponse { Status = HttpStatusCode.OK, Body = "{\"value\":[]}", Delay = TimeSpan.FromMilliseconds(40) };

        var calls = Enumerable.Range(0, 10).Select(i => harness.Client.GetAsync(new GraphRequest("/users/u" + i), Ct)).ToArray();
        await Task.WhenAll(calls);

        Assert.Equal(10, harness.Handler.Requests.Count);
        Assert.InRange(harness.Handler.MaxConcurrency, 1, 2);
    }

    [Fact]
    [Trait("Category", "Timing")]
    public async Task TheDefaultAllowsFourConcurrentCalls()
    {
        using var harness = new ClientHarness();
        harness.Handler.Fallback = _ => new ScriptedResponse { Status = HttpStatusCode.OK, Body = "{\"value\":[]}", Delay = TimeSpan.FromMilliseconds(300) };

        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => harness.Client.GetAsync(new GraphRequest("/users/u" + i), Ct)));

        Assert.InRange(harness.Handler.MaxConcurrency, 2, 4);
    }
}
