using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph.Tests.Infrastructure;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary>
/// Review finding S-07 (unbounded response, paging and retry): the response size cap (also after decompression), the request size cap, the page and
/// item caps, the per-attempt Retry-After clamp and the wall-time budget of one logical call. Every limit has an exactly-at-the-limit and a one-over case.
/// </summary>
public sealed class Review1B_LimitsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string BodyOfLength(int length)
    {
        // {"a":"xxx"} is 8 characters plus the x's.
        return "{\"a\":\"" + new string('x', length - 8) + "\"}";
    }

    // ---- defaults ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheDefaultLimitsAreTheDocumentedOnes()
    {
        var options = new GraphOptions();
        Assert.Equal(16L * 1024 * 1024, options.MaxResponseBytes);
        Assert.Equal(4L * 1024 * 1024, options.MaxRequestBodyBytes);
        Assert.Equal(2_000, options.MaxPages);
        Assert.Equal(500_000, options.MaxItems);
        Assert.Equal(TimeSpan.FromSeconds(120), options.Timeout);
        Assert.Equal(4, options.MaxParallel);

        var retry = new RetryOptions();
        Assert.Equal(TimeSpan.FromSeconds(120), retry.MaxRetryAfter);
        Assert.Equal(TimeSpan.FromMinutes(10), retry.MaxTotalTime);
    }

    // ---- response size -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1000, true)]
    [InlineData(999, false)]
    public async Task AResponseExactlyAtTheLimitIsAcceptedAndOneByteOverIsRefused(int limit, bool accepted)
    {
        using var rig = new Review1BRig(new ScriptedHttpHandler(ScriptedResponse.Json(BodyOfLength(1000))), configure: o => o.MaxResponseBytes = limit);

        if (accepted)
        {
            var node = await rig.Client.GetAsync(new GraphRequest("/users"), Ct);
            Assert.NotNull(node);
            return;
        }

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => rig.Client.GetAsync(new GraphRequest("/users"), Ct));
        Assert.Equal(GraphTransportFailure.ResponseTooLarge, error.Failure);
        Assert.False(error.IsRetryable);
        Assert.False(error.Ambiguous);
        Assert.Contains("999", error.Message, StringComparison.Ordinal);
        Assert.Single(rig.Scripted!.Requests);
    }

    [Fact]
    public async Task AnErrorResponseOverTheLimitIsAlsoRefused()
    {
        using var rig = new Review1BRig(
            new ScriptedHttpHandler(new ScriptedResponse { Status = HttpStatusCode.InternalServerError, Body = BodyOfLength(5000) }),
            configure: o => o.MaxResponseBytes = 1000);

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => rig.Client.GetAsync(new GraphRequest("/users"), Ct));

        Assert.Equal(GraphTransportFailure.ResponseTooLarge, error.Failure);
    }

    [Fact]
    public async Task ABodyWithoutAContentLengthIsCutOffWhileItStreams()
    {
        using var http = new HttpClient(new EndlessBodyHandler(), disposeHandler: true);
        using var rig = new Review1BRig(http: http, configure: o => o.MaxResponseBytes = 64 * 1024);

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => rig.Client.GetAsync(new GraphRequest("/users"), Ct));

        Assert.Equal(GraphTransportFailure.ResponseTooLarge, error.Failure);
    }

    [Fact]
    public async Task AnOversizedResponseToAWriteIsAmbiguous()
    {
        using var rig = new Review1BRig(
            new ScriptedHttpHandler(ScriptedResponse.Json(BodyOfLength(5000), HttpStatusCode.Created)),
            configure: o => o.MaxResponseBytes = 1000);

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => rig.Client.PostAsync(new GraphRequest("/groups"), new JsonObject(), Ct));

        Assert.Equal(GraphTransportFailure.ResponseTooLarge, error.Failure);
        Assert.True(error.Ambiguous);
        Assert.Single(rig.Scripted!.Requests);
    }

    // ---- request size --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(28, true)]
    [InlineData(27, false)]
    public async Task TheRequestBodyLimitCountsUtf8BytesNotCharacters(int limit, bool accepted)
    {
        // {"a":"éééééééééé"} = 8 + 10 * 2 = 28 bytes (18 characters).
        var body = new JsonObject { ["a"] = new string('é', 10) };
        using var rig = new Review1BRig(new ScriptedHttpHandler(ScriptedResponse.Json("{}")), configure: o => o.MaxRequestBodyBytes = limit);

        if (accepted)
        {
            await rig.Client.PostAsync(new GraphRequest("/groups"), body, Ct);
            Assert.Single(rig.Scripted!.Requests);
            return;
        }

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => rig.Client.PostAsync(new GraphRequest("/groups"), body, Ct));
        Assert.Equal(GraphTransportFailure.RequestTooLarge, error.Failure);
        Assert.False(error.Ambiguous);
        Assert.Empty(rig.Scripted!.Requests);
        Assert.Empty(rig.Tokens.Scopes);
    }

    // ---- paging --------------------------------------------------------------------------------------------------

    private static string Page(int number, int items, bool hasNext)
    {
        var values = string.Join(',', Enumerable.Range(0, items).Select(i => "{\"id\":\"" + number + "-" + i + "\"}"));
        var next = hasNext ? ",\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/users?$skiptoken=P" + (number + 1) + "\"" : string.Empty;
        return "{\"value\":[" + values + "]" + next + "}";
    }

    private static async Task<(List<JsonNode> Items, Exception? Failure)> Drain(IAsyncEnumerable<JsonNode> pages)
    {
        var items = new List<JsonNode>();
        try
        {
            await foreach (var item in pages.WithCancellation(Ct))
            {
                items.Add(item);
            }

            return (items, null);
        }
        catch (GraphTransportException ex)
        {
            return (items, ex);
        }
    }

    [Fact]
    public async Task ExactlyMaxPagesPagesAreFollowed()
    {
        var number = 0;
        using var rig = new Review1BRig(new ScriptedHttpHandler { Fallback = _ => ScriptedResponse.Json(Page(++number, 2, hasNext: number < 3)) }, configure: o => o.MaxPages = 3);

        var (items, failure) = await Drain(rig.Client.GetAllPagesAsync(new GraphRequest("/users"), Ct));

        Assert.Null(failure);
        Assert.Equal(6, items.Count);
        Assert.Equal(3, rig.Scripted!.Requests.Count);
    }

    [Fact]
    public async Task OnePageBeyondMaxPagesIsAClearError()
    {
        var number = 0;
        using var rig = new Review1BRig(new ScriptedHttpHandler { Fallback = _ => ScriptedResponse.Json(Page(++number, 2, hasNext: true)) }, configure: o => o.MaxPages = 3);

        var (items, failure) = await Drain(rig.Client.GetAllPagesAsync(new GraphRequest("/users"), Ct));

        var error = Assert.IsType<GraphTransportException>(failure);
        Assert.Equal(GraphTransportFailure.PagingLimitExceeded, error.Failure);
        Assert.Contains("more than 3 pages", error.Message, StringComparison.Ordinal);
        Assert.Equal(6, items.Count);
        Assert.Equal(3, rig.Scripted!.Requests.Count);
    }

    [Fact]
    public async Task ExactlyMaxItemsItemsAreYielded()
    {
        var number = 0;
        using var rig = new Review1BRig(new ScriptedHttpHandler { Fallback = _ => ScriptedResponse.Json(Page(++number, 2, hasNext: number < 2)) }, configure: o => o.MaxItems = 4);

        var (items, failure) = await Drain(rig.Client.GetAllPagesAsync(new GraphRequest("/users"), Ct));

        Assert.Null(failure);
        Assert.Equal(4, items.Count);
    }

    [Fact]
    public async Task OneItemBeyondMaxItemsIsAClearError()
    {
        var number = 0;
        using var rig = new Review1BRig(new ScriptedHttpHandler { Fallback = _ => ScriptedResponse.Json(Page(++number, 3, hasNext: true)) }, configure: o => o.MaxItems = 5);

        var (items, failure) = await Drain(rig.Client.GetAllPagesAsync(new GraphRequest("/users"), Ct));

        var error = Assert.IsType<GraphTransportException>(failure);
        Assert.Equal(GraphTransportFailure.PagingLimitExceeded, error.Failure);
        Assert.Contains("more than 5 items", error.Message, StringComparison.Ordinal);
        Assert.Equal(5, items.Count);
        Assert.Equal(2, rig.Scripted!.Requests.Count);
    }

    [Fact]
    public async Task ARepeatedNextLinkIsStillCaughtBeforeAnyCap()
    {
        using var rig = new Review1BRig(new ScriptedHttpHandler { Fallback = _ => ScriptedResponse.Json("{\"value\":[{\"id\":\"1\"}],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/users?$skiptoken=SAME\"}") });

        var (_, failure) = await Drain(rig.Client.GetAllPagesAsync(new GraphRequest("/users"), Ct));

        var error = Assert.IsType<GraphTransportException>(failure);
        Assert.Equal(GraphTransportFailure.InvalidResponse, error.Failure);
        Assert.Contains("already followed", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, rig.Scripted!.Requests.Count);
    }

    // ---- Retry-After clamp ---------------------------------------------------------------------------------------

    private static ScriptedResponse Throttled(string retryAfter)
    {
        return ScriptedResponse.Error(HttpStatusCode.TooManyRequests, "TooManyRequests", "slow down").WithHeader("Retry-After", retryAfter);
    }

    [Theory]
    [InlineData("119", 119, false)]
    [InlineData("120", 120, false)]
    [InlineData("121", 120, true)]
    [InlineData("299", 120, true)]
    [InlineData("99999", 120, true)]
    public async Task RetryAfterIsHonouredUpToTwoMinutesAndClampedBeyondThatWithALogLine(string header, double expectedSeconds, bool clamped)
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(Throttled(header), ScriptedResponse.Json("{\"value\":[]}")));

        await harness.Client.GetAsync(new GraphRequest("/users"), Ct);

        Assert.Equal([TimeSpan.FromSeconds(expectedSeconds)], harness.Delays.Delays);
        var clampLines = harness.Log.Entries.Where(static e => e.EventId.Id == 1022).ToList();
        Assert.Equal(clamped ? 1 : 0, clampLines.Count);
        if (clamped)
        {
            Assert.Contains("120", clampLines[0].Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ARetryAfterOnA503IsClampedToo()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(
            ScriptedResponse.Error(HttpStatusCode.ServiceUnavailable, "serviceNotAvailable", "later").WithHeader("Retry-After", "250"),
            ScriptedResponse.Json("{\"value\":[]}")));

        await harness.Client.GetAsync(new GraphRequest("/users"), Ct);

        Assert.Equal([TimeSpan.FromSeconds(120)], harness.Delays.Delays);
    }

    [Fact]
    public void TheRetryAfterCapIsConfigurable()
    {
        var policy = new RetryPolicy(new RetryOptions { MaxRetryAfter = TimeSpan.FromSeconds(10) }, new RecordingDelayProvider(), new TestLogger<RetryPolicy>(new LogSink()));
        var error = new GraphException(HttpStatusCode.TooManyRequests, "TooManyRequests", "slow", RetryHint.Default, null, TimeSpan.FromSeconds(30));

        var decision = policy.Decide(error, RetryHint.Default, new RetryState());

        Assert.Equal(TimeSpan.FromSeconds(10), decision.Delay);
    }

    // ---- wall-time budget ----------------------------------------------------------------------------------------

    [Fact]
    public async Task TheRetryBudgetOfOneCallStopsTheRetriesWhenTheNextDelayWouldExceedIt()
    {
        var clock = new Review1BStepClock();
        using var rig = new Review1BRig(
            new ScriptedHttpHandler { Fallback = _ => Throttled("100") },
            configureRetry: o =>
            {
                o.MaxTotalTime = TimeSpan.FromSeconds(600);
                o.ThrottleMaxRetries = 100;
            },
            clock: clock);

        var error = await Assert.ThrowsAsync<GraphException>(() => rig.Client.GetAsync(new GraphRequest("/users"), Ct));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.Status);
        // Six waits of 100 s fit exactly into 600 s (elapsed + delay == budget is allowed); the seventh would not.
        Assert.Equal(6, rig.AllDelays.Count);
        Assert.Equal(7, rig.Scripted!.Requests.Count);
        Assert.Contains(rig.Log.Entries, static e => e.EventId.Id == 1023);
    }

    [Fact]
    public async Task ABudgetSmallerThanTheFirstDelayMeansNoRetryAtAll()
    {
        var clock = new Review1BStepClock();
        using var rig = new Review1BRig(
            new ScriptedHttpHandler { Fallback = _ => Throttled("60") },
            configureRetry: o => o.MaxTotalTime = TimeSpan.FromSeconds(59),
            clock: clock);

        await Assert.ThrowsAsync<GraphException>(() => rig.Client.GetAsync(new GraphRequest("/users"), Ct));

        Assert.Single(rig.Scripted!.Requests);
        Assert.Empty(rig.AllDelays);
    }

    [Fact]
    public async Task AnAttemptThatItselfTakesTimeCountsAgainstTheBudget()
    {
        var clock = new Review1BStepClock();
        using var rig = new Review1BRig(
            new ScriptedHttpHandler { Fallback = _ => { clock.Advance(TimeSpan.FromSeconds(90)); return ScriptedResponse.Error(HttpStatusCode.ServiceUnavailable, "serviceNotAvailable", "later"); } },
            configureRetry: o => o.MaxTotalTime = TimeSpan.FromSeconds(100),
            clock: clock);

        await Assert.ThrowsAsync<GraphException>(() => rig.Client.GetAsync(new GraphRequest("/users"), Ct));

        // First failure at 90 s plus a 1 s back-off fits in 100 s; the second failure is at 181 s.
        Assert.Equal(2, rig.Scripted!.Requests.Count);
    }

    /// <summary>A handler whose responses never end and carry no length: the size cap must stop the read, not memory.</summary>
    private sealed class EndlessBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new EndlessStream()) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return Task.FromResult(response);
        }
    }

    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'x', offset, count);
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            buffer.Span.Fill((byte)'x');
            return ValueTask.FromResult(buffer.Length);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}
