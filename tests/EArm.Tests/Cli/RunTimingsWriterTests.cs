using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EArm.Cli;
using EArm.Cli.Commands;
using EArm.Cli.Hosting;
using EArm.Cli.Output;
using EArm.Engine.Timing;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// The <c>--timings</c> table (<see cref="RunTimingsWriter"/>): a title, a header, the <c>startup</c> row, one row per phase in start order, the
/// <c>first write at</c> row only when the run wrote, the <c>total</c> row and the footer. Columns: the phase name padded to the widest name (at least 24),
/// then milliseconds (8), Graph requests (16) and scheduler wait (9), right-aligned; empty trailing cells are trimmed.
/// </summary>
public sealed class RunTimingsWriterTests
{
    private const string Footer = "'first write at' and 'total' count from the process start";

    private static string Row(string phase, string ms, string requests, string wait, int nameWidth = 24)
    {
        return ("  " + phase.PadRight(nameWidth) + ms.PadLeft(8) + requests.PadLeft(16) + wait.PadLeft(9)).TrimEnd();
    }

    private static string N(long value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string[] Lines(RunTimings timings)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        RunTimingsWriter.Write(timings, writer);
        return writer.ToString().Split(writer.NewLine);
    }

    private static void Requests(RunTimings timings, int count, bool write = false)
    {
        for (int i = 0; i < count; i++)
        {
            timings.Requests.Started(write);
            timings.Requests.Finished();
        }
    }

    private static RunTimings ReadOnlyRun()
    {
        var timings = new RunTimings();
        using (timings.Measure("load template"))
        {
        }

        using (timings.Measure("sign-in and preflight"))
        {
            Requests(timings, 1);
        }

        using (timings.Measure("what-if reads"))
        {
            Requests(timings, 12);
            timings.Requests.SchedulerWaited(TimeSpan.FromMilliseconds(30));
        }

        return timings;
    }

    [Fact]
    public void TheTable_HasATitle_AHeader_TheStartupRow_ThePhasesInOrder_TheTotal_AndTheFooter()
    {
        var timings = ReadOnlyRun();

        var lines = Lines(timings);

        Assert.Equal("Timings", lines[0]);
        Assert.Equal(Row("Phase", "ms", "Graph requests", "wait ms"), lines[1]);
        Assert.Equal("  Phase                         ms  Graph requests  wait ms", lines[1]);

        // startup: process start to the run's start (no request or wait cells).
        Assert.Matches(new Regex(@"^  startup {17}\s*\d+$"), lines[2]);
        var startupMs = long.Parse(lines[2].Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture);
        Assert.Equal(Row("startup", N(startupMs), string.Empty, string.Empty), lines[2]);

        var phases = timings.Phases;
        Assert.Equal(3, phases.Count);
        for (int i = 0; i < phases.Count; i++)
        {
            Assert.Equal(Row(phases[i].Name, N(phases[i].ElapsedMs), N(phases[i].Requests), N(phases[i].SchedulerWaitMs)), lines[3 + i]);
        }

        Assert.Equal(Row("what-if reads", N(phases[2].ElapsedMs), "12", "30"), lines[5]);

        // total: startup + the run's elapsed time, every request, the whole scheduler wait.
        Assert.StartsWith("  total ", lines[6], StringComparison.Ordinal);
        var total = lines[6].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["total", "13", "30"], [total[0], total[2], total[3]]);
        Assert.True(long.Parse(total[1], CultureInfo.InvariantCulture) >= startupMs);

