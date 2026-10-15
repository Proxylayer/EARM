using EArm.Engine.Planning;

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
}
