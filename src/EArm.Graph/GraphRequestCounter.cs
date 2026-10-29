using System;
using System.Diagnostics;
using System.Threading;

namespace EArm.Graph;

/// <summary>
/// Counts the HTTP attempts an <see cref="HttpGraphClient"/> makes (each retry, each <c>@odata.nextLink</c> page and each <c>$batch</c> envelope is one), the
/// most that were in flight at once, the time they spent waiting for the request scheduler's budget, and when the first write went out. Lock-free:
/// read it from any thread while requests run. Diagnostics only (<c>--timings</c>); nothing decides on it.
/// </summary>
public sealed class GraphRequestCounter
{
    private long _sent;
    private int _inFlight;
    private int _maxInFlight;
    private long _schedulerWaitTicks;
    private long _firstWriteTimestamp;

    /// <summary>HTTP attempts that took a connection slot so far.</summary>
    public long Sent => Interlocked.Read(ref _sent);

    /// <summary>The most attempts that held a connection slot at the same time.</summary>
    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    /// <summary>The total time attempts waited for the request scheduler (summed over concurrent attempts, so it can exceed the wall-clock time).</summary>
    public TimeSpan SchedulerWait => TimeSpan.FromTicks(Interlocked.Read(ref _schedulerWaitTicks));

    /// <summary>The <see cref="Stopwatch.GetTimestamp"/> at which the first write (any method but GET/HEAD/OPTIONS) took a connection slot; <c>null</c> before any.</summary>
    public long? FirstWriteTimestamp => Interlocked.Read(ref _firstWriteTimestamp) is var value && value != 0 ? value : null;

    internal void SchedulerWaited(TimeSpan wait)
    {
        Interlocked.Add(ref _schedulerWaitTicks, wait.Ticks);
    }

    internal void Started(bool write)
    {
        Interlocked.Increment(ref _sent);
        int now = Interlocked.Increment(ref _inFlight);
        int max = Volatile.Read(ref _maxInFlight);
        while (now > max)
        {
            int seen = Interlocked.CompareExchange(ref _maxInFlight, now, max);
            if (seen == max)
            {
                break;
            }

            max = seen;
        }

        if (write)
        {
            Interlocked.CompareExchange(ref _firstWriteTimestamp, Stopwatch.GetTimestamp(), 0);
        }
    }

    internal void Finished()
    {
        Interlocked.Decrement(ref _inFlight);
    }
}
