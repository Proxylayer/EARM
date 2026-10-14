using System;
using EntraArm.Engine.Execution;
using EntraArm.Engine.Providers;

namespace EntraArm.Engine.State;

/// <summary>
/// One resource operation of the last run as recorded in the state file (<c>deployment operation list</c>):
/// what happened to which resource, when, and the (redacted) error when it failed.
/// </summary>
public sealed record OperationRecord
{
    public required string ResourceId { get; init; }

    public required string Type { get; init; }

    public required OperationStatus Status { get; init; }

    public ChangeType? ChangeType { get; init; }

    /// <summary>The dependency batch the resource was applied in; <c>null</c> for the mode pass.</summary>
    public int? Batch { get; init; }

    public DateTimeOffset? StartedOn { get; init; }

    public DateTimeOffset? FinishedOn { get; init; }

    public string? GraphId { get; init; }

    /// <summary>The failure message, passed through the secret redactor.</summary>
    public string? Error { get; init; }

    public string? ErrorCode { get; init; }

    public TimeSpan? Duration => StartedOn is { } started && FinishedOn is { } finished ? finished - started : null;

    /// <summary>The ARM-style symbol, as <see cref="ResourceOperation.Symbol"/>.</summary>
    public char Symbol => Status switch
    {
        OperationStatus.Failed => '!',
        OperationStatus.Skipped => '?',
        OperationStatus.Ignored => '*',
        OperationStatus.Detached => '*',
        _ => ChangeType switch
        {
            Providers.ChangeType.Create => '+',
            Providers.ChangeType.Modify => '~',
            Providers.ChangeType.Delete => '-',
            Providers.ChangeType.NoChange => '=',
            _ => '*',
        },
    };
}
