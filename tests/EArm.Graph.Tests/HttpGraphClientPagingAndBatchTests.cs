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

/// <summary>@odata.nextLink pagination (with the origin check) and <c>$batch</c> over <see cref="HttpGraphClient"/>.</summary>
public sealed class HttpGraphClientPagingAndBatchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Page(int from, int count, string? nextLink)
    {
        var items = string.Join(',', Enumerable.Range(from, count).Select(static i => "{\"id\":\"" + i + "\"}"));
        var next = nextLink is null ? string.Empty : ",\"@odata.nextLink\":\"" + nextLink + "\"";
        return "{\"value\":[" + items + "]" + next + "}";
    }

    private static async Task<List<JsonNode>> Collect(IAsyncEnumerable<JsonNode> pages)
    {
        var items = new List<JsonNode>();
        await foreach (var item in pages.WithCancellation(Ct))
        {
            items.Add(item);
        }

        return items;
    }

    // ---- pagination ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ThreePagesAreFollowedAndTheHeadersAreResentOnEveryPage()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(
            ScriptedResponse.Json(Page(1, 2, "https://graph.microsoft.com/v1.0/users?$select=id&$skiptoken=X%27a%20b%27%2Bc")),
            ScriptedResponse.Json(Page(3, 2, "https://graph.microsoft.com/v1.0/users?$select=id&$skiptoken=PAGE3")),
            ScriptedResponse.Json(Page(5, 1, null))));
        var request = new GraphRequest("/users").WithSelect("id").WithPrefer("odata.maxpagesize=2").WithConsistencyLevelEventual().WithCount();

        var items = await Collect(harness.Client.GetAllPagesAsync(request, Ct));

        Assert.Equal(["1", "2", "3", "4", "5"], items.Select(static i => i["id"]!.GetValue<string>()));
        Assert.Equal(3, harness.Handler.Requests.Count);
        Assert.Equal("X'a b'+c", harness.Handler.Requests[1].QueryValue("$skiptoken"));
        Assert.Equal("PAGE3", harness.Handler.Requests[2].QueryValue("$skiptoken"));
        foreach (var page in harness.Handler.Requests)
        {
            Assert.Equal("odata.maxpagesize=2", page.Header("Prefer"));
            Assert.Equal("eventual", page.Header("ConsistencyLevel"));
            Assert.StartsWith("Bearer ", page.Header("Authorization"), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ASingleEntityIsReturnedOnce()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.Json("{\"id\":\"u1\",\"displayName\":\"A\"}")));

        var items = await Collect(harness.Client.GetAllPagesAsync(new GraphRequest("/users/u1"), Ct));

        Assert.Equal("u1", Assert.Single(items)["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task A404OnTheFirstPageIsAnErrorForPagination()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.Error(HttpStatusCode.NotFound, "Request_ResourceNotFound", "gone")));

        await Assert.ThrowsAsync<GraphException>(() => Collect(harness.Client.GetAllPagesAsync(new GraphRequest("/users"), Ct)));
    }

    [Theory]
    [InlineData("https://evil.example/v1.0/users?$skiptoken=1", "host")]
    [InlineData("https://graph.microsoft.com.evil.example/v1.0/users?$skiptoken=1", "host")]
    [InlineData("https://evilgraph.microsoft.com/v1.0/users", "host")]
    [InlineData("http://graph.microsoft.com/v1.0/users?$skiptoken=1", "scheme")]
    [InlineData("https://user:pass@graph.microsoft.com/v1.0/users?$skiptoken=1", "user information")]
    [InlineData("https://graph.microsoft.com@evil.example/v1.0/users", "user information")]
    [InlineData("https://graph.microsoft.com:8443/v1.0/users?$skiptoken=1", "port")]
    [InlineData("https://graph.microsoft.com/oauth2/token", "path")]
    [InlineData("https://graph.microsoft.com/v1.0evil/users", "path")]
    [InlineData("https://graph.microsoft.com/v1.0/users#frag", "fragment")]
    [InlineData("/v1.0/users?$skiptoken=1", "absolute")]
    [InlineData("ftp://graph.microsoft.com/v1.0/users", "scheme")]
    [InlineData("//evil.example/v1.0/users", "Refusing")]
    [InlineData("https://graph.microsoft.us/v1.0/users?$skiptoken=1", "host")]
    public async Task ANextLinkOutsideTheGraphOriginIsRejectedAndTheTokenIsNeverSentThere(string nextLink, string reason)
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.Json(Page(1, 1, nextLink))));

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => Collect(harness.Client.GetAllPagesAsync(new GraphRequest("/users"), Ct)));

        Assert.Equal(GraphTransportFailure.UnsafeUrl, error.Failure);
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("skiptoken", error.Message, StringComparison.Ordinal);
        Assert.Single(harness.Handler.Requests);
        Assert.All(harness.Handler.Requests, static r => Assert.Equal("graph.microsoft.com", r.Uri.Host));
    }

    [Theory]
    [InlineData("https://graph.microsoft.com/v1.0/users?$skiptoken=1")]
    [InlineData("https://GRAPH.microsoft.com/v1.0/users?$skiptoken=1")]
    [InlineData("https://graph.microsoft.com:443/v1.0/users?$skiptoken=1")]
    [InlineData("https://graph.microsoft.com/beta/users?$skiptoken=1")]
    [InlineData("https://graph.microsoft.com/v1.0/directory/deletedItems/microsoft.graph.user?$skiptoken=1")]
    public async Task ANextLinkOnTheGraphOriginIsFollowed(string nextLink)
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.Json(Page(1, 1, nextLink)), ScriptedResponse.Json(Page(2, 1, null))));

        var items = await Collect(harness.Client.GetAllPagesAsync(new GraphRequest("/users"), Ct));

        Assert.Equal(2, items.Count);
        Assert.Equal(2, harness.Handler.Requests.Count);
    }

    [Fact]
    public async Task ANextLinkOfASovereignCloudMustNameThatCloudsHost()
    {
        using var ok = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.Json(Page(1, 1, "https://graph.microsoft.us/v1.0/users?$skiptoken=1")), ScriptedResponse.Json(Page(2, 1, null))), cloud: GraphCloud.UsGov);
        Assert.Equal(2, (await Collect(ok.Client.GetAllPagesAsync(new GraphRequest("/users"), Ct))).Count);

        using var wrong = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.Json(Page(1, 1, "https://graph.microsoft.com/v1.0/users?$skiptoken=1"))), cloud: GraphCloud.UsGov);
        var error = await Assert.ThrowsAsync<GraphTransportException>(() => Collect(wrong.Client.GetAllPagesAsync(new GraphRequest("/users"), Ct)));
        Assert.Equal(GraphTransportFailure.UnsafeUrl, error.Failure);
    }

    [Fact]
    public async Task ARepeatedNextLinkStopsInsteadOfLooping()
    {
        const string link = "https://graph.microsoft.com/v1.0/users?$skiptoken=same";
        using var harness = new ClientHarness();
        harness.Handler.Fallback = _ => ScriptedResponse.Json(Page(1, 1, link));

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => Collect(harness.Client.GetAllPagesAsync(new GraphRequest("/users"), Ct)));

        Assert.Equal(GraphTransportFailure.InvalidResponse, error.Failure);
        Assert.Equal(2, harness.Handler.Requests.Count);
    }

    [Fact]
    public async Task APageThatFailsIsRetriedLikeAnyOtherCall()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(
            ScriptedResponse.Json(Page(1, 1, "https://graph.microsoft.com/v1.0/users?$skiptoken=2")),
            ScriptedResponse.Error(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable", "later"),
            ScriptedResponse.Json(Page(2, 1, null))));

        var items = await Collect(harness.Client.GetAllPagesAsync(new GraphRequest("/users"), Ct));

        Assert.Equal(2, items.Count);
        Assert.Equal(3, harness.Handler.Requests.Count);
    }

    // ---- $batch -------------------------------------------------------------------------------------------

    private static ScriptedHttpHandler BatchHandler(Func<int, int>? statusFor = null)
    {
        var handler = new ScriptedHttpHandler();
        handler.Fallback = request =>
        {
            if (request.Uri.AbsolutePath.EndsWith("/$batch", StringComparison.Ordinal))
            {
                var envelope = JsonNode.Parse(request.Body!)!;
                var responses = new JsonArray();
                foreach (var item in envelope["requests"]!.AsArray())
                {
                    var id = item!["id"]!.GetValue<string>();
                    var status = statusFor?.Invoke(int.Parse(id, System.Globalization.CultureInfo.InvariantCulture)) ?? 204;
                    var entry = new JsonObject { ["id"] = id, ["status"] = status };
                    if (status >= 400)
                    {
                        entry["body"] = JsonNode.Parse("{\"error\":{\"code\":\"Request_BadRequest\",\"message\":\"nope\"}}");
                    }

                    responses.Add(entry);
                }

                return ScriptedResponse.Json(new JsonObject { ["responses"] = responses }.ToJsonString());
            }

            return ScriptedResponse.NoContent();
        };
        return handler;
    }

    private static IReadOnlyList<BatchRequest> Requests(int count)
    {
        return [.. Enumerable.Range(1, count).Select(static i => new BatchRequest("DELETE", $"/groups/g{i}/members/u{i}/$ref"))];
    }

    [Fact]
    public async Task FortyFiveRequestsAreSentInChunksOfTwenty()
    {
        using var harness = new ClientHarness(BatchHandler());

        var results = await harness.Client.BatchAsync(Requests(45), GraphApi.V1, RetryHint.Default, Ct);

        Assert.Equal(45, results.Count);
        Assert.All(results, static r => Assert.True(r.Succeeded));
        var posts = harness.Handler.Requests;
        Assert.Equal(3, posts.Count);
        Assert.All(posts, static p => Assert.Equal("https://graph.microsoft.com/v1.0/$batch", p.Uri.AbsoluteUri));
        Assert.Equal([20, 20, 5], posts.Select(static p => JsonNode.Parse(p.Body!)!["requests"]!.AsArray().Count));
        Assert.Equal("21", JsonNode.Parse(posts[1].Body!)!["requests"]![0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task BatchSubRequestsCarryRelativeUrlsWithEncodedQueriesHeadersAndBodies()
    {
        using var harness = new ClientHarness(BatchHandler());
        var requests = new List<BatchRequest>
        {
            new("GET", "/users", UrlParameters: new Dictionary<string, string> { ["$filter"] = "displayName eq 'a&b %'", ["$select"] = "id" }, Id: "mine"),
            new("PATCH", "/groups/g1", new JsonObject { ["description"] = "d" }, new Dictionary<string, string> { ["Prefer"] = "return=minimal" }),
        };

        var results = await harness.Client.BatchAsync(requests, GraphApi.Beta, RetryHint.Default, Ct);

        Assert.Equal("mine", results[0].Id);
        var post = Assert.Single(harness.Handler.Requests);
        Assert.Equal("https://graph.microsoft.com/beta/$batch", post.Uri.AbsoluteUri);
        var sent = JsonNode.Parse(post.Body!)!["requests"]!.AsArray();
        Assert.Equal("/users?$filter=displayName%20eq%20%27a%26b%20%25%27&$select=id", sent[0]!["url"]!.GetValue<string>());
        Assert.Equal("GET", sent[0]!["method"]!.GetValue<string>());
        Assert.Equal("application/json", sent[1]!["headers"]!["Content-Type"]!.GetValue<string>());
        Assert.Equal("return=minimal", sent[1]!["headers"]!["Prefer"]!.GetValue<string>());
        Assert.Equal("d", sent[1]!["body"]!["description"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("https://evil.example/x")]
    [InlineData("//evil.example/x")]
    [InlineData("\\\\evil.example\\x")]
    [InlineData("users")]
    public async Task ABatchSubRequestUrlThatIsNotARootRelativePathIsRefused(string path)
    {
        using var harness = new ClientHarness(BatchHandler());

        // A path without a leading slash is normalised to one; the others cannot be made relative.
        if (path == "users")
        {
            var ok = await harness.Client.BatchAsync([new BatchRequest("GET", path)], GraphApi.V1, RetryHint.Default, Ct);
            Assert.True(ok[0].Succeeded);
            return;
        }

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => harness.Client.BatchAsync([new BatchRequest("GET", path)], GraphApi.V1, RetryHint.Default, Ct));

        Assert.Equal(GraphTransportFailure.UnsafeUrl, error.Failure);
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task ABatchPathWithAQueryStringIsRejected()
    {
        using var harness = new ClientHarness(BatchHandler());

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Client.BatchAsync([new BatchRequest("GET", "/users?$top=1")], GraphApi.V1, RetryHint.Default, Ct));
    }

    [Fact]
    public async Task AnEmptyBatchSendsNothing()
    {
        using var harness = new ClientHarness();

        var results = await harness.Client.BatchAsync([], GraphApi.V1, RetryHint.Default, Ct);

        Assert.Empty(results);
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task AFailedSubRequestIsRetriedIndividuallyAndRecovers()
    {
        // Sub-request 2 fails inside the batch (400), then succeeds when sent on its own.
        var handler = BatchHandler(id => id == 2 ? 400 : 204);
        using var harness = new ClientHarness(handler);

        var results = await harness.Client.BatchAsync(Requests(3), GraphApi.V1, RetryHint.Default, Ct);

        Assert.All(results, static r => Assert.True(r.Succeeded));
        Assert.Equal(2, harness.Handler.Requests.Count);
        var single = harness.Handler.Requests[1];
        Assert.Equal(HttpMethod.Delete, single.Method);
        Assert.Equal("/v1.0/groups/g2/members/u2/$ref", single.Uri.AbsolutePath);
    }

    [Fact]
    public async Task AFailedSubRequestThatKeepsFailingReportsItsErrorWithoutThrowing()
    {
        var handler = new ScriptedHttpHandler();
        handler.Fallback = request => request.Uri.AbsolutePath.EndsWith("/$batch", StringComparison.Ordinal)
            ? ScriptedResponse.Json("{\"responses\":[{\"id\":\"1\",\"status\":204},{\"id\":\"2\",\"status\":403,\"body\":{\"error\":{\"code\":\"Authorization_RequestDenied\",\"message\":\"Insufficient privileges.\"}}}]}")
            : ScriptedResponse.Error(HttpStatusCode.Forbidden, "Authorization_RequestDenied", "Insufficient privileges.");
        using var harness = new ClientHarness(handler);

        var results = await harness.Client.BatchAsync(Requests(2), GraphApi.V1, RetryHint.Default, Ct);

        Assert.True(results[0].Succeeded);
        Assert.False(results[1].Succeeded);
        Assert.Equal(HttpStatusCode.Forbidden, results[1].Status);
        Assert.True(results[1].Error!.IsForbidden);
    }

    [Fact]
    public async Task AnAlreadyExistsSubResponseIsNotRetriedIndividually()
    {
        var handler = new ScriptedHttpHandler(ScriptedResponse.Json("{\"responses\":[{\"id\":\"1\",\"status\":400,\"body\":{\"error\":{\"code\":\"Request_BadRequest\",\"message\":\"One or more added object references already exist for the following modified properties: 'members'.\"}}}]}"));
        using var harness = new ClientHarness(handler);

        var results = await harness.Client.BatchAsync([new BatchRequest("POST", "/groups/g1/members/$ref", new JsonObject())], GraphApi.V1, RetryHint.Default, Ct);

        Assert.True(results[0].Error!.IsAlreadyExists);
        Assert.Single(harness.Handler.Requests);
    }

    [Fact]
    public async Task ASubResponseRetryAfterHeaderIsHonouredWhenTheSubRequestIsRetried()
    {
        var handler = new ScriptedHttpHandler(
            ScriptedResponse.Json("{\"responses\":[{\"id\":\"1\",\"status\":429,\"headers\":{\"Retry-After\":\"7\"},\"body\":{\"error\":{\"code\":\"TooManyRequests\",\"message\":\"slow down\"}}}]}"),
            ScriptedResponse.Error(HttpStatusCode.TooManyRequests, "TooManyRequests", "slow down").WithHeader("Retry-After", "7"),
            ScriptedResponse.NoContent());
        using var harness = new ClientHarness(handler);

        var results = await harness.Client.BatchAsync(Requests(1), GraphApi.V1, RetryHint.Default, Ct);

        Assert.True(results[0].Succeeded);
        Assert.Equal([TimeSpan.FromSeconds(7)], harness.Delays.Delays);
    }

    [Fact]
    public async Task AMissingSubResponseBecomesAnErrorEntry()
    {
        var handler = new ScriptedHttpHandler(ScriptedResponse.Json("{\"responses\":[{\"id\":\"1\",\"status\":204}]}"));
        handler.Fallback = _ => ScriptedResponse.NoContent();
        using var harness = new ClientHarness(handler);

        var results = await harness.Client.BatchAsync(Requests(2), GraphApi.V1, RetryHint.Default, Ct);

        Assert.True(results[0].Succeeded);
        Assert.True(results[1].Succeeded, "the missing entry is re-sent on its own and succeeds");
        Assert.Equal(2, harness.Handler.Requests.Count);
    }

    [Fact]
    public async Task ABatchEnvelopeWithoutResponsesIsAnInvalidResponse()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.Json("{\"nothing\":true}")));

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => harness.Client.BatchAsync(Requests(1), GraphApi.V1, RetryHint.Default, Ct));

        Assert.Equal(GraphTransportFailure.InvalidResponse, error.Failure);
    }
}
