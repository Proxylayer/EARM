using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph.Tests.Infrastructure;
using EArm.Graph.Throttling;
using Microsoft.Extensions.Logging;
using Xunit;
using static EArm.Graph.Tests.Infrastructure.SchedulerRig;

namespace EArm.Graph.Tests;

/// <summary>
/// <see cref="GraphRequestScheduler"/> on a clock that only moves when the test moves it: the directory resource-unit and write budgets, Conditional
/// Access at one request per second and one at a time, the per-area pause after a 429, the areas without a published rate, refunds and the
/// tenant-size probe. Published numbers: 3,500 / 5,000 / 8,000 resource units per 10 s, 3,000 writes per 150 s, 1 Conditional Access request per second.
/// </summary>
public sealed class GraphRequestSchedulerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- directory budget ---------------------------------------------------------------------------------

    [Fact]
    public async Task MoreThanTheSmallBudgetInTenSecondsWaitsExactlyTheDeficit()
    {
        var rig = new SchedulerRig();

        var first = await rig.DelaysOf(() => rig.SendAsync(Call(Directory(3_500))));
        var second = await rig.DelaysOf(() => rig.SendAsync(Call(Directory(350))));
        var third = await rig.DelaysOf(() => rig.SendAsync(Call(Directory(35))));

        Assert.Empty(first);

        // 3,500 units per 10 s is 350 per second: a deficit of 350 units is one second, the next 35 wait behind it.
        Assert.Equal([TimeSpan.FromSeconds(1)], second);
        Assert.Equal([TimeSpan.FromSeconds(385d / 350)], third);
        Assert.Equal(TenantSizeTier.Small, rig.Scheduler.Tier);
    }

    [Fact]
    public async Task RealReadsFillTheBudgetAtTheirPublishedCost()
    {
        var rig = new SchedulerRig();

        // GET /users costs 2 units: 1,750 of them are exactly the 3,500-unit budget.
        for (var i = 0; i < 1_750; i++)
        {
            await rig.SendAsync(Call("GET", "/v1.0/users"));
        }

        Assert.Empty(rig.Delays.Delays);
        await rig.SendAsync(Call("GET", "/v1.0/users"));
        Assert.Equal([TimeSpan.FromSeconds(2d / 350)], rig.Delays.Delays);
    }

    [Fact]
    public async Task TheBudgetRefillsWithTime()
    {
        var rig = new SchedulerRig();
        await rig.SendAsync(Call(Directory(3_500)));

        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        await rig.SendAsync(Call(Directory(350)));
        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        await rig.SendAsync(Call(Directory(3_500)));

        Assert.Empty(rig.Delays.Delays);
    }

    [Fact]
    public async Task The3001stWriteIn150SecondsWaits()
    {
        var rig = new SchedulerRig();

        for (var i = 0; i < 3_000; i++)
        {
            await rig.SendAsync(Call(Directory(1, write: true)));
        }

        Assert.Empty(rig.Delays.Delays);

        // 3,000 writes per 150 s is 20 per second; the 3,001 units are still within the 3,500-unit budget.
        await rig.SendAsync(Call(Directory(1, write: true)));
        Assert.Equal([TimeSpan.FromSeconds(0.05)], rig.Delays.Delays);
        Assert.Contains("budget", Assert.Single(rig.Events(1060)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadsDoNotSpendTheWriteQuota()
    {
        var rig = new SchedulerRig();

        for (var i = 0; i < 3_000; i++)
        {
            await rig.SendAsync(Call(Directory(1)));
        }

        await rig.SendAsync(Call(Directory(1, write: true)));

        Assert.Empty(rig.Delays.Delays);
    }

    [Fact]
    public async Task ChargedResourceUnitsAboveTheEstimateAreChargedToo()
    {
        var rig = new SchedulerRig();

        var ticket = await rig.SendAsync(Call(Directory(1)));
        rig.Scheduler.Complete(ticket, Ok(resourceUnits: 3_500));
        await rig.SendAsync(Call(Directory(350)));

        Assert.Equal([TimeSpan.FromSeconds(1)], rig.Delays.Delays);
    }

    [Fact]
    public async Task ChargedResourceUnitsBelowTheEstimateAreGivenBack()
    {
        var rig = new SchedulerRig();

        var ticket = await rig.SendAsync(Call(Directory(3_500)));
        rig.Scheduler.Complete(ticket, Ok(resourceUnits: 3_150));
        await rig.SendAsync(Call(Directory(350)));
        await rig.SendAsync(Call(Directory(350)));

        // 350 units were given back: the first 350 go at once, the next 350 wait one second.
        Assert.Equal([TimeSpan.FromSeconds(1)], rig.Delays.Delays);
    }

    [Fact]
    public async Task AResourceUnitHeaderIsIgnoredOnA429AnEnvelopeAndANonDirectoryCall()
    {
        var rig = new SchedulerRig();

        var directory = await rig.SendAsync(Call(Directory(1)));
        rig.Scheduler.Complete(directory, new ThrottleSignals(429, TimeSpan.FromMilliseconds(1), 3_500, null, null));
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        var envelope = await rig.Scheduler.AcquireAsync(new ScheduledCall([Directory(1)], isBatch: true), Ct);
        envelope.Dispose();
        rig.Scheduler.Complete(envelope, Ok(resourceUnits: 3_500));
        var governance = await rig.SendAsync(Call(In(GraphServiceArea.IdentityGovernance)));
        rig.Scheduler.Complete(governance, Ok(resourceUnits: 3_500));

        // Only the estimates were spent (the directory unit before the 1 s refill, the envelope's after it): 3,499 units are left.
        await rig.SendAsync(Call(Directory(3_499)));

        Assert.Empty(rig.Delays.Delays);
    }

    // ---- Conditional Access -------------------------------------------------------------------------------

    [Fact]
    public async Task ConditionalAccessRequestsAreSpacedOneSecondApart()
    {
        var clock = new Review1BStepClock();
        var delays = new Review1BAdvancingDelayProvider(clock);
        var rig = new SchedulerRig(delays, clock: clock);

        for (var i = 0; i < 4; i++)
        {
            await rig.SendAsync(Call("POST", "/v1.0/identity/conditionalAccess/policies"));
        }

        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)], delays.Delays);
        Assert.Equal(TimeSpan.FromSeconds(3), clock.GetElapsedTime(0));
    }

    [Fact]
    public async Task ConditionalAccessRequestsQueuedAtOnceGetConsecutiveOneSecondSlots()
    {
        var rig = new SchedulerRig();

        for (var i = 0; i < 3; i++)
        {
            await rig.SendAsync(Call("GET", "/beta/identity/conditionalAccess/namedLocations/n1"));
        }

        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], rig.Delays.Delays);
    }

    [Fact]
    public async Task AConditionalAccessRequestIsNeverSentWhileAnotherOneIsInFlight()
    {
        var rig = new SchedulerRig();
        var first = await rig.Scheduler.AcquireAsync(Call("GET", "/identity/conditionalAccess/policies"), Ct);

        var second = rig.Scheduler.AcquireAsync(Call("PATCH", "/identity/conditionalAccess/policies/p1"), Ct);
        await Task.Delay(100, Ct);

        Assert.False(second.IsCompleted);

        // Another area goes past the waiting Conditional Access request.
        var directory = rig.Scheduler.AcquireAsync(Call("GET", "/users"), Ct);
        var governance = rig.Scheduler.AcquireAsync(Call("GET", "/identityGovernance/accessReviews/definitions"), Ct);
        Assert.True(directory.IsCompletedSuccessfully);
        Assert.True(governance.IsCompletedSuccessfully);

        first.Dispose();
        first.Dispose();
        var secondTicket = await second.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // Disposing the first ticket twice released the serialisation once: a third request still waits for the second.
        var third = rig.Scheduler.AcquireAsync(Call("DELETE", "/identity/conditionalAccess/namedLocations/n1"), Ct);
        await Task.Delay(100, Ct);
        Assert.False(third.IsCompleted);
        secondTicket.Dispose();
        (await third.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Dispose();
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], rig.Delays.Delays);
    }

    [Fact]
    public async Task OtherAreasAreNotDelayedByConditionalAccessPacing()
    {
        var rig = new SchedulerRig();
        for (var i = 0; i < 5; i++)
        {
            await rig.SendAsync(Call("GET", "/identity/conditionalAccess/policies"));
        }

        var delays = await rig.DelaysOf(async () =>
        {
            await rig.SendAsync(Call("GET", "/users"));
            await rig.SendAsync(Call("POST", "/groups"));
            await rig.SendAsync(Call("GET", "/identity/conditionalAccess/authenticationContextClassReferences"));
            await rig.SendAsync(Call("GET", "/policies/roleManagementPolicies"));
            await rig.SendAsync(Call("GET", "/roleManagement/directory/roleAssignments"));
            await rig.SendAsync(Call("GET", "/identityGovernance/accessReviews/definitions"));
        });

        Assert.Empty(delays);
        Assert.Equal(4, rig.Delays.Delays.Count);
    }

    // ---- 429 ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A429PausesOnlyItsArea()
    {
        var rig = new SchedulerRig();
        var tripping = await rig.SendAsync(Call("GET", "/identityGovernance/accessReviews/definitions"));

        rig.Scheduler.Complete(tripping, Throttled(10, scope: "Tenant_Application"));

        var others = await rig.DelaysOf(async () =>
        {
            await rig.SendAsync(Call("GET", "/users"));
            await rig.SendAsync(Call("GET", "/identity/conditionalAccess/policies"));
            await rig.SendAsync(Call("GET", "/roleManagement/directory/roleAssignments"));
            await rig.SendAsync(Call("GET", "/policies/roleManagementPolicies"));
            await rig.SendAsync(Call("GET", "/groupLifecyclePolicies"));
        });
        var same = await rig.DelaysOf(() => rig.SendAsync(Call("POST", "/identityGovernance/entitlementManagement/assignmentRequests")));

        Assert.Empty(others);
        Assert.Equal([TimeSpan.FromSeconds(10)], same);
        var paused = Assert.Single(rig.Events(1062));
        Assert.Contains("IdentityGovernance", paused.Message, StringComparison.Ordinal);
        Assert.Contains("Tenant_Application", paused.Message, StringComparison.Ordinal);
        Assert.Contains("paused after a 429", Assert.Single(rig.Events(1060)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCallThatTrippedThePauseIsNotMadeToWaitForItAgain()
    {
        var rig = new SchedulerRig();
        var call = Call("GET", "/users");
        var ticket = await rig.SendAsync(call);

        rig.Scheduler.Complete(ticket, Throttled(30));
        var retry = await rig.DelaysOf(async () => ticket = await rig.SendAsync(call));

        // Its retry attempt goes at once (the retry policy already waited the Retry-After); a second 429 on that retry does not change that.
        rig.Scheduler.Complete(ticket, Throttled(30));
        var secondRetry = await rig.DelaysOf(() => rig.SendAsync(call));

        Assert.Empty(retry);
        Assert.Empty(secondRetry);
        Assert.Equal([TimeSpan.FromSeconds(30)], await rig.DelaysOf(() => rig.SendAsync(Call("GET", "/groups"))));
    }

    [Fact]
    public async Task ACallWaitsForAPauseThatAnotherCallTrippedAfterItsOwn()
    {
        var rig = new SchedulerRig();
        var first = Call("GET", "/users");
        var second = Call("GET", "/groups");
        var firstTicket = await rig.SendAsync(first);
        rig.Scheduler.Complete(firstTicket, Throttled(5));
        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        var secondTicket = await rig.SendAsync(second);
        rig.Scheduler.Complete(secondTicket, Throttled(20));

        Assert.Equal([TimeSpan.FromSeconds(20)], await rig.DelaysOf(() => rig.SendAsync(first)));
        Assert.Empty(await rig.DelaysOf(() => rig.SendAsync(second)));
    }

    [Fact]
    public async Task APauseThatStartsWhileARequestWaitsExtendsItsWait()
    {
        var clock = new Review1BStepClock();
        var gated = new GatedDelayProvider(clock);
        var rig = new SchedulerRig(gated, clock: clock);
        var tripping = await rig.Scheduler.AcquireAsync(Call(Directory(1)), Ct);
        tripping.Dispose();
        await rig.SendAsync(Call(Directory(3_499)));

        var waiting = rig.SendAsync(Call(Directory(350)));
        await gated.WaitForPendingAsync(1);
        rig.Scheduler.Complete(tripping, Throttled(5));
        gated.ReleaseNext();
        await gated.WaitForPendingAsync(1);
        gated.ReleaseNext();
        await waiting.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // One second for the budget; the 5 s pause began at 0 s, so 4 s of it are left when the budget wait ends.
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4)], gated.Requested);
        var debug = rig.Events(1060);
        Assert.Equal(2, debug.Count);
        Assert.Contains("budget", debug[0].Message, StringComparison.Ordinal);
        Assert.Contains("paused after a 429", debug[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APauseThatEndsBeforeTheBudgetWaitAddsNoWait()
    {
        var clock = new Review1BStepClock();
        var gated = new GatedDelayProvider(clock);
        var rig = new SchedulerRig(gated, clock: clock);
        var tripping = await rig.Scheduler.AcquireAsync(Call(Directory(1)), Ct);
        tripping.Dispose();
        await rig.SendAsync(Call(Directory(3_499)));

        var waiting = rig.SendAsync(Call(Directory(700)));
        await gated.WaitForPendingAsync(1);
        rig.Scheduler.Complete(tripping, Throttled(1));
        gated.ReleaseNext();
        await waiting.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal([TimeSpan.FromSeconds(2)], gated.Requested);
    }

    [Fact]
    public async Task WithoutRetryAfterThePauseFollowsThePerAreaThrottleBackoffAndResetsOnSuccess()
    {
        var rig = new SchedulerRig();
        var directory = Call("GET", "/users");
        var governance = Call("GET", "/identityGovernance/accessReviews/definitions");

        async Task<TimeSpan> PauseAfter429(ScheduledCall tripping, string observerPath)
        {
            var ticket = await rig.SendAsync(tripping);
            rig.Scheduler.Complete(ticket, Throttled());
            var waits = await rig.DelaysOf(() => rig.SendAsync(Call("GET", observerPath)));
            rig.Clock.Advance(TimeSpan.FromMinutes(5));
            return waits.Single();
        }

        // 2 s, 4 s, 8 s (RetryOptions.ThrottleBaseDelay doubling) for IdentityGovernance ...
        Assert.Equal(TimeSpan.FromSeconds(2), await PauseAfter429(governance, "/identityGovernance/accessReviews/instances"));
        Assert.Equal(TimeSpan.FromSeconds(4), await PauseAfter429(governance, "/identityGovernance/accessReviews/instances"));
        Assert.Equal(TimeSpan.FromSeconds(8), await PauseAfter429(governance, "/identityGovernance/accessReviews/instances"));

        // ... while the directory's own schedule starts at 2 s.
        Assert.Equal(TimeSpan.FromSeconds(2), await PauseAfter429(directory, "/groups"));

        // A success of another area does not reset IdentityGovernance; its own success does.
        var success = await rig.SendAsync(directory);
        rig.Scheduler.Complete(success, Ok());
        Assert.Equal(TimeSpan.FromSeconds(16), await PauseAfter429(governance, "/identityGovernance/accessReviews/instances"));
        success = await rig.SendAsync(governance);
        rig.Scheduler.Complete(success, Ok());
        Assert.Equal(TimeSpan.FromSeconds(2), await PauseAfter429(governance, "/identityGovernance/accessReviews/instances"));
    }

    [Fact]
    public async Task TheThrottleBackoffIsCappedAtTheThrottleMaxDelay()
    {
        var rig = new SchedulerRig();
        var call = Call("GET", "/roleManagement/directory/roleAssignments");

        for (var i = 0; i < 8; i++)
        {
            var ticket = await rig.SendAsync(call);
            rig.Scheduler.Complete(ticket, Throttled());
        }

        Assert.Equal([TimeSpan.FromSeconds(60)], await rig.DelaysOf(() => rig.SendAsync(Call("GET", "/roleManagement/directory/roleDefinitions"))));
    }

    [Theory]
    [InlineData(600, 120)]
    [InlineData(121, 120)]
    [InlineData(120, 120)]
    [InlineData(7, 7)]
    public async Task TheRetryAfterPauseIsCappedAtMaxRetryAfter(double retryAfter, double expected)
    {
        var rig = new SchedulerRig();
        var ticket = await rig.SendAsync(Call("GET", "/groups"));

        rig.Scheduler.Complete(ticket, Throttled(retryAfter));

        Assert.Equal([TimeSpan.FromSeconds(expected)], await rig.DelaysOf(() => rig.SendAsync(Call("GET", "/users"))));
    }

    [Fact]
    public async Task TheRetryAfterCapFollowsTheRetryOptions()
    {
        var rig = new SchedulerRig(retry: new RetryOptions { MaxRetryAfter = TimeSpan.FromSeconds(30) });
        var ticket = await rig.SendAsync(Call("GET", "/groups"));

        rig.Scheduler.Complete(ticket, Throttled(600));

        Assert.Equal([TimeSpan.FromSeconds(30)], await rig.DelaysOf(() => rig.SendAsync(Call("GET", "/users"))));
    }

    [Fact]
    public async Task AZeroRetryAfterFallsBackToTheThrottleBackoff()
    {
        var rig = new SchedulerRig();
        var ticket = await rig.SendAsync(Call("GET", "/groups"));

        rig.Scheduler.Complete(ticket, new ThrottleSignals(429, TimeSpan.Zero, null, null, null));

        Assert.Equal([TimeSpan.FromSeconds(2)], await rig.DelaysOf(() => rig.SendAsync(Call("GET", "/users"))));
    }

    [Fact]
    public async Task AShorterLaterPauseDoesNotCutAnEarlierLongerOne()
    {
        var rig = new SchedulerRig();
        var ticket = await rig.SendAsync(Call("GET", "/groups"));
        rig.Scheduler.Complete(ticket, Throttled(60));
        ticket = await rig.SendAsync(Call("GET", "/users"));
        rig.Scheduler.Complete(ticket, Throttled(5));

        Assert.Equal([TimeSpan.FromSeconds(60)], await rig.DelaysOf(() => rig.SendAsync(Call("GET", "/applications"))));
    }

    // ---- areas without a published rate ------------------------------------------------------------------

    [Theory]
    [InlineData("/identityGovernance/entitlementManagement/accessPackages")]
    [InlineData("/roleManagement/directory/roleEligibilityScheduleRequests")]
    [InlineData("/policies/roleManagementPolicyAssignments")]
    [InlineData("/groupLifecyclePolicies")]
    [InlineData("/identity/conditionalAccess/authenticationContextClassReferences")]
    public async Task AnUnpublishedAreaIsNeverPacedButA429StillPausesIt(string path)
    {
        var rig = new SchedulerRig();

        for (var i = 0; i < 5_000; i++)
        {
            await rig.SendAsync(Call(i % 2 == 0 ? "GET" : "POST", path));
        }

        Assert.Empty(rig.Delays.Delays);

        var ticket = await rig.SendAsync(Call("GET", path));
        rig.Scheduler.Complete(ticket, Throttled(12));
        Assert.Equal([TimeSpan.FromSeconds(12)], await rig.DelaysOf(() => rig.SendAsync(Call("GET", path + "/x"))));
        Assert.Empty(await rig.DelaysOf(() => rig.SendAsync(Call("GET", "/users"))));
    }

    [Fact]
    public async Task TheBatchEnvelopeItselfChargesNothing()
    {
        var rig = new SchedulerRig();

        for (var i = 0; i < 5_000; i++)
        {
            await rig.SendAsync(Call("POST", "/$batch"));
        }

        var ticket = await rig.SendAsync(Call("POST", "/$batch"));
        rig.Scheduler.Complete(ticket, Throttled(10));
        await rig.SendAsync(Call("POST", "/$batch"));

        Assert.Empty(rig.Delays.Delays);
    }

    // ---- refunds ------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACallCancelledWhileWaitingGivesItsBudgetBack()
    {
        var rig = new SchedulerRig();
        await rig.SendAsync(Call(Directory(3_500, write: true)));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Scheduler.AcquireAsync(Call(Directory(350, write: true)), cancelled.Token));
        var next = await rig.DelaysOf(() => rig.SendAsync(Call(Directory(350))));

        // The cancelled call's 350 units were given back: the next one waits one second, not two.
        Assert.Equal([TimeSpan.FromSeconds(1)], next);
    }

    [Fact]
    public async Task ACancelledConditionalAccessCallGivesItsSlotAndTheSerialisationBack()
    {
        var clock = new Review1BStepClock();
        var gated = new GatedDelayProvider(clock);
        var rig = new SchedulerRig(gated, clock: clock);
        await rig.SendAsync(Call("GET", "/identity/conditionalAccess/policies"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var cancelled = rig.Scheduler.AcquireAsync(Call("GET", "/identity/conditionalAccess/policies/p1"), cts.Token);
        await gated.WaitForPendingAsync(1);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        var next = rig.SendAsync(Call("GET", "/identity/conditionalAccess/policies/p2"));
        await gated.WaitForPendingAsync(1);
        gated.ReleaseNext();
        await next.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)], gated.Requested);
    }

    // ---- logging ------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryWaitIsADebugLine()
    {
        var rig = new SchedulerRig();
        await rig.SendAsync(Call(Directory(3_500)));

        for (var i = 0; i < 5; i++)
        {
            await rig.SendAsync(Call(Directory(35)));
        }

        var debug = rig.Events(1060);
        Assert.Equal(5, debug.Count);
        Assert.All(debug, static e => Assert.Equal(LogLevel.Debug, e.Level));
        Assert.All(debug, static e => Assert.Contains("Directory", e.Message, StringComparison.Ordinal));

        // The information summary is written once for these five waits within the same 30 s.
        var summary = Assert.Single(rig.Events(1061));
        Assert.Equal(LogLevel.Information, summary.Level);
    }

    [Fact]
    public void ThePacingSummaryIsWrittenOncePerAreaPerThirtySeconds()
    {
        var sink = new LogSink();
        var log = new SchedulerLog(new TestLogger<GraphRequestScheduler>(sink));
        var second = TimeSpan.FromSeconds(1);

        log.Delayed(GraphServiceArea.Directory, second, false, TimeSpan.Zero);
        for (var i = 0; i < 4; i++)
        {
            log.Delayed(GraphServiceArea.Directory, second, false, TimeSpan.FromSeconds(10));
        }

        log.Delayed(GraphServiceArea.ConditionalAccess, second, false, TimeSpan.FromSeconds(10));
        log.Delayed(GraphServiceArea.Directory, second, true, TimeSpan.FromSeconds(29.9));
        log.Delayed(GraphServiceArea.Directory, second, false, TimeSpan.FromSeconds(30));
        log.Delayed(GraphServiceArea.ConditionalAccess, second, false, TimeSpan.FromSeconds(39.9));
        log.Delayed(GraphServiceArea.ConditionalAccess, second, false, TimeSpan.FromSeconds(40));

        var summaries = sink.Entries.Where(static e => e.EventId.Id == 1061).Select(static e => e.Message).ToList();
        Assert.Equal(4, summaries.Count);
        Assert.Contains("Directory", summaries[0], StringComparison.Ordinal);
        Assert.Contains(": 1 request(s)", summaries[0], StringComparison.Ordinal);
        Assert.Contains("ConditionalAccess", summaries[1], StringComparison.Ordinal);
        Assert.Contains(": 1 request(s)", summaries[1], StringComparison.Ordinal);

        // The 30 s report sums every wait since the previous one (4 + 1 + 1).
        Assert.Contains("Directory", summaries[2], StringComparison.Ordinal);
        Assert.Contains(": 6 request(s)", summaries[2], StringComparison.Ordinal);
        Assert.Contains("ConditionalAccess", summaries[3], StringComparison.Ordinal);
        Assert.Contains(": 2 request(s)", summaries[3], StringComparison.Ordinal);
        Assert.Equal(10, sink.Entries.Count(static e => e.EventId.Id == 1060));
        Assert.All(sink.Entries.Where(static e => e.EventId.Id == 1061), static e => Assert.Equal(LogLevel.Information, e.Level));
    }

    [Fact]
    public async Task TheThrottleLimitPercentageIsLoggedAtMostOnceAMinute()
    {
        var rig = new SchedulerRig();

        async Task ReportAt(double seconds, string path = "/users")
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(seconds) - rig.Clock.GetElapsedTime(0));
            var ticket = await rig.SendAsync(Call("GET", path));
            rig.Scheduler.Complete(ticket, Ok(percentage: 85.5, scope: "Tenant_Application"));
        }

        await ReportAt(0);
        await ReportAt(30);
        await ReportAt(59.9);
        Assert.Single(rig.Events(1065));

        await ReportAt(60);
        await ReportAt(61);
        Assert.Equal(2, rig.Events(1065).Count);

        // A sub-response's header counts too (and the minute is shared).
        await ReportAt(119);
        rig.Scheduler.CompleteSubRequest(Directory(1), Ok(percentage: 90));
        Assert.Equal(2, rig.Events(1065).Count);
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        rig.Scheduler.CompleteSubRequest(Directory(1), Ok(percentage: 90));
        Assert.Equal(3, rig.Events(1065).Count);
        Assert.Contains("Tenant_Application", rig.Events(1065)[0].Message, StringComparison.Ordinal);
        Assert.All(rig.Events(1065), static e => Assert.Equal(LogLevel.Information, e.Level));
    }

    [Fact]
    public async Task TheThrottleLimitPercentageOfANonDirectoryResponseIsNotReported()
    {
        var rig = new SchedulerRig();

        var ticket = await rig.SendAsync(Call("GET", "/identityGovernance/accessReviews/definitions"));
        rig.Scheduler.Complete(ticket, Ok(percentage: 95));
        rig.Scheduler.CompleteSubRequest(In(GraphServiceArea.ConditionalAccess), Ok(percentage: 95));

        Assert.Empty(rig.Events(1065));
    }

    // ---- sub-responses ------------------------------------------------------------------------------------

    [Fact]
    public async Task ASubResponse429PausesItsAreaForEveryRequest()
    {
        var rig = new SchedulerRig();

        rig.Scheduler.CompleteSubRequest(In(GraphServiceArea.RoleManagement), Throttled(9));
        rig.Scheduler.CompleteSubRequest(new GraphRequestCost(GraphServiceArea.None, 0, false), Throttled(50));

        Assert.Equal([TimeSpan.FromSeconds(9)], await rig.DelaysOf(() => rig.SendAsync(Call("GET", "/roleManagement/directory/roleAssignments"))));
        Assert.Empty(await rig.DelaysOf(() => rig.SendAsync(Call("GET", "/users"))));
        Assert.Single(rig.Events(1062));
    }

    [Fact]
    public async Task ASubResponseResourceUnitHeaderIsReconciled()
    {
        var rig = new SchedulerRig();
        await rig.SendAsync(new ScheduledCall([Directory(1)], isBatch: true));

        rig.Scheduler.CompleteSubRequest(Directory(1), Ok(resourceUnits: 3_500));

        Assert.Equal([TimeSpan.FromSeconds(1)], await rig.DelaysOf(() => rig.SendAsync(Call(Directory(350)))));
    }

    // ---- tenant size --------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, "Small")]
    [InlineData(49, "Small")]
    [InlineData(50, "Medium")]
    [InlineData(500, "Medium")]
    [InlineData(501, "Large")]
    [InlineData(1_000_000, "Large")]
    public void TheTierFollowsThePublishedUserCounts(long users, string tier)
    {
        Assert.Equal(Enum.Parse<TenantSizeTier>(tier), DirectoryLimits.TierOf(users));
    }

    [Theory]
    [InlineData(30, "Small", 3_500)]
    [InlineData(200, "Medium", 5_000)]
    [InlineData(1_000, "Large", 8_000)]
    public async Task TheTenantSizeResizesTheDirectoryBudget(long users, string tier, int budget)
    {
        var rig = new SchedulerRig(countUsers: _ => Task.FromResult(users));

        var detected = await rig.Scheduler.DetectTenantSizeAsync(Ct);

        Assert.Equal(Enum.Parse<TenantSizeTier>(tier), detected);
        Assert.Equal(detected, rig.Scheduler.Tier);
        Assert.Empty(await rig.DelaysOf(() => rig.SendAsync(Call(Directory(budget)))));
        Assert.Equal([TimeSpan.FromSeconds(1)], await rig.DelaysOf(() => rig.SendAsync(Call(Directory(budget / 10)))));
        var logged = Assert.Single(rig.Events(1063));
        Assert.Contains(users.ToString(System.Globalization.CultureInfo.InvariantCulture) + " users", logged.Message, StringComparison.Ordinal);
        Assert.Contains(tier, logged.Message, StringComparison.Ordinal);
        Assert.Contains(budget.ToString(System.Globalization.CultureInfo.InvariantCulture), logged.Message, StringComparison.Ordinal);
        Assert.Empty(rig.Events(1064));
    }

    [Fact]
    public async Task ResizingKeepsWhatWasAlreadySpent()
    {
        var rig = new SchedulerRig(countUsers: _ => Task.FromResult(1_000L));
        await rig.SendAsync(Call(Directory(3_000)));

        await rig.Scheduler.DetectTenantSizeAsync(Ct);

        Assert.Empty(await rig.DelaysOf(() => rig.SendAsync(Call(Directory(5_000)))));
        Assert.Equal([TimeSpan.FromSeconds(1)], await rig.DelaysOf(() => rig.SendAsync(Call(Directory(800)))));
    }

    [Fact]
    public async Task TheTenantSizeIsReadOnceForEveryCaller()
    {
        var calls = 0;
        var answer = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = new SchedulerRig(countUsers: _ =>
        {
            Interlocked.Increment(ref calls);
            return answer.Task;
        });

        var first = rig.Scheduler.DetectTenantSizeAsync(Ct);
        var second = rig.Scheduler.DetectTenantSizeAsync(Ct);
        Assert.Equal(TenantSizeTier.Small, rig.Scheduler.Tier);
        answer.SetResult(200);

        Assert.Equal(TenantSizeTier.Medium, await first);
        Assert.Equal(TenantSizeTier.Medium, await second);
        Assert.Equal(TenantSizeTier.Medium, await rig.Scheduler.DetectTenantSizeAsync(Ct));
        Assert.Equal(1, calls);
        Assert.Single(rig.Events(1063));
    }

    public static TheoryData<string> ProbeFailures => ["403", "transport", "cancelled"];

    [Theory]
    [MemberData(nameof(ProbeFailures))]
    public async Task AnUnreadableTenantSizeKeepsTheSmallestBudgetAndIsLoggedOnce(string failure)
    {
        var calls = 0;
        var rig = new SchedulerRig(countUsers: _ =>
        {
            Interlocked.Increment(ref calls);
            Exception error = failure switch
            {
                "403" => new GraphException(System.Net.HttpStatusCode.Forbidden, "Authorization_RequestDenied", "Insufficient privileges to complete the operation."),
                "transport" => new GraphTransportException(GraphTransportFailure.Network, "connection reset"),
                _ => new OperationCanceledException(),
            };
            return Task.FromException<long>(error);
        });

        Assert.Equal(TenantSizeTier.Small, await rig.Scheduler.DetectTenantSizeAsync(Ct));
        Assert.Equal(TenantSizeTier.Small, await rig.Scheduler.DetectTenantSizeAsync(Ct));

        Assert.Equal(1, calls);
        var logged = Assert.Single(rig.Events(1064));
        Assert.Contains("3500", logged.Message.Replace(",", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        if (failure == "403")
        {
            Assert.Contains("403 Authorization_RequestDenied", logged.Message, StringComparison.Ordinal);
        }

        Assert.Empty(rig.Events(1063));
        Assert.Empty(await rig.DelaysOf(() => rig.SendAsync(Call(Directory(3_500)))));
        Assert.Equal([TimeSpan.FromSeconds(1)], await rig.DelaysOf(() => rig.SendAsync(Call(Directory(350)))));
    }

    [Fact]
    public async Task AProbeDefectIsSurfacedToEveryCallerInsteadOfHanging()
    {
        var rig = new SchedulerRig(countUsers: _ => Task.FromException<long>(new InvalidOperationException("defect")));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Scheduler.DetectTenantSizeAsync(Ct));

        Assert.Equal("defect", error.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Scheduler.DetectTenantSizeAsync(Ct));
    }

    [Fact]
    public async Task TheCallersCancellationPropagatesWithoutDecidingTheTierForOthers()
    {
        var answer = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken probeToken = default;
        var rig = new SchedulerRig(countUsers: token =>
        {
            probeToken = token;
            return answer.Task;
        });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var cancelled = rig.Scheduler.DetectTenantSizeAsync(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(probeToken.CanBeCanceled);
        answer.SetResult(1_000);
        Assert.Equal(TenantSizeTier.Large, await rig.Scheduler.DetectTenantSizeAsync(Ct));
        Assert.Empty(rig.Events(1064));
    }

    [Fact]
    public async Task AnAlreadyCancelledCallerIsRefused()
    {
        var rig = new SchedulerRig(countUsers: _ => new TaskCompletionSource<long>().Task);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Scheduler.DetectTenantSizeAsync(cts.Token));
    }
}
