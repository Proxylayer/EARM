using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace EArm.Az;

/// <summary>Knobs for <see cref="RetryPolicy"/>; the defaults are the plan's table 3.6.</summary>
public sealed class RetryOptions
{
    /// <summary>429: first delay, doubling each retry.</summary>
    public TimeSpan ThrottleBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>429: cap on the computed delay (a parsable Retry-After is honoured up to five minutes).</summary>
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

    /// <summary>az exit != 0 without a Graph body: first delay, doubling.</summary>
    public TimeSpan AzFailureBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>az exit != 0 without a Graph body: total attempts (3 = two retries).</summary>
    public int AzFailureMaxAttempts { get; set; } = 3;

    private static readonly TimeSpan RetryAfterCap = TimeSpan.FromMinutes(5);

    internal static TimeSpan ClampRetryAfter(TimeSpan value)
    {
        return value > RetryAfterCap ? RetryAfterCap : value;
    }
}

public enum RetryReason
{
    None,
    Throttled,
    ServerError,
    Replication,
    AuthRefresh,
    AzFailure,
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

    /// <summary>Attempts made so far for az-level failures (starts at 1: the attempt that just failed).</summary>
    public int AzFailureAttempts { get; internal set; } = 1;

    public bool AuthRefreshed { get; internal set; }

    public int TotalRetries => ThrottleRetries + ServerErrorRetries + ReplicationRetries + (AzFailureAttempts - 1) + (AuthRefreshed ? 1 : 0);
}

/// <summary>
/// Retry classification from the plan (§3.6):
/// 429 → 2 s doubling to 60 s, max 8, Retry-After honoured; 5xx → exponential, max 5;
/// replication (404 not-found, listed 400 messages) only under <see cref="RetryHint.ExpectReplicationLag"/> → every 5 s up to 90 s;
/// idempotent-success ("already exists") → never retried, surfaced via <see cref="GraphException.IsAlreadyExists"/>;
/// 401 → one <c>az account get-access-token</c> refresh then terminal; az exit != 0 without a Graph body → 3 attempts.
/// <see cref="Decide"/> is pure (given the state) so the table is testable without delays.
/// </summary>
public sealed class RetryPolicy
{
    private readonly RetryOptions _options;
    private readonly IDelayProvider _delayProvider;
    private readonly ILogger<RetryPolicy> _logger;

    public RetryPolicy(ILogger<RetryPolicy> logger)
        : this(new RetryOptions(), TaskDelayProvider.Instance, logger)
    {
    }

    public RetryPolicy(RetryOptions options, IDelayProvider delayProvider, ILogger<RetryPolicy> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _delayProvider = delayProvider ?? throw new ArgumentNullException(nameof(delayProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public RetryOptions Options => _options;

    /// <summary>
    /// Runs <paramref name="operation"/> until it succeeds or the table says stop. <paramref name="refreshAuthentication"/>
    /// is invoked once for a 401 before the single auth retry; when null, 401 is terminal.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        RetryHint hint,
        Func<CancellationToken, Task>? refreshAuthentication,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var state = new RetryState();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is GraphException or AzCliException)
            {
                var decision = Decide(ex, hint, state);
                if (!decision.Retry)
                {
                    throw;
                }

                if (decision.Reason == RetryReason.AuthRefresh)
                {
                    if (refreshAuthentication is null)
                    {
                        throw;
                    }

                    AzLog.RefreshingToken(_logger);
                    await refreshAuthentication(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                AzLog.Retrying(_logger, decision.Reason.ToString(), decision.Delay.TotalMilliseconds, state.TotalRetries, ex.Message);
                await _delayProvider.DelayAsync(decision.Delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Classifies one failure and advances the counters in <paramref name="state"/>.</summary>
    public RetryDecision Decide(Exception error, RetryHint hint, RetryState state)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(state);

        if (hint == RetryHint.None)
        {
            return RetryDecision.Stop;
        }

        return error switch
        {
            GraphException graph => DecideGraph(graph, state),
            AzCliException az => DecideAz(az, state),
            _ => RetryDecision.Stop,
        };
    }

    private RetryDecision DecideGraph(GraphException error, RetryState state)
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
                ? RetryOptions.ClampRetryAfter(retryAfter)
                : Backoff(_options.ThrottleBaseDelay, state.ThrottleRetries, _options.ThrottleMaxDelay);
            state.ThrottleRetries++;
            return new RetryDecision(true, delay, RetryReason.Throttled);
        }

        if (error.IsTransient)
        {
            if (state.ServerErrorRetries >= _options.ServerErrorMaxRetries)
            {
                return RetryDecision.Stop;
            }

            var delay = Backoff(_options.ServerErrorBaseDelay, state.ServerErrorRetries, _options.ServerErrorMaxDelay);
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

    private RetryDecision DecideAz(AzCliException error, RetryState state)
    {
        if (!error.IsRetryable || state.AzFailureAttempts >= _options.AzFailureMaxAttempts)
        {
            return RetryDecision.Stop;
        }

        var delay = Backoff(_options.AzFailureBaseDelay, state.AzFailureAttempts - 1, _options.ServerErrorMaxDelay);
        state.AzFailureAttempts++;
        return new RetryDecision(true, delay, RetryReason.AzFailure);
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
