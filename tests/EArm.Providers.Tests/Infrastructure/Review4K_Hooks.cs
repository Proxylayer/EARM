using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
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
