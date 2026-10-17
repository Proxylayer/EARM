using System;
using EArm.Graph;

namespace EArm.Engine.Execution;

/// <summary>
/// How long <see cref="DeploymentExecutor"/> keeps looking for an object by its key after a create whose outcome it cannot take from the answer: the
/// answer was lost (a timeout, a dropped connection, a 5xx) or it was "already exists". Graph replicates a new object to the replica that serves the
/// next read with some delay, so a "not found" right after the create (an empty <c>$filter</c> result as much as a 404) is not yet proof that nothing
/// exists. The read is repeated until the object is found or <see cref="Window"/> of waiting has been scheduled. The executor's own waits (the read after
/// an uncertain create, the read-your-write after a create, a refused delete) are adaptive (<see cref="IntervalAfter"/>): 1, 2, 4 s, then every
/// <see cref="Interval"/>, because most objects show up within a second or two and a fixed 5 s step added slack to every run; the window is unchanged.
/// The provider waits (PIM onboarding, schedule requests) keep the fixed <see cref="Interval"/>. The defaults are the replication row of
/// <see cref="RetryPolicy"/> (<see cref="RetryOptions.ReplicationInterval"/>, <see cref="RetryOptions.ReplicationWindow"/>).
/// </summary>
public sealed record ReplicationWait
{
    private static readonly RetryOptions PolicyDefaults = new();

    public static ReplicationWait Default { get; } = new();

    /// <summary>The fixed wait between two reads, and the longest wait of the adaptive schedule (<see cref="IntervalAfter"/>); must be positive.</summary>
    public TimeSpan Interval
    {
        get;
        init => field = value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(Interval), value, "The interval must be positive.");
    } = PolicyDefaults.ReplicationInterval;

    /// <summary>The first wait of the adaptive schedule (<see cref="IntervalAfter"/>); each later wait doubles, up to <see cref="Interval"/>. Must be positive.</summary>
    public TimeSpan InitialInterval
    {
        get;
        init => field = value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(InitialInterval), value, "The initial interval must be positive.");
    } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The wait after miss number <paramref name="attempt"/> (1 for the wait after the first read) of an adaptive schedule:
    /// <see cref="InitialInterval"/> doubled per attempt and capped at <see cref="Interval"/> (1, 2, 4, 5, 5... seconds by default).
    /// </summary>
    public TimeSpan IntervalAfter(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        var cap = Interval;
        var wait = InitialInterval < cap ? InitialInterval : cap;
        for (int i = 1; i < attempt && wait < cap; i++)
        {
            wait = wait + wait < cap ? wait + wait : cap;
        }

        return wait;
    }

    /// <summary>The total scheduled wait before the object is given up as "not there" (measured in scheduled wait, like the retry policy's window); zero reads once.</summary>
    public TimeSpan Window
    {
        get;
        init => field = value >= TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(Window), value, "The window must not be negative.");
    } = PolicyDefaults.ReplicationWindow;

    /// <summary>
    /// Read-your-write after a create of a type whose key read lags (<c>KeyReadLagsCreate</c>): how many reads IN A ROW must list the created
    /// object before the create is taken as visible. Graph's list endpoint for these types flaps (one replica lists the object, the next does
    /// not), so one hit is not enough; a miss after a hit starts the count again. At least 1.
    /// </summary>
    public int ConsecutiveVisibleReads
    {
        get;
        init => field = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(ConsecutiveVisibleReads), value, "At least one read is required.");
    } = 2;

    /// <summary>The wait between two reads that both list the object (shorter than <see cref="Interval"/>; counted against <see cref="Window"/> too).</summary>
    public TimeSpan StableGap
    {
        get;
        init => field = value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(StableGap), value, "The gap must be positive.");
    } = TimeSpan.FromSeconds(2);

    /// <summary>Waits between the reads; tests pass one that does not sleep.</summary>
    public IDelayProvider Delay
    {
        get;
        init => field = value ?? throw new ArgumentNullException(nameof(Delay));
    } = TaskDelayProvider.Instance;
}
