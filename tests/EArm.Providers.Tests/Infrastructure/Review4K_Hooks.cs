using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using EArm.Engine.State;
using EArm.Graph;
using Microsoft.Extensions.Logging;

namespace EArm.Providers.Tests.Infrastructure;

/// <summary>
/// A forwarding <see cref="IGraphClient"/> with hooks around every call (<c>GET</c>, <c>POST</c>, <c>PATCH</c>, <c>PUT</c>, <c>DELETE</c>), for tests that need something to
/// happen at an exact moment of a run: a cancellation, an object that vanishes, a failure after a write was committed, a result rewritten.
/// </summary>
internal sealed class HookedGraphClient(IGraphClient inner) : IGraphClient
{
    private int _inFlight;
    private int _maxInFlight;

    /// <summary>Runs before the call is forwarded; may throw.</summary>
    public Func<string, GraphRequest, Task>? Before { get; set; }

    /// <summary>Runs after the call was forwarded (the write is committed); may throw.</summary>
    public Func<string, GraphRequest, Task>? After { get; set; }

    /// <summary>Sees the answer of every <c>POST</c> (an action such as <c>addPassword</c> answers with the one-time secret).</summary>
    public Action<GraphRequest, GraphResponse>? OnPostResponse { get; set; }

    /// <summary>Rewrites the answer of a <c>GET</c>.</summary>
    public Func<GraphRequest, JsonNode?, JsonNode?>? RewriteGet { get; set; }

    /// <summary>Holds every <c>GET</c> this long (so concurrent reads overlap and <see cref="MaxInFlight"/> means something).</summary>
    public TimeSpan HoldReads { get; set; }

    /// <summary>The largest number of reads that were in flight at once.</summary>
    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    public async Task<JsonNode?> GetAsync(GraphRequest request, CancellationToken cancellationToken)
    {
        int now = Interlocked.Increment(ref _inFlight);
        int seen;
        do
        {
            seen = Volatile.Read(ref _maxInFlight);
        }
        while (now > seen && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen);

        try
        {
            if (HoldReads > TimeSpan.Zero)
            {
                await Task.Delay(HoldReads, cancellationToken).ConfigureAwait(false);
            }

            await RunAsync(Before, "GET", request).ConfigureAwait(false);
            var result = await inner.GetAsync(request, cancellationToken).ConfigureAwait(false);
            if (RewriteGet is not null)
            {
                result = RewriteGet(request, result);
            }

            await RunAsync(After, "GET", request).ConfigureAwait(false);
            return result;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public async Task<GraphResponse> PostAsync(GraphRequest request, JsonNode? body, CancellationToken cancellationToken)
    {
        await RunAsync(Before, "POST", request).ConfigureAwait(false);
        var response = await inner.PostAsync(request, body, cancellationToken).ConfigureAwait(false);
        OnPostResponse?.Invoke(request, response);
        await RunAsync(After, "POST", request).ConfigureAwait(false);
        return response;
    }

    public async Task<GraphResponse> PatchAsync(GraphRequest request, JsonNode body, CancellationToken cancellationToken)
    {
        await RunAsync(Before, "PATCH", request).ConfigureAwait(false);
        var response = await inner.PatchAsync(request, body, cancellationToken).ConfigureAwait(false);
        await RunAsync(After, "PATCH", request).ConfigureAwait(false);
        return response;
    }

    public async Task<GraphResponse> PutAsync(GraphRequest request, JsonNode body, CancellationToken cancellationToken)
    {
        await RunAsync(Before, "PUT", request).ConfigureAwait(false);
        var response = await inner.PutAsync(request, body, cancellationToken).ConfigureAwait(false);
        await RunAsync(After, "PUT", request).ConfigureAwait(false);
        return response;
    }

    public async Task DeleteAsync(GraphRequest request, CancellationToken cancellationToken)
    {
        await RunAsync(Before, "DELETE", request).ConfigureAwait(false);
        await inner.DeleteAsync(request, cancellationToken).ConfigureAwait(false);
        await RunAsync(After, "DELETE", request).ConfigureAwait(false);
    }

    public IAsyncEnumerable<JsonNode> GetAllPagesAsync(GraphRequest request, CancellationToken cancellationToken)
    {
        return inner.GetAllPagesAsync(request, cancellationToken);
    }

    public Task<IReadOnlyList<BatchResponse>> BatchAsync(IReadOnlyList<BatchRequest> requests, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        return inner.BatchAsync(requests, api, hint, cancellationToken);
    }

    private static Task RunAsync(Func<string, GraphRequest, Task>? hook, string method, GraphRequest request)
    {
        return hook is null ? Task.CompletedTask : hook(method, request);
    }
}

/// <summary>An <see cref="ILogger{T}"/> that calls a hook with every event id logged (to act at the exact point an engine log line is written).</summary>
internal sealed class HookLogger<T>(Action<EventId> onLog) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        onLog(eventId);
    }
}

