using System;

namespace EntraArm.Az;

/// <summary>
/// A required tool (PowerShell 7.4+) is missing, too old, or cannot be started. Terminal: never retried.
/// </summary>
public sealed class ToolchainException : Exception
{
    public ToolchainException(string tool, string message)
        : base(message)
    {
        Tool = tool;
    }

    public ToolchainException(string tool, string message, Exception innerException)
        : base(message, innerException)
    {
        Tool = tool;
    }

    /// <summary>The tool concerned, e.g. <c>pwsh</c>.</summary>
    public string Tool { get; }
}
