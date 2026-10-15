using EArm.Engine.State;

namespace EArm.Engine.Execution;

/// <summary>Knobs of <see cref="DeploymentExecutor.ExecuteAsync"/> (the CLI flags of <c>deployment create</c>).</summary>
public sealed record ExecuteOptions
{
    public static ExecuteOptions Default { get; } = new();

    /// <summary>Resources applied concurrently inside a batch; <c>null</c> uses the plan's parallelism.</summary>
    public int? Parallelism { get; init; }

    /// <summary>Keep deploying resources that do not depend on a failed one (default: stop at the batch boundary).</summary>
    public bool ContinueOnError { get; init; }

    /// <summary><c>--delete-adopted</c>: Complete mode may also delete state entries that were adopted (existed before the deployment managed them).</summary>
    public bool DeleteAdopted { get; init; }

    /// <summary>
    /// <c>--max-deletes</c> as a hard limit of the run: the most objects the run may delete (Complete-mode orphans and the old objects of recreates).
    /// <c>null</c> (<c>--allow-delete-all</c>, or no limit) is unlimited. The CLI estimates the number before the run starts; this limit holds even when the
    /// estimate was too low (a symbolic name that only resolves during the run, a state entry added meanwhile).
    /// </summary>
    public int? MaxDeletes { get; init; }

    /// <summary>Show <c>securestring</c>/<c>secureObject</c> output values instead of <c>***</c>.</summary>
    public bool RevealSecureOutputs { get; init; }

    /// <summary>Write the revealed secure outputs to this file (owner-only permissions).</summary>
    public string? SecureOutputsFile { get; init; }

    /// <summary>Receives every applied resource (<see cref="FileStateSink"/> persists them).</summary>
    public IStateSink StateSink { get; init; } = NullStateSink.Instance;

    /// <summary>
    /// The deployment's state before this run: pinned Graph ids are passed to the providers' reads, and the mode pass
    /// detaches (Incremental) or deletes (Complete) the entries the template no longer
    /// declares. <c>null</c> skips both.
    /// </summary>
    public StateFile? State { get; init; }
}
