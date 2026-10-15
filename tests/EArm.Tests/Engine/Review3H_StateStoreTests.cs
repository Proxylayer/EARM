using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine.Execution;
using EArm.Engine.State;
using Xunit;
using static EArm.Tests.Engine.Review1A;

namespace EArm.Tests.Engine;

/// <summary>Review round 2, findings S2-11 (lock correctness), S2-18 (links above the state directory), S2-19 (two runs sharing a secure outputs file).</summary>
public sealed class Review3H_StateStoreTests
{
    private const string Name = "r3h-lock";

    private static void WriteLock(FileStateStore store, string host, int processId, DateTimeOffset acquiredOn, string lockId = "foreign")
    {
        var document = new StateLockDocument { ProcessId = processId, Host = host, User = "someone", AcquiredOn = acquiredOn, HeartbeatOn = acquiredOn, LockId = lockId };
        Directory.CreateDirectory(store.Directory);
        File.WriteAllText(store.LockPathFor(Name), JsonSerializer.Serialize(document, StateJsonContext.Default.StateLockDocument));
    }

    // ---- S2-11e: a holder that is gone from this host does not keep the lock for an hour ----

    [Fact]
    public async Task AFreshLock_OfAProcessThatNoLongerRunsOnThisHost_IsTakenOverAtOnce()
    {
        using var directory = new TestDirectory(nameof(AFreshLock_OfAProcessThatNoLongerRunsOnThisHost_IsTakenOverAtOnce));
        var clock = new FakeTimeProvider(Start);
        var store = new FileStateStore(directory.Path, clock);
        WriteLock(store, Environment.MachineName, int.MaxValue - 1, Start);

        await using var lease = await store.LockAsync(Name, TimeSpan.Zero, Ct);

        Assert.Equal(Environment.ProcessId, (await store.GetLockAsync(Name, Ct))!.ProcessId);
    }

    [Fact]
    public async Task AFreshLock_OfALiveHolder_OfAnotherHost_OrOfThisProcess_IsNotTakenOver()
    {
        using var directory = new TestDirectory(nameof(AFreshLock_OfALiveHolder_OfAnotherHost_OrOfThisProcess_IsNotTakenOver));
        var clock = new FakeTimeProvider(Start);

        var alive = new FileStateStore(directory.Path, clock) { IsHolderAlive = _ => true };
        WriteLock(alive, Environment.MachineName, 4242, Start);
        await Assert.ThrowsAsync<StateLockedException>(() => alive.LockAsync(Name, TimeSpan.Zero, Ct));

        var other = new FileStateStore(directory.Path, clock);
        WriteLock(other, "some-other-host", 4242, Start);
        await Assert.ThrowsAsync<StateLockedException>(() => other.LockAsync(Name, TimeSpan.Zero, Ct));

        WriteLock(other, Environment.MachineName, Environment.ProcessId, Start);
        await Assert.ThrowsAsync<StateLockedException>(() => other.LockAsync(Name, TimeSpan.Zero, Ct));
    }

    [Fact]
    public async Task ARealRunningProcess_HoldingAFreshLock_KeepsIt()
    {
        using var directory = new TestDirectory(nameof(ARealRunningProcess_HoldingAFreshLock_KeepsIt));
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new FileStateStore(directory.Path, clock);
        using var sleeper = Process.Start(new ProcessStartInfo(OperatingSystem.IsWindows() ? "ping" : "sleep", OperatingSystem.IsWindows() ? "-n 30 127.0.0.1" : "30") { CreateNoWindow = true, RedirectStandardOutput = true, UseShellExecute = false })!;
        try
        {
            WriteLock(store, Environment.MachineName, sleeper.Id, clock.GetUtcNow());

            await Assert.ThrowsAsync<StateLockedException>(() => store.LockAsync(Name, TimeSpan.Zero, Ct));
        }
        finally
        {
            sleeper.Kill(entireProcessTree: true);
        }
    }

    // ---- S2-11b: the stale takeover is serialised ----

    [Fact]
    public async Task ATaker_WhoseJudgedLockIsReplacedByAnotherTakersNewLock_DoesNotDeleteIt()
    {
        using var directory = new TestDirectory(nameof(ATaker_WhoseJudgedLockIsReplacedByAnotherTakersNewLock_DoesNotDeleteIt));
        var clock = new FakeTimeProvider(Start.AddHours(3));
        string? newLockContent = null;
        FileStateStore? store = null;
        store = new FileStateStore(directory.Path, clock)
        {
            IsHolderAlive = _ => false,
            BeforeStaleDelete = path =>
            {
                // The other taker wins between this one's judgement and its takeover: it deletes the stale lock and creates its own.
                File.Delete(path);
                var document = new StateLockDocument { ProcessId = Environment.ProcessId, Host = Environment.MachineName, User = "taker-b", AcquiredOn = clock.GetUtcNow(), HeartbeatOn = clock.GetUtcNow(), LockId = "taker-b" };
                newLockContent = JsonSerializer.Serialize(document, StateJsonContext.Default.StateLockDocument);
                File.WriteAllText(path, newLockContent);
            },
        };
        WriteLock(store, "other-host", 4242, Start);

        var error = await Assert.ThrowsAsync<StateLockedException>(() => store.LockAsync(Name, TimeSpan.Zero, Ct));

        Assert.Equal("taker-b", error.Holder!.User);
        Assert.Equal(newLockContent, await File.ReadAllTextAsync(store.LockPathFor(Name), Ct));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.stale"));
    }

