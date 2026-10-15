using System;

namespace EArm.Az;

/// <summary>Raw outcome of one Azure CLI invocation through the pwsh host.</summary>
/// <param name="ExitCode">az's exit code as forwarded by the host script; -1 when the process tree was killed.</param>
/// <param name="StandardOutput">az's stdout (UTF-8), read back from the output file.</param>
/// <param name="StandardError">stderr of az and the host script, unredacted (never logged as-is).</param>
/// <param name="Elapsed">Wall-clock time from process start to exit.</param>
/// <param name="TimedOut">True when the per-call timeout expired and the process tree was killed.</param>
public sealed record AzResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Elapsed, bool TimedOut)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}
