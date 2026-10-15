using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using EArm.Engine.State;
using Xunit;
using static EArm.Tests.Engine.Review1A;

namespace EArm.Tests.Engine;

/// <summary>
/// The lock rules of <see cref="FileStateStore"/> pinned at their boundaries (the defaults, the age of a live holder, an own-process or pid-0 lock, a reused pid), the
/// links it refuses, and <see cref="StateDirectory.IsExpectedLocation"/> on a case-insensitive file system and around the trailing separator.
/// </summary>
public sealed class Review4K_FileStateStoreTests
{
    private const string Name = "r4k-lock";

    private static void WriteLock(FileStateStore store, string host, int processId, DateTimeOffset acquiredOn)
    {
        var document = new StateLockDocument { ProcessId = processId, Host = host, User = "someone", AcquiredOn = acquiredOn, HeartbeatOn = acquiredOn, LockId = "foreign" };
        Directory.CreateDirectory(store.Directory);
        File.WriteAllText(store.LockPathFor(Name), JsonSerializer.Serialize(document, StateJsonContext.Default.StateLockDocument));
    }

    // ---- defaults ----

    [Fact]
    public void TheLockTimingDefaults_AreTheDocumentedOnes()
    {
        var store = new FileStateStore(Path.Combine(Path.GetTempPath(), "earm-r4k-defaults"));

        Assert.Equal(TimeSpan.FromHours(1), store.StaleLockAge);
        Assert.Equal(TimeSpan.FromHours(24), store.MaxLiveHolderAge);
        Assert.Equal(TimeSpan.FromSeconds(30), store.HeartbeatInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(250), store.LockPollInterval);
    }

    // ---- a live holder keeps its lock, but only up to MaxLiveHolderAge ----

    [Fact]
    public async Task ALiveHolder_KeepsAStaleLock_UntilItsAgeReachesMaxLiveHolderAge()
    {
        using var directory = new TestDirectory(nameof(ALiveHolder_KeepsAStaleLock_UntilItsAgeReachesMaxLiveHolderAge));
        var clock = new FakeTimeProvider(Start);
        var store = new FileStateStore(directory.Path, clock) { IsHolderAlive = _ => true };

        // One tick short of the limit: the holder is alive and the lock older than an hour, so it is kept.
        WriteLock(store, Environment.MachineName, 4242, Start - store.MaxLiveHolderAge + TimeSpan.FromTicks(1));
        await Assert.ThrowsAsync<StateLockedException>(() => store.LockAsync(Name, TimeSpan.Zero, Ct));

        // Exactly at the limit it is abandoned whatever the probe says.
        WriteLock(store, Environment.MachineName, 4242, Start - store.MaxLiveHolderAge);
        await using var lease = await store.LockAsync(Name, TimeSpan.Zero, Ct);

        Assert.Equal(Environment.ProcessId, (await store.GetLockAsync(Name, Ct))!.ProcessId);
    }

    [Fact]
    public async Task ALockThatIsExactlyStaleLockAgeOld_IsStale_AndOneTickYoungerIsNot()
    {
        using var directory = new TestDirectory(nameof(ALockThatIsExactlyStaleLockAgeOld_IsStale_AndOneTickYoungerIsNot));
        var clock = new FakeTimeProvider(Start);
        var store = new FileStateStore(directory.Path, clock) { IsHolderAlive = _ => false };

        // A holder that is gone is taken over at once; use another host so the probe is not consulted for "gone".
        WriteLock(store, "some-other-host", 4242, Start - store.StaleLockAge + TimeSpan.FromTicks(1));
        await Assert.ThrowsAsync<StateLockedException>(() => store.LockAsync(Name, TimeSpan.Zero, Ct));

        WriteLock(store, "some-other-host", 4242, Start - store.StaleLockAge);
        await using var lease = await store.LockAsync(Name, TimeSpan.Zero, Ct);

        Assert.Equal(Environment.ProcessId, (await store.GetLockAsync(Name, Ct))!.ProcessId);
    }

