using System;
using EArm.Graph;

namespace EArm.Engine.Execution;

/// <summary>
/// How long <see cref="DeploymentExecutor"/> keeps looking for an object by its key after a create whose outcome it cannot take from the answer: the
/// answer was lost (a timeout, a dropped connection, a 5xx) or it was "already exists". Graph replicates a new object to the replica that serves the
/// next read with some delay, so a "not found" right after the create (an empty <c>$filter</c> result as much as a 404) is not yet proof that nothing
/// exists. The read is repeated every <see cref="Interval"/> until the object is found or <see cref="Window"/> of waiting has been scheduled. The
/// defaults are the replication row of <see cref="RetryPolicy"/> (<see cref="RetryOptions.ReplicationInterval"/>, <see cref="RetryOptions.ReplicationWindow"/>).
/// </summary>
public sealed record ReplicationWait
{
    private static readonly RetryOptions PolicyDefaults = new();

    public static ReplicationWait Default { get; } = new();

    /// <summary>The fixed wait between two reads; must be positive.</summary>
    public TimeSpan Interval
    {
        get;
        init => field = value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(Interval), value, "The interval must be positive.");
    } = PolicyDefaults.ReplicationInterval;

    /// <summary>The total scheduled wait before the object is given up as "not there" (measured in scheduled wait, like the retry policy's window); zero reads once.</summary>
    public TimeSpan Window
    {
        get;
        init => field = value >= TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(Window), value, "The window must not be negative.");
    } = PolicyDefaults.ReplicationWindow;

    /// <summary>Waits between the reads; tests pass one that does not sleep.</summary>
    public IDelayProvider Delay
    {
        get;
        init => field = value ?? throw new ArgumentNullException(nameof(Delay));
    } = TaskDelayProvider.Instance;
}