        Assert.Equal("  at most 1 Graph request(s) in flight; " + Footer, lines[7]);
        Assert.Equal(string.Empty, lines[8]);
        Assert.Equal(9, lines.Length);
    }

    [Fact]
    public void TheFirstWriteRow_AppearsOnlyWhenTheRunWrote_BetweenThePhasesAndTheTotal()
    {
        var readOnly = Lines(ReadOnlyRun());
        Assert.DoesNotContain(readOnly, l => l.TrimStart().StartsWith("first write at", StringComparison.Ordinal));

        var timings = ReadOnlyRun();
        using (timings.Measure("apply"))
        {
            Requests(timings, 1);
            Requests(timings, 2, write: true);
        }

        var lines = Lines(timings);

        int apply = Array.FindIndex(lines, l => l.StartsWith("  apply ", StringComparison.Ordinal));
        int firstWrite = Array.FindIndex(lines, l => l.StartsWith("  first write at ", StringComparison.Ordinal));
        int total = Array.FindIndex(lines, l => l.StartsWith("  total ", StringComparison.Ordinal));
        Assert.True(apply > 0 && firstWrite == apply + 1 && total == firstWrite + 1, string.Join(Environment.NewLine, lines));

        // Counted from the process start like 'total' (startup + time into the run), with no request or wait cells.
        var cells = lines[firstWrite].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["first", "write", "at"], cells[..3]);
        Assert.Single(cells[3..]);
        long startup = long.Parse(lines[2].Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture);
        long firstWriteMs = long.Parse(cells[3], CultureInfo.InvariantCulture);
        long totalMs = long.Parse(lines[total].Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture);
        Assert.InRange(firstWriteMs, startup, totalMs);
        Assert.Equal(Row("first write at", cells[3], string.Empty, string.Empty), lines[firstWrite]);
        Assert.Equal("16", lines[total].Split(' ', StringSplitOptions.RemoveEmptyEntries)[2]);
    }

    [Fact]
    public void ALongPhaseName_WidensTheNameColumn_ForEveryRow_SoTheNumbersStayAligned()
    {
        var timings = new RunTimings();
        const string longName = "a phase name that is longer than 24";
        using (timings.Measure("short"))
        {
            Requests(timings, 1);
        }

        using (timings.Measure(longName))
        {
            Requests(timings, 22);
        }

        var lines = Lines(timings);
        int width = longName.Length + 2;

        Assert.Equal(Row("Phase", "ms", "Graph requests", "wait ms", width), lines[1]);
        var phases = timings.Phases;
        Assert.Equal(Row("short", N(phases[0].ElapsedMs), "1", "0", width), lines[3]);
        Assert.Equal(Row(longName, N(phases[1].ElapsedMs), "22", "0", width), lines[4]);

        // Every row with a wait cell ends at the same column; the ms cell ends at the same column in every row.
        var full = lines.Skip(1).Where(l => l.StartsWith("  ", StringComparison.Ordinal) && !l.StartsWith("  at most", StringComparison.Ordinal)).ToList();
        int msEnd = 2 + width + 8;
        Assert.All(full, l => Assert.True(l.Length >= msEnd && l[msEnd - 1] != ' ' && (l.Length == msEnd || l[msEnd] == ' '), "ms column misaligned: '" + l + "'"));
        Assert.All(full.Where(l => l.Length > msEnd), l => Assert.Equal(msEnd + 16 + 9, l.Length));
    }

    [Fact]
    public void ARunWithoutPhases_StillPrintsTheTotalAndTheFooter()
    {
        var lines = Lines(new RunTimings());

        Assert.Equal("Timings", lines[0]);
        Assert.StartsWith("  startup ", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("  total ", lines[3], StringComparison.Ordinal);
        Assert.Equal(["0", "0"], lines[3].Split(' ', StringSplitOptions.RemoveEmptyEntries)[2..]);
        Assert.Equal("  at most 0 Graph request(s) in flight; " + Footer, lines[4]);
    }

    [Fact]
    public void TheFooter_ReportsThePeakOfRequestsInFlight()
    {
        var timings = new RunTimings();
        timings.Requests.Started(false);
        timings.Requests.Started(false);
        timings.Requests.Started(false);
        timings.Requests.Finished();
        timings.Requests.Finished();
        timings.Requests.Finished();

        Assert.Contains(Lines(timings), l => l.StartsWith("  at most 3 Graph request(s) in flight; ", StringComparison.Ordinal));
    }

    [Fact]
    public void Begin_ReturnsAReport_OnlyWhenTheRunAskedForTimings_AndWritesTheTableWhenDisposed()
    {
        var asked = new DeploymentInvocation { TemplateFile = "t.entra.json", Output = OutputFormat.Json, Connection = new ConnectionSettings(), ShowTimings = true };
        var notAsked = asked with { ShowTimings = false };
        using var stderr = new StringWriter(CultureInfo.InvariantCulture);

        Assert.Null(RunTimingsWriter.Begin(null, stderr));
        Assert.Null(RunTimingsWriter.Begin(notAsked, stderr));

        var report = RunTimingsWriter.Begin(asked, stderr);
        Assert.NotNull(report);
        using (asked.Timings.Measure("load template"))
        {
        }

        Assert.Equal(string.Empty, stderr.ToString());

        report!.Dispose();

        Assert.StartsWith("Timings" + stderr.NewLine, stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("  load template ", stderr.ToString(), StringComparison.Ordinal);
    }
}
