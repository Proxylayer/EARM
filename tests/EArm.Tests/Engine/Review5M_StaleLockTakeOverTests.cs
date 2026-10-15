using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine.State;
using Xunit;

namespace EArm.Tests.Engine;

/// <summary>
/// Review 5M: taking over a stale state lock is serialised by a gate, so a taker that judged the lock stale from an old read cannot displace the FRESH lock another
/// process has created since (which let a third process create a lock of its own: two holders at once, seen as <c>ManyTakersOfOneStaleLock_NeverHoldItAtOnce</c> failing
/// with 2 under load).
/// </summary>
public sealed class Review5M_StaleLockTakeOverTests
{
    private const string Name = "r5m-lock";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void WriteStaleLock(FileStateStore store)
    {
        var acquired = DateTimeOffset.UtcNow.AddHours(-3);
        var document = new StateLockDocument { ProcessId = 4242, Host = "other-host", User = "someone", AcquiredOn = acquired, HeartbeatOn = acquired, LockId = "foreign" };
        Directory.CreateDirectory(store.Directory);
        File.WriteAllText(store.LockPathFor(Name), JsonSerializer.Serialize(document, StateJsonContext.Default.StateLockDocument));
    }

    [Fact]
    public async Task AStaleLock_WhoseTakeOverGateIsHeldByAnotherTaker_IsWaitedFor_NotDisplaced()
    {
        using var directory = new TestDirectory(nameof(AStaleLock_WhoseTakeOverGateIsHeldByAnotherTaker_IsWaitedFor_NotDisplaced));
        var store = new FileStateStore(directory.Path) { LockPollInterval = TimeSpan.FromMilliseconds(2) };
        WriteStaleLock(store);
        string before = await File.ReadAllTextAsync(store.LockPathFor(Name), Ct);
        using var gate = new FileStream(store.LockPathFor(Name) + ".takeover", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        await Assert.ThrowsAsync<StateLockedException>(() => store.LockAsync(Name, TimeSpan.FromMilliseconds(200), Ct));

        Assert.Equal(before, await File.ReadAllTextAsync(store.LockPathFor(Name), Ct));
    }

    [Fact]
    public async Task AStaleLock_IsTakenOver_OnceTheGateIsFree_AndTheGateLeavesNothingBehind()
    {
        using var directory = new TestDirectory(nameof(AStaleLock_IsTakenOver_OnceTheGateIsFree_AndTheGateLeavesNothingBehind));
        var store = new FileStateStore(directory.Path) { LockPollInterval = TimeSpan.FromMilliseconds(2) };
        WriteStaleLock(store);
        var gate = new FileStream(store.LockPathFor(Name) + ".takeover", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        var release = Task.Run(async () =>
        {
            await Task.Delay(150, Ct);
            await gate.DisposeAsync();
        }, Ct);

        await using (await store.LockAsync(Name, TimeSpan.FromSeconds(20), Ct))
        {
            Assert.Equal(Environment.ProcessId, (await store.GetLockAsync(Name, Ct))!.ProcessId);
        }

        await release;
        Assert.Empty(Directory.GetFiles(directory.Path, "*.takeover"));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.stale"));
    }

    [Fact]
    public async Task ManyRoundsOfManyTakersOfOneStaleLock_NeverHoldItAtOnce()
    {
        int maxInside = 0;
        int rounds = 0;
        for (int round = 0; round < 12; round++)
        {
            using var directory = new TestDirectory(nameof(ManyRoundsOfManyTakersOfOneStaleLock_NeverHoldItAtOnce) + round);
            var seed = new FileStateStore(directory.Path);
            WriteStaleLock(seed);
            int inside = 0;

            async Task TakerAsync()
            {
                var store = new FileStateStore(directory.Path) { LockPollInterval = TimeSpan.FromMilliseconds(2) };
                for (int i = 0; i < 4; i++)
                {
                    await using var lease = await store.LockAsync(Name, TimeSpan.FromSeconds(30), Ct);
                    int now = Interlocked.Increment(ref inside);
                    int seen;
                    do
                    {
                        seen = Volatile.Read(ref maxInside);
                    }
                    while (now > seen && Interlocked.CompareExchange(ref maxInside, now, seen) != seen);

                    await Task.Delay(2, Ct);
                    Interlocked.Increment(ref rounds);
                    Interlocked.Decrement(ref inside);
                }
            }

            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(TakerAsync, Ct)));
            Assert.False(File.Exists(seed.LockPathFor(Name)));
        }

        Assert.Equal(1, maxInside);
        Assert.Equal(12 * 8 * 4, rounds);
    }
}
