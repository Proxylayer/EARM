using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace EArm.Graph.Throttling;

/// <summary>
/// Paces every Graph request of one <see cref="HttpGraphClient"/> (each <c>$batch</c> sub-request included) per Microsoft service so the client stays
/// within Microsoft's PUBLISHED throttling limits (https://learn.microsoft.com/en-us/graph/throttling-limits) and waits before sending instead of
/// collecting 429s. The budgets are the full published numbers, fixed:
/// <list type="bullet">
/// <item><b>Directory</b> resource units: a token bucket of 3,500 / 5,000 / 8,000 units per 10 s by tenant size (<see cref="TenantSizeTier"/>), the
/// estimate of <see cref="GraphRequestClassifier"/> reconciled with <c>x-ms-resource-unit</c>; and the write quota, 3,000 writes per 150 s.</item>
/// <item><b>Conditional Access</b> policies and named locations: one request per second, sent one at a time.</item>
/// <item>The areas without a published rate get no budget of EARM's own.</item>
/// </list>
/// A 429 from any area pauses that whole area: every request to it waits until <c>Retry-After</c> has elapsed (without one, the retry policy's
/// exponential throttle schedule), then the area resumes at the same rate. The request that hit the 429 waits in the retry policy, not twice.
/// <para>
/// Each request waits exactly once for its budget (<see cref="TokenBucket.Reserve"/>), plus once more for each pause tripped while it was waiting,
/// so the scheduler works with any <see cref="TimeProvider"/> and <see cref="IDelayProvider"/>, including a clock that tests never move.
/// The tenant size is read the first time the smallest tier's directory budget would make a request wait (one <c>GET /users/$count</c>); until
/// then every tier behaves the same. When it cannot be read the smallest tier stays.
/// </para>
/// </summary>
internal sealed class GraphRequestScheduler
{
    private static readonly int AreaCount = Enum.GetValues<GraphServiceArea>().Length;

    private readonly object _lock = new();
    private readonly RetryOptions _retry;
    private readonly IDelayProvider _delay;
    private readonly TimeProvider _time;
    private readonly long _origin;
    private readonly SchedulerLog _log;
    private readonly Func<CancellationToken, Task<long>> _countUsers;
    private readonly TokenBucket _units;
    private readonly TokenBucket _writes;
    private readonly TokenBucket _conditionalAccess;
    private readonly SemaphoreSlim _conditionalAccessSerial = new(1, 1);
    private readonly AreaPause[] _pauses = new AreaPause[AreaCount];
    private Task<TenantSizeTier>? _tierDetection;
    private TenantSizeTier _tier = TenantSizeTier.Small;
    private bool _tierKnown;

    /// <param name="retry">The throttle schedule (<see cref="RetryOptions.ThrottleBaseDelay"/>, <see cref="RetryOptions.ThrottleMaxDelay"/>) and the <c>Retry-After</c> cap.</param>
    /// <param name="delay">Waits before sending.</param>
    /// <param name="time">The monotonic clock (<see cref="TimeProvider.GetTimestamp"/>).</param>
    /// <param name="logger">Diagnostics.</param>
    /// <param name="countUsers">Reads the number of users of the tenant (the tenant-size probe); its request must be scheduled as a <see cref="ScheduledCall.IsTierProbe"/> call.</param>
    public GraphRequestScheduler(RetryOptions retry, IDelayProvider delay, TimeProvider time, ILogger logger, Func<CancellationToken, Task<long>> countUsers)
    {
        _retry = retry ?? throw new ArgumentNullException(nameof(retry));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _log = new SchedulerLog(logger);
        _countUsers = countUsers ?? throw new ArgumentNullException(nameof(countUsers));
        _origin = _time.GetTimestamp();
        var now = Now;
        _units = new TokenBucket(DirectoryLimits.ResourceUnits(TenantSizeTier.Small), DirectoryLimits.ResourceUnitPeriod, now);
        _writes = new TokenBucket(DirectoryLimits.Writes, DirectoryLimits.WritePeriod, now);
        _conditionalAccess = new TokenBucket(1, DirectoryLimits.ConditionalAccessInterval, now);
        for (var i = 0; i < AreaCount; i++)
        {
            _pauses[i] = new AreaPause();
        }
    }

    /// <summary>The directory tier in use (<see cref="TenantSizeTier.Small"/> until the tenant size was read).</summary>
    public TenantSizeTier Tier
    {
        get
        {
            lock (_lock)
            {
                return _tier;
            }
        }
    }

    private TimeSpan Now => _time.GetElapsedTime(_origin);

