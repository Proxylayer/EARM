using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph.Tests.Infrastructure;
using EArm.Graph.Throttling;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary>
/// The request scheduler inside <see cref="HttpGraphClient"/>: every attempt and every <c>$batch</c> sub-request is charged to its area, envelopes
/// carry at most one Conditional Access request, 429s and <c>x-ms-resource-unit</c> from responses and sub-responses reach the scheduler, and the
/// tenant-size probe (<see cref="HttpGraphClient.DetectTenantSizeAsync"/>). The clock never moves on its own and delays are recorded, so every wait is exact.
/// </summary>
public sealed class HttpGraphClientSchedulingTests
{
    private const string CaPolicies = "/identity/conditionalAccess/policies";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClientHarness Harness(ScriptedHttpHandler handler, Action<GraphOptions>? configure = null)
    {
        return new ClientHarness(handler, configure, time: new Review1BStepClock());
    }

    private static bool IsBatch(RecordedRequest request)
    {
        return request.Uri.AbsolutePath.EndsWith("/$batch", StringComparison.Ordinal);
    }

    private static JsonArray SubRequests(RecordedRequest envelope)
    {
        return JsonNode.Parse(envelope.Body!)!["requests"]!.AsArray();
    }

    /// <summary>Answers every envelope with one sub-response per sub-request (204 unless <paramref name="answer"/> says otherwise), and 204 to anything else.</summary>
    private static ScriptedHttpHandler BatchHandler(Func<string, JsonObject, JsonObject?>? answer = null)
    {
        var handler = new ScriptedHttpHandler();
        handler.Fallback = request =>
        {
            if (!IsBatch(request))
            {
                return ScriptedResponse.NoContent();
            }

            var responses = new JsonArray();
            foreach (var item in SubRequests(request))
            {
                var id = item!["id"]!.GetValue<string>();
                responses.Add(answer?.Invoke(id, item.AsObject()) ?? new JsonObject { ["id"] = id, ["status"] = 204 });
            }

            return ScriptedResponse.Json(new JsonObject { ["responses"] = responses }.ToJsonString());
        };
        return handler;
    }

    private static BatchRequest Dir(int i)
    {
        return new BatchRequest("GET", $"/groups/g{i}");
    }

    private static BatchRequest Ca(int i)
    {
        return new BatchRequest("GET", $"{CaPolicies}/p{i}");
    }

    // ---- single requests ----------------------------------------------------------------------------------

