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
/// Review finding E-05 (transport part): a non-idempotent write (POST) must never be repeated blindly after a failure that may have followed a
/// commit (timeout, dropped connection, 5xx); it is still retried when Graph provably did not process it (429, replication lag, 401 refresh,
/// a failure before the request was sent). The ambiguous outcome is surfaced so the caller can re-read by key.
/// </summary>
public sealed class Review1B_IdempotencyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ScriptedResponse Created()
    {
        return ScriptedResponse.Json("{\"id\":\"new\"}", HttpStatusCode.Created);
    }

    private static ScriptedResponse Fail(HttpStatusCode status)
    {
        return ScriptedResponse.Error(status, "serviceNotAvailable", "The service is unavailable.");
    }

    // ---- the default per method --------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", true)]
    [InlineData("get", true)]
    [InlineData("HEAD", true)]
    [InlineData("PUT", true)]
    [InlineData("DELETE", true)]
    [InlineData("PATCH", true)]
    [InlineData("POST", false)]
    [InlineData("post", false)]
    [InlineData("SOMETHING", false)]
    public void TheDefaultIsDecidedByTheHttpMethod(string method, bool expected)
    {
        Assert.Equal(expected, GraphRequest.IsIdempotentMethod(method));
        Assert.Equal(expected, new GraphRequest("/x").IsIdempotentFor(method));
        Assert.Equal(expected, new BatchRequest(method, "/x").IsIdempotent);
    }

    [Fact]
    public void AnExplicitFlagOverridesTheMethodDefault()
    {
        Assert.True(new GraphRequest("/x").WithIdempotent(true).IsIdempotentFor("POST"));
        Assert.False(new GraphRequest("/x").WithIdempotent(false).IsIdempotentFor("PUT"));
        Assert.Null(new GraphRequest("/x").Idempotent);
        Assert.True(new BatchRequest("POST", "/x", Idempotent: true).IsIdempotent);
        Assert.False(new BatchRequest("GET", "/x", Idempotent: false).IsIdempotent);
    }

    // ---- POST: never repeated after an ambiguous failure --------------------------------------------------------

    [Fact]
    public async Task APostThatTimesOutIsNotRepeatedAndIsAmbiguous()
    {
        using var rig = new Review1BRig(configure: o => o.Timeout = TimeSpan.FromMilliseconds(50));
        rig.Scripted!.Fallback = _ => new ScriptedResponse { Delay = TimeSpan.FromSeconds(30) };

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => rig.Client.PostAsync(new GraphRequest("/groups"), new JsonObject(), Ct));

        Assert.Equal(GraphTransportFailure.Timeout, error.Failure);
        Assert.True(error.Ambiguous);
        Assert.Single(rig.Scripted.Requests);
        Assert.Empty(rig.AllDelays);
    }

    [Fact]
    public async Task AGetThatTimesOutIsStillRetriedAndNotAmbiguous()
    {
        using var rig = new Review1BRig(configure: o => o.Timeout = TimeSpan.FromMilliseconds(50));
        rig.Scripted!.Fallback = _ => new ScriptedResponse { Delay = TimeSpan.FromSeconds(30) };

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => rig.Client.GetAsync(new GraphRequest("/users"), Ct));

        Assert.Equal(GraphTransportFailure.Timeout, error.Failure);
        Assert.False(error.Ambiguous);
        Assert.Equal(3, rig.Scripted.Requests.Count);
    }

    [Fact]
    public async Task APostWhoseConnectionDropsAfterSendingIsNotRepeated()
    {
        using var rig = new Review1BRig(new ScriptedHttpHandler(new ScriptedResponse { Throw = new HttpRequestException("The response ended prematurely.") }, Created()));

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => rig.Client.PostAsync(new GraphRequest("/applications/a1/addPassword"), new JsonObject(), Ct));

        Assert.Equal(GraphTransportFailure.Network, error.Failure);
        Assert.True(error.Ambiguous);
        Assert.Single(rig.Scripted!.Requests);
    }

    [Theory]
    [InlineData(HttpRequestError.ConnectionError)]
    [InlineData(HttpRequestError.NameResolutionError)]
    [InlineData(HttpRequestError.SecureConnectionError)]
    [InlineData(HttpRequestError.ProxyTunnelError)]
    public async Task APostThatFailedBeforeAnythingWasSentIsRetried(HttpRequestError kind)
    {
        using var rig = new Review1BRig(new ScriptedHttpHandler(new ScriptedResponse { Throw = new HttpRequestException(kind, "no route") }, Created()));

        var response = await rig.Client.PostAsync(new GraphRequest("/groups"), new JsonObject(), Ct);

        Assert.True(response.IsCreated);
        Assert.Equal(2, rig.Scripted!.Requests.Count);
    }

    [Fact]
    public async Task APostThatNeverConnectsEndsAsANonAmbiguousNetworkFailure()
    {
        using var rig = new Review1BRig();
        rig.Scripted!.Fallback = _ => new ScriptedResponse { Throw = new HttpRequestException(HttpRequestError.ConnectionError, "refused") };

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => rig.Client.PostAsync(new GraphRequest("/groups"), new JsonObject(), Ct));

        Assert.Equal(GraphTransportFailure.Network, error.Failure);
        Assert.False(error.Ambiguous);
        Assert.Equal(3, rig.Scripted.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task APostThatGetsA5xxIsNotRepeatedAndTheFailureSaysTheOutcomeIsUnknown(HttpStatusCode status)
    {
        using var rig = new Review1BRig(new ScriptedHttpHandler(Fail(status), Created()));

        var error = await Assert.ThrowsAsync<GraphException>(() => rig.Client.PostAsync(new GraphRequest("/identity/conditionalAccess/policies"), new JsonObject(), Ct));

        Assert.Equal(status, error.Status);
        Assert.True(error.IsAmbiguous);
        Assert.Single(rig.Scripted!.Requests);
        Assert.Empty(rig.AllDelays);
    }

    [Fact]
    public async Task A4xxIsNeverAmbiguous()
    {
        using var rig = new Review1BRig(new ScriptedHttpHandler(ScriptedResponse.Error(HttpStatusCode.BadRequest, "Request_BadRequest", "bad")));

        var error = await Assert.ThrowsAsync<GraphException>(() => rig.Client.PostAsync(new GraphRequest("/groups"), new JsonObject(), Ct));

        Assert.False(error.IsAmbiguous);
    }

    [Fact]
    public async Task APostRejectedWith429IsRetriedAfterRetryAfter()
    {
        using var rig = new Review1BRig(new ScriptedHttpHandler(
            ScriptedResponse.Error(HttpStatusCode.TooManyRequests, "TooManyRequests", "slow").WithHeader("Retry-After", "7"),
            Created()));

        var response = await rig.Client.PostAsync(new GraphRequest("/groups"), new JsonObject(), Ct);

        Assert.True(response.IsCreated);
        Assert.Equal([TimeSpan.FromSeconds(7)], rig.AllDelays);
        Assert.Equal(2, rig.Scripted!.Requests.Count);
    }

    [Fact]
    public async Task APostIsStillRetriedForReplicationLag()
    {
        using var rig = new Review1BRig(new ScriptedHttpHandler(
            ScriptedResponse.Error(HttpStatusCode.NotFound, "Request_ResourceNotFound", "Resource does not exist or one of its queried reference-property objects are not present."),
            Created()));

        var response = await rig.Client.PostAsync(new GraphRequest("/servicePrincipals/s1/appRoleAssignedTo").WithHint(RetryHint.ExpectReplicationLag), new JsonObject(), Ct);

        Assert.True(response.IsCreated);
        Assert.Equal([TimeSpan.FromSeconds(5)], rig.AllDelays);
    }

    [Fact]
    public async Task APostIsStillRetriedOnceAfterA401TokenRefresh()
    {
        using var rig = new Review1BRig(new ScriptedHttpHandler(ScriptedResponse.Error(HttpStatusCode.Unauthorized, "InvalidAuthenticationToken", "expired"), Created()));

        var response = await rig.Client.PostAsync(new GraphRequest("/groups"), new JsonObject(), Ct);

        Assert.True(response.IsCreated);
        Assert.Equal(1, rig.Tokens.Invalidations);
        Assert.Equal(2, rig.Scripted!.Requests.Count);
    }

    [Fact]
    public async Task APostMarkedIdempotentIsRetriedLikeAGet()
    {
        using var rig = new Review1BRig(new ScriptedHttpHandler(Fail(HttpStatusCode.ServiceUnavailable), Created()));

        var response = await rig.Client.PostAsync(new GraphRequest("/applications/a1/restore").WithIdempotent(true), new JsonObject(), Ct);

        Assert.True(response.IsCreated);
        Assert.Equal(2, rig.Scripted!.Requests.Count);
    }

    // ---- the other methods stay retried -------------------------------------------------------------------------

    [Fact]
    public async Task GetPutPatchAndDeleteAreRetriedOn503()
    {
        using var rig = new Review1BRig();
        rig.Scripted!.Fallback = request => rig.Scripted.Requests.Count(r => r.Method == request.Method) % 2 == 1
            ? Fail(HttpStatusCode.ServiceUnavailable)
            : ScriptedResponse.Json("{\"ok\":true}");

        await rig.Client.GetAsync(new GraphRequest("/users/u1"), Ct);
        await rig.Client.PutAsync(new GraphRequest("/users/u1/manager/$ref"), new JsonObject(), Ct);
        await rig.Client.PatchAsync(new GraphRequest("/users/u1"), new JsonObject(), Ct);
        await rig.Client.DeleteAsync(new GraphRequest("/users/u1"), Ct);

        Assert.Equal(8, rig.Scripted.Requests.Count);
        Assert.Equal(["GET", "GET", "PUT", "PUT", "PATCH", "PATCH", "DELETE", "DELETE"], rig.Scripted.Requests.Select(static r => r.Method.Method));
    }

    [Fact]
    public async Task APutMarkedNotIdempotentIsNotRepeatedOn5xx()
    {
        using var rig = new Review1BRig(new ScriptedHttpHandler(Fail(HttpStatusCode.BadGateway), ScriptedResponse.Json("{}")));

        var error = await Assert.ThrowsAsync<GraphException>(() => rig.Client.PutAsync(new GraphRequest("/x/$ref").WithIdempotent(false), new JsonObject(), Ct));

        Assert.True(error.IsAmbiguous);
        Assert.Single(rig.Scripted!.Requests);
    }

    [Fact]
    public async Task AReadThatFailsWith5xxIsNeverAmbiguous()
    {
        using var rig = new Review1BRig();
        rig.Scripted!.Fallback = _ => Fail(HttpStatusCode.BadGateway);

        var error = await Assert.ThrowsAsync<GraphException>(() => rig.Client.GetAsync(new GraphRequest("/users"), Ct));

        Assert.False(error.IsAmbiguous);
    }

    // ---- RetryPolicy.Decide with the flag ------------------------------------------------------------------------

    [Fact]
    public void DecideStopsOnAmbiguousTransportFailuresOnlyForNonIdempotentCalls()
    {
        var policy = new RetryPolicy(new RetryOptions(), new RecordingDelayProvider(), new TestLogger<RetryPolicy>(new LogSink()));
        var ambiguous = new GraphTransportException(GraphTransportFailure.Timeout, "timeout", null, ambiguous: true);
        var beforeSending = new GraphTransportException(GraphTransportFailure.Network, "refused", null, ambiguous: false);

        Assert.False(policy.Decide(ambiguous, RetryHint.Default, new RetryState(), idempotent: false).Retry);
        Assert.True(policy.Decide(ambiguous, RetryHint.Default, new RetryState(), idempotent: true).Retry);
        Assert.True(policy.Decide(beforeSending, RetryHint.Default, new RetryState(), idempotent: false).Retry);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.BadGateway, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    [InlineData(HttpStatusCode.GatewayTimeout, false)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    public void DecideForANonIdempotentCallRetriesOnlyWhatWasRejectedBeforeProcessing(HttpStatusCode status, bool retried)
    {
        var policy = new RetryPolicy(new RetryOptions(), new RecordingDelayProvider(), new TestLogger<RetryPolicy>(new LogSink()));
        var error = new GraphException(status, "c", "m");

        Assert.Equal(retried, policy.Decide(error, RetryHint.Default, new RetryState(), idempotent: false).Retry);
    }

    // ---- $batch --------------------------------------------------------------------------------------------------

    private static string WireIds(RecordedRequest envelope, out List<(string Id, string Method)> subs)
    {
        subs = [];
        var body = JsonNode.Parse(envelope.Body!)!;
        foreach (var item in body["requests"]!.AsArray())
        {
            subs.Add((item!["id"]!.GetValue<string>(), item["method"]!.GetValue<string>()));
        }

        return envelope.Body!;
    }

    private static string BatchReply(IEnumerable<(string Id, int Status)> subs)
    {
        var responses = string.Join(',', subs.Select(static s => "{\"id\":\"" + s.Id + "\",\"status\":" + s.Status + ",\"body\":" + (s.Status >= 400 ? "{\"error\":{\"code\":\"x\",\"message\":\"m\"}}" : "{\"ok\":true}") + "}"));
        return "{\"responses\":[" + responses + "]}";
    }

    [Fact]
    public async Task AnEnvelopeOfOnlyGetsIsRetriedOn504()
    {
        using var rig = new Review1BRig(new ScriptedHttpHandler(Fail(HttpStatusCode.GatewayTimeout), ScriptedResponse.Json(BatchReply([("1", 200), ("2", 200)]))));

        var results = await rig.Client.BatchAsync([new BatchRequest("GET", "/users/a"), new BatchRequest("GET", "/users/b")], GraphApi.V1, RetryHint.Default, Ct);

        Assert.All(results, static r => Assert.True(r.Succeeded));
        Assert.Equal(2, rig.Scripted!.Requests.Count);
    }

    [Fact]
    public async Task AnEnvelopeContainingAPostIsNotRetriedOn504()
    {
        using var rig = new Review1BRig(new ScriptedHttpHandler(Fail(HttpStatusCode.GatewayTimeout), ScriptedResponse.Json(BatchReply([("1", 200), ("2", 201)]))));

        var error = await Assert.ThrowsAsync<GraphException>(() => rig.Client.BatchAsync([new BatchRequest("GET", "/users/a"), new BatchRequest("POST", "/groups", new JsonObject())], GraphApi.V1, RetryHint.Default, Ct));

        Assert.True(error.IsAmbiguous);
        Assert.Single(rig.Scripted!.Requests);
    }

    [Fact]
    public async Task AnEnvelopeContainingAPostMarkedIdempotentIsRetried()
    {
        using var rig = new Review1BRig(new ScriptedHttpHandler(Fail(HttpStatusCode.GatewayTimeout), ScriptedResponse.Json(BatchReply([("1", 201)]))));

        var results = await rig.Client.BatchAsync([new BatchRequest("POST", "/groups", new JsonObject(), Idempotent: true)], GraphApi.V1, RetryHint.Default, Ct);

        Assert.True(Assert.Single(results).Succeeded);
        Assert.Equal(2, rig.Scripted!.Requests.Count);
    }

    [Fact]
    public async Task AFailedPostSubRequestWith5xxIsNotResentButAFailedGetIs()
    {
        using var rig = new Review1BRig(new ScriptedHttpHandler(
            ScriptedResponse.Json(BatchReply([("1", 503), ("2", 503)])),
            ScriptedResponse.Json("{\"single\":true}")));

        var results = await rig.Client.BatchAsync([new BatchRequest("POST", "/groups", new JsonObject()), new BatchRequest("GET", "/users/b")], GraphApi.V1, RetryHint.Default, Ct);

        Assert.False(results[0].Succeeded);
        Assert.True(results[0].Error!.IsAmbiguous);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, results[0].Status);
        Assert.True(results[1].Succeeded);
        Assert.Equal(2, rig.Scripted!.Requests.Count);
        Assert.Equal("GET", rig.Scripted.Requests[1].Method.Method);
        Assert.EndsWith("/users/b", rig.Scripted.Requests[1].Uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AThrottledPostSubRequestIsResentBecauseItWasNotProcessed()
    {
        using var rig = new Review1BRig(new ScriptedHttpHandler(
            ScriptedResponse.Json(BatchReply([("1", 429)])),
            ScriptedResponse.Json("{\"id\":\"g1\"}", HttpStatusCode.Created)));

        var results = await rig.Client.BatchAsync([new BatchRequest("POST", "/groups", new JsonObject())], GraphApi.V1, RetryHint.Default, Ct);

        Assert.True(Assert.Single(results).Succeeded);
        Assert.Equal("POST", rig.Scripted!.Requests[1].Method.Method);
    }

    [Fact]
    public async Task AReplicationLagPostSubRequestIsResentUnderTheHint()
    {
        using var rig = new Review1BRig(new ScriptedHttpHandler(
            ScriptedResponse.Json("{\"responses\":[{\"id\":\"1\",\"status\":404,\"body\":{\"error\":{\"code\":\"Request_ResourceNotFound\",\"message\":\"Resource does not exist or one of its queried reference-property objects are not present.\"}}}]}"),
            ScriptedResponse.Json("{}", HttpStatusCode.Created)));

        var results = await rig.Client.BatchAsync([new BatchRequest("POST", "/groups/g/members/$ref", new JsonObject())], GraphApi.V1, RetryHint.ExpectReplicationLag, Ct);

        Assert.True(Assert.Single(results).Succeeded);
        Assert.Equal(2, rig.Scripted!.Requests.Count);
    }

    [Fact]
    public async Task APostSubRequestMarkedIdempotentIsResentOn5xx()
    {
        using var rig = new Review1BRig(new ScriptedHttpHandler(
            ScriptedResponse.Json(BatchReply([("1", 503)])),
            ScriptedResponse.Json("{}", HttpStatusCode.Created)));

        var results = await rig.Client.BatchAsync([new BatchRequest("POST", "/groups", new JsonObject(), Idempotent: true)], GraphApi.V1, RetryHint.Default, Ct);

        Assert.True(Assert.Single(results).Succeeded);
        Assert.Equal(2, rig.Scripted!.Requests.Count);
    }

    [Fact]
    public async Task AFailedPostSubRequestWith400IsNotResent()
    {
        using var rig = new Review1BRig(new ScriptedHttpHandler(ScriptedResponse.Json(BatchReply([("1", 400)])), ScriptedResponse.Json("{}")));

        var results = await rig.Client.BatchAsync([new BatchRequest("POST", "/groups", new JsonObject())], GraphApi.V1, RetryHint.Default, Ct);

        Assert.False(Assert.Single(results).Succeeded);
        Assert.False(results[0].Error!.IsAmbiguous);
        Assert.Single(rig.Scripted!.Requests);
    }

    [Fact]
    public async Task ASubRequestThatCannotBeResentDoesNotDiscardTheOthers()
    {
        using var rig = new Review1BRig(new ScriptedHttpHandler(
            ScriptedResponse.Json(BatchReply([("1", 503), ("2", 200)]))));
        rig.Scripted!.Fallback = _ => new ScriptedResponse { Throw = new HttpRequestException(HttpRequestError.ConnectionError, "refused") };

        var results = await rig.Client.BatchAsync([new BatchRequest("GET", "/users/a"), new BatchRequest("GET", "/users/b")], GraphApi.V1, RetryHint.Default, Ct);

        Assert.False(results[0].Succeeded);
        Assert.Equal(HttpStatusCode.BadGateway, results[0].Status);
        Assert.True(results[1].Succeeded);
    }

    // ---- B10: a missing sub-response is an error, never OK ------------------------------------------------------

    [Fact]
    public async Task AMissingSubResponseOfAWriteIsAnAmbiguousErrorAndIsNotResent()
    {
        using var rig = new Review1BRig(new ScriptedHttpHandler(ScriptedResponse.Json(BatchReply([("1", 200)])), ScriptedResponse.Json("{}", HttpStatusCode.Created)));

        var results = await rig.Client.BatchAsync([new BatchRequest("GET", "/users/a"), new BatchRequest("POST", "/groups", new JsonObject())], GraphApi.V1, RetryHint.Default, Ct);

        Assert.True(results[0].Succeeded);
        Assert.False(results[1].Succeeded);
        Assert.NotNull(results[1].Error);
        Assert.Equal(HttpStatusCode.InternalServerError, results[1].Status);
        Assert.True(results[1].Error!.IsAmbiguous);
        Assert.Contains("no response", results[1].Error!.Message, StringComparison.Ordinal);
        Assert.Single(rig.Scripted!.Requests);
    }

    [Fact]
    public async Task AMissingSubResponseOfARead_IsAnErrorUnlessTheIndividualResendSucceeds()
    {
        using var rig = new Review1BRig(new ScriptedHttpHandler(ScriptedResponse.Json(BatchReply([("1", 200)]))));
        rig.Scripted!.Fallback = _ => Fail(HttpStatusCode.InternalServerError);

        var results = await rig.Client.BatchAsync([new BatchRequest("GET", "/users/a"), new BatchRequest("GET", "/users/b")], GraphApi.V1, RetryHint.Default, Ct);

        Assert.True(results[0].Succeeded);
        Assert.False(results[1].Succeeded);
        Assert.NotNull(results[1].Error);
        Assert.Equal(HttpStatusCode.InternalServerError, results[1].Status);
        Assert.True(rig.Scripted.Requests.Count > 1, "the read is re-sent individually");
    }

    [Fact]
    public async Task AMissingSubResponseOfAReadIsHealedByTheIndividualResend()
    {
        using var rig = new Review1BRig(new ScriptedHttpHandler(ScriptedResponse.Json(BatchReply([("1", 200)])), ScriptedResponse.Json("{\"healed\":true}")));

        var results = await rig.Client.BatchAsync([new BatchRequest("GET", "/users/a"), new BatchRequest("GET", "/users/b")], GraphApi.V1, RetryHint.Default, Ct);

        Assert.True(results[1].Succeeded);
        Assert.True(results[1].Body!["healed"]!.GetValue<bool>());
    }

    // ---- G09: sub-response ids are bounded to their own chunk ----------------------------------------------------

    [Fact]
    public async Task SubResponsesOutsideTheirOwnChunkAreIgnored()
    {
        // 25 GETs: chunk 1 carries wire ids 1-20, chunk 2 carries 21-25.
        var requests = Enumerable.Range(1, 25).Select(static i => new BatchRequest("GET", "/users/u" + i)).ToList();
        var envelopes = 0;
        using var rig = new Review1BRig(new ScriptedHttpHandler());
        rig.Scripted!.Fallback = request =>
        {
            if (!request.Uri.AbsolutePath.EndsWith("/$batch", StringComparison.Ordinal))
            {
                return ScriptedResponse.Json("{\"single\":true}");
            }

            envelopes++;
            WireIds(request, out var subs);
            var entries = subs.Select(static s => "{\"id\":\"" + s.Id + "\",\"status\":200,\"body\":{\"own\":\"" + s.Id + "\"}}").ToList();
            if (envelopes == 1)
            {
                // Intruders: id 21 belongs to chunk 2, id 0 and 99 to nobody; real ids 1-20 follow.
                entries.Add("{\"id\":\"21\",\"status\":200,\"body\":{\"bogus\":true}}");
                entries.Add("{\"id\":\"0\",\"status\":200,\"body\":{\"bogus\":true}}");
                entries.Add("{\"id\":\"99\",\"status\":200,\"body\":{\"bogus\":true}}");
            }
            else
            {
                // Chunk 2 answers 22-25 only (21 is "missing") and tries to overwrite id 20 and 1 of chunk 1, plus ids beyond the count.
                entries.RemoveAll(static e => e.Contains("\"id\":\"21\"", StringComparison.Ordinal));
                entries.Add("{\"id\":\"20\",\"status\":500,\"body\":{\"error\":{\"code\":\"x\",\"message\":\"intruder\"}}}");
                entries.Add("{\"id\":\"1\",\"status\":500,\"body\":{\"error\":{\"code\":\"x\",\"message\":\"intruder\"}}}");
                entries.Add("{\"id\":\"26\",\"status\":500,\"body\":{\"error\":{\"code\":\"x\",\"message\":\"intruder\"}}}");
            }

            return ScriptedResponse.Json("{\"responses\":[" + string.Join(',', entries) + "]}");
        };

        var results = await rig.Client.BatchAsync(requests, GraphApi.V1, RetryHint.Default, Ct);

        Assert.Equal(25, results.Count);
        Assert.Equal(2, envelopes);
        for (var i = 0; i < 25; i++)
        {
            if (i == 20)
            {
                // The only sub-response claiming id 21 sat in chunk 1: ignored, so the request was missing and was re-sent individually.
                Assert.True(results[i].Succeeded);
                Assert.True(results[i].Body!["single"]!.GetValue<bool>());
                continue;
            }

            Assert.True(results[i].Succeeded, $"request {i + 1} must keep its own success");
            Assert.Equal((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), results[i].Body!["own"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task TheFirstAndLastWireIdOfEveryChunkAreAccepted()
    {
        var requests = Enumerable.Range(1, 22).Select(static i => new BatchRequest("GET", "/users/u" + i)).ToList();
        using var rig = new Review1BRig(new ScriptedHttpHandler());
        rig.Scripted!.Fallback = request =>
        {
            WireIds(request, out var subs);
            return ScriptedResponse.Json(BatchReply(subs.Select(static s => (s.Id, 200))));
        };

        var results = await rig.Client.BatchAsync(requests, GraphApi.V1, RetryHint.Default, Ct);

        Assert.All(results, static r => Assert.True(r.Succeeded));
        Assert.Equal(2, rig.Scripted.Requests.Count);
        Assert.Equal(Enumerable.Range(1, 22).Select(static i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), results.Select(static r => r.Id));
    }
}
