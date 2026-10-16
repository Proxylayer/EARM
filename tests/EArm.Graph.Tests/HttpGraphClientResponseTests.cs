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

/// <summary>How <see cref="HttpGraphClient"/> turns real HTTP responses into <see cref="GraphResponse"/> or exceptions.</summary>
public sealed class HttpGraphClientResponseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.Accepted)]
    public async Task SuccessStatusesAreReportedAsTheyAre(HttpStatusCode status)
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.Json("{\"id\":\"x\"}", status)));

        var response = await harness.Client.PostAsync(new GraphRequest("/groups"), new JsonObject(), Ct);

        Assert.Equal(status, response.Status);
        Assert.Equal("x", response.Body!["id"]!.GetValue<string>());
        Assert.Equal(status == HttpStatusCode.Created, response.IsCreated);
    }

    [Fact]
    public async Task NoContentHasNoBodyAndTheRealStatus()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.NoContent()));

        var response = await harness.Client.PatchAsync(new GraphRequest("/groups/g1"), new JsonObject { ["a"] = 1 }, Ct);

        Assert.True(response.IsNoContent);
        Assert.Null(response.Body);
    }

    [Fact]
    public async Task AnOkWithAnEmptyBodyIsAnOkWithoutABody()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(new ScriptedResponse { Status = HttpStatusCode.OK, Body = string.Empty }));

        var response = await harness.Client.PostAsync(new GraphRequest("/x"), null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Null(response.Body);
    }

    [Fact]
    public async Task APostThatReturns200IsNotInventedIntoA201()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.Json("{\"id\":\"x\"}")));

        var response = await harness.Client.PostAsync(new GraphRequest("/x"), new JsonObject(), Ct);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.False(response.IsCreated);
    }

    [Fact]
    public async Task GetReturnsNullForA404ButThrowsUnderReplicationLag()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.Error(HttpStatusCode.NotFound, "Request_ResourceNotFound", "Resource 'x' does not exist.")));
        Assert.Null(await harness.Client.GetAsync(new GraphRequest("/users/x"), Ct));

        using var lagging = new ClientHarness();
        lagging.Handler.Fallback = _ => ScriptedResponse.Error(HttpStatusCode.NotFound, "Request_ResourceNotFound", "Resource 'x' does not exist.");
        var error = await Assert.ThrowsAsync<GraphException>(() => lagging.Client.GetAsync(new GraphRequest("/users/x").WithHint(RetryHint.ExpectReplicationLag), Ct));
        Assert.Equal(HttpStatusCode.NotFound, error.Status);
        Assert.True(error.IsReplication);
        Assert.Equal(19, lagging.Handler.Requests.Count);
        Assert.Equal(18, lagging.Delays.Delays.Count);
    }

    [Fact]
    public async Task AConditionalAccessCreateNamingANamedLocationThatHasNotReplicatedIsRetriedUnderReplicationLag()
    {
        // The real tenant answer to a CA policy POST sent 0.7 s after its named location was created.
        const string body = "{\"error\":{\"code\":\"BadRequest\",\"message\":\"1040: NamedLocation with id 071774e8-8656-4733-9914-e564af5f3d93 does not exist in the directory.\"}}";
        using var harness = new ClientHarness(new ScriptedHttpHandler(
            ScriptedResponse.Json(body, HttpStatusCode.BadRequest),
            ScriptedResponse.Json(body, HttpStatusCode.BadRequest),
            ScriptedResponse.Json("{\"id\":\"p1\"}", HttpStatusCode.Created)));

        var response = await harness.Client.PostAsync(new GraphRequest("/identity/conditionalAccess/policies").WithHint(RetryHint.ExpectReplicationLag), new JsonObject(), Ct);

        Assert.True(response.IsCreated);
        Assert.Equal(3, harness.Handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)], harness.Delays.Delays);
    }

    // The real tenant answer to a PIM for Groups request sent 1-6 s after the group was created (accepted about 15 s later).
    private const string PimGroupForbidden = "{\"error\":{\"code\":\"UnauthorizedAccessException\",\"message\":\"Attempted to perform an unauthorized operation.\"}}";

    [Fact]
    public async Task APimForGroupsRequestAnswered403UnauthorizedAccessExceptionIsRetriedUnderReplicationLag()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(
            ScriptedResponse.Json(PimGroupForbidden, HttpStatusCode.Forbidden),
            ScriptedResponse.Json(PimGroupForbidden, HttpStatusCode.Forbidden),
            ScriptedResponse.Json("{\"id\":\"r1\",\"status\":\"Provisioned\"}", HttpStatusCode.Created)));

        var response = await harness.Client.PostAsync(new GraphRequest("/identityGovernance/privilegedAccess/group/eligibilityScheduleRequests").WithHint(RetryHint.ExpectReplicationLag), new JsonObject(), Ct);

        Assert.True(response.IsCreated);
        Assert.Equal(3, harness.Handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)], harness.Delays.Delays);
    }

    [Fact]
    public async Task APimForGroupsListThatKeepsAnswering403StopsAfterTheReplicationWindow()
    {
        using var harness = new ClientHarness();
        harness.Handler.Fallback = _ => ScriptedResponse.Json(PimGroupForbidden, HttpStatusCode.Forbidden);

        var error = await Assert.ThrowsAsync<GraphException>(() => harness.Client.GetAsync(new GraphRequest("/identityGovernance/privilegedAccess/group/assignmentSchedules").WithHint(RetryHint.ExpectReplicationLag), Ct));

        Assert.True(error.IsForbidden);
        Assert.True(error.IsReplication);
        Assert.Equal(19, harness.Handler.Requests.Count);
        Assert.Equal(18, harness.Delays.Delays.Count);
    }

    [Fact]
    public async Task APimForGroups403WithoutTheReplicationHintFailsAtOnce()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.Json(PimGroupForbidden, HttpStatusCode.Forbidden)));

        var error = await Assert.ThrowsAsync<GraphException>(() => harness.Client.PostAsync(new GraphRequest("/identityGovernance/privilegedAccess/group/eligibilityScheduleRequests"), new JsonObject(), Ct));

        Assert.True(error.IsForbidden);
        Assert.False(error.IsReplication);
        Assert.Single(harness.Handler.Requests);
        Assert.Empty(harness.Delays.Delays);
    }

    [Fact]
    public async Task EachPimForGroups403RetriedAsReplication_IsLoggedAtInformation()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(
            ScriptedResponse.Json(PimGroupForbidden, HttpStatusCode.Forbidden),
            ScriptedResponse.Json(PimGroupForbidden, HttpStatusCode.Forbidden),
            ScriptedResponse.Json("{\"id\":\"r1\",\"status\":\"Provisioned\"}", HttpStatusCode.Created)));

        await harness.Client.PostAsync(new GraphRequest("/identityGovernance/privilegedAccess/group/assignmentScheduleRequests").WithHint(RetryHint.ExpectReplicationLag), new JsonObject(), Ct);

        var lines = harness.Log.Entries.Where(e => e.EventId.Id == 1024).ToList();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, e => Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, e.Level));
        Assert.All(lines, e => Assert.Contains("treated as replication lag", e.Text, StringComparison.Ordinal));
        Assert.All(lines, e => Assert.Contains("assignmentScheduleRequests", e.Text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task APimForGroups403WithoutTheReplicationHint_IsLoggedAtDebug()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.Json(PimGroupForbidden, HttpStatusCode.Forbidden)));

        await Assert.ThrowsAsync<GraphException>(() => harness.Client.GetAsync(new GraphRequest("/identityGovernance/privilegedAccess/group/eligibilitySchedules"), Ct));

        var line = Assert.Single(harness.Log.Entries, e => e.EventId.Id == 1025);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Debug, line.Level);
        Assert.DoesNotContain(harness.Log.Entries, e => e.EventId.Id == 1024);
    }

    [Theory]
    [InlineData("/roleManagement/directory/roleEligibilityScheduleRequests", "UnauthorizedAccessException")]
    [InlineData("/identityGovernance/privilegedAccess/group/eligibilityScheduleRequests", "Authorization_RequestDenied")]
    public async Task OtherForbiddenAnswersAreNotRetriedEvenUnderReplicationLag(string path, string code)
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.Error(HttpStatusCode.Forbidden, code, "Denied.")));

        var error = await Assert.ThrowsAsync<GraphException>(() => harness.Client.PostAsync(new GraphRequest(path).WithHint(RetryHint.ExpectReplicationLag), new JsonObject(), Ct));

        Assert.False(error.IsReplication);
        Assert.Single(harness.Handler.Requests);
    }

    [Fact]
    public async Task TheSameConditionalAccessAnswerWithoutTheReplicationHintFailsAtOnce()
    {
        const string body = "{\"error\":{\"code\":\"BadRequest\",\"message\":\"1040: NamedLocation with id 071774e8-8656-4733-9914-e564af5f3d93 does not exist in the directory.\"}}";
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.Json(body, HttpStatusCode.BadRequest)));

        var error = await Assert.ThrowsAsync<GraphException>(() => harness.Client.PostAsync(new GraphRequest("/identity/conditionalAccess/policies"), new JsonObject(), Ct));

        Assert.Equal(HttpStatusCode.BadRequest, error.Status);
        Assert.Equal("BadRequest", error.Code);
        Assert.False(error.IsReplication);
        Assert.Single(harness.Handler.Requests);
        Assert.Empty(harness.Delays.Delays);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "Request_BadRequest", false)]
    [InlineData(HttpStatusCode.Forbidden, "Authorization_RequestDenied", false)]
    [InlineData(HttpStatusCode.Conflict, "Request_MultipleObjectsWithSameKeyValue", false)]
    [InlineData(HttpStatusCode.MethodNotAllowed, "Request_UnsupportedQuery", false)]
    public async Task ClientErrorsMapToGraphExceptionWithTheRealStatusAndTheGraphCode(HttpStatusCode status, string code, bool transient)
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.Error(status, code, "Something is wrong.", "req-1")));

        var error = await Assert.ThrowsAsync<GraphException>(() => harness.Client.PostAsync(new GraphRequest("/x"), new JsonObject(), Ct));

        Assert.Equal(status, error.Status);
        Assert.Equal(code, error.Code);
        Assert.Equal("Something is wrong.", error.GraphMessage);
        Assert.Equal(transient, error.IsTransient);
        Assert.Equal("req-1", error.RequestId);
        Assert.StartsWith($"Graph {(int)status} {code}", error.Message, StringComparison.Ordinal);
        Assert.Single(harness.Handler.Requests);
        Assert.Empty(harness.Delays.Delays);
    }

    [Fact]
    public async Task TheRequestIdHeaderBeatsTheInnerErrorAndTheClientRequestIdIsEchoed()
    {
        var response = ScriptedResponse.Error(HttpStatusCode.BadRequest, "Request_BadRequest", "bad", "inner-id")
            .WithHeader("request-id", "header-id")
            .WithHeader("client-request-id", "echo-id");
        using var harness = new ClientHarness(new ScriptedHttpHandler(response));

        var error = await Assert.ThrowsAsync<GraphException>(() => harness.Client.GetAsync(new GraphRequest("/x").WithHint(RetryHint.None), Ct));

        Assert.Equal("header-id", error.RequestId);
        Assert.Equal("echo-id", error.ClientRequestId);
        Assert.Equal("Graph 400 Request_BadRequest: bad (request-id header-id, client-request-id echo-id)", error.Message);
    }

    [Fact]
    public async Task TheMessage_CarriesTheRequestIdsAndTheInnerErrorDate_ForSupport()
    {
        var response = ScriptedResponse.Json(
                """{"error":{"code":"UnauthorizedAccessException","message":"Attempted to perform an unauthorized operation.","innerError":{"date":"2026-10-15T08:00:00","request-id":"inner-id","client-request-id":"inner-client"}}}""",
                HttpStatusCode.Forbidden)
            .WithHeader("client-request-id", "echo-id");
        using var harness = new ClientHarness(new ScriptedHttpHandler(response));

        var error = await Assert.ThrowsAsync<GraphException>(() => harness.Client.GetAsync(new GraphRequest("/x").WithHint(RetryHint.None), Ct));

        Assert.Equal("Graph 403 UnauthorizedAccessException: Attempted to perform an unauthorized operation. (request-id inner-id, client-request-id echo-id, date 2026-10-15T08:00:00)", error.Message);
    }

    [Fact]
    public void TheMessage_WithoutIds_IsUnchanged_AndRegisteredSecretsStayMaskedWithIds()
    {
        Assert.Equal("Graph 404 Request_ResourceNotFound: gone", new GraphException(HttpStatusCode.NotFound, "Request_ResourceNotFound", "gone").Message);

        var withIds = new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", "password=Hunter2Secret! rejected", requestId: "r-1", clientRequestId: "c-1");
        Assert.DoesNotContain("Hunter2Secret!", withIds.Message, StringComparison.Ordinal);
        Assert.EndsWith("(request-id r-1, client-request-id c-1)", withIds.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheClientRequestIdWeSentIsUsedWhenGraphDoesNotEchoIt()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.Error(HttpStatusCode.BadRequest, "Request_BadRequest", "bad")));

        var error = await Assert.ThrowsAsync<GraphException>(() => harness.Client.GetAsync(new GraphRequest("/x"), Ct));

        Assert.Equal(Assert.Single(harness.Handler.Requests).Header("client-request-id"), error.ClientRequestId);
    }

    [Fact]
    public async Task ANonJsonErrorBodyStillYieldsAGraphExceptionWithTheStatus()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(new ScriptedResponse { Status = HttpStatusCode.Forbidden, Body = "<html>Access denied by gateway</html>" }));

        var error = await Assert.ThrowsAsync<GraphException>(() => harness.Client.GetAsync(new GraphRequest("/x"), Ct));

        Assert.Equal(HttpStatusCode.Forbidden, error.Status);
        Assert.Equal(string.Empty, error.Code);
        Assert.DoesNotContain("gateway", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASuccessBodyThatIsNotJsonIsAnInvalidResponse()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(new ScriptedResponse { Status = HttpStatusCode.OK, Body = "this is not json" }));

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => harness.Client.GetAsync(new GraphRequest("/x"), Ct));

        Assert.Equal(GraphTransportFailure.InvalidResponse, error.Failure);
        Assert.False(error.IsRetryable);
        Assert.Single(harness.Handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task ARedirectIsAnErrorAndIsNeverFollowed(HttpStatusCode status)
    {
        var redirect = new ScriptedResponse { Status = status }.WithHeader("Location", "https://evil.example/steal");
        using var harness = new ClientHarness(new ScriptedHttpHandler(redirect));

        var error = await Assert.ThrowsAsync<GraphException>(() => harness.Client.GetAsync(new GraphRequest("/users"), Ct));

        Assert.Equal(status, error.Status);
        Assert.Equal("UnexpectedRedirect", error.Code);
        Assert.False(error.IsTransient);
        Assert.DoesNotContain("evil.example", error.Message, StringComparison.Ordinal);
        var only = Assert.Single(harness.Handler.Requests);
        Assert.Equal("graph.microsoft.com", only.Uri.Host);
    }

    [Fact]
    public async Task ALoneSurrogateEscapeInAResponseIsReplacedSoLaterReadsDoNotThrow()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.Json("{\"displayName\":\"bad\\ud800name\",\"ok\":\"\\ud83d\\ude00\"}")));

        var body = Assert.IsType<JsonObject>(await harness.Client.GetAsync(new GraphRequest("/users/u"), Ct));

        Assert.Equal("bad�name", body["displayName"]!.GetValue<string>());
        Assert.Equal("😀", body["ok"]!.GetValue<string>());
    }

    [Fact]
    public async Task NonAsciiResponsesDecodeAsUtf8()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.Json("{\"displayName\":\"Ünï 日本語 😀\"}")));

        var body = Assert.IsType<JsonObject>(await harness.Client.GetAsync(new GraphRequest("/users/u"), Ct));

        Assert.Equal("Ünï 日本語 😀", body["displayName"]!.GetValue<string>());
    }

    [Fact]
    public async Task ABomPrefixedBodyIsParsed()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.Json("﻿{\"a\":1}")));

        var body = Assert.IsType<JsonObject>(await harness.Client.GetAsync(new GraphRequest("/x"), Ct));

        Assert.Equal(1, body["a"]!.GetValue<int>());
    }

    [Fact]
    public async Task ErrorMessagesAreRedactedOfSecretLookingValues()
    {
        const string secret = "Sup3rS3cret-Value";
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.Error(HttpStatusCode.BadRequest, "Request_BadRequest", "Invalid password=" + secret + " given")));

        var error = await Assert.ThrowsAsync<GraphException>(() => harness.Client.PostAsync(new GraphRequest("/x"), new JsonObject(), Ct));

        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, error.GraphMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeleteSucceedsOn204And404IsAnError()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.NoContent(), ScriptedResponse.Error(HttpStatusCode.NotFound, "Request_ResourceNotFound", "gone")));

        await harness.Client.DeleteAsync(new GraphRequest("/groups/g1"), Ct);
        var error = await Assert.ThrowsAsync<GraphException>(() => harness.Client.DeleteAsync(new GraphRequest("/groups/g2"), Ct));

        Assert.True(error.IsNotFound);
        Assert.All(harness.Handler.Requests, static r => Assert.Equal(HttpMethod.Delete, r.Method));
    }
}