    // ---- this process, pid 0 ----

    [Fact]
    public async Task AStaleLock_OfThisVeryProcess_IsALeakedLease_AndIsTakenOver()
    {
        using var directory = new TestDirectory(nameof(AStaleLock_OfThisVeryProcess_IsALeakedLease_AndIsTakenOver));
        var clock = new FakeTimeProvider(Start);
        var store = new FileStateStore(directory.Path, clock);
        WriteLock(store, Environment.MachineName, Environment.ProcessId, Start - TimeSpan.FromHours(2));

        await using var lease = await store.LockAsync(Name, TimeSpan.Zero, Ct);

        Assert.NotEqual("someone", (await store.GetLockAsync(Name, Ct))!.User);
    }

    [Fact]
    public async Task AStaleLock_WithProcessId0_IsTakenOver_NotMistakenForTheIdleProcess()
    {
        using var directory = new TestDirectory(nameof(AStaleLock_WithProcessId0_IsTakenOver_NotMistakenForTheIdleProcess));
        var clock = new FakeTimeProvider(Start);
        var store = new FileStateStore(directory.Path, clock);
        WriteLock(store, Environment.MachineName, 0, Start - TimeSpan.FromHours(2));

        await using var lease = await store.LockAsync(Name, TimeSpan.Zero, Ct);

        Assert.Equal(Environment.ProcessId, (await store.GetLockAsync(Name, Ct))!.ProcessId);
    }

    [Fact]
    public async Task AFreshLock_WithProcessId0_IsNotGone_ItIsJustNotProbeable()
    {
        using var directory = new TestDirectory(nameof(AFreshLock_WithProcessId0_IsNotGone_ItIsJustNotProbeable));
        var clock = new FakeTimeProvider(Start);
        var store = new FileStateStore(directory.Path, clock);
        WriteLock(store, Environment.MachineName, 0, Start);

        await Assert.ThrowsAsync<StateLockedException>(() => store.LockAsync(Name, TimeSpan.Zero, Ct));
    }

    // ---- a reused pid ----