    /// <summary>
    /// Waits until one attempt of <paramref name="call"/> may be sent: its budgets are reserved, an area paused after a 429 is waited out, and a call with
    /// Conditional Access requests holds the Conditional Access serialisation until the returned ticket is disposed. Cancelling gives the budget back.
    /// </summary>
    public async Task<SchedulerTicket> AcquireAsync(ScheduledCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        var serial = call.ConditionalAccessRequests > 0;
        if (serial)
        {
            await _conditionalAccessSerial.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            if (!call.IsTierProbe && NeedsTier(call.ResourceUnits))
            {
                await DetectTenantSizeAsync(cancellationToken).ConfigureAwait(false);
            }

            var generations = new long[AreaCount];
            var wait = Reserve(call, generations, out var area, out var paused);
            try
            {
                while (wait > TimeSpan.Zero)
                {
                    lock (_lock)
                    {
                        _log.Delayed(area, wait, paused, Now);
                    }

                    await _delay.DelayAsync(wait, cancellationToken).ConfigureAwait(false);
                    wait = NewPauseWait(call, generations, out area);
                    paused = true;
                }
            }
            catch
            {
                Refund(call);
                throw;
            }

            return new SchedulerTicket(call, serial ? () => _conditionalAccessSerial.Release() : null);
        }
        catch
        {
            if (serial)
            {
                _conditionalAccessSerial.Release();
            }

            throw;
        }
    }

