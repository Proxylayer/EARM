using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// Log event 2090 (<c>Phase {Phase}: {ElapsedMs} ms, {Requests} Graph requests</c>) is Debug: a default what-if writes no phase line to the console log,
/// <c>--verbose</c> (Debug) does. The console logger writes to the process's standard error (not the invocation's), so the test swaps
/// <see cref="Console.Error"/> and runs in the <see cref="ProcessStateCollection"/>, alone.
/// </summary>
[Collection(ProcessStateCollection.Name)]
public sealed partial class PhaseTimingLogTests
{
    [GeneratedRegex(@"Phase [A-Za-z ()-]+: \d+ ms, \d+ Graph requests", RegexOptions.CultureInvariant)]
    private static partial Regex PhaseLine();

    private static async Task<(CliResult Result, string Log)> WhatIfAsync(CliSandbox sandbox, params string[] extra)
    {
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();
        var previous = Console.Error;
        using var log = new StringWriter();
        Console.SetError(log);
        try
        {
            // The logger factory is disposed before the run returns, which flushes the console logger's queue.
            var result = await CliRunner.RunAsync(["deployment", "what-if", "-f", template, .. extra]);
            return (result, log.ToString());
        }
        finally
        {
            Console.SetError(previous);
        }
    }

    [Fact]
    public async Task ADefaultRun_LogsNoPhaseLine()
    {
        using var sandbox = new CliSandbox(nameof(ADefaultRun_LogsNoPhaseLine));

        var (result, log) = await WhatIfAsync(sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotMatch(PhaseLine(), log);
        Assert.DoesNotMatch(PhaseLine(), result.StandardError);
        Assert.DoesNotMatch(PhaseLine(), result.StandardOutput);
    }

    [Fact]
    public async Task AVerboseRun_LogsEachPhase_AtDebug()
    {
        using var sandbox = new CliSandbox(nameof(AVerboseRun_LogsEachPhase_AtDebug));

        var (result, log) = await WhatIfAsync(sandbox, "--verbose");

        Assert.Equal(0, result.ExitCode);
        var phases = PhaseLine().Matches(log).Select(m => m.Value).ToList();
        Assert.Contains(phases, p => p.StartsWith("Phase load template: ", StringComparison.Ordinal));
        Assert.Contains(phases, p => p.StartsWith("Phase sign-in and preflight: ", StringComparison.Ordinal));
    }
}
