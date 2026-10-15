using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace EArm.Graph;

/// <summary>Knobs for <see cref="RetryPolicy"/>; the defaults are those documented on <see cref="RetryPolicy"/>.</summary>
public sealed class RetryOptions
{
    /// <summary>429: first delay, doubling each retry.</summary>
    public TimeSpan ThrottleBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>429: cap on the computed delay (a parsable Retry-After is honoured up to <see cref="MaxRetryAfter"/>).</summary>
    public TimeSpan ThrottleMaxDelay { get; set; } = TimeSpan.FromSeconds(60);

    public int ThrottleMaxRetries { get; set; } = 8;

    /// <summary>5xx: first delay, doubling each retry.</summary>
    public TimeSpan ServerErrorBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan ServerErrorMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    public int ServerErrorMaxRetries { get; set; } = 5;

    /// <summary>Replication lag (only under <see cref="RetryHint.ExpectReplicationLag"/>): fixed interval.</summary>
    public TimeSpan ReplicationInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Replication lag: total scheduled wait before giving up.</summary>
    public TimeSpan ReplicationWindow { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>Transport failure (network, timeout) without a Graph response: first delay, doubling.</summary>
    public TimeSpan TransportFailureBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Transport failure (network, timeout): total attempts (3 = two retries).</summary>
    public int TransportFailureMaxAttempts { get; set; } = 3;

    /// <summary>
    /// The longest single wait a server-sent <c>Retry-After</c> may impose on one attempt; a longer one is clamped to this (and logged). A hostile or
    /// broken service cannot park the CLI for minutes per attempt. Default 120 s.
    /// </summary>
    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Wall-clock budget of ONE logical call (every attempt, response time and scheduled delay included): when the next delay would push the call past
    /// it, the last failure is thrown instead of retrying. Default 10 minutes.
    /// </summary>
    public TimeSpan MaxTotalTime { get; set; } = TimeSpan.FromMinutes(10);

    internal TimeSpan ClampRetryAfter(TimeSpan value)
    {
        return value > MaxRetryAfter ? MaxRetryAfter : value;
    }
}

public enum RetryReason
{
    None,
    Throttled,
    ServerError,
    Replication,
    AuthRefresh,
    TransportFailure,
}

/// <summary>What to do after a failure: retry after <see cref="Delay"/> (zero for an auth refresh), or stop.</summary>
public sealed record RetryDecision(bool Retry, TimeSpan Delay, RetryReason Reason)
{
    public static readonly RetryDecision Stop = new(false, TimeSpan.Zero, RetryReason.None);
}

/// <summary>Per-operation counters consulted and advanced by <see cref="RetryPolicy.Decide"/>.</summary>
public sealed class RetryState
{
    public int ThrottleRetries { get; internal set; }

    public int ServerErrorRetries { get; internal set; }

    public int ReplicationRetries { get; internal set; }

    /// <summary>Sum of replication delays scheduled so far (the 90 s window is measured in scheduled wait, not wall clock).</summary>
    public TimeSpan ReplicationWaited { get; internal set; }

    /// <summary>Attempts made so far for transport failures (starts at 1: the attempt that just failed).</summary>
    public int TransportAttempts { get; internal set; } = 1;

    public bool AuthRefreshed { get; internal set; }

    public int TotalRetries => ThrottleRetries + ServerErrorRetries + ReplicationRetries + (TransportAttempts - 1) + (AuthRefreshed ? 1 : 0);
}

/// <summary>
/// Retry classification:
/// 429 → 2 s doubling to 60 s, max 8, Retry-After honoured; 5xx → exponential, max 5 (a parsable Retry-After on a 503/504 replaces the computed delay);
/// replication (404 not-found, listed 400 messages) only under <see cref="RetryHint.ExpectReplicationLag"/> → every 5 s up to 90 s;
/// idempotent-success ("already exists") → never retried, surfaced via <see cref="GraphException.IsAlreadyExists"/>;
/// 401 → one token refresh then terminal; network / timeout failures without a Graph response → 3 attempts.
/// <see cref="Decide"/> is pure (given the state) so the table is testable without delays.
/// </summary>
public sealed class RetryPolicy
{
    private readonly RetryOptions _options;
    private readonly IDelayProvider _delayProvider;
    private readonly ILogger<RetryPolicy> _logger;
    private readonly TimeProvider _time;

    public RetryPolicy(ILogger<RetryPolicy> logger)
        : this(new RetryOptions(), TaskDelayProvider.Instance, logger)
    {
    }

