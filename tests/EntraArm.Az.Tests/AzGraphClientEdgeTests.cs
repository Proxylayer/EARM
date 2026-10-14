using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Az;
using EntraArm.Az.Tests.Infrastructure;
using Xunit;

namespace EntraArm.Az.Tests;

/// <summary>
/// <see cref="AzGraphClient"/> against an in-memory runner (no pwsh): malformed answers, malformed or partial
/// <c>$batch</c> envelopes, retry exhaustion, the token refresh failing, cancellation, and argument guards. The
/// process-level behaviour is covered by the fake-az tests.
/// </summary>
public sealed class AzGraphClientEdgeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (AzGraphClient Client, RecordingDelayProvider Delays, LogSink Log) Create(IAzRunner runner, RetryOptions? options = null)
    {
        var delays = new RecordingDelayProvider();
        var log = new LogSink();
        var policy = new RetryPolicy(options ?? new RetryOptions(), delays, new TestLogger<RetryPolicy>(log));
        return (new AzGraphClient(runner, policy, new TestLogger<AzGraphClient>(log)), delays, log);
    }

    private static AzResult Json(string json)
    {
        return ScriptedAzRunner.Ok(json);
    }

    private static string Fixture(string name)
    {
        return GraphErrorFixtures.Get(name);
    }

    private static JsonObject Sub(object id, object? status, JsonNode? body = null)
    {
        var sub = new JsonObject { ["id"] = JsonValue.Create(id.ToString()) };
        if (status is not null)
        {
            sub["status"] = JsonValue.Create(status);
        }

        sub["body"] = body;
        return sub;
    }

    private static AzResult Envelope(params JsonNode?[] responses)
    {
        return Json(new JsonObject { ["responses"] = new JsonArray(responses) }.ToJsonString());
    }

    private static IReadOnlyList<BatchRequest> Requests(int count)
    {
        return Enumerable.Range(1, count).Select(i => new BatchRequest("DELETE", "/groups/g1/members/m" + i.ToString(CultureInfo.InvariantCulture) + "/$ref")).ToList();
    }

    // ---- guards ----

    [Fact]
    public async Task Constructor_AndMethods_RejectNullArguments()
    {
        var runner = ScriptedAzRunner.Sequence(Json("{}"));
        var policy = new RetryPolicy(new RetryOptions(), new RecordingDelayProvider(), new TestLogger<RetryPolicy>(new LogSink()));
        var logger = new TestLogger<AzGraphClient>(new LogSink());
        Assert.Throws<ArgumentNullException>(() => new AzGraphClient(null!, policy, logger));
        Assert.Throws<ArgumentNullException>(() => new AzGraphClient(runner, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new AzGraphClient(runner, policy, null!));

        var (client, _, _) = Create(runner);
        var request = new GraphRequest("/users");
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.GetAsync(null!, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.PostAsync(null!, null, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.PatchAsync(null!, new JsonObject(), Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.PatchAsync(request, null!, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.PutAsync(null!, new JsonObject(), Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.PutAsync(request, null!, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.DeleteAsync(null!, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.BatchAsync(null!, GraphApi.V1, RetryHint.Default, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        {
            await foreach (var _ in client.GetAllPagesAsync(null!, Ct))
            {
            }
        });
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task Put_SendsAPutWithTheBody_AndMapsTheAnswerAsOk()
    {
        var runner = ScriptedAzRunner.Sequence(Json("{\"ok\":true}"));
        var (client, _, _) = Create(runner);

        var response = await client.PutAsync(new GraphRequest("/applications/a1/x"), new JsonObject { ["v"] = 1 }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        var sent = Assert.Single(runner.Requests);
        Assert.Equal("PUT", sent.Method);
        Assert.Equal("application/json", sent.Headers!["Content-Type"]);
        Assert.Equal(1, sent.Body!["v"]!.GetValue<int>());
    }

    [Fact]
    public async Task ALoneSurrogateEscapeInAGraphReply_IsReadAsTheReplacementCharacter_NotAnInvalidOperationLater()
    {
        // Graph text is data: a display name with a lone surrogate escape must not make a later read, compare or hash throw.
        var runner = ScriptedAzRunner.Sequence(Json("{\"id\":\"g1\",\"displayName\":\"bad \\ud800 name\",\"mail\":\"\\udc00\",\"o\\ud800k\":1,\"ok\":\"\\ud83d\\ude00 pair\"}"));
        var (client, _, _) = Create(runner);

        var response = await client.GetAsync(new GraphRequest("/groups/g1"), Ct);

        var body = Assert.IsType<JsonObject>(response);
        Assert.Equal("bad \uFFFD name", body["displayName"]!.GetValue<string>());
        Assert.Equal("\uFFFD", body["mail"]!.GetValue<string>());
        Assert.Equal("\uD83D\uDE00 pair", body["ok"]!.GetValue<string>());
        Assert.Equal(1, body["o\uFFFDk"]!.GetValue<int>());
        Assert.DoesNotContain("ud800", body.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnEscapedBackslashBeforeU_InAGraphReply_IsLeftAlone()
    {
        var runner = ScriptedAzRunner.Sequence(Json("{\"displayName\":\"C:\\\\ud800\"}"));
        var (client, _, _) = Create(runner);

        var response = await client.GetAsync(new GraphRequest("/groups/g1"), Ct);

        Assert.Equal("C:\\ud800", response!["displayName"]!.GetValue<string>());
    }

    // ---- malformed single answers ----

    [Theory]
    [InlineData("this is not json")]
    [InlineData("{\"half\": ")]
    [InlineData("<html>gateway</html>")]
    [InlineData("{'single': 'quotes'}")]
    [InlineData("[1, 2,")]
    public async Task NonJsonOutputWithExitZero_IsAnUnknownAzFailure_ThatIsRetriedThenSurfaces(string stdout)
    {
        var runner = ScriptedAzRunner.Sequence(Json(stdout));
        var (client, delays, _) = Create(runner);

        var ex = await Assert.ThrowsAsync<AzCliException>(() => client.GetAsync(new GraphRequest("/users/1"), Ct));

        Assert.Equal(AzCliFailure.Unknown, ex.Failure);
        Assert.Contains("not JSON", ex.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(ex.InnerException);
        Assert.Equal(3, runner.Requests.Count);
        Assert.Equal(2, delays.Delays.Count);
    }

    [Fact]
    public async Task WhitespaceOnlyOutput_IsNoContent_AndAnErrorDocumentWithExitZero_IsThrown()
    {
        var runner = ScriptedAzRunner.Sequence(Json("  \r\n\t "), Json("{\"error\":{\"code\":\"Request_ResourceNotFound\",\"message\":\"gone\"}}"));
        var (client, _, _) = Create(runner);

        var empty = await client.PostAsync(new GraphRequest("/x"), null, Ct);
        var notFound = await client.GetAsync(new GraphRequest("/y"), Ct);

        Assert.Equal(HttpStatusCode.NoContent, empty.Status);
        Assert.Null(empty.Body);
        Assert.Null(notFound);
    }

    // ---- retry exhaustion and refresh ----

    [Fact]
    public async Task Throttling_IsRetriedEightTimes_ThenThrown_WithTheBackoffLadder()
    {
        var runner = ScriptedAzRunner.Sequence(ScriptedAzRunner.Fail(Fixture("tooManyRequestsNoRetryAfter")));
        var (client, delays, _) = Create(runner);

        var ex = await Assert.ThrowsAsync<GraphException>(() => client.GetAsync(new GraphRequest("/users"), Ct));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.Status);
        Assert.Equal(9, runner.Requests.Count);
        Assert.Equal(8, delays.Delays.Count);
        Assert.Equal(TimeSpan.FromSeconds(60), delays.Delays.Max());
    }

    [Fact]
    public async Task ServerErrors_AreRetriedFiveTimes_ThenThrown()
    {
        var runner = ScriptedAzRunner.Sequence(ScriptedAzRunner.Fail(Fixture("serviceUnavailable")));
        var (client, delays, _) = Create(runner);

        var ex = await Assert.ThrowsAsync<GraphException>(() => client.DeleteAsync(new GraphRequest("/groups/g"), Ct));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.Status);
        Assert.Equal(6, runner.Requests.Count);
        Assert.Equal(5, delays.Delays.Count);
    }

    [Fact]
    public async Task ATimedOutAz_IsRetriedAsTransient_ThenThrown()
    {
        var runner = ScriptedAzRunner.Sequence(new AzResult(-1, string.Empty, string.Empty, TimeSpan.FromSeconds(120), TimedOut: true));
        var (client, _, _) = Create(runner);

        var ex = await Assert.ThrowsAsync<AzCliException>(() => client.GetAsync(new GraphRequest("/users"), Ct));

        Assert.True(ex.TimedOut);
        Assert.Equal(AzCliFailure.Transient, ex.Failure);
        Assert.Equal(3, runner.Requests.Count);
    }

    [Fact]
    public async Task NotLoggedIn_IsTerminal_WithTheLoginHint_AndNeverRetried()
    {
        var runner = ScriptedAzRunner.Sequence(ScriptedAzRunner.Fail(Fixture("notLoggedIn")));
        var (client, delays, _) = Create(runner);

        var ex = await Assert.ThrowsAsync<AzCliException>(() => client.GetAsync(new GraphRequest("/users"), Ct));

        Assert.Equal(AzCliFailure.NotLoggedIn, ex.Failure);
        Assert.Contains(AzCliException.LoginHint, ex.Message, StringComparison.Ordinal);
        Assert.Single(runner.Requests);
        Assert.Empty(delays.Delays);
    }

    [Fact]
    public async Task AnUnauthorizedAnswer_RefreshesTheTokenOnce_ThenRetries()
    {
        var runner = new ScriptedAzRunner((request, index) => index switch
        {
            0 => ScriptedAzRunner.Fail(Fixture("unauthorized")),
            1 => Json("{\"token\":\"redacted\"}"),
            _ => Json("{\"id\":\"u1\"}"),
        });
        var (client, delays, _) = Create(runner);

        var body = await client.GetAsync(new GraphRequest("/users/u1"), Ct);

        Assert.Equal("u1", body!["id"]!.GetValue<string>());
        Assert.Equal(3, runner.Requests.Count);
        Assert.Equal("account get-access-token", runner.Requests[1].CommandText);
        Assert.Contains("ms-graph", runner.Requests[1].Arguments!);
        Assert.Empty(delays.Delays);
    }

    [Fact]
    public async Task AFailedTokenRefresh_SurfacesTheAzFailure_WithoutRetryingTheCall()
    {
        var runner = new ScriptedAzRunner((request, index) => index == 0 ? ScriptedAzRunner.Fail(Fixture("unauthorized")) : ScriptedAzRunner.Fail(Fixture("notLoggedIn")));
        var (client, _, _) = Create(runner);

        var ex = await Assert.ThrowsAsync<AzCliException>(() => client.GetAsync(new GraphRequest("/users/u1"), Ct));

        Assert.Equal(AzCliFailure.NotLoggedIn, ex.Failure);
        Assert.Contains("az account get-access-token", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, runner.Requests.Count);
    }

    [Fact]
    public async Task Cancellation_BeforeAndBetweenCalls_StopsTheClient()
    {
        var runner = ScriptedAzRunner.Sequence(Json("{}"));
        var (client, _, _) = Create(runner);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync(new GraphRequest("/users"), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.BatchAsync(Requests(2), GraphApi.V1, RetryHint.Default, cts.Token));
        Assert.Empty(runner.Requests);
    }

    // ---- paging ----

    [Fact]
    public async Task GetAllPages_AnEmptyAnswer_YieldsNothing_AndANonStringNextLinkEndsThePaging()
    {
        var runner = ScriptedAzRunner.Sequence(Json(string.Empty), Json("{\"value\":[{\"id\":1},null,{\"id\":2}],\"@odata.nextLink\":42}"));
        var (client, _, _) = Create(runner);

        var none = new List<JsonNode>();
        await foreach (var item in client.GetAllPagesAsync(new GraphRequest("/users"), Ct))
        {
            none.Add(item);
        }

        var some = new List<JsonNode>();
        await foreach (var item in client.GetAllPagesAsync(new GraphRequest("/users"), Ct))
        {
            some.Add(item);
        }

        Assert.Empty(none);
        Assert.Equal([1, 2], some.Select(i => i["id"]!.GetValue<int>()));
        Assert.Equal(2, runner.Requests.Count);
    }

    [Fact]
    public async Task GetAllPages_AFailureOnALaterPage_PropagatesAfterTheEarlierItems()
    {
        var runner = new ScriptedAzRunner((request, index) => index == 0
            ? Json("{\"value\":[{\"id\":1}],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/users?$skiptoken=abc\"}")
            : ScriptedAzRunner.Fail(Fixture("forbidden")));
        var (client, _, _) = Create(runner);
        var seen = new List<int>();

        var ex = await Assert.ThrowsAsync<GraphException>(async () =>
        {
            await foreach (var item in client.GetAllPagesAsync(new GraphRequest("/users"), Ct))
            {
                seen.Add(item["id"]!.GetValue<int>());
            }
        });

        Assert.Equal([1], seen);
        Assert.Equal(HttpStatusCode.Forbidden, ex.Status);
        Assert.Equal("abc", runner.Requests[1].UrlParameters!["$skiptoken"]);
    }

    // ---- $batch envelopes ----

    [Fact]
    public async Task Batch_WithoutAResponsesArray_IsAnUnknownAzFailure()
    {
        foreach (string answer in new[] { "{}", "{\"responses\":\"nope\"}", "[1,2]", "null" })
        {
            var runner = ScriptedAzRunner.Sequence(Json(answer));
            var (client, _, _) = Create(runner);

            var ex = await Assert.ThrowsAsync<AzCliException>(() => client.BatchAsync(Requests(2), GraphApi.V1, RetryHint.Default, Ct));

            Assert.Equal(AzCliFailure.Unknown, ex.Failure);
            Assert.Contains("no 'responses' array", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Batch_MissingOrUnmappableSubResponses_AreRetriedOneByOne()
    {
        // Three requests; the envelope answers only the second properly and adds junk: a non-object, an unparsable id,
        // ids out of range (0 and 4), and an entry without an id.
        var runner = new ScriptedAzRunner((request, index) => index == 0
            ? Envelope(JsonValue.Create(5), Sub("abc", 204), Sub(0, 204), Sub(4, 204), new JsonObject { ["status"] = 204 }, Sub(2, 204))
            : ScriptedAzRunner.Ok());
        var (client, _, _) = Create(runner);

        var results = await client.BatchAsync(Requests(3), GraphApi.V1, RetryHint.Default, Ct);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.True(r.Succeeded, r.Error?.Message));
        Assert.Equal(HttpStatusCode.NoContent, results[1].Status);
        // The two unanswered requests were sent on their own (requests 1 and 3).
        Assert.Equal(3, runner.Requests.Count);
        Assert.EndsWith("/m1/$ref", runner.Requests[1].Url, StringComparison.Ordinal);
        Assert.EndsWith("/m3/$ref", runner.Requests[2].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Batch_ASubResponseWithoutAStatusOrWithATextBody_IsAFailureThatIsRetriedIndividually()
    {
        var runner = new ScriptedAzRunner((request, index) => index switch
        {
            0 => Envelope(Sub(1, null, JsonValue.Create("plain text body")), Sub(2, 200, new JsonObject { ["id"] = "x" })),
            _ => ScriptedAzRunner.Fail(Fixture("forbidden")),
        });
        var (client, _, _) = Create(runner);

        var results = await client.BatchAsync(Requests(2), GraphApi.V1, RetryHint.Default, Ct);

        Assert.False(results[0].Succeeded);
        Assert.Equal(HttpStatusCode.Forbidden, results[0].Status);
        Assert.True(results[0].Error!.IsForbidden);
        Assert.True(results[1].Succeeded);
        Assert.Equal("x", results[1].Body!["id"]!.GetValue<string>());
        Assert.Equal(2, runner.Requests.Count);
    }

    [Fact]
    public async Task Batch_AnAlreadyExistsSubResponse_IsNotRetried_AndKeepsItsError()
    {
        var already = new JsonObject
        {
            ["error"] = new JsonObject { ["code"] = "Request_BadRequest", ["message"] = "One or more added object references already exist for the following modified properties: 'members'." },
        };
        var runner = new ScriptedAzRunner((request, index) => index == 0 ? Envelope(Sub(1, 400, already), Sub(2, 204)) : ScriptedAzRunner.Fail("unexpected extra call"));
        var (client, _, log) = Create(runner);

        var results = await client.BatchAsync(Requests(2), GraphApi.V1, RetryHint.Default, Ct);

        Assert.True(results[0].Error!.IsAlreadyExists);
        Assert.True(results[1].Succeeded);
        Assert.Single(runner.Requests);
        Assert.Contains("0 failed", log.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Batch_DuplicateIds_KeepTheLastAnswer()
    {
        var runner = ScriptedAzRunner.Sequence(Envelope(Sub(1, 204), Sub(1, 200, new JsonObject { ["id"] = "last" })));
        var (client, _, _) = Create(runner);

        var results = await client.BatchAsync(Requests(1), GraphApi.V1, RetryHint.Default, Ct);

        Assert.Equal(HttpStatusCode.OK, results[0].Status);
        Assert.Equal("last", results[0].Body!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Batch_UrlParametersAndHeaders_TravelInsideTheSubRequest()
    {
        var runner = ScriptedAzRunner.Sequence(Envelope(Sub(1, 200, new JsonObject { ["value"] = new JsonArray() })));
        var (client, _, _) = Create(runner);
        var request = new BatchRequest("get", "groups/g1/members")
        {
            UrlParameters = new Dictionary<string, string> { ["$select"] = "id,displayName", ["$filter"] = "startswith(displayName,'a b')" },
            Headers = new Dictionary<string, string> { ["ConsistencyLevel"] = "eventual" },
        };

        await client.BatchAsync([request], GraphApi.Beta, RetryHint.Default, Ct);

        var sent = Assert.Single(runner.Requests);
        Assert.Contains("beta", sent.Url, StringComparison.Ordinal);
        var item = Assert.IsType<JsonObject>(sent.Body!["requests"]![0]);
        Assert.Equal("GET", item["method"]!.GetValue<string>());
        string url = item["url"]!.GetValue<string>();
        Assert.StartsWith("/groups/g1/members?", url, StringComparison.Ordinal);
        Assert.Contains("$select=id%2CdisplayName", url, StringComparison.Ordinal);
        Assert.Contains("%27a%20b%27", url, StringComparison.Ordinal);
        Assert.Equal("eventual", item["headers"]!["ConsistencyLevel"]!.GetValue<string>());
        Assert.False(item.ContainsKey("body"));
    }

    [Fact]
    public async Task Batch_ABodyIsSentWithAContentType()
    {
        var runner = ScriptedAzRunner.Sequence(Envelope(Sub(1, 204)));
        var (client, _, _) = Create(runner);

        await client.BatchAsync([new BatchRequest("POST", "/groups/g/members/$ref", new JsonObject { ["a"] = 1 })], GraphApi.V1, RetryHint.Default, Ct);

        var item = (JsonObject)((JsonArray)runner.Requests[0].Body!["requests"]!)[0]!;
        Assert.Equal("application/json", item["headers"]!["Content-Type"]!.GetValue<string>());
    }

    /// <summary>
    /// DEFECT (low severity, not fixed here: src/ is out of scope for the test pass). AzGraphClient.BuildBatchItem (src/EntraArm.Az/AzGraphClient.cs:280)
    /// tests <c>headers.ContainsKey("Content-Type")</c> on a case-sensitive JsonObject, while the single-request path (SendAsync) uses a case-insensitive
    /// dictionary. A caller-supplied lower-case <c>content-type</c> therefore yields two Content-Type entries in the sub-request.
    /// Expected: one Content-Type header (the caller's). Actual: both "content-type" and "Content-Type".
    /// </summary>
    [Fact(Skip = "DEFECT: batch sub-requests duplicate a caller-supplied lower-case content-type header (AzGraphClient.cs:280).")]
    public async Task Defect_Batch_ACallerContentTypeInAnotherCase_IsDuplicated()
    {
        var runner = ScriptedAzRunner.Sequence(Envelope(Sub(1, 204)));
        var (client, _, _) = Create(runner);
        var request = new BatchRequest("POST", "/groups/g/members/$ref", new JsonObject { ["a"] = 2 }) { Headers = new Dictionary<string, string> { ["content-type"] = "application/json; charset=utf-8" } };

        await client.BatchAsync([request], GraphApi.V1, RetryHint.Default, Ct);

        var headers = ((JsonObject)((JsonArray)runner.Requests[0].Body!["requests"]!)[0]!)["headers"]!.AsObject();
        Assert.Single(headers, h => string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase));
    }
}
