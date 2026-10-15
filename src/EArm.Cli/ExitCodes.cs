namespace EArm.Cli;

/// <summary>Process exit codes of <c>earm</c>.</summary>
internal static class ExitCodes
{
    public const int Ok = 0;

    /// <summary>Deployment failure, or a template/parameter file that could not be loaded at all.</summary>
    public const int Failure = 1;

    public const int ValidationFailed = 2;

    public const int AuthOrPreflight = 3;

    // 4 was StateLocked (the local state lock, removed with the state subsystem); it is not reused so the other codes keep their meaning.

    public const int WhatIfChangesDetected = 5;
}
