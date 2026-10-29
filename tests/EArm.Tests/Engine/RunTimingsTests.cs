using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine.Timing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EArm.Tests.Engine;

/// <summary>
/// <see cref="RunTimings"/>: the phases of one run (<c>--timings</c>, log event 2090). A phase is measured from <see cref="RunTimings.Measure"/> to the disposal of
/// its <see cref="RunPhase"/>; its Graph request count and scheduler wait are the deltas of <see cref="RunTimings.Requests"/> over that span.
/// </summary>
public sealed class RunTimingsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Waits until the run's clock has moved by at least <paramref name="milliseconds"/> whole milliseconds, so two phases cannot start in the same one.</summary>
    private static async Task NextMillisecondsAsync(int milliseconds = 15)
    {
        await Task.Delay(milliseconds, Ct);
    }

    private static void Requests(RunTimings timings, int count, bool write = false)
    {
        for (int i = 0; i < count; i++)
        {
            timings.Requests.Started(write);
            timings.Requests.Finished();
        }
    }

    [Fact]
    public void ANewRecorder_HasNoPhase_NoRequest_AndNoWrite()
    {
        var timings = new RunTimings();

        Assert.Empty(timings.Phases);
        Assert.Equal(0, timings.Requests.Sent);
        Assert.Null(timings.FirstWriteAfter);
        Assert.InRange(timings.StartedUtc, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void APhaseNeedsAName(string name)
    {
        Assert.Throws<ArgumentException>(() => new RunTimings().Measure(name));
    }

    [Fact]
    public void APhase_IsRecordedWhenDisposed_AndOnlyOnce()
    {
        var timings = new RunTimings();
        var phase = timings.Measure("load template");

        Assert.Empty(timings.Phases);

        phase.Dispose();
        phase.Dispose();

        var recorded = Assert.Single(timings.Phases);
        Assert.Equal("load template", recorded.Name);
        Assert.True(recorded.ElapsedMs >= 0);
        Assert.True(recorded.StartedAtMs >= 0);
    }

    [Fact]
    public void EachPhaseCountsTheRequestsStartedWhileItRan()
    {
        var timings = new RunTimings();
        Requests(timings, 2);

        using (timings.Measure("sign-in and preflight"))
        {
            Requests(timings, 1);
        }

        using (timings.Measure("plan: expand and graph"))
        {
        }

        using (timings.Measure("what-if reads"))
        {
            Requests(timings, 7);
        }

        Assert.Equal(
            [("sign-in and preflight", 1L), ("plan: expand and graph", 0L), ("what-if reads", 7L)],
            timings.Phases.Select(p => (p.Name, p.Requests)));
        Assert.Equal(10, timings.Requests.Sent);
    }

    [Fact]
    public void EachPhaseCountsTheSchedulerWaitOfItsOwnSpan()
    {
        var timings = new RunTimings();
        timings.Requests.SchedulerWaited(TimeSpan.FromMilliseconds(40));

        using (timings.Measure("first"))
        {
            timings.Requests.SchedulerWaited(TimeSpan.FromMilliseconds(250));
            timings.Requests.SchedulerWaited(TimeSpan.FromMilliseconds(125));
        }

        using (timings.Measure("second"))
        {
        }

        Assert.Equal([("first", 375L), ("second", 0L)], timings.Phases.Select(p => (p.Name, p.SchedulerWaitMs)));
        Assert.Equal(TimeSpan.FromMilliseconds(415), timings.Requests.SchedulerWait);
    }

    [Fact]
    [Trait("Category", "Timing")]
    public async Task Phases_AreListedInTheOrderTheyStarted_NotTheOrderTheyEnded()
    {
        var timings = new RunTimings();
        var outer = timings.Measure("outer");
        await NextMillisecondsAsync();
        var inner = timings.Measure("inner");
        await NextMillisecondsAsync();
        inner.Dispose();
        await NextMillisecondsAsync();
        var later = timings.Measure("later");
        later.Dispose();
        outer.Dispose();

        var phases = timings.Phases;
        Assert.Equal(["outer", "inner", "later"], phases.Select(p => p.Name));
        Assert.True(phases[0].StartedAtMs < phases[1].StartedAtMs && phases[1].StartedAtMs < phases[2].StartedAtMs, string.Join(", ", phases));
    }

    [Fact]
    [Trait("Category", "Timing")]
    public async Task NestedAndOverlappingPhases_EachCountTheRequestsOfTheirWholeSpan()
    {
        // Like 'tenant size (overlapped)', which runs alongside 'Key Vault secrets' and the plan: a phase that overlaps another counts the other's requests too.
        var timings = new RunTimings();
        var overlapped = timings.Measure("tenant size (overlapped)");
        Requests(timings, 1);
        await NextMillisecondsAsync();
        var keyVault = timings.Measure("Key Vault secrets");
        Requests(timings, 2);
        await NextMillisecondsAsync();
        var plan = timings.Measure("plan: expand and graph");
        Requests(timings, 3);
        keyVault.Dispose();
        overlapped.Dispose();
        Requests(timings, 4);
        plan.Dispose();

        var phases = timings.Phases;
        Assert.Equal(
            [("tenant size (overlapped)", 6L), ("Key Vault secrets", 5L), ("plan: expand and graph", 7L)],
            phases.Select(p => (p.Name, p.Requests)));

        // The inner phase lies within the outer one.
        var outer = phases[0];
        var nested = phases[1];
        Assert.True(nested.StartedAtMs >= outer.StartedAtMs, string.Join(", ", phases));
        Assert.True(nested.StartedAtMs + nested.ElapsedMs <= outer.StartedAtMs + outer.ElapsedMs + 1, string.Join(", ", phases));
    }

    [Fact]
    public void PhasesThatStartInTheSameMillisecond_AreStillListedInTheOrderTheyStarted()
    {
        var timings = new RunTimings();
        var outer = timings.Measure("outer");
        var inner = timings.Measure("inner");
        inner.Dispose();
        outer.Dispose();

        Assert.Equal(["outer", "inner"], timings.Phases.Select(p => p.Name));
    }

    [Fact]
    public void UseLogger_ReplaysTheFinishedPhasesAsEvent2090_ThenLogsEachNewOneAsItEnds()
    {
        var timings = new RunTimings();
        using (timings.Measure("load template"))
        {
        }

        using (timings.Measure("run scopes"))
        {
            Requests(timings, 3);
        }

        var running = timings.Measure("validate");
        var logger = new EventLogger();

        timings.UseLogger(logger);

        Assert.Equal(2, logger.Entries.Count);
        Assert.All(logger.Entries, e => Assert.Equal((2090, LogLevel.Information), (e.EventId, e.Level)));
        var finished = timings.Phases;
        Assert.Equal(
            [$"Phase load template: {finished[0].ElapsedMs} ms, 0 Graph requests", $"Phase run scopes: {finished[1].ElapsedMs} ms, 3 Graph requests"],
            logger.Entries.Select(e => e.Message));

        Requests(timings, 1);
        running.Dispose();

        Assert.Equal(3, logger.Entries.Count);
        Assert.Equal($"Phase validate: {timings.Phases.Single(p => p.Name == "validate").ElapsedMs} ms, 1 Graph requests", logger.Entries[2].Message);
        Assert.Equal(2090, logger.Entries[2].EventId);
    }

    [Fact]
    public void UseLoggerNull_DetachesTheLogger_AndThePhasesAreStillRecorded()
    {
        var timings = new RunTimings();
        var logger = new EventLogger();
        timings.UseLogger(logger);
        using (timings.Measure("sign-in and preflight"))
        {
        }

        timings.UseLogger(null);
        using (timings.Measure("apply"))
        {
            Requests(timings, 2, write: true);
        }

        Assert.Equal(["Phase sign-in and preflight"], logger.Entries.Select(e => e.Message.Split(':')[0]));
        Assert.Equal(["sign-in and preflight", "apply"], timings.Phases.Select(p => p.Name));

        // A logger attached again gets every finished phase, the one recorded while detached included.
        var again = new EventLogger();
        timings.UseLogger(again);
        Assert.Equal(["Phase sign-in and preflight", "Phase apply"], again.Entries.Select(e => e.Message.Split(':')[0]));
    }

    [Fact]
    public void FirstWriteAfter_IsSetByTheFirstWriteOnly()
    {
        var timings = new RunTimings();
        Requests(timings, 3);

        Assert.Null(timings.FirstWriteAfter);

        Requests(timings, 1, write: true);
        var first = timings.FirstWriteAfter;
        var stamp = timings.Requests.FirstWriteTimestamp;
        Requests(timings, 1, write: true);

        Assert.NotNull(first);
        Assert.True(first >= TimeSpan.Zero);
        Assert.Equal(stamp, timings.Requests.FirstWriteTimestamp);
        Assert.Equal(5, timings.Requests.Sent);
    }

    [Fact]
    public async Task ConcurrentPhases_AreAllRecorded()
    {
        var timings = new RunTimings();

        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => Task.Run(() =>
        {
            using (timings.Measure("phase " + i))
            {
                Requests(timings, 1);
            }
        }, Ct)));

        Assert.Equal(50, timings.Phases.Count);
        Assert.Equal(50, timings.Requests.Sent);
        Assert.Equal(50, timings.Phases.Select(p => p.Name).Distinct().Count());
    }

    /// <summary>Captures the event id, level and formatted message of every log call.</summary>
    private sealed class EventLogger : ILogger
    {
        private readonly List<(int EventId, LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(int EventId, LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            lock (_entries)
            {
                _entries.Add((eventId.Id, logLevel, formatter(state, exception)));
            }
        }
    }
}
