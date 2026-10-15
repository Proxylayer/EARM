using System;
using System.Threading;
using System.Threading.Tasks;

namespace EArm.Az;

/// <summary>Waits between retry attempts. Injectable so tests run the retry table without real delays.</summary>
public interface IDelayProvider
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>Production delay provider backed by <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</summary>
public sealed class TaskDelayProvider : IDelayProvider
{
    public static readonly TaskDelayProvider Instance = new();

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        return delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, cancellationToken);
    }
}