    /// <summary>Records what the response to one attempt said: a 429 pauses the call's areas, <c>x-ms-resource-unit</c> corrects the estimate.</summary>
    public void Complete(SchedulerTicket ticket, ThrottleSignals signals)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var call = ticket.Call;
        lock (_lock)
        {
            var now = Now;
            foreach (var area in call.Areas)
            {
                if (signals.IsThrottled)
                {
                    call.MarkTripped(area, Pause(area, signals, now));
                }
                else
                {
                    _pauses[(int)area].ConsecutiveThrottles = 0;
                }
            }

            // A $batch envelope's headers say nothing about one sub-request; its sub-responses are reported one by one instead.
            if (!call.IsBatch && call.Costs.Count == 1 && !signals.IsThrottled)
            {
                Observe(call.Costs[0], signals, now);
            }
        }
    }

    /// <summary>Records one <c>$batch</c> sub-response: a 429 pauses its area (for every request: nobody waited for it yet), its headers reconcile its cost.</summary>
    public void CompleteSubRequest(GraphRequestCost cost, ThrottleSignals signals)
    {
        if (cost.Area == GraphServiceArea.None)
        {
            return;
        }

        lock (_lock)
        {
            var now = Now;
            if (signals.IsThrottled)
            {
                Pause(cost.Area, signals, now);
                return;
            }

            _pauses[(int)cost.Area].ConsecutiveThrottles = 0;
            Observe(cost, signals, now);
        }
    }

    /// <summary>
    /// Reads the tenant size once per client (later calls share the first result) and sizes the directory budget by it. A failure to read it keeps
    /// the smallest tier and is logged; it never fails the caller.
    /// </summary>
    public Task<TenantSizeTier> DetectTenantSizeAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<TenantSizeTier>? owner = null;
        Task<TenantSizeTier> detection;
        lock (_lock)
        {
            if (_tierDetection is null)
            {
                owner = new TaskCompletionSource<TenantSizeTier>(TaskCreationOptions.RunContinuationsAsynchronously);
                _tierDetection = owner.Task;
            }

            detection = _tierDetection;
        }

        if (owner is not null)
        {
            _ = RunDetectionAsync(owner);
        }

        return detection.WaitAsync(cancellationToken);
    }

    private async Task RunDetectionAsync(TaskCompletionSource<TenantSizeTier> owner)
    {
        try
        {
            // Not the caller's token: one caller's cancellation must not decide the tier for every other request. The probe is one attempt
            // bounded by the client's request timeout.
            var users = await _countUsers(CancellationToken.None).ConfigureAwait(false);
            var tier = DirectoryLimits.TierOf(users);
            lock (_lock)
            {
                _tier = tier;
                _tierKnown = true;
                _units.Resize(DirectoryLimits.ResourceUnits(tier), DirectoryLimits.ResourceUnitPeriod, Now);
                _log.Tier(users, tier);
            }

            owner.SetResult(tier);
        }
        catch (Exception ex) when (ex is GraphException or GraphTransportException or OperationCanceledException)
        {
            lock (_lock)
            {
                _tierKnown = true;
                _log.TierUnknown(ex is GraphException graph ? ((int)graph.Status).ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + graph.Code : ex.GetType().Name);
            }

            owner.SetResult(TenantSizeTier.Small);
        }
        catch (Exception ex)
        {
            // Not a Graph answer: a defect. Every waiting request sees it instead of hanging on a detection that never completes.
            owner.TrySetException(ex);
        }
    }

    /// <summary>Whether the tenant size must be read first: it is still unknown and the smallest tier's budget would make this request wait.</summary>
    private bool NeedsTier(int resourceUnits)
    {
        if (resourceUnits <= 0)
        {
            return false;
        }

        lock (_lock)
        {
            return !_tierKnown && _units.WaitFor(resourceUnits, Now) > TimeSpan.Zero;
        }
    }

    /// <summary>Reserves the call's budgets and returns the longest wait: a bucket's, or an area pause the call did not trip itself.</summary>
    private TimeSpan Reserve(ScheduledCall call, long[] generations, out GraphServiceArea area, out bool paused)
    {
        lock (_lock)
        {
            var now = Now;
            var wait = TimeSpan.Zero;
            area = GraphServiceArea.None;
            paused = false;
            if (call.ResourceUnits > 0)
            {
                var units = _units.Reserve(call.ResourceUnits, now);
                Longer(ref wait, ref area, call.IsTierProbe ? TimeSpan.Zero : units, GraphServiceArea.Directory);
            }

            if (call.Writes > 0)
            {
                Longer(ref wait, ref area, _writes.Reserve(call.Writes, now), GraphServiceArea.Directory);
            }

            if (call.ConditionalAccessRequests > 0)
            {
                Longer(ref wait, ref area, _conditionalAccess.Reserve(call.ConditionalAccessRequests, now), GraphServiceArea.ConditionalAccess);
            }

            foreach (var touched in call.Areas)
            {
                var pause = _pauses[(int)touched];
                generations[(int)touched] = pause.Generation;
                if (!call.Tripped(touched, pause.Generation) && pause.Until - now > wait)
                {
                    wait = pause.Until - now;
                    area = touched;
                    paused = true;
                }
            }

            return wait;
        }
    }

    /// <summary>After a wait: the remaining wait for a pause tripped (by another request) while this one was waiting; zero when there is none.</summary>
    private TimeSpan NewPauseWait(ScheduledCall call, long[] generations, out GraphServiceArea area)
    {
        lock (_lock)
        {
            var now = Now;
            var wait = TimeSpan.Zero;
            area = GraphServiceArea.None;
            foreach (var touched in call.Areas)
            {
                var pause = _pauses[(int)touched];
                if (pause.Generation == generations[(int)touched])
                {
                    continue;
                }

                generations[(int)touched] = pause.Generation;
                if (!call.Tripped(touched, pause.Generation) && pause.Until - now > wait)
                {
                    wait = pause.Until - now;
                    area = touched;
                }
            }

            return wait;
        }
    }

    /// <summary>Gives back the budget of a call that was cancelled before it was sent.</summary>
    private void Refund(ScheduledCall call)
    {
        lock (_lock)
        {
            var now = Now;
            if (call.ResourceUnits > 0)
            {
                _units.Adjust(-call.ResourceUnits, now);
            }

            if (call.Writes > 0)
            {
                _writes.Adjust(-call.Writes, now);
            }

            if (call.ConditionalAccessRequests > 0)
            {
                _conditionalAccess.Adjust(-call.ConditionalAccessRequests, now);
            }
        }
    }

    /// <summary>Pauses <paramref name="area"/> for the response's <c>Retry-After</c> (capped like the retry policy's) or the throttle backoff; returns the pause's generation.</summary>
    private long Pause(GraphServiceArea area, ThrottleSignals signals, TimeSpan now)
    {
        var pause = _pauses[(int)area];
        var duration = signals.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero
            ? _retry.ClampRetryAfter(retryAfter)
            : RetryPolicy.Backoff(_retry.ThrottleBaseDelay, pause.ConsecutiveThrottles, _retry.ThrottleMaxDelay);
        pause.ConsecutiveThrottles++;
        if (now + duration > pause.Until)
        {
            pause.Until = now + duration;
        }

        pause.Generation++;
        _log.Paused(area, signals.Scope, duration);
        return pause.Generation;
    }

    /// <summary>Reconciles a directory request's estimate with the units Graph charged, and reports the near-limit header.</summary>
    private void Observe(GraphRequestCost cost, ThrottleSignals signals, TimeSpan now)
    {
        if (cost.Area != GraphServiceArea.Directory)
        {
            return;
        }

        if (signals.ResourceUnits is { } actual && actual != cost.ResourceUnits)
        {
            _units.Adjust(actual - cost.ResourceUnits, now);
        }

        if (signals.LimitPercentage is { } percentage)
        {
            _log.NearLimit(percentage, signals.Scope, now);
        }
    }

    private static void Longer(ref TimeSpan wait, ref GraphServiceArea area, TimeSpan candidate, GraphServiceArea candidateArea)
    {
        if (candidate > wait)
        {
            wait = candidate;
            area = candidateArea;
        }
    }

    /// <summary>The 429 state of one area: paused until <see cref="Until"/>; <see cref="Generation"/> counts the pauses.</summary>
    private sealed class AreaPause
    {
        public TimeSpan Until { get; set; }

        public long Generation { get; set; }

        public int ConsecutiveThrottles { get; set; }
    }
}
