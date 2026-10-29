using System;
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
/// <see cref="GraphRequestCounter"/> as <see cref="HttpGraphClient"/> feeds it (<see cref="GraphOptions.RequestCounter"/>, read back through
/// <see cref="HttpGraphClient.Requests"/>): every HTTP attempt counts once (a retry, a page, a <c>$batch</c> envelope), the most attempts holding a connection slot
/// at once, the timestamp of the first write, and the time attempts waited for the request scheduler.
/// </summary>
public sealed class GraphRequestCounterTests
{
    private const string CaPolicies = "/identity/conditionalAccess/policies";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClientHarness Harness(ScriptedHttpHandler handler, GraphRequestCounter counter, int maxParallel = 4)
    {
        return new ClientHarness(handler, o =>
        {
            o.RequestCounter = counter;
            o.MaxParallel = maxParallel;
        }, time: new Review1BStepClock());
    }

    [Fact]
    public void ANewCounter_IsEmpty()
    {
        var counter = new GraphRequestCounter();

        Assert.Equal(0, counter.Sent);
        Assert.Equal(0, counter.MaxInFlight);
        Assert.Equal(TimeSpan.Zero, counter.SchedulerWait);
        Assert.Null(counter.FirstWriteTimestamp);
    }

    [Fact]
    public void TheClientExposesTheCounterOfItsOptions_AndTheOptionsDefaultToANewOne()
    {
        var counter = new GraphRequestCounter();
        using var harness = Harness(new ScriptedHttpHandler(), counter);

        Assert.Same(counter, harness.Client.Requests);
        Assert.NotNull(new GraphOptions().RequestCounter);
        Assert.NotSame(new GraphOptions().RequestCounter, new GraphOptions().RequestCounter);
    }

