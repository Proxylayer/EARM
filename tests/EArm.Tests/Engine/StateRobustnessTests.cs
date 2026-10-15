using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.State;
using Xunit;

namespace EArm.Tests.Engine;

/// <summary>
/// State file corruption and lock edge cases: whatever is on disk, loading fails with the one documented exception
/// type (never a crash of another kind), a failed save leaves nothing behind, and unreadable / skewed / hostile lock
/// files are handled the way the lock rules say. Clocks are fixed, directories are private.
/// </summary>
public sealed class StateRobustnessTests
{
    private const string Name = "orders-dev";
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StateFile Sample()
    {
        var state = StateFile.CreateNew(Name, EngineHost.Tenant, Start);
        state.Upsert(StateSerializerTests.Entry("/providers/Microsoft.Entra/applications/app", Fixture.AppType, "a1000000-0000-4000-8000-000000000001"));
        return state;
    }

    // ---- corrupt state files ----

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n\t ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"a string\"")]
    [InlineData("{")]
    [InlineData("{ \"schemaVersion\": 1, \"resources\": [")]
    [InlineData("{ \"schemaVersion\": 1, \"resources\": }")]
    [InlineData("{ \"schemaVersion\": 1 \"resources\": {} }")]
    [InlineData("{ \"schemaVersion\": \"one\" }")]
    [InlineData("{ \"schemaVersion\": 1.5 }")]
    [InlineData("{ \"schemaVersion\": 99 }")]
    [InlineData("{ \"schemaVersion\": -1 }")]
    [InlineData("{ \"schemaVersion\": 1, \"resources\": \"nope\" }")]
    [InlineData("{ \"schemaVersion\": 1, \"resources\": { \"/providers/Microsoft.Entra/applications/a\": 7 } }")]
    [InlineData("{ \"schemaVersion\": 1, \"resources\": { \"/providers/Microsoft.Entra/applications/a\": { \"type\": 5, \"id\": \"A1\" } } }")]
    [InlineData("{ \"schemaVersion\": 1, \"operations\": \"nope\" }")]
    [InlineData("{ \"schemaVersion\": 1, \"operations\": [ 1, 2 ] }")]
    [InlineData("\u0000\u0001\u0002 binary")]
    public async Task Load_OfACorruptFile_FailsWithTheDocumentedException(string content)
    {
        using var directory = new TestDirectory(nameof(Load_OfACorruptFile_FailsWithTheDocumentedException));
        var store = new FileStateStore(directory.Path);
        await File.WriteAllTextAsync(store.PathFor(Name), content, new UTF8Encoding(false), Ct);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => store.LoadAsync(Name, Ct));

        Assert.True(error is StateSchemaUnsupportedException, $"expected StateSchemaUnsupportedException, got {error.GetType().Name}: {error.Message}");
        Assert.Equal(Name, ((StateSchemaUnsupportedException)error).DeploymentName);
        Assert.Contains(Name, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_AcceptsAUtf8Bom_UnknownFields_AndDeeplyNestedExtra()
    {
        using var directory = new TestDirectory(nameof(Load_AcceptsAUtf8Bom_UnknownFields_AndDeeplyNestedExtra));
        var store = new FileStateStore(directory.Path);
        await store.SaveAsync(Sample(), Ct);
        string path = store.PathFor(Name);
        string json = await File.ReadAllTextAsync(path, Ct);
        string nested = string.Concat(Enumerable.Repeat("{ \"n\": ", 40)) + "1" + string.Concat(Enumerable.Repeat(" }", 40));
        string withExtras = json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"futureField\": " + nested, StringComparison.Ordinal);
        Assert.NotEqual(json, withExtras);
        await File.WriteAllBytesAsync(path, [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(withExtras)], Ct);

        var loaded = await store.LoadAsync(Name, Ct);

        Assert.NotNull(loaded);
        Assert.Equal("a1000000-0000-4000-8000-000000000001", loaded.GraphIdOf("/providers/Microsoft.Entra/applications/app"));
    }

    [Fact]
    public async Task Load_ADocumentOfAnotherDeployment_IsReadUnderTheRequestedName()
    {
        using var directory = new TestDirectory(nameof(Load_ADocumentOfAnotherDeployment_IsReadUnderTheRequestedName));
        var store = new FileStateStore(directory.Path);
        await store.SaveAsync(Sample(), Ct);
        File.Copy(store.PathFor(Name), store.PathFor("copied"));

        var loaded = await store.LoadAsync("copied", Ct);

        Assert.NotNull(loaded);
        Assert.Equal("a1000000-0000-4000-8000-000000000001", loaded.GraphIdOf("/providers/Microsoft.Entra/applications/app"));
    }

    // ---- failed saves ----

    [Fact]
    public async Task Save_WhenTheTargetIsADirectory_Throws_AndLeavesNoTemporaryFile()
    {
        using var directory = new TestDirectory(nameof(Save_WhenTheTargetIsADirectory_Throws_AndLeavesNoTemporaryFile));
        var store = new FileStateStore(directory.Path);
        Directory.CreateDirectory(store.PathFor(Name));

        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(Sample(), Ct));

        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
        Assert.True(Directory.Exists(store.PathFor(Name)));
    }

    [Fact]
    public async Task EveryOperation_WhenTheStateDirectoryIsAFile_ThrowsAStateStoreException()
    {
        // Review finding S-06: a state directory that is a file is reported (a clean message, exit 1), never read as "no deployments".
        using var directory = new TestDirectory(nameof(EveryOperation_WhenTheStateDirectoryIsAFile_ThrowsAStateStoreException));
        string blocker = directory.File("state-is-a-file");
        await File.WriteAllTextAsync(blocker, "x", Ct);
        var store = new FileStateStore(blocker);

        await Assert.ThrowsAsync<StateStoreException>(() => store.SaveAsync(Sample(), Ct));
        await Assert.ThrowsAsync<StateStoreException>(() => store.LockAsync(Name, TimeSpan.Zero, Ct));
        await Assert.ThrowsAsync<StateStoreException>(() => store.ListAsync(Ct));
        await Assert.ThrowsAsync<StateStoreException>(() => store.LoadAsync(Name, Ct));
    }

    [Fact]
    public async Task Save_ANullState_AndAnInvalidDirectory_AreRejected()
    {
        using var directory = new TestDirectory(nameof(Save_ANullState_AndAnInvalidDirectory_AreRejected));
        var store = new FileStateStore(directory.Path);

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.SaveAsync(null!, Ct));
        Assert.Throws<ArgumentException>(() => new FileStateStore(" "));
        Assert.Throws<ArgumentException>(() => new FileStateStore(string.Empty));
    }

    // ---- lock files ----

    [Fact]
    public async Task Lock_AGarbageFile_BecomesStaleByItsFileTime_AndIsTakenOver()
    {
        using var directory = new TestDirectory(nameof(Lock_AGarbageFile_BecomesStaleByItsFileTime_AndIsTakenOver));
        var probe = new FileStateStore(directory.Path);
        string lockPath = probe.LockPathFor(Name);
        await File.WriteAllTextAsync(lockPath, "garbage, not json", Ct);
        var written = new DateTimeOffset(File.GetLastWriteTimeUtc(lockPath), TimeSpan.Zero);
        var clock = new FakeTimeProvider(written.AddMinutes(30));
        var store = new FileStateStore(directory.Path, clock);

        var fresh = await Assert.ThrowsAsync<StateLockedException>(() => store.LockAsync(Name, TimeSpan.Zero, Ct));
        Assert.Null(fresh.Holder);

        clock.Advance(TimeSpan.FromMinutes(40));
        await using var lease = await store.LockAsync(Name, TimeSpan.Zero, Ct);

        Assert.NotNull(await store.GetLockAsync(Name, Ct));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("")]
    public async Task Lock_AnEmptyOrDegenerateLockDocument_StillBlocks(string content)
    {
        using var directory = new TestDirectory(nameof(Lock_AnEmptyOrDegenerateLockDocument_StillBlocks));
        var store = new FileStateStore(directory.Path, new FakeTimeProvider(DateTimeOffset.UtcNow));
        await File.WriteAllTextAsync(store.LockPathFor(Name), content, Ct);

        var error = await Assert.ThrowsAsync<StateLockedException>(() => store.LockAsync(Name, TimeSpan.Zero, Ct));

        Assert.Equal(Name, error.DeploymentName);
    }

    [Fact]
    public async Task Lock_ADocumentWithoutAnAcquisitionTime_IsOlderThanAnyThreshold_AndTakenOver()
    {
        using var directory = new TestDirectory(nameof(Lock_ADocumentWithoutAnAcquisitionTime_IsOlderThanAnyThreshold_AndTakenOver));
        var store = new FileStateStore(directory.Path, new FakeTimeProvider(Start));
        await File.WriteAllTextAsync(store.LockPathFor(Name), "{}", Ct);

        await using var lease = await store.LockAsync(Name, TimeSpan.Zero, Ct);

        Assert.Equal(Start, (await store.GetLockAsync(Name, Ct))!.AcquiredOn);
    }

    [Fact]
    public async Task Lock_AHolderFromTheFuture_IsNotStale()
    {
        using var directory = new TestDirectory(nameof(Lock_AHolderFromTheFuture_IsNotStale));
        var clock = new FakeTimeProvider(Start);
        var holderStore = new FileStateStore(directory.Path, new FakeTimeProvider(Start.AddDays(3)));
        _ = await holderStore.LockAsync(Name, TimeSpan.Zero, Ct);
        var store = new FileStateStore(directory.Path, clock);

        var error = await Assert.ThrowsAsync<StateLockedException>(() => store.LockAsync(Name, TimeSpan.Zero, Ct));

        Assert.Equal(Start.AddDays(3), error.Holder!.AcquiredOn);
    }

    [Fact]
    public async Task Lock_NegativeWait_IsTreatedAsNoWait_AndCancellationStopsAWaitingCaller()
    {
        using var directory = new TestDirectory(nameof(Lock_NegativeWait_IsTreatedAsNoWait_AndCancellationStopsAWaitingCaller));
        var store = new FileStateStore(directory.Path) { LockPollInterval = TimeSpan.FromMilliseconds(10) };
        await using var held = await store.LockAsync(Name, TimeSpan.Zero, Ct);

        await Assert.ThrowsAsync<StateLockedException>(() => store.LockAsync(Name, TimeSpan.FromSeconds(-5), Ct));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var waiting = store.LockAsync(Name, TimeSpan.FromMinutes(5), cts.Token);
        await Task.Delay(50, Ct);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        using var already = new CancellationTokenSource();
        await already.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LockAsync(Name, TimeSpan.Zero, already.Token));
    }

    [Fact]
    public async Task ForceUnlock_OfAGarbageLock_DeletesIt_AndReportsNoHolder()
    {
        using var directory = new TestDirectory(nameof(ForceUnlock_OfAGarbageLock_DeletesIt_AndReportsNoHolder));
        var store = new FileStateStore(directory.Path);
        await File.WriteAllTextAsync(store.LockPathFor(Name), "garbage", Ct);

        var former = await store.ForceUnlockAsync(Name, Ct);

        Assert.Null(former);
        Assert.False(File.Exists(store.LockPathFor(Name)));
        await using var lease = await store.LockAsync(Name, TimeSpan.Zero, Ct);
    }

    [Fact]
    public async Task ForceUnlock_WhileAHolderIsActive_LetsANewHolderIn_AndTheOldLeaseReleasesQuietly()
    {
        using var directory = new TestDirectory(nameof(ForceUnlock_WhileAHolderIsActive_LetsANewHolderIn_AndTheOldLeaseReleasesQuietly));
        var store = new FileStateStore(directory.Path);
        var old = await store.LockAsync(Name, TimeSpan.Zero, Ct);

        var former = await store.ForceUnlockAsync(Name, Ct);
        Assert.Equal(Environment.ProcessId, former!.ProcessId);

        var replacement = await store.LockAsync(Name, TimeSpan.Zero, Ct);
        await old.DisposeAsync();

        // Review round 2 (S2-11a): the old lease releases by lockId (compare-and-delete): the replacement's lock stays in place, nobody else's lock is deleted.
        Assert.True(File.Exists(store.LockPathFor(Name)));
        Assert.NotNull(await store.GetLockAsync(Name, Ct));
        await replacement.DisposeAsync();
        Assert.False(File.Exists(store.LockPathFor(Name)));
    }

    [Fact]
    public async Task List_SkipsDirectoriesAndTemporaryFiles_AndIsCaseInsensitivelyOrdered()
    {
        using var directory = new TestDirectory(nameof(List_SkipsDirectoriesAndTemporaryFiles_AndIsCaseInsensitivelyOrdered));
        var store = new FileStateStore(directory.Path);
        await store.SaveAsync(Sample(), Ct);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "orders-dev.json.abcd1234.tmp"), "{}", Ct);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "UPPER.JSON"), "{}", Ct);
        Directory.CreateDirectory(Path.Combine(directory.Path, "folder.json"));

        var names = await store.ListAsync(Ct);

        Assert.Contains(Name, names);
        Assert.DoesNotContain(names, n => n.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(names.Order(StringComparer.OrdinalIgnoreCase).ToList(), names);
    }
}
