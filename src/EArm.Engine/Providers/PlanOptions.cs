using EArm.Engine.Planning;
using EArm.Engine.State;

namespace EArm.Engine.Providers;

/// <summary>What to do when the key of a resource to create is held by a soft-deleted object.</summary>
public enum SoftDeletedConflict
{
    Error,
    Restore,
}

/// <summary>Deployment-wide knobs a provider may consult while planning a change.</summary>
public sealed record PlanOptions
{
    public static PlanOptions Default { get; } = new();

    public DeploymentMode Mode { get; init; } = DeploymentMode.Incremental;

    public PlanMode PlanMode { get; init; } = PlanMode.WhatIf;

    /// <summary><c>--allow-ca-enforce</c>: a Conditional Access policy may be deployed with <c>state: enabled</c>.</summary>
    public bool AllowCaEnforce { get; init; }

    public SoftDeletedConflict OnSoftDeletedConflict { get; init; } = SoftDeletedConflict.Error;

    /// <summary>
    /// The deployment's state before this run (<c>null</c> when there is none). A provider whose live object cannot
    /// carry everything its diff needs reads the entry's <c>extra</c> data here (password credentials: the
    /// <c>rotationTag</c> and the defaulted <c>endDateTime</c>, which Graph does not store). Read-only for providers;
    /// the executor keeps writing entries while a batch runs, so look an entry up and copy what you need.
    /// </summary>
    public StateFile? State { get; init; }
}
