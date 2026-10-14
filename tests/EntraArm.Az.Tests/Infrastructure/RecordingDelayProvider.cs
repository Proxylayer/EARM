using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Az;

namespace EntraArm.Az.Tests.Infrastructure;

/// <summary>Records every delay the retry policy asks for and returns immediately.</summary>
internal sealed class RecordingDelayProvider : IDelayProvider
{
    private readonly List<TimeSpan> _delays = [];
    private readonly object _gate = new();

    public IReadOnlyList<TimeSpan> Delays
    {
        get
        {
            lock (_gate)
            {
                return [.. _delays];
            }
        }
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _delays.Add(delay);
        }

        return Task.CompletedTask;
    }
}
