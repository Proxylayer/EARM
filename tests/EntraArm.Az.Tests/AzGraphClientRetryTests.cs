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

/// <summary>README items 7 and 9: retry sequences and <c>$batch</c> through the real pwsh host and fake az, delays recorded instead of slept.</summary>
public sealed class AzGraphClientRetryTests
{
    private const string V1 = "https://graph.microsoft.com/v1.0";

    private static readonly IReadOnlyList<string> TokenRefreshArgv = ["account", "get-access-token", "--resource-type", "ms-graph", "--only-show-errors", "-o", "json"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Throttled_TwiceThenSuccess_HonoursRetryAfter()
    {
        using var context = new FakeAzContext(nameof(Throttled_TwiceThenSuccess_HonoursRetryAfter));
        context.UseScenario(new FakeScenario()
            .Add(FakeResponse.GraphError("tooManyRequests"))
            .Add(FakeResponse.GraphError("tooManyRequests"))
            .Add(FakeResponse.Json("{\"id\":\"u1\"}")));
        using var runner = context.CreateRunner();
        var delays = new RecordingDelayProvider();
        var client = context.CreateGraphClient(runner, delays);

        var body = await client.GetAsync(new GraphRequest("/users/u1"), Ct);

        Assert.Equal("u1", body?["id"]?.GetValue<string>());
        Assert.Equal([TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)], delays.Delays);
        var records = context.ReadRecords();
        Assert.Equal(3, records.Count);
        Assert.All(records, r => Assert.Equal(V1 + "/users/u1", r.ValueAfter("--url")));
        Assert.Equal(2, context.Log.Entries.Count(e => e.EventId.Id == 1020));
    }

