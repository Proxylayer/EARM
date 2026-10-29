using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using EArm.Cli.Commands;
using EArm.Engine.Timing;

namespace EArm.Cli.Output;

/// <summary>
/// The <c>--timings</c> table: one row per phase of the run (milliseconds, Graph HTTP requests, request-scheduler wait), the time from the process start to
/// the first Graph write, and the total. Written to stderr whatever the log level and the output format, so <c>-o json</c> keeps one document on stdout.
/// </summary>
internal static class RunTimingsWriter
{
    /// <summary>
    /// Returns a scope that writes the table of <paramref name="invocation"/>'s timings to <paramref name="stderr"/> when disposed, or <c>null</c> when the run did
    /// not ask for <c>--timings</c>. Declare it first in the verb so it is disposed last, after the run's services.
    /// </summary>
    public static IDisposable? Begin(DeploymentInvocation? invocation, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(stderr);
        return invocation is { ShowTimings: true } ? new Report(invocation.Timings, stderr) : null;
    }

    public static void Write(RunTimings timings, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(timings);
        ArgumentNullException.ThrowIfNull(writer);
        var phases = timings.Phases;
        long startup = StartupMilliseconds(timings);
        int nameWidth = Math.Max(24, phases.Select(p => p.Name.Length).DefaultIfEmpty(0).Max() + 2);

        writer.WriteLine("Timings");
        writer.WriteLine(Row("Phase", "ms", "Graph requests", "wait ms", nameWidth));
        if (startup >= 0)
        {
            writer.WriteLine(Row("startup", Number(startup), string.Empty, string.Empty, nameWidth));
        }

        foreach (var phase in phases)
        {
            writer.WriteLine(Row(phase.Name, Number(phase.ElapsedMs), Number(phase.Requests), Number(phase.SchedulerWaitMs), nameWidth));
        }

        if (timings.FirstWriteAfter is { } firstWrite)
        {
            writer.WriteLine(Row("first write at", Number(Math.Max(0, startup) + (long)firstWrite.TotalMilliseconds), string.Empty, string.Empty, nameWidth));
        }

        var requests = timings.Requests;
        writer.WriteLine(Row("total", Number(Math.Max(0, startup) + (long)timings.Elapsed.TotalMilliseconds), Number(requests.Sent), Number((long)requests.SchedulerWait.TotalMilliseconds), nameWidth));
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  at most {requests.MaxInFlight} Graph request(s) in flight; 'first write at' and 'total' count from the process start"));
    }

    private static string Row(string phase, string ms, string requests, string wait, int nameWidth)
    {
        return ("  " + phase.PadRight(nameWidth) + ms.PadLeft(8) + requests.PadLeft(16) + wait.PadLeft(9)).TrimEnd();
    }

    private static string Number(long value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Milliseconds from the process start to the start of the run; -1 when the platform does not tell the process start.</summary>
    private static long StartupMilliseconds(RunTimings timings)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var milliseconds = (long)(timings.StartedUtc - process.StartTime.ToUniversalTime()).TotalMilliseconds;
            return milliseconds >= 0 ? milliseconds : -1;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            return -1;
        }
    }

    private sealed class Report(RunTimings timings, TextWriter stderr) : IDisposable
    {
        public void Dispose()
        {
            Write(timings, stderr);
        }
    }
}
