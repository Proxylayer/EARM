using System;
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
