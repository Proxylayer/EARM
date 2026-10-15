using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Engine.Providers;

namespace EArm.Engine.Execution;

public enum DeploymentStatus
{
    Succeeded,
    Failed,

    /// <summary>The run was cancelled; <see cref="DeploymentExecutor.ExecuteAsync"/> then ends with an <see cref="OperationCanceledException"/> instead of a result.</summary>
    Cancelled,
}

public enum OperationStatus
{
    Succeeded,
    Failed,

    /// <summary>Not attempted because a dependency failed (or an earlier batch failed without <c>--continue-on-error</c>).</summary>
    Skipped,

    /// <summary><c>condition</c> evaluated to false.</summary>
    Ignored,
}

/// <summary>What happened to one resource instance during execution.</summary>
public sealed record ResourceOperation(
    ResourceIdentifier Id,
    string Type,
    int Batch,
    OperationStatus Status,
    ChangeType? ChangeType,
    ChangeMode? Mode,
    string? GraphId,
    string? Error,
    string? ErrorCode,
    TimeSpan Duration)
{
    /// <summary>The ARM-style symbol of the applied change, or <c>!</c> / <c>?</c> for failed / skipped.</summary>
    public char Symbol => Status switch
    {
        OperationStatus.Failed => '!',
        OperationStatus.Skipped => '?',
        OperationStatus.Ignored => '*',
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

/// <summary>The outcome of <see cref="DeploymentExecutor.ExecuteAsync"/>.</summary>
public sealed class DeploymentResult
{
    internal DeploymentResult(
        string deploymentName,
        DeploymentStatus status,
        IReadOnlyList<ResourceOperation> operations,
        IReadOnlyList<ResourceChange> changes,
        JsonObject outputs,
        JsonObject secureOutputs,
        IReadOnlyList<Diagnostic> diagnostics,
        string? secureOutputsFile,
        TimeSpan duration,
        IReadOnlyList<ResourceChange>? orphans = null)
    {
        DeploymentName = deploymentName;
        Status = status;
        Operations = operations;
        Changes = changes;
        Outputs = outputs;
        SecureOutputs = secureOutputs;
        Diagnostics = diagnostics;
        SecureOutputsFile = secureOutputsFile;
        Duration = duration;
        Orphans = orphans ?? [];
    }

    public string DeploymentName { get; }

    public DeploymentStatus Status { get; }

    public bool Succeeded => Status == DeploymentStatus.Succeeded;

    /// <summary>One entry per instance, in batch order.</summary>
    public IReadOnlyList<ResourceOperation> Operations { get; }

    /// <summary>The changes the executor computed right before applying each resource (live plan), in batch order.</summary>
    public IReadOnlyList<ResourceChange> Changes { get; }

    /// <summary>
    /// <c>{ name: { type, value } }</c>. Secure outputs hold <c>***</c> unless <see cref="ExecuteOptions.RevealSecureOutputs"/>
    /// was set; <see cref="SecureOutputs"/> always holds the revealed values (in memory only).
    /// </summary>
    public JsonObject Outputs { get; }

    /// <summary>The revealed secure outputs (<c>{ name: { type, value } }</c>); never logged.</summary>
    public JsonObject SecureOutputs { get; }

    /// <summary>Output evaluation problems and other non-resource findings.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public string? SecureOutputsFile { get; }

    public TimeSpan Duration { get; }

    /// <summary>
    /// The orphan report of the run: live objects no declared resource matches (<see cref="ResourceChange.Orphan"/>: undeclared in Complete mode, retained
    /// assignments and grants of declared principals in both modes). Reported only; nothing was deleted, and they do not affect <see cref="Status"/>.
    /// </summary>
    public IReadOnlyList<ResourceChange> Orphans { get; }

    /// <summary>
    /// True when the run wrote something: a resource created, updated, replaced or deleted, or a relationship entry added or removed by the edge pass
    /// (<c>create --exit-code-on-changes</c>). The orphan report never counts: nothing is written for it.
    /// </summary>
    public bool HasChanges => Changes.Any(change => change.IsWrite && !change.IsOrphan);

    /// <summary>Resources created, updated (or replaced) or found unchanged.</summary>
    public int SucceededCount => Operations.Count(o => o.Status == OperationStatus.Succeeded);

    public int FailedCount => Operations.Count(o => o.Status == OperationStatus.Failed);

    public int SkippedCount => Operations.Count(o => o.Status == OperationStatus.Skipped);

    public int IgnoredCount => Operations.Count(o => o.Status == OperationStatus.Ignored);
}
