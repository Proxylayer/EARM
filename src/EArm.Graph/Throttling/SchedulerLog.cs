using System;
using Microsoft.Extensions.Logging;

namespace EArm.Graph.Throttling;

/// <summary>
/// The log of the <see cref="GraphRequestScheduler"/>, aggregated so a long paced run does not flood the output: every wait is a debug line, and an
/// information line per area sums the waits up at most every <see cref="ReportInterval"/>; the near-limit header is reported at most every
/// <see cref="NearLimitInterval"/>. Callers serialise access (the scheduler's lock).
/// </summary>
internal sealed class SchedulerLog
{
    public static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan NearLimitInterval = TimeSpan.FromSeconds(60);

    private readonly ILogger _logger;
    private readonly int[] _count = new int[Enum.GetValues<GraphServiceArea>().Length];
    private readonly TimeSpan[] _total = new TimeSpan[Enum.GetValues<GraphServiceArea>().Length];
    private readonly TimeSpan?[] _lastReport = new TimeSpan?[Enum.GetValues<GraphServiceArea>().Length];
    private TimeSpan? _lastNearLimit;

    public SchedulerLog(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Delayed(GraphServiceArea area, TimeSpan wait, bool paused, TimeSpan now)
    {
        var index = (int)area;
        GraphLog.SchedulerDelay(_logger, area.ToString(), wait.TotalMilliseconds, paused ? "the area is paused after a 429" : "budget");
        _count[index]++;
        _total[index] += wait;
        if (_lastReport[index] is { } last && now - last < ReportInterval)
        {
            return;
        }

        GraphLog.SchedulerPacing(_logger, area.ToString(), _count[index], _total[index].TotalSeconds);
        _lastReport[index] = now;
        _count[index] = 0;
        _total[index] = TimeSpan.Zero;
    }

    public void Paused(GraphServiceArea area, string? scope, TimeSpan pause)
    {
        GraphLog.SchedulerPaused(_logger, area.ToString(), scope ?? "n/a", pause.TotalSeconds);
    }

    public void NearLimit(double percentage, string? scope, TimeSpan now)
    {
        if (_lastNearLimit is { } last && now - last < NearLimitInterval)
        {
            return;
        }

        _lastNearLimit = now;
        GraphLog.SchedulerNearLimit(_logger, percentage, scope ?? "n/a");
    }

    public void Tier(long users, TenantSizeTier tier)
    {
        GraphLog.SchedulerTier(_logger, users, tier.ToString(), DirectoryLimits.ResourceUnits(tier));
    }

    /// <summary>
    /// Logs that the tenant size could not be read; <paramref name="permissionDenied"/> (Graph answered 401 or 403) adds the permission the read
    /// needs, any other failure is logged with its reason only.
    /// </summary>
    public void TierUnknown(string reason, bool permissionDenied)
    {
        var units = DirectoryLimits.ResourceUnits(TenantSizeTier.Small);
        if (permissionDenied)
        {
            GraphLog.SchedulerTierUnknown(_logger, reason, units);
        }
        else
        {
            GraphLog.SchedulerTierUnread(_logger, reason, units);
        }
    }
}
