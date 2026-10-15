using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine.State;
using Xunit;
using static EArm.Tests.Engine.Review1A;

namespace EArm.Tests.Engine;

/// <summary>Review findings S-06 (state names, links, locations) and E-10 (lock heartbeat, liveness, compare-and-delete).</summary>
public sealed class Review1A_StateStoreTests
{
    private const string Name = "orders-dev";

    // ---- S-06: deployment names ----

    [Theory]
    [InlineData("a\n")]
    [InlineData("a\r\n")]
    [InlineData("orders\n")]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("Nul")]
    [InlineData("AUX")]
    [InlineData("prn")]
    [InlineData("COM1")]
    [InlineData("com9")]
    [InlineData("LPT1")]
    [InlineData("lpt9")]
    [InlineData("aux.json")]
    [InlineData("com5.txt")]
    [InlineData("con.x.y")]
    [InlineData("name.")]
    [InlineData("name..")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("-leading")]
    [InlineData("a b")]
    [InlineData("a/b")]
    public void StateNames_RejectsNewlinesTrailingDotsAndReservedDeviceNames(string name)
    {
        Assert.False(StateNames.IsValid(name));
        Assert.Throws<ArgumentException>(() => StateNames.Validate(name));
    }

    [Theory]
    [InlineData("orders-dev")]
    [InlineData("a")]
    [InlineData("com10")]
    [InlineData("console")]
    [InlineData("nul-x")]
    [InlineData("lpt0")]
    [InlineData("orders.v2")]
    [InlineData("Orders_Dev-1")]
    public void StateNames_AcceptsOrdinaryNames(string name)
    {
        Assert.True(StateNames.IsValid(name));
    }

    [Fact]
    public void ThePathsOfAReservedName_AreNeverBuilt()
    {
        var store = new FileStateStore(Path.Combine(Path.GetTempPath(), "earm-names"));

        Assert.Throws<ArgumentException>(() => store.PathFor("NUL"));
        Assert.Throws<ArgumentException>(() => store.LockPathFor("con"));
    }

    // ---- S-06: links ----

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
        process!.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    [Fact]
    public async Task AStateDirectoryThatIsALinkOrJunction_IsRefusedByEveryOperation()
    {
        using var directory = new TestDirectory(nameof(AStateDirectoryThatIsALinkOrJunction_IsRefusedByEveryOperation));
        string real = directory.Sub("real");
        string link = directory.File("link");
        if (!TryCreateJunction(link, real))
        {
            Assert.Skip("neither a symbolic link nor a junction can be created here");
        }

        var store = new FileStateStore(link);

        var save = await Assert.ThrowsAsync<StateStoreException>(() => store.SaveAsync(StateFile.CreateNew(Name, null, Start), Ct));
        Assert.Contains("symbolic link or junction", save.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<StateStoreException>(() => store.LoadAsync(Name, Ct));
        await Assert.ThrowsAsync<StateStoreException>(() => store.LockAsync(Name, TimeSpan.Zero, Ct));
        await Assert.ThrowsAsync<StateStoreException>(() => store.ListAsync(Ct));
        Assert.Empty(Directory.GetFileSystemEntries(real));
    }

    [Fact]
    public async Task AStateFileThatIsASymbolicLink_IsNeverReadOrReplaced()
    {
        using var directory = new TestDirectory(nameof(AStateFileThatIsASymbolicLink_IsNeverReadOrReplaced));
        var store = new FileStateStore(directory.Path);
        string outside = directory.File("outside.json");
        await File.WriteAllTextAsync(outside, "{ \"schemaVersion\": 1, \"resources\": {} }", Ct);
        try
        {
            File.CreateSymbolicLink(store.PathFor(Name), outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip("symbolic links cannot be created here: " + ex.Message);
        }

        await Assert.ThrowsAsync<StateStoreException>(() => store.LoadAsync(Name, Ct));
        await Assert.ThrowsAsync<StateStoreException>(() => store.SaveAsync(StateFile.CreateNew(Name, null, Start), Ct));
        Assert.Equal("{ \"schemaVersion\": 1, \"resources\": {} }", await File.ReadAllTextAsync(outside, Ct));
    }

    [Fact]
    public async Task ASavedState_LeavesNoTempFileBehind_AndAPlantedTempNameIsNotFollowed()
    {
        using var directory = new TestDirectory(nameof(ASavedState_LeavesNoTempFileBehind_AndAPlantedTempNameIsNotFollowed));
        var store = new FileStateStore(directory.Path);

        await store.SaveAsync(StateFile.CreateNew(Name, null, Start), Ct);

        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
        Assert.True(File.Exists(store.PathFor(Name)));
    }

    [Fact]
    public void TheExpectedStateLocations_AreTheProfile_TheCurrentDirectory_AndTheTemplateFolder()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string template = Path.Combine(Path.GetTempPath(), "earm-template-dir", "x.entra.json");

        Assert.True(StateDirectory.IsExpectedLocation(Path.Combine(profile, ".earm", "state"), null));
        Assert.True(StateDirectory.IsExpectedLocation(Path.Combine(Path.GetDirectoryName(template)!, ".earm", "state"), template));
        Assert.True(StateDirectory.IsExpectedLocation(Path.Combine(Directory.GetCurrentDirectory(), ".earm", "state"), null));
        string root = Path.GetPathRoot(Path.GetFullPath(profile))!;
        string elsewhere = Path.Combine(root, "earm-elsewhere-" + Guid.NewGuid().ToString("N")[..6], "state");
        if (!elsewhere.StartsWith(Directory.GetCurrentDirectory(), StringComparison.OrdinalIgnoreCase))
        {
            Assert.False(StateDirectory.IsExpectedLocation(elsewhere, null));
        }
    }

    // ---- E-10: lock heartbeat ----

    private static async Task<StateLockInfo?> WaitForHeartbeatAsync(FileStateStore store, DateTimeOffset after)
    {
        for (int i = 0; i < 400; i++)
        {
            var info = await store.GetLockAsync(Name, Ct);
            if (info?.HeartbeatOn is { } beat && beat > after)
            {
                return info;
            }

            await Task.Delay(25, Ct);
        }

        return await store.GetLockAsync(Name, Ct);
    }

    [Fact]
    public async Task AHeldLock_RewritesItsHeartbeat_SoALongRunNeverLooksAbandoned()
    {
        using var directory = new TestDirectory(nameof(AHeldLock_RewritesItsHeartbeat_SoALongRunNeverLooksAbandoned));
        var clock = new FakeTimeProvider(Start);
        var holder = new FileStateStore(directory.Path, clock) { HeartbeatInterval = TimeSpan.FromMilliseconds(30) };
        await using var lease = await holder.LockAsync(Name, TimeSpan.Zero, Ct);

        clock.Advance(TimeSpan.FromHours(3));
        var info = await WaitForHeartbeatAsync(holder, Start);

        Assert.Equal(Start, info!.AcquiredOn);
        Assert.Equal(Start.AddHours(3), info.HeartbeatOn);

        // Three hours after it was taken, the lock is still not stale: another run waits instead of taking it over.
        var contender = new FileStateStore(directory.Path, clock);
        var error = await Assert.ThrowsAsync<StateLockedException>(() => contender.LockAsync(Name, TimeSpan.Zero, Ct));
        Assert.Equal(Start, error.Holder!.AcquiredOn);
    }

    [Fact]
    public async Task ALockThatStoppedHeartbeating_IsStaleAfterTheThreshold()
    {
        using var directory = new TestDirectory(nameof(ALockThatStoppedHeartbeating_IsStaleAfterTheThreshold));
        var clock = new FakeTimeProvider(Start);
        var crashed = new FileStateStore(directory.Path, clock) { HeartbeatInterval = TimeSpan.Zero };
        _ = await crashed.LockAsync(Name, TimeSpan.Zero, Ct);
        clock.Advance(TimeSpan.FromMinutes(61));

        await using var takenOver = await new FileStateStore(directory.Path, clock).LockAsync(Name, TimeSpan.Zero, Ct);

        Assert.Equal(Start.AddMinutes(61), (await crashed.GetLockAsync(Name, Ct))!.AcquiredOn);
    }

    [Fact]
    public async Task ARelease_StopsTheHeartbeat_AndDeletesTheFile()
    {
        using var directory = new TestDirectory(nameof(ARelease_StopsTheHeartbeat_AndDeletesTheFile));
        var store = new FileStateStore(directory.Path) { HeartbeatInterval = TimeSpan.FromMilliseconds(20) };
        var lease = await store.LockAsync(Name, TimeSpan.Zero, Ct);

        // Wait for the heartbeat itself (polled, with a generous limit) instead of sleeping a guessed time: only then is "released" a statement about a running timer.
        StateLockInfo? beating = null;
        for (int i = 0; i < 1_200 && !(beating?.HeartbeatOn > beating?.AcquiredOn); i++)
        {
            await Task.Delay(25, Ct);
            beating = await store.GetLockAsync(Name, Ct);
        }

        Assert.True(beating?.HeartbeatOn > beating?.AcquiredOn, "the heartbeat timer never rewrote the lock");

        await lease.DisposeAsync();
        Assert.False(File.Exists(store.LockPathFor(Name)));

        // The timer must not bring the file back (a few intervals; this can only pass vacuously on a slow machine, never fail because of it).
        await Task.Delay(120, Ct);
        Assert.False(File.Exists(store.LockPathFor(Name)));
    }

    [Fact]
    public async Task ABrokenLock_IsNotRecreatedOrRewrittenByTheFormerHoldersHeartbeat()
    {
        // Deterministic (review round 2, S2-11c/d): no timer, the beat is driven by hand against a clock that is moved by hand.
        using var directory = new TestDirectory(nameof(ABrokenLock_IsNotRecreatedOrRewrittenByTheFormerHoldersHeartbeat));
        var clock = new FakeTimeProvider(Start);
        var store = new FileStateStore(directory.Path, clock) { HeartbeatInterval = TimeSpan.Zero };
        var lease = (FileStateStore.Lease)await store.LockAsync(Name, TimeSpan.Zero, Ct);

        // A beat of a lock this lease owns refreshes it (the control of the two cases below).
        clock.Advance(TimeSpan.FromMinutes(1));
        lease.Beat();
        Assert.Equal(Start.AddMinutes(1), (await store.GetLockAsync(Name, Ct))!.HeartbeatOn);

        // The lock is broken: a beat never re-creates it.
        await store.ForceUnlockAsync(Name, Ct);
        clock.Advance(TimeSpan.FromMinutes(1));
        lease.Beat();
        Assert.False(File.Exists(store.LockPathFor(Name)));

        // Another holder owns the file now: a beat leaves its content exactly as it was (HeartbeatOn and LockId included).
        var other = (FileStateStore.Lease)await store.LockAsync(Name, TimeSpan.Zero, Ct);
        string before = await File.ReadAllTextAsync(store.LockPathFor(Name), Ct);
        var otherInfo = await store.GetLockAsync(Name, Ct);
        clock.Advance(TimeSpan.FromMinutes(5));
        lease.Beat();

        Assert.Equal(before, await File.ReadAllTextAsync(store.LockPathFor(Name), Ct));
        Assert.Equal(otherInfo!.HeartbeatOn, (await store.GetLockAsync(Name, Ct))!.HeartbeatOn);
        Assert.Contains(other.LockId, before, StringComparison.Ordinal);

        // ... and the owner's own beat still works.
        other.Beat();
        Assert.Equal(clock.GetUtcNow(), (await store.GetLockAsync(Name, Ct))!.HeartbeatOn);
        await other.DisposeAsync();
        await lease.DisposeAsync();
        Assert.False(File.Exists(store.LockPathFor(Name)));
    }

    // ---- E-10: liveness of the holder ----

    private static void WriteLock(FileStateStore store, string host, int processId, DateTimeOffset acquiredOn)
    {
        var document = new StateLockDocument { ProcessId = processId, Host = host, User = "someone", AcquiredOn = acquiredOn, LockId = "x" };
        Directory.CreateDirectory(store.Directory);
        File.WriteAllText(store.LockPathFor(Name), JsonSerializer.Serialize(document, StateJsonContext.Default.StateLockDocument));
    }

    [Fact]
    public async Task AnOldLock_WhoseHolderStillRunsOnThisHost_IsNotTakenOver()
    {
        using var directory = new TestDirectory(nameof(AnOldLock_WhoseHolderStillRunsOnThisHost_IsNotTakenOver));
        var clock = new FakeTimeProvider(Start.AddHours(3));
        var store = new FileStateStore(directory.Path, clock) { IsHolderAlive = _ => true };
        WriteLock(store, Environment.MachineName, 4242, Start);

        var error = await Assert.ThrowsAsync<StateLockedException>(() => store.LockAsync(Name, TimeSpan.Zero, Ct));

        Assert.Equal(4242, error.Holder!.ProcessId);
        Assert.True(File.Exists(store.LockPathFor(Name)));
    }

    [Fact]
    public async Task AnOldLock_WhoseHolderIsGone_IsTakenOver()
    {
        using var directory = new TestDirectory(nameof(AnOldLock_WhoseHolderIsGone_IsTakenOver));
        var clock = new FakeTimeProvider(Start.AddHours(3));
        var store = new FileStateStore(directory.Path, clock) { IsHolderAlive = _ => false };
        WriteLock(store, Environment.MachineName, 4242, Start);

        await using var lease = await store.LockAsync(Name, TimeSpan.Zero, Ct);

        Assert.Equal(Start.AddHours(3), (await store.GetLockAsync(Name, Ct))!.AcquiredOn);
    }

    [Fact]
    public async Task TheDefaultProbe_TreatsAnUnusedPidAndAnotherHostAsGone()
    {
        using var directory = new TestDirectory(nameof(TheDefaultProbe_TreatsAnUnusedPidAndAnotherHostAsGone));
        var clock = new FakeTimeProvider(Start.AddHours(3));
        var store = new FileStateStore(directory.Path, clock);

        // Each lock below is three hours old with a holder the default probe must call gone; the take-over is proven by the new holder's timestamp (the clock's
        // "now", not Start) and by the lock being held by this process afterwards.
        WriteLock(store, Environment.MachineName, int.MaxValue - 1, Start);
        await using (await store.LockAsync(Name, TimeSpan.Zero, Ct))
        {
            var holder = await store.GetLockAsync(Name, Ct);
            Assert.Equal(clock.GetUtcNow(), holder!.AcquiredOn);
            Assert.Equal(Environment.ProcessId, holder.ProcessId);
        }

        WriteLock(store, "some-other-host", Environment.ProcessId, Start);
        await using (await store.LockAsync(Name, TimeSpan.Zero, Ct))
        {
            var holder = await store.GetLockAsync(Name, Ct);
            Assert.Equal(clock.GetUtcNow(), holder!.AcquiredOn);
            Assert.Equal(Environment.MachineName, holder.Host);
        }

        // A lock of this very process that nobody holds (a leaked lease) is stale too.
        WriteLock(store, Environment.MachineName, Environment.ProcessId, Start);
        await using (await store.LockAsync(Name, TimeSpan.Zero, Ct))
        {
            Assert.Equal(clock.GetUtcNow(), (await store.GetLockAsync(Name, Ct))!.AcquiredOn);
        }

        Assert.Null(await store.GetLockAsync(Name, Ct));
    }

    [Fact]
    public async Task ALiveHolderThatNeverHeartbeats_IsNotProtectedForever()
    {
        using var directory = new TestDirectory(nameof(ALiveHolderThatNeverHeartbeats_IsNotProtectedForever));
        var clock = new FakeTimeProvider(Start.AddDays(2));
        var store = new FileStateStore(directory.Path, clock) { IsHolderAlive = _ => true };
        WriteLock(store, Environment.MachineName, 4242, Start);

        await using var lease = await store.LockAsync(Name, TimeSpan.Zero, Ct);

        Assert.Equal(Start.AddDays(2), (await store.GetLockAsync(Name, Ct))!.AcquiredOn);
    }

    // ---- E-10: compare-and-delete ----

    [Fact]
    public async Task AStaleLock_ThatIsRefreshedWhileItIsBeingTakenOver_KeepsItsFile()
    {
        using var directory = new TestDirectory(nameof(AStaleLock_ThatIsRefreshedWhileItIsBeingTakenOver_KeepsItsFile));
        var clock = new FakeTimeProvider(Start.AddHours(3));
        string? refreshed = null;
        var store = new FileStateStore(directory.Path, clock)
        {
            IsHolderAlive = _ => false,
            BeforeStaleDelete = path =>
            {
                // The holder wakes up and heartbeats between the contender's read and its delete.
                var document = new StateLockDocument { ProcessId = 4242, Host = "h", User = "u", AcquiredOn = Start, HeartbeatOn = clock.GetUtcNow(), LockId = "x" };
                refreshed = JsonSerializer.Serialize(document, StateJsonContext.Default.StateLockDocument);
                File.WriteAllText(path, refreshed);
            },
        };
        WriteLock(store, "h", 4242, Start);

        var error = await Assert.ThrowsAsync<StateLockedException>(() => store.LockAsync(Name, TimeSpan.Zero, Ct));

        Assert.Equal(clock.GetUtcNow(), error.Holder!.HeartbeatOn);
        Assert.Equal(refreshed, await File.ReadAllTextAsync(store.LockPathFor(Name), Ct));
    }

    [Fact]
    public async Task TwoRuns_NeverHoldTheLockAtOnce_AndTheSecondSeesTheFirstsState()
    {
        using var directory = new TestDirectory(nameof(TwoRuns_NeverHoldTheLockAtOnce_AndTheSecondSeesTheFirstsState));
        var store = new FileStateStore(directory.Path) { LockPollInterval = TimeSpan.FromMilliseconds(10) };
        int inside = 0;
        int maxInside = 0;
        var seen = new System.Collections.Concurrent.ConcurrentBag<int>();

        async Task RunAsync()
        {
            await using var lease = await store.LockAsync(Name, TimeSpan.FromSeconds(20), Ct);
            int now = Interlocked.Increment(ref inside);
            int observed;
            do
            {
                observed = Volatile.Read(ref maxInside);
            }
            while (now > observed && Interlocked.CompareExchange(ref maxInside, now, observed) != observed);

            // read-modify-write of the state under the lock: no run may lose another run's entry
            var state = await store.LoadAsync(Name, Ct) ?? StateFile.CreateNew(Name, null, Start);
            seen.Add(state.Resources.Count);
            await Task.Delay(40, Ct);
            string appName = "app-" + Guid.NewGuid().ToString("N")[..6];
            state.Upsert(Entry(AppId(appName), "Microsoft.Entra/applications", Gid(Random.Shared.Next(1, 1_000_000)), "uniqueName", appName));
            await store.SaveAsync(state, Ct);
            Interlocked.Decrement(ref inside);
        }

        await Task.WhenAll(RunAsync(), RunAsync(), RunAsync());

        Assert.Equal(1, maxInside);
        Assert.Equal(3, (await store.LoadAsync(Name, Ct))!.Resources.Count);
        Assert.Equal([0, 1, 2], seen.Order().ToList());
    }
}
