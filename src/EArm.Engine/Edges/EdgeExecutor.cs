using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine.Providers;
using EArm.Graph;
using Microsoft.Extensions.Logging;

namespace EArm.Engine.Edges;

/// <summary>The outcome of one edge operation.</summary>
public sealed record EdgeOperationResult(bool Succeeded, bool AlreadyInPlace, string? Error, JsonObject? Body)
{
    public static EdgeOperationResult Ok(JsonObject? body)
    {
        return new EdgeOperationResult(true, false, null, body);
    }

    public static EdgeOperationResult Already { get; } = new(true, true, null, null);

    public static EdgeOperationResult Failed(string error)
    {
        return new EdgeOperationResult(false, false, error, null);
    }
}

/// <summary>What the edge executor did: per-operation results, the edges it added (for state recording) and the failed rows.</summary>
public sealed class EdgeApplyResult
{
    private readonly Dictionary<EdgeOperation, EdgeOperationResult> _results = [];
    private readonly Dictionary<string, JsonObject?> _added = new(StringComparer.Ordinal);
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<EdgeOperation, EdgeOperationResult> Results => _results;

    public int AddedCount => _added.Count;

    /// <summary>True when the pass stopped early because the run was cancelled; the operations not started are recorded as failed.</summary>
    public bool Cancelled { get; internal set; }

    public int FailedCount => _results.Count(r => !r.Value.Succeeded);

    internal void Record(EdgeOperation operation, EdgeOperationResult result)
    {
        _results[operation] = result;
        if (operation.Action == EdgeAction.Add && result.Succeeded && !result.AlreadyInPlace)
        {
            _added[operation.Identity] = result.Body;
        }
        else if (operation.Action == EdgeAction.Remove && result.Succeeded)
        {
            _removed.Add(operation.Identity);
        }
    }

    /// <summary>True when the pass removed the edge (or found it already gone).</summary>
    public bool WasRemoved(string identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return _removed.Contains(identity);
    }

    /// <summary>True when the pass created the edge (an "already exists" answer does not count: someone else created it).</summary>
    public bool WasAdded(string identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return _added.ContainsKey(identity);
    }

    /// <summary>The add response body (assignment ids) when the pass created the edge.</summary>
    public JsonObject? BodyOf(string identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return _added.TryGetValue(identity, out var body) ? body : null;
    }

    public bool Failed(EdgeRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return _results.Any(r => !r.Value.Succeeded && r.Key.Rows.Contains(row));
    }

    public IReadOnlyList<string> ErrorsFor(EdgeRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return _results.Where(r => !r.Value.Succeeded && r.Key.Rows.Contains(row)).Select(r => $"{r.Key.Describe()}: {r.Value.Error}").ToList();
    }
}

/// <summary>
/// The edge pass, apply half: runs the planned operations, part A before part B, adds before removes, one request
/// per edge up to <see cref="BatchThreshold"/> operations per (api, hint) group and <c>$batch</c> beyond that.
/// "Already exists" on an add and 404 on a remove are idempotent successes. Operations attributed only to rows
/// with validation errors are skipped.
/// </summary>
public static class EdgeExecutor
{
    public const int BatchThreshold = 20;

    /// <summary>
    /// Applies the plan. <paramref name="cancellationToken"/> is only consulted BETWEEN operations (a Ctrl-C stops new writes; the one in flight finishes, so
    /// its outcome is known and recorded): the operations not started are recorded as failed ("cancelled") and <see cref="EdgeApplyResult.Cancelled"/> is set.
    /// </summary>
    public static async Task<EdgeApplyResult> ApplyAsync(EdgePlan plan, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);