    /// <param name="options">The knobs.</param>
    /// <param name="delayProvider">Waits between attempts.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <param name="time">The clock that measures <see cref="RetryOptions.MaxTotalTime"/> (<see cref="TimeProvider.GetTimestamp"/>); default the system clock.</param>
    public RetryPolicy(RetryOptions options, IDelayProvider delayProvider, ILogger<RetryPolicy> logger, TimeProvider? time = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _delayProvider = delayProvider ?? throw new ArgumentNullException(nameof(delayProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
    }

    public RetryOptions Options => _options;

    /// <summary>
    /// Runs <paramref name="operation"/> until it succeeds or the table says stop. <paramref name="refreshAuthentication"/>
    /// is invoked once for a 401 before the single auth retry; when null, 401 is terminal. A call that is not
    /// <paramref name="idempotent"/> is never repeated after a failure that may have followed a commit (timeout, dropped connection, 5xx).
    /// The whole call is bounded by <see cref="RetryOptions.MaxTotalTime"/>.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        RetryHint hint,
        Func<CancellationToken, Task>? refreshAuthentication,
        CancellationToken cancellationToken,
        bool idempotent = true)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var state = new RetryState();
        var started = _time.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is GraphException or GraphTransportException)
            {
                var decision = Decide(ex, hint, state, idempotent);
                if (!decision.Retry)
                {
                    throw;
                }

                var elapsed = _time.GetElapsedTime(started);
                if (elapsed + decision.Delay > _options.MaxTotalTime)
                {
                    GraphLog.RetryBudgetExhausted(_logger, elapsed.TotalSeconds, _options.MaxTotalTime.TotalSeconds);
                    throw;
                }

                if (decision.Reason == RetryReason.AuthRefresh)
                {
                    if (refreshAuthentication is null)
                    {
                        throw;
                    }

                    GraphLog.RefreshingToken(_logger);
                    await refreshAuthentication(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                GraphLog.Retrying(_logger, decision.Reason.ToString(), decision.Delay.TotalMilliseconds, state.TotalRetries, ex.Message);
                await _delayProvider.DelayAsync(decision.Delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Classifies one failure and advances the counters in <paramref name="state"/>.</summary>
    /// <param name="error">The failure.</param>
    /// <param name="hint">The request's retry hint.</param>
    /// <param name="state">The per-call counters.</param>
    /// <param name="idempotent">False for a write that may not be repeated blindly: failures that may have followed a commit are then terminal.</param>
    public RetryDecision Decide(Exception error, RetryHint hint, RetryState state, bool idempotent = true)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(state);

        if (hint == RetryHint.None)
        {
            return RetryDecision.Stop;
        }

        return error switch
        {
            GraphException graph => DecideGraph(graph, state, idempotent),
            GraphTransportException transport => DecideTransport(transport, state, idempotent),
            _ => RetryDecision.Stop,
        };
    }

    private RetryDecision DecideGraph(GraphException error, RetryState state, bool idempotent)
    {
        if (error.IsAlreadyExists)
        {
            return RetryDecision.Stop;
        }

        if (error.Status == System.Net.HttpStatusCode.TooManyRequests)
        {
            if (state.ThrottleRetries >= _options.ThrottleMaxRetries)
            {
                return RetryDecision.Stop;
            }

            var delay = error.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero
                ? HonourRetryAfter(retryAfter)
                : Backoff(_options.ThrottleBaseDelay, state.ThrottleRetries, _options.ThrottleMaxDelay);
            state.ThrottleRetries++;
            return new RetryDecision(true, delay, RetryReason.Throttled);
        }

        if (error.IsTransient)
        {
            // A 5xx may have followed a commit: only an idempotent request is repeated blindly (the 429 above was rejected before processing).
            if (!idempotent || state.ServerErrorRetries >= _options.ServerErrorMaxRetries)
            {
                return RetryDecision.Stop;
            }

            var delay = error.RetryAfter is { } serverRetryAfter && serverRetryAfter > TimeSpan.Zero
                ? HonourRetryAfter(serverRetryAfter)
                : Backoff(_options.ServerErrorBaseDelay, state.ServerErrorRetries, _options.ServerErrorMaxDelay);
            state.ServerErrorRetries++;
            return new RetryDecision(true, delay, RetryReason.ServerError);
        }

        if (error.IsReplication)
        {
            if (state.ReplicationWaited + _options.ReplicationInterval > _options.ReplicationWindow)
            {
                return RetryDecision.Stop;
            }

            state.ReplicationWaited += _options.ReplicationInterval;
            state.ReplicationRetries++;
            return new RetryDecision(true, _options.ReplicationInterval, RetryReason.Replication);
        }

        if (error.Status == System.Net.HttpStatusCode.Unauthorized && !state.AuthRefreshed)
        {
            state.AuthRefreshed = true;
            return new RetryDecision(true, TimeSpan.Zero, RetryReason.AuthRefresh);
        }

        return RetryDecision.Stop;
    }

    private RetryDecision DecideTransport(GraphTransportException error, RetryState state, bool idempotent)
    {
        // A timeout or dropped connection after a non-idempotent request was sent may hide a commit: surface it (Ambiguous) so the caller re-reads.
        if (!error.IsRetryable || (!idempotent && error.Ambiguous) || state.TransportAttempts >= _options.TransportFailureMaxAttempts)
        {
            return RetryDecision.Stop;
        }

        var delay = Backoff(_options.TransportFailureBaseDelay, state.TransportAttempts - 1, _options.ServerErrorMaxDelay);
        state.TransportAttempts++;
        return new RetryDecision(true, delay, RetryReason.TransportFailure);
    }

    /// <summary>A server-sent Retry-After, clamped to <see cref="RetryOptions.MaxRetryAfter"/> (logged when it was).</summary>
    private TimeSpan HonourRetryAfter(TimeSpan requested)
    {
        var clamped = _options.ClampRetryAfter(requested);
        if (clamped < requested)
        {
            GraphLog.RetryAfterClamped(_logger, requested.TotalSeconds, clamped.TotalSeconds);
        }

        return clamped;
    }

    /// <summary><c>min(base * 2^attempt, cap)</c>.</summary>
    internal static TimeSpan Backoff(TimeSpan baseDelay, int attempt, TimeSpan cap)
    {
        if (attempt <= 0)
        {
            return baseDelay > cap ? cap : baseDelay;
        }

        var factor = Math.Pow(2, Math.Min(attempt, 30));
        var ticks = baseDelay.Ticks * factor;
        if (ticks >= cap.Ticks)
        {
            return cap;
        }

        return TimeSpan.FromTicks((long)ticks);
    }
}
