using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.State;
using EArm.TestSupport;
using Xunit;

namespace EArm.Tests.Engine;

/// <summary>
/// Review 2G: the persist timeout of <see cref="FileStateSink"/> is injectable, and a state save that hangs (a store that never completes, with or
/// without honouring its token) is abandoned after it and reported as an <see cref="IOException"/> (a state-save failure), never as a hang.
/// </summary>
public sealed class Review2G_StateSinkPersistTimeoutTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(150);

    /// <summary>The test fails (instead of hanging the run) when the code under test does not honour the timeout.</summary>
    private static readonly TimeSpan SafetyNet = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A store whose saves hang from the <paramref name="hangFrom"/>-th call on (1-based) for <paramref name="hangs"/> calls.</summary>
    private sealed class HangingStore : IStateStore
    {
        private readonly bool _honourToken;
        private readonly int _hangFrom;
        private readonly int _hangs;
        private int _calls;

        public HangingStore(bool honourToken, int hangFrom = 1, int hangs = int.MaxValue)
        {
            _honourToken = honourToken;
            _hangFrom = hangFrom;
            _hangs = hangs;
        }

        public int Calls => Volatile.Read(ref _calls);

        public Task SaveAsync(StateFile state, CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref _calls);
            if (call < _hangFrom || call - _hangFrom >= _hangs)
            {
                return Task.CompletedTask;
            }

            return _honourToken ? Task.Delay(Timeout.Infinite, cancellationToken) : new TaskCompletionSource().Task;
        }

        public Task<StateFile?> LoadAsync(string deploymentName, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string deploymentName, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAsyncDisposable> LockAsync(string deploymentName, TimeSpan wait, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<StateLockInfo?> GetLockAsync(string deploymentName, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<StateLockInfo?> ForceUnlockAsync(string deploymentName, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AHungSave_IsAbandonedAfterTheTimeout_AsAStateSaveFailure(bool storeHonoursItsToken)
    {
        var host = new EngineHost();
        var plan = await host.ExpandAsync(await Templates.FixtureAsync());
        var store = new HangingStore(storeHonoursItsToken);
        var sink = new FileStateSink(store, StateFile.CreateNew(EngineHost.DeploymentName, null, Start), plan, timeProvider: new FakeTimeProvider(Start), persistTimeout: Short);
        var user = EngineHost.Evaluated(plan, Fixture.User);

        // WaitAsync(SafetyNet) is the guard: a save that is not abandoned by the persist timeout surfaces as a TimeoutException here, not as the IOException.
        var error = await Assert.ThrowsAsync<IOException>(() => sink.ResourceFailedAsync(user, "boom", Ct).WaitAsync(SafetyNet, Ct));

        Assert.Contains("did not complete", error.Message, StringComparison.Ordinal);
        Assert.True(store.Calls >= 1, "the save was attempted");
    }

    [Fact]
    public async Task AfterAHungSaveIsAbandoned_TheNextChangeIsPersisted()
    {
        var host = new EngineHost();
        var plan = await host.ExpandAsync(await Templates.FixtureAsync());
        var store = new HangingStore(honourToken: false, hangFrom: 1, hangs: 1);
        var state = StateFile.CreateNew(EngineHost.DeploymentName, null, Start);
        var sink = new FileStateSink(store, state, plan, timeProvider: new FakeTimeProvider(Start), persistTimeout: Short);
        var user = EngineHost.Evaluated(plan, Fixture.User);

        await Assert.ThrowsAsync<IOException>(() => sink.ResourceFailedAsync(user, "first", Ct).WaitAsync(SafetyNet, Ct));
        await sink.ResourceFailedAsync(user, "second", Ct).WaitAsync(SafetyNet, Ct);

        Assert.Equal(2, store.Calls);
        Assert.Equal(2, state.Operations.Count(o => o.Status == OperationStatus.Failed));
    }

    [Fact]
    public async Task ASaveThatFinishesInTime_IsNotAffectedByTheTimeout()
    {
        var host = new EngineHost();
        var plan = await host.ExpandAsync(await Templates.FixtureAsync());
        var store = new InMemoryStateStore();
        var sink = new FileStateSink(store, StateFile.CreateNew(EngineHost.DeploymentName, null, Start), plan, timeProvider: new FakeTimeProvider(Start), persistTimeout: TimeSpan.FromSeconds(5));
        var user = EngineHost.Evaluated(plan, Fixture.User);

        await sink.ResourceFailedAsync(user, "boom", Ct);

        Assert.Single(store.Saves);
    }

    [Fact]
    public async Task TheCallersCancellation_DoesNotCancelThePersistence_EvenWithAShortTimeout()
    {
        var host = new EngineHost();
        var plan = await host.ExpandAsync(await Templates.FixtureAsync());
        var store = new InMemoryStateStore();
        var sink = new FileStateSink(store, StateFile.CreateNew(EngineHost.DeploymentName, null, Start), plan, timeProvider: new FakeTimeProvider(Start), persistTimeout: Short);
        var user = EngineHost.Evaluated(plan, Fixture.User);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await sink.ResourceFailedAsync(user, "boom", cancelled.Token);

        Assert.Single(store.Saves);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task AnInvalidPersistTimeout_IsRejected(int milliseconds)
    {
        var host = new EngineHost();
        var plan = await host.ExpandAsync(await Templates.FixtureAsync());

        Assert.Throws<ArgumentOutOfRangeException>(() => new FileStateSink(new InMemoryStateStore(), StateFile.CreateNew(EngineHost.DeploymentName, null, Start), plan, persistTimeout: TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void TheDefaultPersistTimeout_IsStillThirtySeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), FileStateSink.PersistTimeout);
    }

    [Fact]
    public async Task ARunWhoseStateSavesHang_EndsWithAStateSaveFailure_NotAHang()
    {
        var host = new EngineHost();
        host.SeedExisting();
        var plan = await host.ExpandAsync(await Templates.FixtureAsync());
        var state = StateFile.CreateNew(EngineHost.DeploymentName, null, Start);
        // The begin-of-run save succeeds; every later save hangs without looking at its token.
        var store = new HangingStore(honourToken: false, hangFrom: 2);
        var sink = new FileStateSink(store, state, plan, timeProvider: new FakeTimeProvider(Start), persistTimeout: TimeSpan.FromMilliseconds(100));
        await sink.BeginAsync(Ct);

        var result = await host.Executor().ExecuteAsync(plan, new ExecuteOptions { StateSink = sink, State = state }, Ct).WaitAsync(SafetyNet, Ct);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Operations, o => o.ErrorCode == EngineDiagnosticCodes.StateSaveFailed);
        Assert.Contains(result.Diagnostics, d => d.Code == EngineDiagnosticCodes.StateSaveFailed && d.IsError);
    }
}
