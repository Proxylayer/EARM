using System.Collections.Generic;

namespace EArm.Engine.Execution;

/// <summary>Knobs of <see cref="DeploymentExecutor.ExecuteAsync"/> (the CLI flags of <c>deployment create</c>).</summary>
public sealed record ExecuteOptions
{
    public static ExecuteOptions Default { get; } = new();

    /// <summary>Resources applied concurrently inside a batch; <c>null</c> uses the plan's parallelism.</summary>
    public int? Parallelism { get; init; }

    /// <summary>Keep deploying resources that do not depend on a failed one (default: stop at the batch boundary).</summary>
    public bool ContinueOnError { get; init; }

    /// <summary>
    /// The resource ids whose replacement (an immutable property changed: delete, then create again) was confirmed. A recreate of any other resource fails
    /// with <see cref="EngineDiagnosticCodes.RecreateNotConfirmed"/> before anything is deleted. <c>null</c> confirms every replacement (<c>-y</c>). The CLI
    /// fills it from the plan the user confirmed; a replacement the run discovers later (its input only resolved during the run) is therefore refused.
    /// </summary>
    public IReadOnlySet<string>? ApprovedRecreates { get; init; }

    /// <summary>Show <c>securestring</c>/<c>secureObject</c> output values instead of <c>***</c>.</summary>
    public bool RevealSecureOutputs { get; init; }

    /// <summary>Write the revealed secure outputs to this file (owner-only permissions).</summary>
    public string? SecureOutputsFile { get; init; }
}
