using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace EArm.Az;

/// <summary>Outcome of a child process run: exit code, captured streams, timing and whether it was killed on timeout.</summary>
internal sealed record ProcessOutcome(int ExitCode, string StandardOutput, string StandardError, TimeSpan Elapsed, bool TimedOut);

/// <summary>
/// Starts pwsh with redirected UTF-8 streams, enforces a timeout by killing the whole process tree, and propagates
/// caller cancellation (also killing the tree). The only executable EARM ever starts is pwsh.
/// </summary>
internal static class PwshProcess
{
    private static readonly TimeSpan StreamDrainTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan KillWaitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Base start info for pwsh: no shell, no window, UTF-8 pipes, telemetry and update checks off.</summary>
    public static ProcessStartInfo CreateStartInfo(string pwshPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = pwshPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = SecureFile.Utf8NoBom,
            StandardErrorEncoding = SecureFile.Utf8NoBom,
        };

        startInfo.Environment["POWERSHELL_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["POWERSHELL_UPDATECHECK"] = "Off";
        return startInfo;
    }

    public static async Task<ProcessOutcome> RunAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        cancellationToken.ThrowIfCancellationRequested();

        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new ToolchainException("pwsh", $"Could not start PowerShell at '{startInfo.FileName}': {ex.Message}", ex);
        }

        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        var timedOut = false;
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            KillTree(process);
            if (!timedOut)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        var (standardOutput, standardError) = await DrainAsync(stdoutTask, stderrTask).ConfigureAwait(false);
        stopwatch.Stop();

        var exitCode = -1;
        try
        {
            if (process.HasExited)
            {
                exitCode = process.ExitCode;
            }
        }
        catch (InvalidOperationException)
        {
            // Process handle already gone; keep -1.
        }

        return new ProcessOutcome(timedOut ? -1 : exitCode, standardOutput, standardError, stopwatch.Elapsed, timedOut);
    }

    private static async Task<(string StandardOutput, string StandardError)> DrainAsync(Task<string> stdoutTask, Task<string> stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(StreamDrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // An orphaned grandchild still holds a pipe handle; return whatever completed.
        }

        var stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty;
        var stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty;
        return (stdout, stderr);
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and the kill.
        }
        catch (Win32Exception)
        {
            // Access denied or already terminating; the wait below bounds the damage.
        }

        try
        {
            process.WaitForExit((int)KillWaitTimeout.TotalMilliseconds);
        }
        catch (InvalidOperationException)
        {
            // No process associated any more.
        }
    }
}