        var result = new EdgeApplyResult();
        var runnable = plan.Operations.Where(o => o.Rows.Count == 0 || o.Rows.Any(r => !r.HasErrors)).ToList();
        foreach (var phase in new[] { EdgePhase.A, EdgePhase.B })
        {
            var ofPhase = runnable.Where(o => o.Handler.Phase == phase).ToList();
            await RunGroupAsync(ofPhase.Where(o => o.Action == EdgeAction.Add).ToList(), result, context, cancellationToken).ConfigureAwait(false);
            await RunGroupAsync(ofPhase.Where(o => o.Action == EdgeAction.Remove).ToList(), result, context, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private static async Task RunGroupAsync(List<EdgeOperation> operations, EdgeApplyResult result, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        foreach (var group in operations.GroupBy(o => (o.Api, o.Hint)))
        {
            var items = group.ToList();
            if (items.Count > BatchThreshold)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    SkipCancelled(items, result, context);
                    continue;
                }

                await RunBatchAsync(items, group.Key.Api, group.Key.Hint, result, context).ConfigureAwait(false);
                continue;
            }

            for (int i = 0; i < items.Count; i++)
            {
                var operation = items[i];
                if (cancellationToken.IsCancellationRequested)
                {
                    SkipCancelled(items.GetRange(i, items.Count - i), result, context);
                    break;
                }

                // The request in flight is not cancellable: its outcome is recorded either way.
                var outcome = await SendAsync(operation.ToRequest(), operation.Api, operation.Hint, context, CancellationToken.None).ConfigureAwait(false);
                result.Record(operation, Classify(operation.Action, outcome));
                await ReconcileAmbiguousAddAsync(operation, outcome.Exception, result, context).ConfigureAwait(false);
                Log(context.Logger, operation, result.Results[operation]);
            }
        }
    }

    private static void SkipCancelled(List<EdgeOperation> operations, EdgeApplyResult result, EdgeGraphContext context)
    {
        result.Cancelled = true;
        foreach (var operation in operations)
        {
            result.Record(operation, EdgeOperationResult.Failed("the run was cancelled before this edge was applied"));
            Log(context.Logger, operation, result.Results[operation]);
        }
    }