    [Fact]
    public async Task EveryAttemptCountsOnce_ARetry_APage_AndABatchEnvelope()
    {
        var counter = new GraphRequestCounter();
        var handler = new ScriptedHttpHandler(
            ScriptedResponse.Json("{\"id\":\"g1\"}"),
            ScriptedResponse.Error(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable", "try later"),
            ScriptedResponse.Json("{\"id\":\"g2\"}"),
            ScriptedResponse.Json("{\"value\":[{\"id\":\"1\"}],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/groups?$skiptoken=abc\"}"),
            ScriptedResponse.Json("{\"value\":[{\"id\":\"2\"}]}"),
            ScriptedResponse.Json("{\"responses\":[{\"id\":\"1\",\"status\":204},{\"id\":\"2\",\"status\":204}]}"));
        using var harness = Harness(handler, counter);

        await harness.Client.GetAsync(new GraphRequest("/groups/g1"), Ct);
        Assert.Equal(1, counter.Sent);

        // A 503 then a 200: two attempts.
        await harness.Client.GetAsync(new GraphRequest("/groups/g2"), Ct);
        Assert.Equal(3, counter.Sent);

        // Two pages: two attempts.
        await foreach (var _ in harness.Client.GetAllPagesAsync(new GraphRequest("/groups"), Ct))
        {
        }

        Assert.Equal(5, counter.Sent);

        // One envelope for two sub-requests: one attempt.
        await harness.Client.BatchAsync([new BatchRequest("DELETE", "/groups/a/members/x/$ref"), new BatchRequest("DELETE", "/groups/b/members/y/$ref")], GraphApi.V1, RetryHint.Default, Ct);

        Assert.Equal(6, counter.Sent);
        Assert.Equal(handler.Requests.Count, counter.Sent);
    }

    [Fact]
    public async Task SequentialRequests_HoldOneSlotAtATime()
    {
        var counter = new GraphRequestCounter();
        var handler = new ScriptedHttpHandler { Fallback = _ => ScriptedResponse.Json("{\"id\":\"g\"}") };
        using var harness = Harness(handler, counter);

        for (int i = 0; i < 5; i++)
        {
            await harness.Client.GetAsync(new GraphRequest("/groups/g" + i), Ct);
        }

        Assert.Equal(5, counter.Sent);
        Assert.Equal(1, counter.MaxInFlight);
    }

    [Fact]
    public async Task MaxInFlight_IsTheMostAttemptsHoldingASlotAtOnce_BoundedByTheClientsParallelism()
    {
        // Four slots, six requests: the handler holds every request until four are inside at once, so the peak is exactly four (a fifth cannot get a
        // slot, and fewer than four never release the gate). After that, the in-flight count went back down: a lone request does not raise the peak.
        var counter = new GraphRequestCounter();
        using var gate = new ArrivalGate(4);
        using var http = new HttpClient(gate, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new HttpGraphClient(http, new FakeTokenProvider(), new RetryPolicy(new RetryOptions(), new RecordingDelayProvider(), new TestLogger<RetryPolicy>(new LogSink())), new GraphOptions { RequestCounter = counter, MaxParallel = 4 }, new TestLogger<HttpGraphClient>(new LogSink()), new Review1BStepClock());

        await Task.WhenAll(Enumerable.Range(0, 6).Select(i => client.GetAsync(new GraphRequest("/groups/g" + i), Ct))).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(6, counter.Sent);
        Assert.Equal(4, counter.MaxInFlight);
        Assert.Equal(4, gate.MaxConcurrency);

        await client.GetAsync(new GraphRequest("/groups/again"), Ct);

        Assert.Equal(7, counter.Sent);
        Assert.Equal(4, counter.MaxInFlight);
    }

    [Fact]
    public async Task TheFirstWriteTimestamp_IsSetByTheFirstWriteOnly_NeverByAReadAndNeverMovedByALaterWrite()
    {
        var counter = new GraphRequestCounter();
        var handler = new ScriptedHttpHandler(
            ScriptedResponse.Json("{\"id\":\"g\"}"),
            ScriptedResponse.Json("{\"value\":[]}"),
            new ScriptedResponse { Status = HttpStatusCode.Created, Body = "{\"id\":\"new\"}" },
            ScriptedResponse.NoContent(),
            ScriptedResponse.NoContent());
        using var harness = Harness(handler, counter);

        await harness.Client.GetAsync(new GraphRequest("/groups/g"), Ct);
        await foreach (var _ in harness.Client.GetAllPagesAsync(new GraphRequest("/groups"), Ct))
        {
        }

        Assert.Null(counter.FirstWriteTimestamp);

        long before = System.Diagnostics.Stopwatch.GetTimestamp();
        await harness.Client.PostAsync(new GraphRequest("/groups"), new JsonObject { ["displayName"] = "G" }, Ct);
        long after = System.Diagnostics.Stopwatch.GetTimestamp();

        var first = counter.FirstWriteTimestamp;
        Assert.NotNull(first);
        Assert.InRange(first!.Value, before, after);

        await harness.Client.PatchAsync(new GraphRequest("/groups/new"), new JsonObject { ["description"] = "d" }, Ct);
        await harness.Client.DeleteAsync(new GraphRequest("/groups/new"), Ct);

        Assert.Equal(first, counter.FirstWriteTimestamp);
        Assert.Equal(5, counter.Sent);
    }

    [Fact]
    public async Task AWriteThatGraphRefuses_StillMarksTheFirstWrite()
    {
        // The request went out (it took a connection slot): what Graph answered does not matter.
        var counter = new GraphRequestCounter();
        var handler = new ScriptedHttpHandler(ScriptedResponse.Error(HttpStatusCode.BadRequest, "Request_BadRequest", "no"));
        using var harness = Harness(handler, counter);

        await Assert.ThrowsAsync<GraphException>(() => harness.Client.PostAsync(new GraphRequest("/groups"), new JsonObject(), Ct));

        Assert.NotNull(counter.FirstWriteTimestamp);
        Assert.Equal(1, counter.Sent);
    }

    [Fact]
    [Trait("Category", "Timing")]
    public async Task SchedulerWait_IsTheRealTimeAttemptsWaitedForTheScheduler_SummedOverConcurrentAttempts()
    {
        // Conditional Access is paced one request per second: the second and third of three parallel POSTs are held by the scheduler. The delay
        // provider really sleeps (100 ms per wait, whatever was asked), so the counter must sum at least two of those waits; the first POST waits for nothing.
        var counter = new GraphRequestCounter();
        var handler = new ScriptedHttpHandler { Fallback = _ => new ScriptedResponse { Status = HttpStatusCode.Created, Body = "{\"id\":\"p\"}" } };
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var delays = new SleepingDelayProvider(TimeSpan.FromMilliseconds(100));
        var client = new HttpGraphClient(http, new FakeTokenProvider(), new RetryPolicy(new RetryOptions(), delays, new TestLogger<RetryPolicy>(new LogSink())), new GraphOptions { RequestCounter = counter, MaxParallel = 8 }, new TestLogger<HttpGraphClient>(new LogSink()), new Review1BStepClock());

        await client.PostAsync(new GraphRequest(CaPolicies), new JsonObject(), Ct);
        var afterFirst = counter.SchedulerWait;
        await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => client.PostAsync(new GraphRequest(CaPolicies), new JsonObject(), Ct)));

        Assert.True(afterFirst < TimeSpan.FromMilliseconds(100), "the first request had nothing to wait for, yet waited " + afterFirst);
        Assert.Equal(2, delays.Calls);
        Assert.True(counter.SchedulerWait >= TimeSpan.FromMilliseconds(180), "two scheduler waits of 100 ms each, summed: " + counter.SchedulerWait);
        Assert.Equal(3, counter.Sent);
        Assert.Equal(1, counter.MaxInFlight);
    }

    /// <summary>Sleeps a fixed real time for every wait the scheduler asks for (whatever its length) and counts the calls.</summary>
    private sealed class SleepingDelayProvider(TimeSpan sleep) : IDelayProvider
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.Delay(sleep, cancellationToken);
        }
    }

    /// <summary>Holds every request until <c>threshold</c> requests have arrived (a hang guard fails the test after 10 s), then answers 200 to all.</summary>
    private sealed class ArrivalGate(int threshold) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _lock = new();
        private int _arrived;
        private int _inFlight;
        private int _max;

        public int MaxConcurrency
        {
            get
            {
                lock (_lock)
                {
                    return _max;
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _inFlight++;
                _max = Math.Max(_max, _inFlight);
                if (++_arrived >= threshold)
                {
                    _open.TrySetResult();
                }
            }

            try
            {
                await _open.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("{\"id\":\"g\"}", new UTF8Encoding(false), "application/json") };
            }
            finally
            {
                lock (_lock)
                {
                    _inFlight--;
                }
            }
        }
    }
}