    [Fact]
    public async Task ParallelConditionalAccessRequestsAreSentOneAtATimeOneSecondApart()
    {
        var handler = new ScriptedHttpHandler { Fallback = _ => new ScriptedResponse { Status = HttpStatusCode.Created, Body = "{\"id\":\"p\"}", Delay = TimeSpan.FromMilliseconds(40) } };
        using var harness = Harness(handler, o => o.MaxParallel = 8);

        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => harness.Client.PostAsync(new GraphRequest(CaPolicies), new JsonObject(), Ct)));

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(1, handler.MaxConcurrency);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], harness.Delays.Delays);
    }

    [Fact]
    public async Task ANextLinkIsPacedInTheAreaOfItsPath()
    {
        var handler = new ScriptedHttpHandler(
            ScriptedResponse.Json("{\"value\":[{\"id\":\"1\"}],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/identity/conditionalAccess/policies?$skiptoken=abc\"}"),
            ScriptedResponse.Json("{\"value\":[{\"id\":\"2\"}]}"));
        using var harness = Harness(handler);

        var pages = new List<JsonNode>();
        await foreach (var page in harness.Client.GetAllPagesAsync(new GraphRequest(CaPolicies), Ct))
        {
            pages.Add(page);
        }

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1)], harness.Delays.Delays);
    }

    [Fact]
    public async Task A429PausesItsAreaWithoutMakingTheRetryWaitTwice()
    {
        var handler = new ScriptedHttpHandler(
            ScriptedResponse.Error(HttpStatusCode.TooManyRequests, "TooManyRequests", "slow down").WithHeader("Retry-After", "5").WithHeader("x-ms-throttle-scope", "Tenant_Application/ReadWrite"),
            ScriptedResponse.Json("{\"value\":[]}"),
            ScriptedResponse.Json("{\"value\":[]}"),
            ScriptedResponse.Json("{\"value\":[]}"));
        using var harness = Harness(handler);

        await harness.Client.GetAsync(new GraphRequest("/identityGovernance/accessReviews/definitions"), Ct);

        // The retry policy waited the Retry-After; the retry itself was not held again by the pause it tripped.
        Assert.Equal([TimeSpan.FromSeconds(5)], harness.Delays.Delays);

        await harness.Client.GetAsync(new GraphRequest("/users"), Ct);
        Assert.Equal([TimeSpan.FromSeconds(5)], harness.Delays.Delays);

        await harness.Client.GetAsync(new GraphRequest("/identityGovernance/accessReviews/definitions/d1/instances"), Ct);
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)], harness.Delays.Delays);

        var paused = Assert.Single(harness.Log.Entries, static e => e.EventId.Id == 1062);
        Assert.Contains("IdentityGovernance", paused.Message, StringComparison.Ordinal);
        Assert.Contains("Tenant_Application/ReadWrite", paused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheResourceUnitHeaderOfAResponseCorrectsTheEstimate()
    {
        var handler = new ScriptedHttpHandler(
            ScriptedResponse.Json("{\"id\":\"g1\"}").WithHeader("x-ms-resource-unit", "3500"),
            ScriptedResponse.Json("{\"id\":\"g2\"}"));
        using var harness = Harness(handler);

        await harness.Client.GetAsync(new GraphRequest("/groups/g1"), Ct);
        await harness.Client.GetAsync(new GraphRequest("/groups/g2"), Ct);

        // GET /groups/{id} is estimated at 1 unit; Graph charged 3,500, so the next unit waits 1/350 s.
        Assert.Equal([TimeSpan.FromSeconds(1d / 350)], harness.Delays.Delays);
    }

    [Fact]
    public async Task TheThrottleLimitPercentageHeaderIsReported()
    {
        var near = ScriptedResponse.Json("{\"id\":\"g1\"}").WithHeader("x-ms-throttle-limit-percentage", "0.85").WithHeader("x-ms-throttle-scope", "Tenant_Application");
        var handler = new ScriptedHttpHandler(near, near);
        using var harness = Harness(handler);

        await harness.Client.GetAsync(new GraphRequest("/groups/g1"), Ct);
        await harness.Client.GetAsync(new GraphRequest("/groups/g1"), Ct);

        var reported = Assert.Single(harness.Log.Entries, static e => e.EventId.Id == 1065);
        Assert.Contains("Tenant_Application", reported.Message, StringComparison.Ordinal);
    }

    // ---- $batch -------------------------------------------------------------------------------------------

    [Fact]
    public async Task BatchSubRequestsAreChargedToTheDirectoryBudgetAtTheirCost()
    {
        using var harness = Harness(BatchHandler());
        var requests = Enumerable.Range(1, 720).Select(static i => new BatchRequest("GET", $"/groups/g{i}/transitiveMembers")).ToList();

        var results = await harness.Client.BatchAsync(requests, GraphApi.V1, RetryHint.Default, Ct);

        // 20 × 5 units per envelope: 35 envelopes are the 3,500-unit budget, the 36th waits for its 100 units (100/350 s).
        Assert.All(results, static r => Assert.True(r.Succeeded));
        Assert.Equal(36, harness.Handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(100d / 350)], harness.Delays.Delays);
    }

    [Fact]
    public async Task BatchSubRequestsOutsideTheDirectoryDoNotSpendItsBudget()
    {
        using var harness = Harness(BatchHandler());
        var governance = Enumerable.Range(1, 2_000).Select(static i => new BatchRequest("GET", $"/identityGovernance/accessReviews/definitions/d{i}")).ToList();

        await harness.Client.BatchAsync(governance, GraphApi.V1, RetryHint.Default, Ct);
        await harness.Client.BatchAsync(Enumerable.Range(1, 20).Select(static i => new BatchRequest("GET", $"/groups/g{i}/transitiveMembers")).ToList(), GraphApi.V1, RetryHint.Default, Ct);

        Assert.Empty(harness.Delays.Delays);
    }

    [Fact]
    public async Task ABatchWithAConditionalAccessSubRequestWaitsForTheConditionalAccessSlot()
    {
        using var harness = Harness(BatchHandler());
        await harness.Client.PostAsync(new GraphRequest(CaPolicies), new JsonObject(), Ct);

        await harness.Client.BatchAsync([Dir(1), Ca(1)], GraphApi.V1, RetryHint.Default, Ct);

        Assert.Equal([TimeSpan.FromSeconds(1)], harness.Delays.Delays);
    }

    [Fact]
    public async Task EachEnvelopeCarriesAtMostOneConditionalAccessRequestAndChunksStayContiguous()
    {
        using var harness = Harness(BatchHandler());
        var requests = new List<BatchRequest> { Dir(1), Ca(2), Ca(3), Dir(4), Dir(5), Ca(6), Ca(7) };

        var results = await harness.Client.BatchAsync(requests, GraphApi.V1, RetryHint.Default, Ct);

        Assert.All(results, static r => Assert.True(r.Succeeded));
        Assert.Equal(requests, results.Select(static r => r.Request));
        var envelopes = harness.Handler.Requests;
        Assert.Equal(4, envelopes.Count);
        string[][] expectedIds = [["1", "2"], ["3", "4", "5"], ["6"], ["7"]];
        Assert.Equal(expectedIds, envelopes.Select(static e => SubRequests(e).Select(static s => s!["id"]!.GetValue<string>()).ToArray()).ToArray());
        Assert.Equal(
            ["/groups/g1", "/identity/conditionalAccess/policies/p2", "/identity/conditionalAccess/policies/p3", "/groups/g4", "/groups/g5", "/identity/conditionalAccess/policies/p6", "/identity/conditionalAccess/policies/p7"],
            envelopes.SelectMany(static e => SubRequests(e).Select(static s => s!["url"]!.GetValue<string>())));

        // One Conditional Access request per envelope, one envelope per second (the clock of this test never moves).
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)], harness.Delays.Delays);
    }

    [Fact]
    public async Task AConditionalAccessRequestThatFillsAnEnvelopeDoesNotSplitItAndTheNextOneStartsTheNextEnvelope()
    {
        using var harness = Harness(BatchHandler());
        var requests = Enumerable.Range(1, 19).Select(Dir).Append(Ca(20)).Append(Ca(21)).Concat(Enumerable.Range(22, 24).Select(Dir)).ToList();

        await harness.Client.BatchAsync(requests, GraphApi.V1, RetryHint.Default, Ct);

        Assert.Equal([20, 20, 5], harness.Handler.Requests.Select(static e => SubRequests(e).Count));
        Assert.Equal("21", SubRequests(harness.Handler.Requests[1])[0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public void TheChunkSizeIsUpToTwentyWithAtMostOneConditionalAccessRequest()
    {
        var dir = new GraphRequestCost(GraphServiceArea.Directory, 1, false);
        var ca = new GraphRequestCost(GraphServiceArea.ConditionalAccess, 0, false);
        var other = new GraphRequestCost(GraphServiceArea.IdentityGovernance, 0, true);

        Assert.Equal(20, HttpGraphClient.ChunkSize(Enumerable.Repeat(dir, 45).ToArray(), 0));
        Assert.Equal(5, HttpGraphClient.ChunkSize(Enumerable.Repeat(dir, 45).ToArray(), 40));
        Assert.Equal(20, HttpGraphClient.ChunkSize([ca, .. Enumerable.Repeat(other, 30)], 0));
        Assert.Equal(1, HttpGraphClient.ChunkSize([ca, ca, ca], 0));
        Assert.Equal(1, HttpGraphClient.ChunkSize([ca, ca, ca], 2));
        Assert.Equal(3, HttpGraphClient.ChunkSize([dir, dir, ca, ca], 0));
        Assert.Equal(2, HttpGraphClient.ChunkSize([dir, dir, ca, ca], 2) + HttpGraphClient.ChunkSize([dir, dir, ca, ca], 3));
    }

    [Fact]
    public async Task ALaterEnvelopeFailureMarksExactlyThatChunkSentAndTheRestNotSent()
    {
        var envelopes = 0;
        var inner = BatchHandler();
        var handler = new ScriptedHttpHandler
        {
            Fallback = request => Interlocked.Increment(ref envelopes) == 2
                ? ScriptedResponse.Error(HttpStatusCode.BadRequest, "BadRequest", "Invalid batch payload.")
                : inner.Fallback!(request),
        };
        using var harness = Harness(handler);

        // Chunks: [1-3] [4-6] [7] (each chunk ends before its second Conditional Access request); the second envelope fails.
        var requests = new List<BatchRequest> { Dir(1), Ca(2), Dir(3), Ca(4), Dir(5), Dir(6), Ca(7) };
        var results = await harness.Client.BatchAsync(requests, GraphApi.V1, RetryHint.Default, Ct);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(3, SubRequests(handler.Requests[1]).Count);
        Assert.True(results[0].Succeeded);
        Assert.True(results[1].Succeeded);
        Assert.True(results[2].Succeeded);
        for (var i = 3; i <= 5; i++)
        {
            Assert.False(results[i].Succeeded);
            Assert.NotEqual("BatchNotSent", results[i].Error!.Code);
            Assert.Contains("The $batch envelope failed", results[i].Error!.Message, StringComparison.Ordinal);
        }

        Assert.Equal("BatchNotSent", results[6].Error!.Code);
        Assert.Equal(["1", "2", "3", "4", "5", "6", "7"], results.Select(static r => r.Id));
    }

    [Fact]
    public async Task ASubResponse429PausesItsAreaBeforeTheIndividualReSend()
    {
        var delaysAtReSend = -1;
        RecordingDelayProvider? delays = null;
        var handler = BatchHandler((id, _) => id == "1"
            ? new JsonObject
            {
                ["id"] = id,
                ["status"] = 429,
                ["headers"] = new JsonObject { ["Retry-After"] = "9" },
                ["body"] = JsonNode.Parse("{\"error\":{\"code\":\"TooManyRequests\",\"message\":\"slow down\"}}"),
            }
            : null);
        var batchFallback = handler.Fallback!;
        handler.Fallback = request =>
        {
            if (!IsBatch(request))
            {
                delaysAtReSend = delays!.Delays.Count;
            }

            return batchFallback(request);
        };
        using var harness = Harness(handler);
        delays = harness.Delays;

        var results = await harness.Client.BatchAsync(
            [new BatchRequest("GET", "/roleManagement/directory/roleAssignments/r1"), Dir(2)],
            GraphApi.V1,
            RetryHint.Default,
            Ct);

        Assert.All(results, static r => Assert.True(r.Succeeded));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/v1.0/roleManagement/directory/roleAssignments/r1", handler.Requests[1].Uri.AbsolutePath);
        Assert.Equal([TimeSpan.FromSeconds(9)], harness.Delays.Delays);
        Assert.Equal(1, delaysAtReSend);

        // Only RoleManagement was paused.
        await harness.Client.GetAsync(new GraphRequest("/groups/g3"), Ct);
        Assert.Single(harness.Delays.Delays);
    }

    [Fact]
    public async Task ASubResponseResourceUnitHeaderIsReconciledButTheEnvelopesIsNot()
    {
        var subHeader = BatchHandler((id, _) => new JsonObject { ["id"] = id, ["status"] = 200, ["headers"] = new JsonObject { ["x-ms-resource-unit"] = "3500" }, ["body"] = new JsonObject() });
        using (var harness = Harness(subHeader))
        {
            await harness.Client.BatchAsync([Dir(1)], GraphApi.V1, RetryHint.Default, Ct);
            await harness.Client.GetAsync(new GraphRequest("/groups/g2"), Ct);

            Assert.Equal([TimeSpan.FromSeconds(1d / 350)], harness.Delays.Delays);
        }

        var envelopeHeader = new ScriptedHttpHandler(
            ScriptedResponse.Json("{\"responses\":[{\"id\":\"1\",\"status\":200,\"body\":{}}]}").WithHeader("x-ms-resource-unit", "3500"),
            ScriptedResponse.Json("{\"id\":\"g2\"}"));
        using (var harness = Harness(envelopeHeader))
        {
            await harness.Client.BatchAsync([Dir(1)], GraphApi.V1, RetryHint.Default, Ct);
            await harness.Client.GetAsync(new GraphRequest("/groups/g2"), Ct);

            Assert.Empty(harness.Delays.Delays);
        }
    }

    // ---- tenant size --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("30", "Small", 3_500)]
    [InlineData("200", "Medium", 5_000)]
    [InlineData("1000", "Large", 8_000)]
    public async Task TheTenantSizeIsReadWithOneAdvancedCountRequest(string count, string tier, int budget)
    {
        var handler = new ScriptedHttpHandler(ScriptedResponse.Json(count));
        using var harness = Harness(handler);

        await harness.Client.DetectTenantSizeAsync(Ct);
        await harness.Client.DetectTenantSizeAsync(Ct);

        var probe = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, probe.Method);
        Assert.Equal("https://graph.microsoft.com/v1.0/users/$count", probe.Uri.AbsoluteUri);
        Assert.Equal("eventual", probe.Header("ConsistencyLevel"));
        Assert.Equal(Enum.Parse<TenantSizeTier>(tier), harness.Client.Scheduler.Tier);
        var logged = Assert.Single(harness.Log.Entries, static e => e.EventId.Id == 1063);
        Assert.Contains(budget.ToString(System.Globalization.CultureInfo.InvariantCulture), logged.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.Log.Entries, static e => e.EventId.Id == 1064);

        // The probe itself cost 2 units of the budget; the rest is the tier's.
        var rest = await harness.Client.Scheduler.AcquireAsync(new ScheduledCall([new GraphRequestCost(GraphServiceArea.Directory, budget - 2, false)], false), Ct);
        rest.Dispose();
        Assert.Empty(harness.Delays.Delays);
        var over = await harness.Client.Scheduler.AcquireAsync(new ScheduledCall([new GraphRequestCost(GraphServiceArea.Directory, budget / 10, false)], false), Ct);
        over.Dispose();
        Assert.Equal([TimeSpan.FromSeconds(1)], harness.Delays.Delays);
    }

    public static TheoryData<string> ProbeFailures => ["403", "404", "429", "503", "not a number", "object", "negative", "transport"];

    [Theory]
    [MemberData(nameof(ProbeFailures))]
    public async Task AnUnreadableTenantSizeIsOneAttemptKeepsTheSmallestBudgetAndIsLoggedOnce(string failure)
    {
        var response = failure switch
        {
            "403" => ScriptedResponse.Error(HttpStatusCode.Forbidden, "Authorization_RequestDenied", "Insufficient privileges to complete the operation."),
            "404" => ScriptedResponse.Error(HttpStatusCode.NotFound, "Request_ResourceNotFound", "nope"),
            "429" => ScriptedResponse.Error(HttpStatusCode.TooManyRequests, "TooManyRequests", "slow down").WithHeader("Retry-After", "1"),
            "503" => ScriptedResponse.Error(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable", "try later"),
            "not a number" => ScriptedResponse.Json("\"many\""),
            "object" => ScriptedResponse.Json("{\"value\":12}"),
            "negative" => ScriptedResponse.Json("-5"),
            _ => new ScriptedResponse { Throw = new HttpRequestException("connection reset") },
        };
        var handler = new ScriptedHttpHandler(response, ScriptedResponse.Json("1000"));
        using var harness = Harness(handler);

        await harness.Client.DetectTenantSizeAsync(Ct);
        await harness.Client.DetectTenantSizeAsync(Ct);

        Assert.Single(handler.Requests);
        Assert.Equal(TenantSizeTier.Small, harness.Client.Scheduler.Tier);
        Assert.Single(harness.Log.Entries, static e => e.EventId.Id == 1064);
        Assert.DoesNotContain(harness.Log.Entries, static e => e.EventId.Id == 1063);
        if (failure == "403")
        {
            Assert.Contains("403 Authorization_RequestDenied", harness.Log.Entries.Single(static e => e.EventId.Id == 1064).Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ACancelledCallerSeesItsCancellationWhileTheProbeStillDecidesTheTier()
    {
        // The answer takes a moment, so the detection is still running when the cancelled caller asks for it.
        var handler = new ScriptedHttpHandler(new ScriptedResponse { Body = "1000", Delay = TimeSpan.FromMilliseconds(200) });
        using var harness = Harness(handler);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Client.DetectTenantSizeAsync(cts.Token));
        await harness.Client.DetectTenantSizeAsync(Ct);

        Assert.Single(handler.Requests);
        Assert.Equal(TenantSizeTier.Large, harness.Client.Scheduler.Tier);
    }
}