    /// <summary>
    /// A <c>$batch</c> is sent once: the client retries failed sub-requests itself (per item, with the idempotency rules of each method), and the
    /// whole batch is never repeated blindly (review round 2, S2-07). Failures are read per item from <see cref="BatchResponse.Error"/>.
    /// </summary>
    private static async Task RunBatchAsync(List<EdgeOperation> operations, GraphApi api, RetryHint hint, EdgeApplyResult result, EdgeGraphContext context)
    {
        var requests = new List<BatchRequest>(operations.Count);
        for (int i = 0; i < operations.Count; i++)
        {
            requests.Add(operations[i].ToRequest() with { Id = i.ToString(CultureInfo.InvariantCulture) });
        }

        IReadOnlyList<BatchResponse> responses;
        try
        {
            responses = await context.Graph.BatchAsync(requests, api, hint, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GraphException or GraphTransportException)
        {
            foreach (var operation in operations)
            {
                result.Record(operation, EdgeOperationResult.Failed(ex.Message));
                await ReconcileAmbiguousAddAsync(operation, ex, result, context).ConfigureAwait(false);
                Log(context.Logger, operation, result.Results[operation]);
            }

            return;
        }

        var byId = responses.ToDictionary(r => r.Id, r => r, StringComparer.Ordinal);
        for (int i = 0; i < operations.Count; i++)
        {
            var operation = operations[i];
            Exception? failure = null;
            if (!byId.TryGetValue(i.ToString(CultureInfo.InvariantCulture), out var response))
            {
                result.Record(operation, EdgeOperationResult.Failed("the $batch response has no entry for the request."));
            }
            else if (response.Succeeded)
            {
                result.Record(operation, EdgeOperationResult.Ok(response.Body as JsonObject));
            }
            else
            {
                failure = response.Error ?? new GraphException(response.Status, "BatchSubRequestFailed", $"$batch sub-request failed with {(int)response.Status}.", hint);
                result.Record(operation, Classify(operation.Action, SendOutcome.Fail(failure)));
            }

            await ReconcileAmbiguousAddAsync(operation, failure, result, context).ConfigureAwait(false);
            Log(context.Logger, operation, result.Results[operation]);
        }
    }

    /// <summary>
    /// An add whose answer was lost (timeout, dropped connection, 5xx) may have been committed: read the edge back, and when it is live count it as
    /// created by this run (the planner only plans an add for an edge that was not there), so it is recorded in the state and can be removed later
    /// (review round 2, S2-06). A read that fails leaves the add recorded as failed.
    /// </summary>
    private static async Task ReconcileAmbiguousAddAsync(EdgeOperation operation, Exception? failure, EdgeApplyResult result, EdgeGraphContext context)
    {
        if (operation.Action != EdgeAction.Add || failure is null || result.Results[operation].Succeeded || !ProviderReads.IsAmbiguousWriteFailure(failure))
        {
            return;
        }

        try
        {
            context.InvalidateReads();
            var edge = operation.Edge;
            var row = operation.Rows.FirstOrDefault();
            bool live;
            if (row?.SelfId is { } self && Edge.IdEquals(self, edge.PrincipalId))
            {
                var principal = await operation.Handler.ReadPrincipalAsync(edge.PrincipalId, row.Declarer.Type, operation.Api, context, CancellationToken.None).ConfigureAwait(false);
                live = principal.Edges.ContainsKey(Edge.NormalizeId(edge.TargetId));
            }
            else
            {
                var target = await operation.Handler.ReadTargetAsync(edge.TargetId, edge.TargetCollection, includeEdges: true, operation.Api, context, CancellationToken.None).ConfigureAwait(false);
                live = target.Edges is { } edges && edges.ContainsKey(Edge.NormalizeId(edge.PrincipalId));
            }

            if (live)
            {
                result.Record(operation, EdgeOperationResult.Ok(null));
            }
        }
        catch (Exception ex) when (ex is GraphException or GraphTransportException or ProviderException or InvalidOperationException)
        {
            // Could not tell: it stays a failed add.
        }
    }

    /// <summary>The outcome of sending one request: a body, or the Graph/transport exception.</summary>
    public sealed record SendOutcome(JsonObject? Body, Exception? Exception)
    {
        public bool Succeeded => Exception is null;

        public static SendOutcome Ok(JsonObject? body)
        {
            return new SendOutcome(body, null);
        }

        public static SendOutcome Fail(Exception exception)
        {
            return new SendOutcome(null, exception);
        }
    }

    /// <summary>Sends one request (POST / PUT / PATCH / DELETE) inside the retry policy; never throws for Graph or transport failures.</summary>
    public static async Task<SendOutcome> SendAsync(BatchRequest request, GraphApi api, RetryHint hint, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var graphRequest = new GraphRequest(request.Path, api, request.Headers, advanced: false, hint) { UrlParameters = request.UrlParameters };
        try
        {
            var body = await context.WithRetryAsync<JsonNode?>(async ct =>
            {
                switch (request.Method.ToUpperInvariant())
                {
                    case "POST":
                        return (await context.Graph.PostAsync(graphRequest, request.Body, ct).ConfigureAwait(false)).Body;
                    case "PUT":
                        return (await context.Graph.PutAsync(graphRequest, request.Body ?? new JsonObject(), ct).ConfigureAwait(false)).Body;
                    case "PATCH":
                        return (await context.Graph.PatchAsync(graphRequest, request.Body ?? new JsonObject(), ct).ConfigureAwait(false)).Body;
                    case "DELETE":
                        await context.Graph.DeleteAsync(graphRequest, ct).ConfigureAwait(false);
                        return null;
                    default:
                        throw new InvalidOperationException($"Edge requests do not use the HTTP method {request.Method}.");
                }
            }, hint, cancellationToken, idempotent: !string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)).ConfigureAwait(false);
            return SendOutcome.Ok(body as JsonObject);
        }
        catch (Exception ex) when (ex is GraphException or GraphTransportException)
        {
            return SendOutcome.Fail(ex);
        }
    }

    private static EdgeOperationResult Classify(EdgeAction action, SendOutcome outcome)
    {
        if (outcome.Succeeded)
        {
            return EdgeOperationResult.Ok(outcome.Body);
        }

        if (outcome.Exception is GraphException graph)
        {
            if (action == EdgeAction.Add && graph.IsAlreadyExists)
            {
                return EdgeOperationResult.Already;
            }

            if (action == EdgeAction.Remove && graph.IsNotFound)
            {
                return EdgeOperationResult.Already;
            }
        }

        return EdgeOperationResult.Failed(outcome.Exception!.Message);
    }

    private static void Log(ILogger logger, EdgeOperation operation, EdgeOperationResult result)
    {
        if (result.Succeeded)
        {
            EngineLog.EdgeApplied(logger, operation.Describe(), result.AlreadyInPlace ? "already in place" : "done");
        }
        else
        {
            EngineLog.EdgeApplyFailed(logger, operation.Describe(), result.Error ?? "unknown error");
        }
    }
}