    [Fact]
    public async Task ManyTakersOfOneStaleLock_NeverHoldItAtOnce()
    {
        using var directory = new TestDirectory(nameof(ManyTakersOfOneStaleLock_NeverHoldItAtOnce));
        var seed = new FileStateStore(directory.Path);
        WriteLock(seed, "other-host", 4242, DateTimeOffset.UtcNow.AddHours(-3));
        int inside = 0;
        int maxInside = 0;
        int rounds = 0;

        async Task TakerAsync()
        {
            var store = new FileStateStore(directory.Path) { LockPollInterval = TimeSpan.FromMilliseconds(2) };
            for (int i = 0; i < 6; i++)
            {
                await using var lease = await store.LockAsync(Name, TimeSpan.FromSeconds(30), Ct);
                int now = Interlocked.Increment(ref inside);
                int seen;
                do
                {
                    seen = Volatile.Read(ref maxInside);
                }
                while (now > seen && Interlocked.CompareExchange(ref maxInside, now, seen) != seen);

                await Task.Delay(3, Ct);
                Interlocked.Increment(ref rounds);
                Interlocked.Decrement(ref inside);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(TakerAsync, Ct)));

        Assert.Equal(1, maxInside);
        Assert.Equal(48, rounds);
        Assert.False(File.Exists(seed.LockPathFor(Name)));
    }

    // ---- S2-18: a link anywhere above the state directory or the secure outputs file ----

    private static string? TryCreateLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return link;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
        process!.WaitForExit();
        return process.ExitCode == 0 ? link : null;
    }

    [Fact]
    public async Task AStateDirectory_BelowALink_IsRefused_NotOnlyALinkAtTheEnd()
    {
        using var directory = new TestDirectory(nameof(AStateDirectory_BelowALink_IsRefused_NotOnlyALinkAtTheEnd));
        string target = directory.Sub("real");
        string link = Path.Combine(directory.Path, "link");
        if (TryCreateLink(link, target) is null)
        {
            Assert.Skip("This environment cannot create a symbolic link or junction.");
            return;
        }

        var store = new FileStateStore(Path.Combine(link, "nested", "state"));
        var state = NewState();

        var save = await Assert.ThrowsAsync<StateStoreException>(() => store.SaveAsync(state, Ct));
        var load = await Assert.ThrowsAsync<StateStoreException>(() => store.LoadAsync(EngineHost.DeploymentName, Ct));
        var @lock = await Assert.ThrowsAsync<StateStoreException>(() => store.LockAsync(EngineHost.DeploymentName, TimeSpan.Zero, Ct));

        Assert.Contains("symbolic link or junction", save.Message, StringComparison.Ordinal);
        Assert.Contains(link, load.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("symbolic link or junction", @lock.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public void TheSecureOutputsFile_BelowALink_IsRefused()
    {
        using var directory = new TestDirectory(nameof(TheSecureOutputsFile_BelowALink_IsRefused));
        string target = directory.Sub("real");
        string link = Path.Combine(directory.Path, "link");
        if (TryCreateLink(link, target) is null)
        {
            Assert.Skip("This environment cannot create a symbolic link or junction.");
            return;
        }

        Directory.CreateDirectory(Path.Combine(link, "sub"));

        string? problem = SecureOutputsFile.Validate(Path.Combine(link, "sub", "secure.json"));

        Assert.NotNull(problem);
        Assert.Contains("symbolic link or junction", problem, StringComparison.Ordinal);
    }

    // ---- S2-19: the secure outputs file is merged under a lock ----

    [Fact]
    public async Task TwoRunsMergingOneTimeSecretsIntoOneFile_LoseNone()
    {
        using var directory = new TestDirectory(nameof(TwoRunsMergingOneTimeSecretsIntoOneFile_LoseNone));
        string path = directory.File("secure.json");

        await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(() => SecureOutputsFile.MergeOneTimeSecret(path, "/providers/Microsoft.Entra/applications/app/passwordCredentials/c" + i, new JsonObject { ["secretText"] = "secret-" + i }), Ct)));

        var bucket = JsonNode.Parse(await File.ReadAllTextAsync(path, Ct))!["oneTimeSecrets"]!.AsObject();
        Assert.Equal(24, bucket.Count);
        for (int i = 0; i < 24; i++)
        {
            Assert.Equal("secret-" + i, bucket["/providers/Microsoft.Entra/applications/app/passwordCredentials/c" + i]!["secretText"]!.GetValue<string>());
        }

        // The lock file is deleted when its last holder closes it, but a scanner or indexer can keep a just-closed file for a moment (delete pending): wait for it
        // instead of racing the operating system (review 5M; this assertion was the flaky one).
        var clock = Stopwatch.StartNew();
        while (File.Exists(path + ".lock") && clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(20, Ct);
        }

        Assert.False(File.Exists(path + ".lock"));
        Assert.Empty(Directory.GetFiles(directory.Path, "secure.json.pending-*"));
    }

    [Fact]
    public async Task AnUnreadableSecureOutputsFile_IsKeptNextToTheNewOne_WithTheSamePermissions()
    {
        using var directory = new TestDirectory(nameof(AnUnreadableSecureOutputsFile_IsKeptNextToTheNewOne_WithTheSamePermissions));
        string path = directory.File("secure.json");
        await File.WriteAllTextAsync(path, "{ not json", Ct);

        SecureOutputsFile.MergeOneTimeSecret(path, "/providers/Microsoft.Entra/applications/a/passwordCredentials/c", new JsonObject { ["secretText"] = "s" });

        Assert.Equal("{ not json", await File.ReadAllTextAsync(path + ".previous", Ct));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(File.GetUnixFileMode(path), File.GetUnixFileMode(path + ".previous"));
        }
    }
}