    private static Process? StartSleeper()
    {
        try
        {
            var info = new ProcessStartInfo("pwsh", "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 120\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            return Process.Start(info);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    [Fact]
    public async Task APidThatNowBelongsToAProcessStartedAfterTheLock_IsAReusedPid_SoTheLockIsTakenOver()
    {
        using var sleeper = StartSleeper();
        if (sleeper is null)
        {
            Assert.Skip("pwsh is not available to start a child process");
        }

        try
        {
            using var directory = new TestDirectory(nameof(APidThatNowBelongsToAProcessStartedAfterTheLock_IsAReusedPid_SoTheLockIsTakenOver));
            var now = DateTimeOffset.UtcNow;
            var clock = new FakeTimeProvider(now);
            var store = new FileStateStore(directory.Path, clock);

            // The lock was taken a minute BEFORE that process started: the pid was reused, the real holder is gone. Its fresh lock goes at once.
            WriteLock(store, Environment.MachineName, sleeper.Id, now - TimeSpan.FromMinutes(1));
            await using (var lease = await store.LockAsync(Name, TimeSpan.Zero, Ct))
            {
                Assert.Equal(Environment.ProcessId, (await store.GetLockAsync(Name, Ct))!.ProcessId);
            }

            // Control: the lock was taken AFTER the process started, so that process is the holder, and it is alive.
            WriteLock(store, Environment.MachineName, sleeper.Id, now + TimeSpan.FromMinutes(1));
            await Assert.ThrowsAsync<StateLockedException>(() => store.LockAsync(Name, TimeSpan.Zero, Ct));
        }
        finally
        {
            sleeper.Kill(entireProcessTree: true);
            await sleeper.WaitForExitAsync(Ct);
        }
    }

    // ---- links in the lock file ----

    [Fact]
    public async Task ALockFileThatIsASymbolicLink_IsRefused_NeitherReadNorTakenOver()
    {
        using var directory = new TestDirectory(nameof(ALockFileThatIsASymbolicLink_IsRefused_NeitherReadNorTakenOver));
        var store = new FileStateStore(directory.Path);
        Directory.CreateDirectory(store.Directory);
        string outside = directory.File("outside.lock");
        await File.WriteAllTextAsync(outside, "{}", Ct);
        try
        {
            File.CreateSymbolicLink(store.LockPathFor(Name), outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip("symbolic links cannot be created here: " + ex.Message);
        }

        var error = await Assert.ThrowsAsync<StateStoreException>(() => store.LockAsync(Name, TimeSpan.Zero, Ct));

        Assert.Contains("symbolic link", error.Message, StringComparison.Ordinal);
        Assert.Equal("{}", await File.ReadAllTextAsync(outside, Ct));
    }

    [Fact]
    public async Task AStateFileThatIsASymbolicLink_IsNeverDeleted()
    {
        using var directory = new TestDirectory(nameof(AStateFileThatIsASymbolicLink_IsNeverDeleted));
        var store = new FileStateStore(directory.Path);
        Directory.CreateDirectory(store.Directory);
        string outside = directory.File("outside.json");
        await File.WriteAllTextAsync(outside, "{}", Ct);
        try
        {
            File.CreateSymbolicLink(store.PathFor(Name), outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip("symbolic links cannot be created here: " + ex.Message);
        }

        await Assert.ThrowsAsync<StateStoreException>(async () => await store.DeleteAsync(Name, Ct));
        Assert.True(File.Exists(outside));
    }

    // ---- IsExpectedLocation ----

    /// <summary>A folder outside the user profile and the working directory, so only the template directory can make a location "expected".</summary>
    private static string OutsideRoot()
    {
        return Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "earm-r4k-" + Guid.NewGuid().ToString("N")[..8]);
    }

    [Fact]
    public void ALocationUnderTheTemplateDirectory_IsExpected_AndItsSiblingWithTheSamePrefixIsNot()
    {
        string root = OutsideRoot();
        string template = Path.Combine(root, "proj", "t.entra.json");

        Assert.True(StateDirectory.IsExpectedLocation(Path.Combine(root, "proj", ".earm", "state"), template));
        Assert.True(StateDirectory.IsExpectedLocation(Path.Combine(root, "proj"), template));
        Assert.False(StateDirectory.IsExpectedLocation(Path.Combine(root, "proj2", ".earm", "state"), template));
        Assert.False(StateDirectory.IsExpectedLocation(Path.Combine(root, "elsewhere"), template));
        Assert.False(StateDirectory.IsExpectedLocation(Path.Combine(root, "proj"), null));
    }

    [Fact]
    public void ATrailingSeparator_OnTheStateDirectory_DoesNotChangeTheAnswer()
    {
        string root = OutsideRoot();
        string template = Path.Combine(root, "proj", "t.entra.json");
        string separator = Path.DirectorySeparatorChar.ToString();

        Assert.True(StateDirectory.IsExpectedLocation(Path.Combine(root, "proj", ".earm") + separator, template));
        Assert.True(StateDirectory.IsExpectedLocation(Path.Combine(root, "proj") + separator, template));
        Assert.False(StateDirectory.IsExpectedLocation(Path.Combine(root, "proj2") + separator, template));
    }

    [Fact]
    public void TheComparison_IgnoresCaseOnWindows_AndIsCaseSensitiveElsewhere()
    {
        string root = OutsideRoot();
        string template = Path.Combine(root, "Proj", "t.entra.json");
        string differentCase = Path.Combine(root, "pROJ", ".earm");

        Assert.Equal(OperatingSystem.IsWindows(), StateDirectory.IsExpectedLocation(differentCase, template));
        Assert.True(StateDirectory.IsExpectedLocation(Path.Combine(root, "Proj", ".earm"), template));
    }
}
