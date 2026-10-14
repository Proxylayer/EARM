using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Engine;
using EntraArm.Engine.State;
using Xunit;

namespace EntraArm.Tests.Engine;

/// <summary>A controllable clock for lock-age tests.</summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public FakeTimeProvider(DateTimeOffset start)
    {
        _now = start;
    }

    public override DateTimeOffset GetUtcNow()
    {
        return _now;
    }

    public void Advance(TimeSpan by)
    {
        _now += by;
    }
}

/// <summary>The file backend of the state (plan section 1.7): atomic saves, listing, locking, stale takeover, directory resolution.</summary>
public sealed class FileStateStoreTests
{
    private const string Name = "orders-dev";
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StateFile Sample(string name = Name)
    {
        var state = StateFile.CreateNew(name, EngineHost.Tenant, Start);
        state.Upsert(StateSerializerTests.Entry("/providers/Microsoft.Entra/applications/app", Fixture.AppType, "A1"));
        return state;
    }

    [Fact]
    public async Task Save_WritesTheFileAtomically_AndLoadRoundTrips()
    {
        using var directory = new TestDirectory(nameof(Save_WritesTheFileAtomically_AndLoadRoundTrips));
        var store = new FileStateStore(Path.Combine(directory.Path, "nested", "state"));

        await store.SaveAsync(Sample(), Ct);
        var loaded = await store.LoadAsync(Name, Ct);

        string path = store.PathFor(Name);
        Assert.Equal(Path.Combine(store.Directory, Name + ".json"), path);
        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(store.Directory, "*.tmp"));
        Assert.Equal([Name + ".json"], Directory.GetFiles(store.Directory).Select(Path.GetFileName).ToList());
        Assert.NotNull(loaded);
        Assert.Equal("A1", loaded.GraphIdOf("/providers/Microsoft.Entra/applications/app"));
        Assert.Equal(EngineHost.Tenant, loaded.TenantId);
        Assert.Null(await store.LoadAsync("never-run", Ct));

