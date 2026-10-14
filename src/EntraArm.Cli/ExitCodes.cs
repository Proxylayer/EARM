namespace EntraArm.Cli;

/// <summary>Process exit codes of <c>entraarm</c> (plan section 1.9).</summary>
internal static class ExitCodes
{
    public const int Ok = 0;

    /// <summary>Deployment failure, or a template/parameter file that could not be loaded at all.</summary>
    public const int Failure = 1;

    public const int ValidationFailed = 2;

    public const int AuthOrPreflight = 3;

    public const int StateLocked = 4;

    public const int WhatIfChangesDetected = 5;
}
