using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using EArm.Graph;
using Microsoft.Extensions.Logging;

namespace EArm.Engine.Timing;

/// <summary>One finished phase of a run: its wall-clock time, the Graph HTTP attempts and the request-scheduler wait that fell inside it.</summary>
/// <param name="Name">The phase, as shown (<c>load template</c>, <c>plan</c>, ...).</param>
/// <param name="StartedAtMs">Milliseconds from the run's start to the phase's start.</param>
/// <param name="ElapsedMs">Wall-clock milliseconds from start to end.</param>
/// <param name="Requests">Graph HTTP attempts started while the phase ran (a phase that overlaps another also counts the other's).</param>
/// <param name="SchedulerWaitMs">Milliseconds those attempts waited for the request scheduler, summed over concurrent attempts.</param>
public sealed record TimedPhase(string Name, long StartedAtMs, long ElapsedMs, long Requests, long SchedulerWaitMs);

/// <summary>
/// The phase timings of one CLI run (<c>--timings</c>, and log event 2090 per phase): a phase is measured by disposing the <see cref="RunPhase"/> that
/// <see cref="Measure"/> returns. The Graph counts come from <see cref="Requests"/>, the counter the run's <see cref="HttpGraphClient"/> is given
/// (<see cref="GraphOptions.RequestCounter"/>). Thread-safe; recording a phase costs a timestamp and a lock.
/// </summary>
public sealed class RunTimings
{
    private readonly object _lock = new();
    // Each phase with its start sequence: StartedAtMs is whole milliseconds, too coarse to order phases that start in the same millisecond.
    private readonly List<(long Sequence, TimedPhase Phase)> _phases = [];
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private long _nextSequence;
    private ILogger? _logger;

    /// <summary>The counter the run's Graph client counts its attempts on.</summary>
    public GraphRequestCounter Requests { get; } = new();

    /// <summary>When the run started (this recorder was created), UTC.</summary>
    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    /// <summary>The time since the run started.</summary>
    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_startTimestamp);

    /// <summary>The time from the run's start to the first Graph write; <c>null</c> when nothing was written.</summary>
    public TimeSpan? FirstWriteAfter => Requests.FirstWriteTimestamp is { } first ? Stopwatch.GetElapsedTime(_startTimestamp, first) : null;

    /// <summary>The finished phases, in the order they started.</summary>
    public IReadOnlyList<TimedPhase> Phases
    {
        get
        {
            lock (_lock)
            {
                return [.. _phases.OrderBy(p => p.Sequence).Select(p => p.Phase)];
            }
        }
    }

    /// <summary>Starts measuring <paramref name="phase"/>; dispose the result when the phase ends (once; later disposals are ignored).</summary>
    public RunPhase Measure(string phase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        return new RunPhase(this, phase, System.Threading.Interlocked.Increment(ref _nextSequence), Requests.Sent, Requests.SchedulerWait);
    }

    /// <summary>
    /// Logs every phase finished so far as event 2090 to <paramref name="logger"/> and every later one as it finishes; <c>null</c> stops logging (call it before
    /// the logger's factory is disposed).
    /// </summary>
    public void UseLogger(ILogger? logger)
    {
        lock (_lock)
        {
            _logger = logger;
            if (logger is not null)
            {
                foreach (var (_, phase) in _phases)
                {
                    RunTimingsLog.PhaseTimed(logger, phase.Name, phase.ElapsedMs, phase.Requests);
                }
            }
        }
    }

    /// <summary>Milliseconds from the run's start to <paramref name="timestamp"/> (a <see cref="Stopwatch.GetTimestamp"/>).</summary>
    internal long MillisecondsAt(long timestamp)
    {
        return (long)Stopwatch.GetElapsedTime(_startTimestamp, timestamp).TotalMilliseconds;
    }

    internal void Record(long sequence, TimedPhase phase)
    {
        lock (_lock)
        {
            _phases.Add((sequence, phase));
            if (_logger is { } logger)
            {
                RunTimingsLog.PhaseTimed(logger, phase.Name, phase.ElapsedMs, phase.Requests);
            }
        }
    }
}

/// <summary>A phase being measured by <see cref="RunTimings.Measure"/>; disposing it records the phase.</summary>
public sealed class RunPhase : IDisposable
{
    private readonly RunTimings _timings;
    private readonly string _name;
    private readonly long _sequence;
    private readonly long _requestsAtStart;
    private readonly TimeSpan _waitAtStart;
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private int _ended;

    internal RunPhase(RunTimings timings, string name, long sequence, long requestsAtStart, TimeSpan waitAtStart)
    {
        _timings = timings;
        _name = name;
        _sequence = sequence;
        _requestsAtStart = requestsAtStart;
        _waitAtStart = waitAtStart;
    }

    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _ended, 1) != 0)
        {
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(_startTimestamp);
        var requests = _timings.Requests;
        _timings.Record(_sequence, new TimedPhase(
            _name,
            _timings.MillisecondsAt(_startTimestamp),
            (long)elapsed.TotalMilliseconds,
            requests.Sent - _requestsAtStart,
            (long)(requests.SchedulerWait - _waitAtStart).TotalMilliseconds));
    }
}

/// <summary>Source-generated log messages of <see cref="RunTimings"/>.</summary>
internal static partial class RunTimingsLog
{
    [LoggerMessage(EventId = 2090, Level = LogLevel.Debug, Message = "Phase {Phase}: {ElapsedMs} ms, {Requests} Graph requests")]
    public static partial void PhaseTimed(ILogger logger, string phase, long elapsedMs, long requests);
}
