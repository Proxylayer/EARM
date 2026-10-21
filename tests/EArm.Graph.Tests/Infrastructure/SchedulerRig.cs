using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph.Throttling;

namespace EArm.Graph.Tests.Infrastructure;

/// <summary>
/// A <see cref="GraphRequestScheduler"/> on a <see cref="Review1BStepClock"/> that only moves when the test (or an advancing delay provider) moves it,
/// so every wait is exact. The delays are recorded by <see cref="Delays"/> (returns at once, the clock stands still) unless another provider is given.
/// </summary>
internal sealed class SchedulerRig
{
    public SchedulerRig(IDelayProvider? delays = null, RetryOptions? retry = null, Func<CancellationToken, Task<long>>? countUsers = null, Review1BStepClock? clock = null)
    {
        Clock = clock ?? new Review1BStepClock();
        Delays = new RecordingDelayProvider();
        Log = new LogSink();
        Scheduler = new GraphRequestScheduler(retry ?? new RetryOptions(), delays ?? Delays, Clock, new TestLogger<GraphRequestScheduler>(Log), countUsers ?? (_ => Task.FromResult(10L)));
    }

    public Review1BStepClock Clock { get; }

    public RecordingDelayProvider Delays { get; }

    public LogSink Log { get; }

    public GraphRequestScheduler Scheduler { get; }

    public static ScheduledCall Call(params GraphRequestCost[] costs)
    {
        return new ScheduledCall(costs, isBatch: false);
    }

    public static ScheduledCall Call(string method, string path)
    {
        return Call(GraphRequestClassifier.Classify(method, path));
    }

    public static GraphRequestCost Directory(int units, bool write = false)
    {
        return new GraphRequestCost(GraphServiceArea.Directory, units, write);
    }

    public static GraphRequestCost In(GraphServiceArea area, bool write = false)
    {
        return new GraphRequestCost(area, 0, write);
    }

    public static ThrottleSignals Ok(int? resourceUnits = null, double? percentage = null, string? scope = null)
    {
        return new ThrottleSignals(200, null, resourceUnits, percentage, scope);
    }

    public static ThrottleSignals Throttled(double? retryAfterSeconds = null, string? scope = null)
    {
        return new ThrottleSignals(429, retryAfterSeconds is { } s ? TimeSpan.FromSeconds(s) : null, null, null, scope);
    }

    /// <summary>Acquires and releases at once (a call that is sent); returns the ticket (disposed).</summary>
    public async Task<SchedulerTicket> SendAsync(ScheduledCall call)
    {
        var ticket = await Scheduler.AcquireAsync(call, Xunit.TestContext.Current.CancellationToken);
        ticket.Dispose();
        return ticket;
    }

    /// <summary>The delays recorded while <paramref name="action"/> ran.</summary>
    public async Task<IReadOnlyList<TimeSpan>> DelaysOf(Func<Task> action)
    {
        var before = Delays.Delays.Count;
        await action();
        return [.. Delays.Delays.Skip(before)];
    }

    public IReadOnlyList<LogEntry> Events(int id)
    {
        return [.. Log.Entries.Where(e => e.EventId.Id == id)];
    }
}

/// <summary>
/// A delay provider whose waits complete only when the test releases them (and then advance the clock by the wait), so a test can act while a request
/// is waiting. Cancellation completes a wait as cancelled.
/// </summary>
internal sealed class GatedDelayProvider : IDelayProvider
{
    private readonly Review1BStepClock _clock;
    private readonly object _gate = new();
    private readonly List<(TimeSpan Delay, TaskCompletionSource Done)> _pending = [];
    private readonly List<TimeSpan> _requested = [];

    public GatedDelayProvider(Review1BStepClock clock)
    {
        _clock = clock;
    }

    public IReadOnlyList<TimeSpan> Requested
    {
        get
        {
            lock (_gate)
            {
                return [.. _requested];
            }
        }
    }

    public int Pending
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count(static p => !p.Done.Task.IsCompleted);
            }
        }
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => done.TrySetCanceled(cancellationToken));
        lock (_gate)
        {
            _requested.Add(delay);
            _pending.Add((delay, done));
        }

        return done.Task;
    }

    /// <summary>Waits until <paramref name="count"/> delays are pending (a request is blocked in the scheduler).</summary>
    public async Task WaitForPendingAsync(int count)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Pending < count)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Expected {count} pending delay(s), found {Pending}.");
            }

            await Task.Delay(5);
        }
    }

    /// <summary>Completes the oldest pending delay after moving the clock forward by it.</summary>
    public void ReleaseNext()
    {
        (TimeSpan Delay, TaskCompletionSource Done) next;
        lock (_gate)
        {
            next = _pending.First(static p => !p.Done.Task.IsCompleted);
            _pending.Remove(next);
        }

        _clock.Advance(next.Delay);
        next.Done.TrySetResult();
    }
}