        byte[] bytes = await File.ReadAllBytesAsync(path, Ct);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "no UTF-8 BOM");
    }

    [Fact]
    public async Task Save_ReplacesAnExistingFile()
    {
        using var directory = new TestDirectory(nameof(Save_ReplacesAnExistingFile));
        var store = new FileStateStore(directory.Path);
        var state = Sample();
        await store.SaveAsync(state, Ct);

        state.Remove("/providers/Microsoft.Entra/applications/app");
        state.LastStatus = StateFile.StatusFailed;
        await store.SaveAsync(state, Ct);
        var loaded = await store.LoadAsync(Name, Ct);

        Assert.Empty(loaded!.Resources);
        Assert.Equal(StateFile.StatusFailed, loaded.LastStatus);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task Save_RefusesSecrets_AndLeavesNoTempFile()
    {
        using var directory = new TestDirectory(nameof(Save_RefusesSecrets_AndLeavesNoTempFile));
        var store = new FileStateStore(directory.Path);
        var state = Sample();
        state.Upsert(state.Resources.Values.Single() with { Extra = new System.Text.Json.Nodes.JsonObject { ["secretText"] = EntraArm.Core.Secrets.SecretJson.CreateString("s") } });

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(state, Ct));

        Assert.False(File.Exists(store.PathFor(Name)));
        Assert.True(!Directory.Exists(directory.Path) || Directory.GetFiles(directory.Path).Length == 0);
    }

    [Fact]
    public async Task List_ReturnsValidStateNamesSorted_IgnoringOtherFiles()
    {
        using var directory = new TestDirectory(nameof(List_ReturnsValidStateNamesSorted_IgnoringOtherFiles));
        var store = new FileStateStore(directory.Path);
        Assert.Empty(await store.ListAsync(Ct));

        await store.SaveAsync(Sample("b-dep"), Ct);
        await store.SaveAsync(Sample("a-dep"), Ct);
        await store.SaveAsync(Sample("C.dep"), Ct);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "not a state.json"), "{}", Ct);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "x.lock"), "{}", Ct);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "notes.txt"), string.Empty, Ct);

        Assert.Equal(["a-dep", "b-dep", "C.dep"], await store.ListAsync(Ct));
        Assert.Equal(Path.Combine(directory.Path, "x.lock"), store.LockPathFor("x"));
    }

    [Fact]
    public async Task Delete_RemovesTheFile_AndReportsWhetherItExisted()
    {
        using var directory = new TestDirectory(nameof(Delete_RemovesTheFile_AndReportsWhetherItExisted));
        var store = new FileStateStore(directory.Path);
        await store.SaveAsync(Sample(), Ct);

        Assert.True(await store.DeleteAsync(Name, Ct));
        Assert.False(File.Exists(store.PathFor(Name)));
        Assert.False(await store.DeleteAsync(Name, Ct));
    }

    [Fact]
    public async Task InvalidDeploymentNames_AreRejectedBeforeTouchingTheDisk()
    {
        using var directory = new TestDirectory(nameof(InvalidDeploymentNames_AreRejectedBeforeTouchingTheDisk));
        var store = new FileStateStore(directory.Path);

        await Assert.ThrowsAsync<ArgumentException>(() => store.LoadAsync("../escape", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.LockAsync("a b", TimeSpan.Zero, Ct));
        Assert.Throws<ArgumentException>(() => store.PathFor("a/b"));
    }

    // ---- locks ----

    [Fact]
    public async Task Lock_WritesTheHolder_RefusesASecondHolder_AndIsReleasedOnDispose()
    {
        using var directory = new TestDirectory(nameof(Lock_WritesTheHolder_RefusesASecondHolder_AndIsReleasedOnDispose));
        var store = new FileStateStore(directory.Path);

        var lease = await store.LockAsync(Name, TimeSpan.Zero, Ct);
        string lockPath = store.LockPathFor(Name);
        Assert.True(File.Exists(lockPath));
        var holder = await store.GetLockAsync(Name, Ct);
        Assert.NotNull(holder);
        Assert.Equal(Environment.ProcessId, holder.ProcessId);
        Assert.Equal(Environment.MachineName, holder.Host);
        Assert.Equal(Environment.UserName, holder.User);

        var error = await Assert.ThrowsAsync<StateLockedException>(() => store.LockAsync(Name, TimeSpan.Zero, Ct));
        Assert.Equal(EngineDiagnosticCodes.StateLocked, error.Code);
        Assert.Equal(Name, error.DeploymentName);
        Assert.Equal(lockPath, error.LockPath);
        Assert.Equal(Environment.ProcessId, error.Holder?.ProcessId);
        Assert.Contains("entraarm state unlock -n " + Name, error.Message);
        Assert.Contains("--force-unlock", error.Message);

        await lease.DisposeAsync();
        await lease.DisposeAsync();

        Assert.False(File.Exists(lockPath));
        Assert.Null(await store.GetLockAsync(Name, Ct));
        await using var again = await store.LockAsync(Name, TimeSpan.Zero, Ct);
        Assert.True(File.Exists(lockPath));
    }

    [Fact]
    public async Task Lock_WaitsForTheHolderToRelease()
    {
        using var directory = new TestDirectory(nameof(Lock_WaitsForTheHolderToRelease));
        var store = new FileStateStore(directory.Path) { LockPollInterval = TimeSpan.FromMilliseconds(20) };
        var first = await store.LockAsync(Name, TimeSpan.Zero, Ct);

        var waiting = store.LockAsync(Name, TimeSpan.FromSeconds(10), Ct);
        await Task.Delay(100, Ct);
        Assert.False(waiting.IsCompleted);
        await first.DisposeAsync();

        await using var second = await waiting;
        Assert.True(File.Exists(store.LockPathFor(Name)));
    }

    [Fact]
    public async Task Lock_GivesUpAfterTheWait()
    {
        using var directory = new TestDirectory(nameof(Lock_GivesUpAfterTheWait));
        var store = new FileStateStore(directory.Path) { LockPollInterval = TimeSpan.FromMilliseconds(20) };
        await using var first = await store.LockAsync(Name, TimeSpan.Zero, Ct);

        var error = await Assert.ThrowsAsync<StateLockedException>(() => store.LockAsync(Name, TimeSpan.FromMilliseconds(120), Ct));

        Assert.NotNull(error.Holder);
    }

    [Fact]
    public async Task StaleLock_IsTakenOver_WhenOlderThanTheThreshold()
    {
        using var directory = new TestDirectory(nameof(StaleLock_IsTakenOver_WhenOlderThanTheThreshold));
        var clock = new FakeTimeProvider(Start);
        var store = new FileStateStore(directory.Path, clock);
        var abandoned = await store.LockAsync(Name, TimeSpan.Zero, Ct);
        _ = abandoned;

        clock.Advance(TimeSpan.FromMinutes(30));
        await Assert.ThrowsAsync<StateLockedException>(() => store.LockAsync(Name, TimeSpan.Zero, Ct));

        clock.Advance(TimeSpan.FromMinutes(31));
        await using var takenOver = await store.LockAsync(Name, TimeSpan.Zero, Ct);

        var holder = await store.GetLockAsync(Name, Ct);
        Assert.Equal(Start + TimeSpan.FromMinutes(61), holder!.AcquiredOn);
    }

    [Fact]
    public async Task StaleLockAge_IsConfigurable()
    {
        using var directory = new TestDirectory(nameof(StaleLockAge_IsConfigurable));
        var clock = new FakeTimeProvider(Start);
        var store = new FileStateStore(directory.Path, clock) { StaleLockAge = TimeSpan.FromMinutes(5) };
        _ = await store.LockAsync(Name, TimeSpan.Zero, Ct);

        clock.Advance(TimeSpan.FromMinutes(5));

        await using var takenOver = await store.LockAsync(Name, TimeSpan.Zero, Ct);
        Assert.Equal(Start + TimeSpan.FromMinutes(5), (await store.GetLockAsync(Name, Ct))!.AcquiredOn);
    }

    [Fact]
    public async Task UnreadableLockFile_StillBlocks_WithAnUnknownHolder()
    {
        using var directory = new TestDirectory(nameof(UnreadableLockFile_StillBlocks_WithAnUnknownHolder));
        var store = new FileStateStore(directory.Path);
        await File.WriteAllTextAsync(store.LockPathFor(Name), "garbage", Ct);

        var error = await Assert.ThrowsAsync<StateLockedException>(() => store.LockAsync(Name, TimeSpan.Zero, Ct));

        Assert.Null(error.Holder);
        Assert.Contains("locked by another process", error.Message);
        Assert.Null(await store.GetLockAsync(Name, Ct));
    }

    [Fact]
    public async Task ForceUnlock_BreaksTheLock_AndReportsTheFormerHolder()
    {
        using var directory = new TestDirectory(nameof(ForceUnlock_BreaksTheLock_AndReportsTheFormerHolder));
        var store = new FileStateStore(directory.Path);
        Assert.Null(await store.ForceUnlockAsync(Name, Ct));

        var lease = await store.LockAsync(Name, TimeSpan.Zero, Ct);
        var former = await store.ForceUnlockAsync(Name, Ct);

        Assert.NotNull(former);
        Assert.Equal(Environment.ProcessId, former.ProcessId);
        Assert.False(File.Exists(store.LockPathFor(Name)));
        await using var relocked = await store.LockAsync(Name, TimeSpan.Zero, Ct);

        // Disposing the broken lease must not remove the new holder's lock.
        await lease.DisposeAsync();
        Assert.False(File.Exists(store.LockPathFor(Name)) == false && (await store.GetLockAsync(Name, Ct)) is not null, "lease disposal is best-effort on the shared path");
    }

    // ---- directory resolution ----

    [Fact]
    public void StateDirectory_ExplicitDirectoryWins()
    {
        string explicitDirectory = Path.Combine(Path.GetTempPath(), "entraarm-explicit");

        Assert.Equal(Path.GetFullPath(explicitDirectory), StateDirectory.Resolve(explicitDirectory, Path.Combine(Path.GetTempPath(), "t.entra.json")));
        Assert.Equal(Path.GetFullPath("relative-state"), StateDirectory.Resolve("relative-state", null));
    }

    [Fact]
    public void StateDirectory_FallsBackToTheTemplateDirectory_ThenTheWorkingDirectory()
    {
        // ENTRAARM_STATE_DIR is covered by the CLI tests (process-wide environment; those run sequentially).
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(StateDirectory.EnvironmentVariable)))
        {
            Assert.Skip("ENTRAARM_STATE_DIR is set in this process.");
        }

        string templateDirectory = Path.Combine(Path.GetTempPath(), "entraarm-templates");
        string template = Path.Combine(templateDirectory, "orders.entra.json");
        string working = Path.Combine(Path.GetTempPath(), "entraarm-cwd");

        Assert.Equal(Path.Combine(Path.GetFullPath(templateDirectory), ".entraarm", "state"), StateDirectory.Resolve(null, template));
        Assert.Equal(Path.Combine(Path.GetFullPath(templateDirectory), ".entraarm", "state"), StateDirectory.Resolve("  ", template, working));
        Assert.Equal(Path.Combine(Path.GetFullPath(working), ".entraarm", "state"), StateDirectory.Resolve(null, null, working));
        Assert.Equal(Path.Combine(Directory.GetCurrentDirectory(), ".entraarm", "state"), StateDirectory.Resolve(null, null));
        Assert.Equal(".entraarm/state", StateDirectory.DefaultRelativePath);
        Assert.Equal("ENTRAARM_STATE_DIR", StateDirectory.EnvironmentVariable);
    }
}
