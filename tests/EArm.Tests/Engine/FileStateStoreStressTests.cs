using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine.State;
using Xunit;

namespace EArm.Tests.Engine;

/// <summary>D5: lock release retries the delete and reading a lock never blocks its release (FileShare.Delete).</summary>
public sealed class FileStateStoreStressTests
{
    private const string Name = "stress-dep";
    private const int Rounds = 600;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TwoTasksLockingAndReleasing_WithAConcurrentReader_NeverFailAndLeaveNoLockFile()
    {
        using var directory = new TestDirectory(nameof(TwoTasksLockingAndReleasing_WithAConcurrentReader_NeverFailAndLeaveNoLockFile));
        var store = new FileStateStore(directory.Path) { LockPollInterval = TimeSpan.FromMilliseconds(1) };
        int acquired = 0;
        using var stopReader = new CancellationTokenSource();

        async Task Worker()
        {
            for (int i = 0; i < Rounds; i++)
            {
                // Zero wait would throw while the other task holds the lock; a generous wait must always succeed. The wait is the hang guard (no wall-clock assertion: about 1 s idle, much more on a loaded machine).
                await using var lease = await store.LockAsync(Name, TimeSpan.FromSeconds(120), Ct);
                Interlocked.Increment(ref acquired);
            }
        }

        async Task Reader()
        {
            while (!stopReader.IsCancellationRequested)
            {
                await store.GetLockAsync(Name, Ct);
                await Task.Yield();
            }
        }

        var reader = Task.Run(Reader, Ct);
        await Task.WhenAll(Task.Run(Worker, Ct), Task.Run(Worker, Ct));
        await stopReader.CancelAsync();
        await reader;

        Assert.Equal(2 * Rounds, acquired);
        Assert.False(File.Exists(store.LockPathFor(Name)), "a .lock file was left behind");
        Assert.Empty(Directory.GetFiles(directory.Path, "*.lock"));
        Assert.Null(await store.GetLockAsync(Name, Ct));
    }

    [Fact]
    public async Task ForceUnlock_WhileAReaderPolls_LeavesNothingBehind()
    {
        using var directory = new TestDirectory(nameof(ForceUnlock_WhileAReaderPolls_LeavesNothingBehind));
        var store = new FileStateStore(directory.Path);

        for (int i = 0; i < 200; i++)
        {
            var lease = await store.LockAsync(Name, TimeSpan.Zero, Ct);
            var poll = Task.Run(async () =>
            {
                for (int j = 0; j < 20; j++)
                {
                    await store.GetLockAsync(Name, Ct);
                }
            }, Ct);
            await lease.DisposeAsync();
            await poll;
        }

        Assert.Empty(Directory.GetFiles(directory.Path, "*.lock"));
    }
}