/// <summary>
/// A state sink that throws <see cref="OperationCanceledException"/> when it is called with a cancelled token, and records every call that reached it. The end phase of a
/// run records work that already happened and must never hand the caller's (cancelled) token to such a call.
/// </summary>
internal sealed class TokenStrictSink(IStateSink inner) : IStateSink
{
    private readonly object _gate = new();
    private readonly List<string> _calls = [];

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    private Task Run(string call, CancellationToken cancellationToken, Func<Task> forward)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _calls.Add(call);
        }

        return forward();
    }

    public Task ResourceAppliedAsync(ResourceInstance instance, ResourceChange change, AppliedResource applied, CancellationToken cancellationToken)
    {
        return Run("Applied:" + instance.Id.Id, cancellationToken, () => inner.ResourceAppliedAsync(instance, change, applied, cancellationToken));
    }

    public Task ResourceFailedAsync(ResourceInstance instance, string error, CancellationToken cancellationToken)
    {
        return Run("Failed:" + instance.Id.Id, cancellationToken, () => inner.ResourceFailedAsync(instance, error, cancellationToken));
    }

    public Task CompletedAsync(DeploymentStatus status, CancellationToken cancellationToken)
    {
        return Run("Completed:" + status, cancellationToken, () => inner.CompletedAsync(status, cancellationToken));
    }

    public Task ResourceStartingAsync(ResourceInstance instance, CancellationToken cancellationToken)
    {
        return Run("Starting:" + instance.Id.Id, cancellationToken, () => inner.ResourceStartingAsync(instance, cancellationToken));
    }

    public Task ResourceSkippedAsync(ResourceInstance instance, string reason, CancellationToken cancellationToken)
    {
        return Run("Skipped:" + instance.Id.Id, cancellationToken, () => inner.ResourceSkippedAsync(instance, reason, cancellationToken));
    }

    public Task ResourceIgnoredAsync(ResourceInstance instance, string reason, CancellationToken cancellationToken)
    {
        return Run("Ignored:" + instance.Id.Id, cancellationToken, () => inner.ResourceIgnoredAsync(instance, reason, cancellationToken));
    }

    public Task ResourceDetachedAsync(StateEntry entry, CancellationToken cancellationToken)
    {
        return Run("Detached:" + entry.Id.Id, cancellationToken, () => inner.ResourceDetachedAsync(entry, cancellationToken));
    }

    public Task ResourceDeletedAsync(StateEntry entry, CancellationToken cancellationToken)
    {
        return Run("Deleted:" + entry.Id.Id, cancellationToken, () => inner.ResourceDeletedAsync(entry, cancellationToken));
    }

    public Task ResourceDeleteFailedAsync(StateEntry entry, string error, CancellationToken cancellationToken)
    {
        return Run("DeleteFailed:" + entry.Id.Id, cancellationToken, () => inner.ResourceDeleteFailedAsync(entry, error, cancellationToken));
    }

    public Task ResourceEdgesAppliedAsync(ResourceInstance instance, JsonObject? relationships, CancellationToken cancellationToken)
    {
        return Run("EdgesApplied:" + instance.Id.Id, cancellationToken, () => inner.ResourceEdgesAppliedAsync(instance, relationships, cancellationToken));
    }

    public Task ResourceEdgesFailedAsync(ResourceInstance instance, string error, CancellationToken cancellationToken)
    {
        return Run("EdgesFailed:" + instance.Id.Id, cancellationToken, () => inner.ResourceEdgesFailedAsync(instance, error, cancellationToken));
    }
}
