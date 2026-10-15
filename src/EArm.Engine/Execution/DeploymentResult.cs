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
}

public enum OperationStatus
{
    Succeeded,
    Failed,

    /// <summary>Not attempted because a dependency failed (or an earlier batch failed without <c>--continue-on-error</c>).</summary>
    Skipped,

    /// <summary><c>condition</c> evaluated to false.</summary>
    Ignored,

    /// <summary>Mode pass, Incremental: the resource is no longer declared and was dropped from the state (the object stays).</summary>
    Detached,
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
        TimeSpan duration)
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

    /// <summary>Resources created, updated or found unchanged (mode-pass deletes are counted by <see cref="DeletedCount"/>).</summary>
    public int SucceededCount => Operations.Count(o => o.Status == OperationStatus.Succeeded && o.ChangeType != Providers.ChangeType.Delete);

    public int FailedCount => Operations.Count(o => o.Status == OperationStatus.Failed);

    public int SkippedCount => Operations.Count(o => o.Status == OperationStatus.Skipped);

    public int IgnoredCount => Operations.Count(o => o.Status == OperationStatus.Ignored);

    /// <summary>Entries the mode pass dropped from the state without touching the object (Incremental).</summary>
    public int DetachedCount => Operations.Count(o => o.Status == OperationStatus.Detached);

    /// <summary>Objects the mode pass deleted (Complete).</summary>
    public int DeletedCount => Operations.Count(o => o.Status == OperationStatus.Succeeded && o.ChangeType == Providers.ChangeType.Delete);
}
