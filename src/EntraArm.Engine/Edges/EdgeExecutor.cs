using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Az;
using Microsoft.Extensions.Logging;

namespace EntraArm.Engine.Edges;

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

    public IReadOnlyDictionary<EdgeOperation, EdgeOperationResult> Results => _results;

    public int AddedCount => _added.Count;

    public int FailedCount => _results.Count(r => !r.Value.Succeeded);

    internal void Record(EdgeOperation operation, EdgeOperationResult result)
    {
        _results[operation] = result;
        if (operation.Action == EdgeAction.Add && result.Succeeded && !result.AlreadyInPlace)
        {
            _added[operation.Identity] = result.Body;
        }
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
                await RunBatchAsync(items, group.Key.Api, group.Key.Hint, result, context, cancellationToken).ConfigureAwait(false);
                continue;
            }

            foreach (var operation in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outcome = await SendAsync(operation.ToRequest(), operation.Api, operation.Hint, context, cancellationToken).ConfigureAwait(false);
                result.Record(operation, Classify(operation.Action, outcome));
                Log(context.Logger, operation, result.Results[operation]);
            }
        }
    }

    private static async Task RunBatchAsync(List<EdgeOperation> operations, GraphApi api, RetryHint hint, EdgeApplyResult result, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        var requests = new List<BatchRequest>(operations.Count);
        for (int i = 0; i < operations.Count; i++)
        {
            requests.Add(operations[i].ToRequest() with { Id = i.ToString(CultureInfo.InvariantCulture) });
        }

        IReadOnlyList<BatchResponse> responses;
        try
        {
            responses = await context.WithRetryAsync(ct => context.Graph.BatchAsync(requests, api, hint, ct), hint, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GraphException or AzCliException or ToolchainException)
        {
            foreach (var operation in operations)
            {
                result.Record(operation, EdgeOperationResult.Failed(ex.Message));
                Log(context.Logger, operation, result.Results[operation]);
            }

            return;
        }

        var byId = responses.ToDictionary(r => r.Id, r => r, StringComparer.Ordinal);
        for (int i = 0; i < operations.Count; i++)
        {
            var operation = operations[i];
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
                var error = response.Error ?? new GraphException(response.Status, "BatchSubRequestFailed", $"$batch sub-request failed with {(int)response.Status}.", hint);
                result.Record(operation, Classify(operation.Action, SendOutcome.Fail(error)));
            }

            Log(context.Logger, operation, result.Results[operation]);
        }
    }

    /// <summary>The outcome of sending one request: a body, or the Graph/az exception.</summary>
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

    /// <summary>Sends one request (POST / PUT / PATCH / DELETE) inside the retry policy; never throws for Graph or az failures.</summary>
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
            }, hint, cancellationToken).ConfigureAwait(false);
            return SendOutcome.Ok(body as JsonObject);
        }
        catch (Exception ex) when (ex is GraphException or AzCliException or ToolchainException)
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
