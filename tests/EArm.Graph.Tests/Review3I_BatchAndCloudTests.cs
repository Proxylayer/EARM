using System;
using System.Collections.Generic;
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
/// Review 3I, finding 1: a LATER <c>$batch</c> chunk whose envelope fails must not throw away the results of the chunks that already ran (their
/// writes committed); finding 2: <c>@odata.bind</c> (and <c>@odata.id</c>) references name the sovereign cloud's Graph host.
/// </summary>
public sealed class Review3I_BatchAndCloudTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string BatchReply(int from, int count, int status)
    {
        var responses = new JsonArray();
        for (var i = from; i < from + count; i++)
        {
            responses.Add(new JsonObject { ["id"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture), ["status"] = status });
        }

        return new JsonObject { ["responses"] = responses }.ToJsonString();
    }

    private static List<BatchRequest> Posts(int count, string method = "POST")
    {
        return Enumerable.Range(1, count)
            .Select(i => new BatchRequest(method, "/groups/g/members/$ref", new JsonObject { ["@odata.id"] = "https://graph.microsoft.com/v1.0/directoryObjects/" + i }, Id: "m" + i))
            .ToList();
    }

    // ---- finding 1 ----

    [Fact]
    public async Task ALaterChunksEnvelopeFailureKeepsTheEarlierChunksResultsAndMarksTheWritesAmbiguous()
    {
        using var rig = new Review1BRig(new ScriptedHttpHandler(
            ScriptedResponse.Json(BatchReply(1, 20, 204)),
            ScriptedResponse.Error(HttpStatusCode.ServiceUnavailable, "serviceNotAvailable", "down")));

        var results = await rig.Client.BatchAsync(Posts(21), GraphApi.V1, RetryHint.Default, Ct);

        Assert.Equal(21, results.Count);
        Assert.All(results.Take(20), static r => Assert.True(r.Succeeded));
        var last = results[20];
        Assert.False(last.Succeeded);
        Assert.Equal("m21", last.Id);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, last.Status);
        Assert.NotNull(last.Error);
        Assert.True(last.Error!.IsAmbiguous, "the write may have been processed before the envelope failed");
        Assert.Equal(2, rig.Scripted!.Requests.Count);
    }

    [Fact]
    public async Task ALaterChunkThatIsRejectedOutrightIsNotAmbiguousAndLaterChunksAreNeverSent()
    {
        using var rig = new Review1BRig(new ScriptedHttpHandler(
            ScriptedResponse.Json(BatchReply(1, 20, 204)),
            ScriptedResponse.Error(HttpStatusCode.Forbidden, "Authorization_RequestDenied", "no")));

        var results = await rig.Client.BatchAsync(Posts(45), GraphApi.V1, RetryHint.Default, Ct);

        Assert.Equal(45, results.Count);
        Assert.All(results.Take(20), static r => Assert.True(r.Succeeded));
        Assert.All(results.Skip(20), static r =>
        {
            Assert.False(r.Succeeded);
            Assert.False(r.Error!.IsAmbiguous);
            Assert.Equal(HttpStatusCode.Forbidden, r.Status);
        });
        Assert.Equal("Authorization_RequestDenied", results[20].Error!.Code);
        Assert.Equal("BatchNotSent", results[44].Error!.Code);
        Assert.Equal(2, rig.Scripted!.Requests.Count);
    }

    [Fact]
    public async Task OnlyTheWritesOfAFailedEnvelopeAreAmbiguous()
    {
        using var rig = new Review1BRig(new ScriptedHttpHandler(
            ScriptedResponse.Json(BatchReply(1, 20, 200)),
            ScriptedResponse.Error(HttpStatusCode.BadGateway, "badGateway", "x")));
        var requests = Enumerable.Range(1, 20).Select(i => new BatchRequest("GET", "/users/" + i)).ToList();
        requests.Add(new BatchRequest("GET", "/users/read"));
        requests.Add(new BatchRequest("POST", "/groups", new JsonObject { ["displayName"] = "g" }));

        var results = await rig.Client.BatchAsync(requests, GraphApi.V1, RetryHint.Default, Ct);

        Assert.False(results[20].Error!.IsAmbiguous);
        Assert.True(results[21].Error!.IsAmbiguous);
    }

    [Fact]
    public async Task ATransportFailureOfALaterChunkIsReportedPerItem()
    {
        using var rig = new Review1BRig(new ScriptedHttpHandler(ScriptedResponse.Json(BatchReply(1, 20, 204))));
        rig.Scripted!.Fallback = _ => new ScriptedResponse { Throw = new HttpRequestException(HttpRequestError.ConnectionError, "refused") };

        var results = await rig.Client.BatchAsync(Posts(21), GraphApi.V1, RetryHint.Default, Ct);

        Assert.All(results.Take(20), static r => Assert.True(r.Succeeded));
        Assert.False(results[20].Succeeded);
        Assert.Equal(HttpStatusCode.BadGateway, results[20].Status);
        Assert.False(results[20].Error!.IsAmbiguous, "a refused connection cannot hide a commit");
    }

    [Fact]
    public async Task AnEnvelopeWithoutResponsesInALaterChunkIsReportedPerItem()
    {
        using var rig = new Review1BRig(new ScriptedHttpHandler(ScriptedResponse.Json(BatchReply(1, 20, 204)), ScriptedResponse.Json("{\"nothing\":true}")));

        var results = await rig.Client.BatchAsync(Posts(21), GraphApi.V1, RetryHint.Default, Ct);

        Assert.True(results[0].Succeeded);
        Assert.False(results[20].Succeeded);
        Assert.NotNull(results[20].Error);
    }

    [Fact]
    public async Task AFirstChunkFailureStillThrowsBecauseNothingHasBeenReportedYet()
    {
        using var rig = new Review1BRig(new ScriptedHttpHandler(ScriptedResponse.Error(HttpStatusCode.ServiceUnavailable, "serviceNotAvailable", "down")));

        var error = await Assert.ThrowsAsync<GraphException>(() => rig.Client.BatchAsync(Posts(3), GraphApi.V1, RetryHint.Default, Ct));

        Assert.True(error.IsAmbiguous);
    }

    // ---- finding 2 ----

    [Theory]
    [InlineData("usgov", "https://graph.microsoft.us")]
    [InlineData("usgovdod", "https://dod-graph.microsoft.us")]
    [InlineData("china", "https://microsoftgraph.chinacloudapi.cn")]
    public async Task ODataBindReferencesNameTheSovereignCloudHost(string cloudKey, string origin)
    {
        var cloud = GraphCloud.Parse(cloudKey);
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.Json("{\"id\":\"g1\"}", HttpStatusCode.Created)), cloud: cloud);
        var body = new JsonObject
        {
            ["displayName"] = "see https://graph.microsoft.com/v1.0/directoryObjects/untouched",
            ["owners@odata.bind"] = new JsonArray("https://graph.microsoft.com/v1.0/directoryObjects/o1", "https://graph.microsoft.com/v1.0/directoryObjects/o2"),
            ["members@odata.bind"] = new JsonArray("https://graph.microsoft.com/beta/directoryObjects/m1"),
            ["manager@odata.bind"] = "https://graph.microsoft.com/v1.0/users/u1",
            ["nested"] = new JsonObject { ["@odata.id"] = "https://graph.microsoft.com/v1.0/directoryObjects/n1" },
        };

        await harness.Client.PostAsync(new GraphRequest("/groups"), body, Ct);

        var sent = JsonNode.Parse(harness.Handler.Requests.Single().Body!)!.AsObject();
        Assert.Equal(origin + "/v1.0/directoryObjects/o1", sent["owners@odata.bind"]![0]!.GetValue<string>());
        Assert.Equal(origin + "/v1.0/directoryObjects/o2", sent["owners@odata.bind"]![1]!.GetValue<string>());
        Assert.Equal(origin + "/beta/directoryObjects/m1", sent["members@odata.bind"]![0]!.GetValue<string>());
        Assert.Equal(origin + "/v1.0/users/u1", sent["manager@odata.bind"]!.GetValue<string>());
        Assert.Equal(origin + "/v1.0/directoryObjects/n1", sent["nested"]!["@odata.id"]!.GetValue<string>());
        Assert.Equal("see https://graph.microsoft.com/v1.0/directoryObjects/untouched", sent["displayName"]!.GetValue<string>());
        Assert.Equal("https://graph.microsoft.com/v1.0/directoryObjects/o1", body["owners@odata.bind"]![0]!.GetValue<string>());
    }

    [Theory]
    [InlineData("usgov", "https://graph.microsoft.us")]
    [InlineData("china", "https://microsoftgraph.chinacloudapi.cn")]
    public async Task ReferencesInsideABatchEnvelopeAreRewrittenToo(string cloudKey, string origin)
    {
        var cloud = GraphCloud.Parse(cloudKey);
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.Json(BatchReply(1, 1, 204))), cloud: cloud);
        var request = new BatchRequest("POST", "/groups/g/members/$ref", new JsonObject { ["@odata.id"] = "https://graph.microsoft.com/v1.0/directoryObjects/x" });

        await harness.Client.BatchAsync([request], GraphApi.V1, RetryHint.Default, Ct);

        Assert.Contains(origin + "/v1.0/directoryObjects/x", harness.Handler.Requests.Single().Body, StringComparison.Ordinal);
        Assert.DoesNotContain("graph.microsoft.com", harness.Handler.Requests.Single().Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePublicCloudLeavesReferencesAlone()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.Json("{\"id\":\"g1\"}", HttpStatusCode.Created)));

        await harness.Client.PostAsync(new GraphRequest("/groups"), new JsonObject { ["owners@odata.bind"] = new JsonArray("https://graph.microsoft.com/v1.0/directoryObjects/o1") }, Ct);

        Assert.Contains("https://graph.microsoft.com/v1.0/directoryObjects/o1", harness.Handler.Requests.Single().Body, StringComparison.Ordinal);
    }
}
