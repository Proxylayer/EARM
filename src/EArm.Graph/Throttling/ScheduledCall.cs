using System;
using System.Collections.Generic;
using System.Linq;

namespace EArm.Graph.Throttling;

/// <summary>
/// One logical Graph call as the <see cref="GraphRequestScheduler"/> sees it, across all of its retry attempts: the costs it charges (one for a single
/// request, one per sub-request for a <c>$batch</c> envelope) and the pauses it tripped itself. The call that hit a 429 already waits out
/// <c>Retry-After</c> in the retry policy, so the area pause it set does not make it wait a second time; every other request of the area does.
/// </summary>
internal sealed class ScheduledCall
{
    private static readonly int AreaCount = Enum.GetValues<GraphServiceArea>().Length;

    private readonly long[] _trippedPause = new long[AreaCount];

    public ScheduledCall(IReadOnlyList<GraphRequestCost> costs, bool isBatch, bool isTierProbe = false)
    {
        ArgumentNullException.ThrowIfNull(costs);
        Costs = costs;
        IsBatch = isBatch;
        IsTierProbe = isTierProbe;
        Areas = [.. costs.Select(static c => c.Area).Where(static a => a != GraphServiceArea.None).Distinct()];
        foreach (var cost in costs)
        {
            if (cost.Area == GraphServiceArea.Directory)
            {
                ResourceUnits += cost.ResourceUnits;
                Writes += cost.IsWrite ? 1 : 0;
            }
            else if (cost.Area == GraphServiceArea.ConditionalAccess)
            {
                ConditionalAccessRequests++;
            }
        }
    }

    public IReadOnlyList<GraphRequestCost> Costs { get; }

    /// <summary>A <c>$batch</c> envelope: its own response headers say nothing about one sub-request's resource units.</summary>
    public bool IsBatch { get; }

    /// <summary>The tenant-size probe: it never waits for the resource-unit budget nor triggers tier detection itself.</summary>
    public bool IsTierProbe { get; }

    /// <summary>The distinct areas the call touches.</summary>
    public IReadOnlyList<GraphServiceArea> Areas { get; }

    public int ResourceUnits { get; }

    public int Writes { get; }

    public int ConditionalAccessRequests { get; }

    /// <summary>Whether the latest pause of <paramref name="area"/> (its generation) was tripped by this call.</summary>
    internal bool Tripped(GraphServiceArea area, long generation)
    {
        return _trippedPause[(int)area] == generation;
    }

    internal void MarkTripped(GraphServiceArea area, long generation)
    {
        _trippedPause[(int)area] = generation;
    }
}

/// <summary>
/// The permission to send one attempt of a <see cref="ScheduledCall"/>, from <see cref="GraphRequestScheduler.AcquireAsync"/>. Disposing it releases
/// the Conditional Access serialisation (when the call holds it); the budget it consumed stays consumed.
/// </summary>
internal sealed class SchedulerTicket : IDisposable
{
    private readonly Action? _release;
    private bool _disposed;

    internal SchedulerTicket(ScheduledCall call, Action? release)
    {
        Call = call;
        _release = release;
    }

    public ScheduledCall Call { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _release?.Invoke();
    }
}