    [Fact]
    public async Task Throttled_TwiceWithoutRetryAfter_BacksOffTwoThenFourSeconds()
    {
        using var context = new FakeAzContext(nameof(Throttled_TwiceWithoutRetryAfter_BacksOffTwoThenFourSeconds));
        context.UseScenario(new FakeScenario()
            .Add(FakeResponse.GraphError("tooManyRequestsNoRetryAfter"))
            .Add(FakeResponse.GraphError("tooManyRequestsNoRetryAfter"))
            .Add(FakeResponse.Json("{\"id\":\"u1\"}")));
        using var runner = context.CreateRunner();
        var delays = new RecordingDelayProvider();
        var client = context.CreateGraphClient(runner, delays);

        var body = await client.GetAsync(new GraphRequest("/users/u1"), Ct);

        Assert.Equal("u1", body?["id"]?.GetValue<string>());
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], delays.Delays);
        Assert.Equal(3, context.ReadRecords().Count);
    }

    [Fact]
    public async Task ServiceUnavailable_SixTimes_ThrowsAfterFiveRetries()
    {
        using var context = new FakeAzContext(nameof(ServiceUnavailable_SixTimes_ThrowsAfterFiveRetries));
        context.UseScenario(new FakeScenario().AddRange(Enumerable.Repeat(0, 6).Select(static _ => FakeResponse.GraphError("serviceUnavailable"))));
        using var runner = context.CreateRunner();
        var delays = new RecordingDelayProvider();
        var client = context.CreateGraphClient(runner, delays);

        var error = await Assert.ThrowsAsync<GraphException>(() => client.GetAsync(new GraphRequest("/users/u1"), Ct));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.Status);
        Assert.True(error.IsTransient);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16)], delays.Delays);
        Assert.Equal(6, context.ReadRecords().Count);
        Assert.Empty(context.LeftoverProtocolFiles());
    }

    [Fact]
    public async Task Replication_UnderExpectReplicationLag_RetriesEveryFiveSecondsThenSucceeds()
    {
        using var context = new FakeAzContext(nameof(Replication_UnderExpectReplicationLag_RetriesEveryFiveSecondsThenSucceeds));
        context.UseScenario(new FakeScenario()
            .Add(FakeResponse.GraphError("notFound"))
            .Add(FakeResponse.GraphError("notFound"))
            .Add(FakeResponse.GraphError("notFound"))
            .Add(FakeResponse.Json("{\"id\":\"sp-1\"}")));
        using var runner = context.CreateRunner();
        var delays = new RecordingDelayProvider();
        var client = context.CreateGraphClient(runner, delays);

        var body = await client.GetAsync(new GraphRequest("/servicePrincipals/sp-1", hint: RetryHint.ExpectReplicationLag), Ct);

        Assert.Equal("sp-1", body?["id"]?.GetValue<string>());
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)], delays.Delays);
        Assert.Equal(4, context.ReadRecords().Count);
    }

    [Fact]
    public async Task Replication_UnderDefault_GetReturnsNullWithoutRetry()
    {
        using var context = new FakeAzContext(nameof(Replication_UnderDefault_GetReturnsNullWithoutRetry));
        context.UseScenario(new FakeScenario()
            .Add(FakeResponse.GraphError("notFound"))
            .Add(FakeResponse.Json("{\"id\":\"never-reached\"}")));
        using var runner = context.CreateRunner();
        var delays = new RecordingDelayProvider();
        var client = context.CreateGraphClient(runner, delays);

        var body = await client.GetAsync(new GraphRequest("/servicePrincipals/sp-1"), Ct);

        Assert.Null(body);
        Assert.Empty(delays.Delays);
        Assert.Single(context.ReadRecords());
    }

    [Fact]
    public async Task Replication_BadRequestMessage_UnderExpectReplicationLag_IsRetriedOnPost()
    {
        using var context = new FakeAzContext(nameof(Replication_BadRequestMessage_UnderExpectReplicationLag_IsRetriedOnPost));
        context.UseScenario(new FakeScenario()
            .Add(FakeResponse.GraphError("badRequestReplication"))
            .Add(FakeResponse.Json("{\"id\":\"assignment-1\"}")));
        using var runner = context.CreateRunner();
        var delays = new RecordingDelayProvider();
        var client = context.CreateGraphClient(runner, delays);
        var request = new GraphRequest("/servicePrincipals/sp-1/appRoleAssignedTo", hint: RetryHint.ExpectReplicationLag);

        var response = await client.PostAsync(request, new JsonObject { ["principalId"] = "p1" }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.Status);
        Assert.Equal([TimeSpan.FromSeconds(5)], delays.Delays);
        Assert.Equal(2, context.ReadRecords().Count);
    }

    [Fact]
    public async Task Unauthorized_TriggersExactlyOneTokenRefresh()
    {
        using var context = new FakeAzContext(nameof(Unauthorized_TriggersExactlyOneTokenRefresh));
        context.UseScenario(new FakeScenario()
            .Add(FakeResponse.GraphError("unauthorized").When(method: "GET"))
            .Add(FakeResponse.Json("{\"accessToken\":\"eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJhdWQiOiJncmFwaCJ9.c2lnbmF0dXJlLXNpZ25hdHVyZQ\",\"expiresOn\":\"2026-10-14 11:00:00.000000\",\"tokenType\":\"Bearer\"}").When(argvContains: "get-access-token"))
            .Add(FakeResponse.Json("{\"id\":\"u1\"}").When(method: "GET")));
        using var runner = context.CreateRunner();
        var delays = new RecordingDelayProvider();
        var client = context.CreateGraphClient(runner, delays);

        var body = await client.GetAsync(new GraphRequest("/users/u1"), Ct);

        Assert.Equal("u1", body?["id"]?.GetValue<string>());
        Assert.Empty(delays.Delays);
        var records = context.ReadRecords();
        Assert.Equal(3, records.Count);
        Assert.Equal("GET", records[0].ValueAfter("--method"));
        Assert.Equal(TokenRefreshArgv, records[1].Argv);
        Assert.Equal("GET", records[2].ValueAfter("--method"));
        Assert.Single(context.Log.Entries, e => e.EventId.Id == 1021);
        Assert.DoesNotContain("eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9", context.Log.AllText);
    }

    [Fact]
    public async Task Unauthorized_WhenTheRefreshFails_SurfacesNotLoggedIn()
    {
        using var context = new FakeAzContext(nameof(Unauthorized_WhenTheRefreshFails_SurfacesNotLoggedIn));
        context.UseScenario(new FakeScenario()
            .Add(FakeResponse.GraphError("unauthorized").When(method: "GET"))
            .Add(FakeResponse.GraphError("notLoggedIn").When(argvContains: "get-access-token")));
        using var runner = context.CreateRunner();
        var client = context.CreateGraphClient(runner, new RecordingDelayProvider());

        var error = await Assert.ThrowsAsync<AzCliException>(() => client.GetAsync(new GraphRequest("/users/u1"), Ct));

        Assert.Equal(AzCliFailure.NotLoggedIn, error.Failure);
        Assert.Contains("az login --allow-no-subscriptions", error.Message);
        Assert.Equal(2, context.ReadRecords().Count);
    }

    [Fact]
    public async Task AlreadyExists_IsThrownImmediatelyEvenUnderExpectReplicationLag()
    {
        using var context = new FakeAzContext(nameof(AlreadyExists_IsThrownImmediatelyEvenUnderExpectReplicationLag));
        context.UseScenario(new FakeScenario()
            .Add(FakeResponse.GraphError("badRequestAlreadyExists"))
            .Add(FakeResponse.Json("{\"id\":\"never-reached\"}")));
        using var runner = context.CreateRunner();
        var delays = new RecordingDelayProvider();
        var client = context.CreateGraphClient(runner, delays);
        var request = new GraphRequest("/applications", hint: RetryHint.ExpectReplicationLag);

        var error = await Assert.ThrowsAsync<GraphException>(() => client.PostAsync(request, new JsonObject { ["uniqueName"] = "dup" }, Ct));

        Assert.True(error.IsAlreadyExists);
        Assert.Equal(HttpStatusCode.BadRequest, error.Status);
        Assert.False(error.IsReplication);
        Assert.Empty(delays.Delays);
        Assert.Single(context.ReadRecords());
    }

    [Fact]
    public async Task AzFailureWithoutGraphBody_IsRetriedThreeTimesInTotal()
    {
        using var context = new FakeAzContext(nameof(AzFailureWithoutGraphBody_IsRetriedThreeTimesInTotal));
        context.UseScenario(new FakeScenario()
            .Add(FakeResponse.GraphError("connectionError"))
            .Add(FakeResponse.GraphError("connectionError"))
            .Add(FakeResponse.GraphError("connectionError"))
            .Add(FakeResponse.Json("{\"id\":\"never-reached\"}")));
        using var runner = context.CreateRunner();
        var delays = new RecordingDelayProvider();
        var client = context.CreateGraphClient(runner, delays);

        var error = await Assert.ThrowsAsync<AzCliException>(() => client.GetAsync(new GraphRequest("/users/u1"), Ct));

        Assert.Equal(AzCliFailure.Transient, error.Failure);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delays.Delays);
        Assert.Equal(3, context.ReadRecords().Count);
    }

    [Fact]
    public async Task Batch_ChunksAtTwentyAndRetriesFailedSubResponsesIndividually()
    {
        using var context = new FakeAzContext(nameof(Batch_ChunksAtTwentyAndRetriesFailedSubResponsesIndividually));
        var requests = Enumerable.Range(1, 25)
            .Select(i => new BatchRequest("POST", "/groups/g1/members/$ref", new JsonObject { ["@odata.id"] = V1 + "/directoryObjects/m" + i.ToString(CultureInfo.InvariantCulture) }))
            .ToList();

        var firstChunk = new JsonArray();
        for (var id = 1; id <= 20; id++)
        {
            firstChunk.Add(id switch
            {
                3 => SubResponse(id, 429, ErrorBody("TooManyRequests", "Too many requests.")),
                7 => SubResponse(id, 404, ErrorBody("Request_ResourceNotFound", "Resource 'm7' does not exist or one of its queried reference-property objects are not present.")),
                9 => SubResponse(id, 400, ErrorBody("Request_BadRequest", "One or more added object references already exist for the following modified properties: 'members'.")),
                _ => SubResponse(id, 204, null),
            });
        }

        var secondChunk = new JsonArray();
        for (var id = 21; id <= 25; id++)
        {
            secondChunk.Add(SubResponse(id, 204, null));
        }

        context.UseScenario(new FakeScenario()
            .Add(FakeResponse.Json(new JsonObject { ["responses"] = firstChunk }).When(method: "POST", urlContains: "/$batch"))
            .Add(FakeResponse.Json(new JsonObject { ["responses"] = secondChunk }).When(method: "POST", urlContains: "/$batch"))
            .Add(FakeResponse.GraphError("tooManyRequestsNoRetryAfter").When(urlContains: "/members/$ref", bodyContains: "/m3\""))
            .Add(FakeResponse.Empty().When(urlContains: "/members/$ref", bodyContains: "/m3\""))
            .Add(FakeResponse.Empty().When(urlContains: "/members/$ref", bodyContains: "/m7\"")));
        using var runner = context.CreateRunner();
        var delays = new RecordingDelayProvider();
        var client = context.CreateGraphClient(runner, delays);

        var results = await client.BatchAsync(requests, GraphApi.V1, RetryHint.Default, Ct);

        Assert.Equal(25, results.Count);
        for (var i = 0; i < results.Count; i++)
        {
            Assert.Same(requests[i], results[i].Request);
            Assert.Equal((i + 1).ToString(CultureInfo.InvariantCulture), results[i].Id);
        }

        var alreadyExists = results[8];
        Assert.False(alreadyExists.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, alreadyExists.Status);
        Assert.NotNull(alreadyExists.Error);
        Assert.True(alreadyExists.Error.IsAlreadyExists);

        foreach (var (result, index) in results.Select(static (r, i) => (r, i)).Where(static p => p.i != 8))
        {
            Assert.True(result.Succeeded, $"sub-request {index} should have succeeded: {result.Error?.Message}");
            Assert.Equal(HttpStatusCode.NoContent, result.Status);
            Assert.Null(result.Error);
        }

        // The 429 sub-response was retried individually through the normal policy (one 2 s backoff), the 404 once.
        Assert.Equal([TimeSpan.FromSeconds(2)], delays.Delays);

        var records = context.ReadRecords();
        Assert.Equal(5, records.Count);

        Assert.Equal(V1 + "/$batch", records[0].ValueAfter("--url"));
        Assert.Equal(V1 + "/$batch", records[1].ValueAfter("--url"));
        Assert.All(records.Take(2), r => Assert.Equal("POST", r.ValueAfter("--method")));
        Assert.All(records.Take(2), r => Assert.Contains("Content-Type=application/json", r.Section("--headers")));

        var firstBody = Assert.IsType<JsonArray>(records[0].BodyJson?["requests"]);
        Assert.Equal(20, firstBody.Count);
        Assert.Equal(Enumerable.Range(1, 20).Select(static i => i.ToString(CultureInfo.InvariantCulture)), firstBody.Select(static r => r?["id"]?.GetValue<string>()));
        Assert.All(firstBody, r => Assert.Equal("POST", r?["method"]?.GetValue<string>()));
        Assert.All(firstBody, r => Assert.Equal("/groups/g1/members/$ref", r?["url"]?.GetValue<string>()));
        Assert.All(firstBody, r => Assert.Equal("application/json", r?["headers"]?["Content-Type"]?.GetValue<string>()));
        Assert.Equal(V1 + "/directoryObjects/m3", firstBody[2]?["body"]?["@odata.id"]?.GetValue<string>());

        var secondBody = Assert.IsType<JsonArray>(records[1].BodyJson?["requests"]);
        Assert.Equal(5, secondBody.Count);
        Assert.Equal(Enumerable.Range(21, 5).Select(static i => i.ToString(CultureInfo.InvariantCulture)), secondBody.Select(static r => r?["id"]?.GetValue<string>()));

        foreach (var retry in records.Skip(2))
        {
            Assert.Equal("POST", retry.ValueAfter("--method"));
            Assert.Equal(V1 + "/groups/g1/members/$ref", retry.ValueAfter("--url"));
            Assert.Contains("Content-Type=application/json", retry.Section("--headers"));
        }

        Assert.Contains("/m3\"", records[2].Body);
        Assert.Contains("/m3\"", records[3].Body);
        Assert.Contains("/m7\"", records[4].Body);
        Assert.Single(context.Log.Entries, e => e.EventId.Id == 1031 && e.Message.Contains("25 sub-requests in 2 chunk(s); 2 failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Batch_RejectsQueryStringsInPathsAndHandlesEmptyInput()
    {
        using var context = new FakeAzContext(nameof(Batch_RejectsQueryStringsInPathsAndHandlesEmptyInput));
        using var runner = context.CreateRunner();
        var client = context.CreateGraphClient(runner, new RecordingDelayProvider());

        var empty = await client.BatchAsync([], GraphApi.V1, RetryHint.Default, Ct);
        Assert.Empty(empty);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => client.BatchAsync([new BatchRequest("GET", "/users?$select=id")], GraphApi.V1, RetryHint.Default, Ct));
        Assert.Equal("requests", exception.ParamName);
        Assert.Empty(context.ReadRecords());
    }

    private static JsonObject SubResponse(int id, int status, JsonNode? body)
    {
        return new JsonObject
        {
            ["id"] = id.ToString(CultureInfo.InvariantCulture),
            ["status"] = status,
            ["headers"] = new JsonObject(),
            ["body"] = body,
        };
    }

    private static JsonObject ErrorBody(string code, string message)
    {
        return new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
                ["innerError"] = new JsonObject { ["request-id"] = "batch-" + code },
            },
        };
    }
}
