using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Types;
using EArm.Engine.Edges;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Graph;
using EArm.Providers;
using EArm.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Tests.Engine;

/// <summary>
/// Review 2F / mutation review of <see cref="EdgeExecutor"/>: the HTTP verb that is sent (a PATCH must not become a PUT), the exact point where the
/// pass switches to <c>$batch</c> (20 operations stay individual requests, 21 go out as one batch), the grouping by api and retry hint,
/// the order (adds before removes), how a missing or failed sub-response is reported, and that request bodies stay valid JSON on the wire for
/// object ids with quotes, backslashes and non-ASCII text.
/// </summary>
public sealed class Review2F_EdgeExecutorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IEdgeKindHandler Membership = Handler(EdgeKind.GroupMembership);

    private const string TargetGroup = "0a0a0a0a-0000-4000-8000-000000000001";

    private static IEdgeKindHandler Handler(EdgeKind kind)
    {
        Assert.True(ProviderDiscovery.CreateEdgeKinds(ResourceTypeCatalog.Default).TryGet(kind, out var handler));
        return handler;
    }

    private static string Principal(int index)
    {
        return new Guid(index, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]).ToString("D");
    }

    private static EdgeOperation Operation(string principalId, EdgeAction action = EdgeAction.Add, RetryHint hint = RetryHint.Default, GraphApi api = GraphApi.V1)
    {
        return new EdgeOperation(new Edge(EdgeKind.GroupMembership, TargetGroup, principalId), action, Membership, api, hint, null);
    }

    private static List<EdgeOperation> Operations(int count, EdgeAction action = EdgeAction.Add, RetryHint hint = RetryHint.Default, int first = 1)
    {
        return Enumerable.Range(first, count).Select(i => Operation(Principal(i), action, hint)).ToList();
    }

    private static EdgePlan Plan(IEnumerable<EdgeOperation> operations)
    {
        return new EdgePlan([], operations.ToList(), []);
    }

    private static EdgeGraphContext Context(IGraphClient graph)
    {
        return new EdgeGraphContext(graph, null, new LiveReferenceResolver(), [], NullLogger.Instance);
    }

    /// <summary>An <see cref="IGraphClient"/> that records what the edge pass asks for, and answers batches through a hook.</summary>
    private sealed class RecordingGraph : IGraphClient
    {
        private readonly object _gate = new();

        public List<(string Method, string Path, JsonNode? Body)> Calls { get; } = [];

        public List<IReadOnlyList<BatchRequest>> Batches { get; } = [];

        /// <summary>Answers a batch; the default answers every sub-request 204.</summary>
        public Func<IReadOnlyList<BatchRequest>, IReadOnlyList<BatchResponse>>? OnBatch { get; set; }

        private Task<GraphResponse> Record(string method, GraphRequest request, JsonNode? body)
        {
            lock (_gate)
            {
                Calls.Add((method, request.Path, body?.DeepClone()));
            }

            return Task.FromResult(new GraphResponse(HttpStatusCode.NoContent, null));
        }

        // Review round 2 (S2-06): an add whose answer was lost (a 5xx, a timeout) is read back once; nothing is ever found here, so such an add stays failed.
        public List<string> Reads { get; } = [];

        public Task<JsonNode?> GetAsync(GraphRequest request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Reads.Add(request.Path);
            }

            return Task.FromResult<JsonNode?>(null);
        }

        public Task<GraphResponse> PostAsync(GraphRequest request, JsonNode? body, CancellationToken cancellationToken) => Record("POST", request, body);

        public Task<GraphResponse> PatchAsync(GraphRequest request, JsonNode body, CancellationToken cancellationToken) => Record("PATCH", request, body);

        public Task<GraphResponse> PutAsync(GraphRequest request, JsonNode body, CancellationToken cancellationToken) => Record("PUT", request, body);

        public Task DeleteAsync(GraphRequest request, CancellationToken cancellationToken) => Record("DELETE", request, null);

        public async IAsyncEnumerable<JsonNode> GetAllPagesAsync(GraphRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Reads.Add(request.Path);
            }

            await Task.CompletedTask;
            yield break;
        }

        public Task<IReadOnlyList<BatchResponse>> BatchAsync(IReadOnlyList<BatchRequest> requests, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Batches.Add(requests);
            }

            IReadOnlyList<BatchResponse> responses = OnBatch is null
                ? [.. requests.Select(r => new BatchResponse(r.Id!, r, HttpStatusCode.NoContent, null, null))]
                : OnBatch(requests);
            return Task.FromResult(responses);
        }
    }

    // ---- the verb ----

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Send_UsesExactlyTheVerbOfTheRequest(string method)
    {
        var graph = new RecordingGraph();
        var body = new JsonObject { ["@odata.id"] = "https://graph.microsoft.com/v1.0/directoryObjects/x" };

        var outcome = await EdgeExecutor.SendAsync(new BatchRequest(method, "/groups/g/members/$ref", method == "DELETE" ? null : body), GraphApi.V1, RetryHint.Default, Context(graph), Ct);

        Assert.True(outcome.Succeeded);
        var call = Assert.Single(graph.Calls);
        Assert.Equal(method, call.Method);
        Assert.Equal("/groups/g/members/$ref", call.Path);
        if (method == "DELETE")
        {
            Assert.Null(call.Body);
        }
        else
        {
            Assert.Equal(body.ToJsonString(), call.Body!.ToJsonString());
        }
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task Send_ABodylessPutOrPatch_SendsAnEmptyObject_NotNull(string method)
    {
        var graph = new RecordingGraph();

        await EdgeExecutor.SendAsync(new BatchRequest(method, "/users/u/manager/$ref"), GraphApi.V1, RetryHint.Default, Context(graph), Ct);

        Assert.Equal("{}", Assert.Single(graph.Calls).Body!.ToJsonString());
    }

    [Fact]
    public async Task Send_RefusesAVerbTheEdgePassNeverUses_InsteadOfSendingSomethingElse()
    {
        var graph = new RecordingGraph();

        await Assert.ThrowsAsync<InvalidOperationException>(() => EdgeExecutor.SendAsync(new BatchRequest("GET", "/groups"), GraphApi.V1, RetryHint.Default, Context(graph), Ct));

        Assert.Empty(graph.Calls);
    }

    [Fact]
    public async Task Send_TheVerbIsCaseInsensitive()
    {
        var graph = new RecordingGraph();

        await EdgeExecutor.SendAsync(new BatchRequest("patch", "/x", new JsonObject()), GraphApi.V1, RetryHint.Default, Context(graph), Ct);

        Assert.Equal("PATCH", Assert.Single(graph.Calls).Method);
    }

    // ---- the $batch threshold: 20 individual requests, 21 one batch ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(19)]
    [InlineData(20)]
    public async Task UpToTwentyOperations_AreSentOneRequestEach_NoBatch(int count)
    {
        var graph = new RecordingGraph();

        var result = await EdgeExecutor.ApplyAsync(Plan(Operations(count)), Context(graph), Ct);

        Assert.Empty(graph.Batches);
        Assert.Equal(count, graph.Calls.Count);
        Assert.All(graph.Calls, c => Assert.Equal("POST", c.Method));
        Assert.Equal(count, result.Results.Count(r => r.Value.Succeeded));
    }

    [Theory]
    [InlineData(21)]
    [InlineData(45)]
    public async Task MoreThanTwentyOperations_GoOutAsOneBatch_InOrder_WithSequentialIds(int count)
    {
        var graph = new RecordingGraph();
        var operations = Operations(count);

        var result = await EdgeExecutor.ApplyAsync(Plan(operations), Context(graph), Ct);

        Assert.Empty(graph.Calls);
        var batch = Assert.Single(graph.Batches);
        Assert.Equal(count, batch.Count);
        Assert.Equal(Enumerable.Range(0, count).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), batch.Select(r => r.Id));
        Assert.All(batch, r => Assert.Equal("POST", r.Method));
        for (int i = 0; i < count; i++)
        {
            Assert.Equal("/groups/" + TargetGroup + "/members/$ref", batch[i].Path);
            Assert.Equal(EdgeGraphContext.ODataId(GraphApi.V1, "directoryObjects", Principal(i + 1)), batch[i].Body!["@odata.id"]!.GetValue<string>());
        }

        Assert.All(operations, o => Assert.True(result.Results[o].Succeeded));
    }

    [Fact]
    public async Task TheThresholdCountsPerApiAndRetryHint_NotInTotal()
    {
        var graph = new RecordingGraph();
        var operations = Operations(15).Concat(Operations(15, hint: RetryHint.ExpectReplicationLag, first: 100)).ToList();

        await EdgeExecutor.ApplyAsync(Plan(operations), Context(graph), Ct);

        // Two groups of fifteen: each below the threshold, so thirty individual requests and no batch.
        Assert.Empty(graph.Batches);
        Assert.Equal(30, graph.Calls.Count);
    }

    [Fact]
    public async Task OnlyTheGroupOverTheThreshold_IsBatched()
    {
        var graph = new RecordingGraph();
        var operations = Operations(21).Concat(Operations(3, hint: RetryHint.ExpectReplicationLag, first: 100)).ToList();

        await EdgeExecutor.ApplyAsync(Plan(operations), Context(graph), Ct);

        Assert.Equal(21, Assert.Single(graph.Batches).Count);
        Assert.Equal(3, graph.Calls.Count);
    }

    [Fact]
    public async Task AddsRunBeforeRemoves_AndEachGroupIsDecidedSeparately()
    {
        var graph = new RecordingGraph();
        var operations = Operations(21, EdgeAction.Remove, first: 200).Concat(Operations(2, EdgeAction.Add)).ToList();

        await EdgeExecutor.ApplyAsync(Plan(operations), Context(graph), Ct);

        // The two adds (below the threshold) are individual POSTs and come first; the 21 removes follow as one batch of DELETEs.
        Assert.Equal(["POST", "POST"], graph.Calls.Select(c => c.Method));
        var batch = Assert.Single(graph.Batches);
        Assert.Equal(21, batch.Count);
        Assert.All(batch, r => Assert.Equal("DELETE", r.Method));
        Assert.All(batch, r => Assert.Null(r.Body));
    }

    // ---- the batch answers ----

    [Fact]
    public async Task AMissingSubResponse_FailsThatOperationOnly_WithAMessage()
    {
        var graph = new RecordingGraph
        {
            OnBatch = requests => [.. requests.Where(r => r.Id != "20").Select(r => new BatchResponse(r.Id!, r, HttpStatusCode.NoContent, null, null))],
        };
        var operations = Operations(21);

        var result = await EdgeExecutor.ApplyAsync(Plan(operations), Context(graph), Ct);

        var missing = result.Results[operations[20]];
        Assert.False(missing.Succeeded);
        Assert.Contains("has no entry", missing.Error, StringComparison.Ordinal);
        Assert.All(operations.Take(20), o => Assert.True(result.Results[o].Succeeded));
    }

    [Fact]
    public async Task SubResponsesAreMatchedById_NotByPosition()
    {
        var graph = new RecordingGraph
        {
            OnBatch = requests =>
            [
                .. requests.Reverse().Select(r => r.Id == "3"
                    ? new BatchResponse(r.Id!, r, HttpStatusCode.Forbidden, null, new GraphException(HttpStatusCode.Forbidden, "Authorization_RequestDenied", "no", RetryHint.Default))
                    : new BatchResponse(r.Id!, r, HttpStatusCode.NoContent, null, null)),
            ],
        };
        var operations = Operations(21);

        var result = await EdgeExecutor.ApplyAsync(Plan(operations), Context(graph), Ct);

        Assert.False(result.Results[operations[3]].Succeeded);
        Assert.Contains("no", result.Results[operations[3]].Error, StringComparison.Ordinal);
        Assert.Equal(20, operations.Count(o => result.Results[o].Succeeded));
    }

    [Fact]
    public async Task ABatchSubResponseThatFailsWithoutAnErrorObject_IsStillAFailure()
    {
        var graph = new RecordingGraph
        {
            OnBatch = requests => [.. requests.Select(r => r.Id == "0"
                ? new BatchResponse(r.Id!, r, HttpStatusCode.InternalServerError, null, null)
                : new BatchResponse(r.Id!, r, HttpStatusCode.NoContent, null, null))],
        };
        var operations = Operations(21);

        var result = await EdgeExecutor.ApplyAsync(Plan(operations), Context(graph), Ct);

        Assert.False(result.Results[operations[0]].Succeeded);
        Assert.Contains("500", result.Results[operations[0]].Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AlreadyThereOnAnAdd_AndGoneOnARemove_AreIdempotentSuccesses_InABatch()
    {
        GraphException Failure(HttpStatusCode status, string code, string message) => new(status, code, message, RetryHint.Default);
        var graph = new RecordingGraph
        {
            OnBatch = requests => [.. requests.Select(r => r.Id switch
            {
                "0" => new BatchResponse(r.Id!, r, HttpStatusCode.BadRequest, null, Failure(HttpStatusCode.BadRequest, "Request_BadRequest", "One or more added object references already exist for the following modified properties: 'members'.")),
                "1" => new BatchResponse(r.Id!, r, HttpStatusCode.NotFound, null, Failure(HttpStatusCode.NotFound, "Request_ResourceNotFound", "Resource does not exist.")),
                _ => new BatchResponse(r.Id!, r, HttpStatusCode.NoContent, null, null),
            })],
        };
        var adds = Operations(21);
        var removes = Operations(21, EdgeAction.Remove, first: 300);

        var addResult = await EdgeExecutor.ApplyAsync(Plan(adds), Context(graph), Ct);
        var removeResult = await EdgeExecutor.ApplyAsync(Plan(removes), Context(graph), Ct);

        Assert.True(addResult.Results[adds[0]].AlreadyInPlace);
        Assert.False(addResult.Results[adds[1]].Succeeded, "a 404 is only idempotent for a remove");
        Assert.False(removeResult.Results[removes[0]].Succeeded, "'already exists' is only idempotent for an add");
        Assert.True(removeResult.Results[removes[1]].AlreadyInPlace);
    }

    [Fact]
    public async Task AWholeBatchThatFails_FailsEveryOperationInIt()
    {
        var graph = new RecordingGraph
        {
            OnBatch = _ => throw new GraphException(HttpStatusCode.BadGateway, "BadGateway", "the gateway fell over", RetryHint.Default),
        };
        var operations = Operations(22);

        var result = await EdgeExecutor.ApplyAsync(Plan(operations), Context(graph), Ct);

        Assert.All(operations, o =>
        {
            Assert.False(result.Results[o].Succeeded);
            Assert.Contains("the gateway fell over", result.Results[o].Error, StringComparison.Ordinal);
        });
    }

    // ---- bodies on the wire (real HTTP stack) ----

    /// <summary>Answers every plain request 204 and every <c>$batch</c> with a 204 sub-response per sub-request id; records what it received.</summary>
    private sealed class EchoBatchHandler : HttpMessageHandler
    {
        public List<(string Method, Uri Uri, string? Body, string? ContentType)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            lock (Requests)
            {
                Requests.Add((request.Method.Method, request.RequestUri!, body, request.Content?.Headers.ContentType?.ToString()));
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("/$batch", StringComparison.Ordinal))
            {
                var responses = new JsonArray();
                foreach (var sub in JsonNode.Parse(body!)!["requests"]!.AsArray())
                {
                    responses.Add(new JsonObject { ["id"] = sub!["id"]!.GetValue<string>(), ["status"] = 204 });
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["responses"] = responses }.ToJsonString(), System.Text.Encoding.UTF8, "application/json"), RequestMessage = request };
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent) { RequestMessage = request };
        }
    }

    private static HttpGraphClient WireClient(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler, disposeHandler: false);
        var retry = new RetryPolicy(new RetryOptions(), new Immediate(), NullLogger<RetryPolicy>.Instance);
        return new HttpGraphClient(http, new FakeJwtTokenProvider(), retry, new GraphOptions { Cloud = GraphCloud.Public }, NullLogger<HttpGraphClient>.Instance, TimeProvider.System);
    }

    private sealed class Immediate : IDelayProvider
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static readonly string[] AwkwardIds =
    [
        "plain-id",
        "with \"quotes\" inside",
        "back\\slash and slash/ and 'apostrophe'",
        "unicode é ü 日本語 😀 \u00A0end",
        "ampersand & percent % plus + hash # question ?",
    ];

    [Fact]
    public async Task IndividualRequests_CarryValidJsonBodies_ForAwkwardObjectIds()
    {
        var handler = new EchoBatchHandler();
        var client = WireClient(handler);
        var operations = AwkwardIds.Select(id => Operation(id)).ToList();

        var result = await EdgeExecutor.ApplyAsync(Plan(operations), Context(client), Ct);

        Assert.All(operations, o => Assert.True(result.Results[o].Succeeded, result.Results[o].Error));
        Assert.Equal(AwkwardIds.Length, handler.Requests.Count);
        for (int i = 0; i < AwkwardIds.Length; i++)
        {
            var request = handler.Requests[i];
            Assert.Equal("POST", request.Method);
            var parsed = JsonNode.Parse(request.Body!)!.AsObject();
            Assert.Equal(EdgeGraphContext.ODataId(GraphApi.V1, "directoryObjects", AwkwardIds[i]), parsed["@odata.id"]!.GetValue<string>());
            Assert.StartsWith("application/json", request.ContentType, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ABatchEnvelope_IsValidJson_AndKeepsEveryAwkwardBody()
    {
        var handler = new EchoBatchHandler();
        var client = WireClient(handler);
        var operations = Enumerable.Range(0, 21).Select(i => Operation(AwkwardIds[i % AwkwardIds.Length] + i)).ToList();

        var result = await EdgeExecutor.ApplyAsync(Plan(operations), Context(client), Ct);

        Assert.All(operations, o => Assert.True(result.Results[o].Succeeded, result.Results[o].Error));
        // 21 operations pass the threshold and become $batch calls; the transport sends at most 20 sub-requests per call.
        Assert.All(handler.Requests, r => Assert.EndsWith("/$batch", r.Uri.AbsolutePath, StringComparison.Ordinal));
        Assert.Equal(2, handler.Requests.Count);
        var sent = handler.Requests.SelectMany(r => JsonNode.Parse(r.Body!)!["requests"]!.AsArray()).Select(n => n!.AsObject()).ToList();
        Assert.Equal(21, sent.Count);
        for (int i = 0; i < 21; i++)
        {
            Assert.Equal("POST", sent[i]["method"]!.GetValue<string>());
            Assert.Equal(EdgeGraphContext.ODataId(GraphApi.V1, "directoryObjects", AwkwardIds[i % AwkwardIds.Length] + i), sent[i]["body"]!["@odata.id"]!.GetValue<string>());
        }
    }
}