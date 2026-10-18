using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace EArm.Graph.Tests.Infrastructure;

/// <summary>A clock that only moves when told to (<see cref="TimeProvider.GetTimestamp"/> and <see cref="TimeProvider.GetUtcNow"/> together).</summary>
internal sealed class Review1BStepClock : TimeProvider
{
    private long _ticks;
    private readonly DateTimeOffset _start = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        return Interlocked.Read(ref _ticks);
    }

    public override DateTimeOffset GetUtcNow()
    {
        return _start + TimeSpan.FromTicks(Interlocked.Read(ref _ticks));
    }

    public void Advance(TimeSpan by)
    {
        Interlocked.Add(ref _ticks, by.Ticks);
    }
}

/// <summary>A delay provider that records the requested delay and advances a <see cref="Review1BStepClock"/> by it (so wall-time budgets are testable).</summary>
internal sealed class Review1BAdvancingDelayProvider : IDelayProvider
{
    private readonly Review1BStepClock _clock;
    private readonly RecordingDelayProvider _recording = new();

    public Review1BAdvancingDelayProvider(Review1BStepClock clock)
    {
        _clock = clock;
    }

    public System.Collections.Generic.IReadOnlyList<TimeSpan> Delays => _recording.Delays;

    public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        await _recording.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
        _clock.Advance(delay);
    }
}

/// <summary>
/// A configurable <see cref="HttpGraphClient"/> test rig: any <see cref="HttpMessageHandler"/> (or a ready <see cref="HttpClient"/>, e.g. the
/// production one), any cloud, any <see cref="RetryOptions"/> / <see cref="GraphOptions"/>, an optional retry clock.
/// </summary>
internal sealed class Review1BRig : IDisposable
{
    private readonly HttpClient _http;

    public Review1BRig(
        HttpMessageHandler? handler = null,
        Action<GraphOptions>? configure = null,
        Action<RetryOptions>? configureRetry = null,
        Review1BStepClock? clock = null,
        GraphCloud? cloud = null,
        HttpClient? http = null)
    {
        handler ??= http is null ? new ScriptedHttpHandler() : null;
        Scripted = handler as ScriptedHttpHandler;
        Tokens = new FakeTokenProvider();
        Log = new LogSink();
        IDelayProvider delay;
        if (clock is null)
        {
            var recording = new RecordingDelayProvider();
            Delays = recording;
            delay = recording;
        }
        else
        {
            var advancing = new Review1BAdvancingDelayProvider(clock);
            DelayList = advancing;
            delay = advancing;
        }

        var options = new GraphOptions { Cloud = cloud ?? GraphCloud.Public };
        configure?.Invoke(options);
        var retryOptions = new RetryOptions();
        configureRetry?.Invoke(retryOptions);
        _http = http ?? new HttpClient(handler!, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        Retry = new RetryPolicy(retryOptions, delay, new TestLogger<RetryPolicy>(Log), clock);
        Client = new HttpGraphClient(_http, Tokens, Retry, options, new TestLogger<HttpGraphClient>(Log), new ManualTimeProvider(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero)));
    }

    public ScriptedHttpHandler? Scripted { get; }

    public FakeTokenProvider Tokens { get; }

    public LogSink Log { get; }

    public RecordingDelayProvider? Delays { get; }

    public Review1BAdvancingDelayProvider? DelayList { get; }

    public RetryPolicy Retry { get; }

    public HttpGraphClient Client { get; }

    /// <summary>The delays asked of the retry policy, whichever provider recorded them.</summary>
    public System.Collections.Generic.IReadOnlyList<TimeSpan> AllDelays => Delays?.Delays ?? DelayList!.Delays;

    public void Dispose()
    {
        _http.Dispose();
    }
}
