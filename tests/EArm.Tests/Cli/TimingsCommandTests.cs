using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using EArm.TestSupport;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// <c>--timings</c> on the deployment verbs (docs/cli.md): the phase table goes to standard error when the run ends, whatever the output format, so
/// <c>-o json</c> keeps one document on standard output; without the flag nothing is printed; <c>validate</c> prints it only at the online levels.
/// </summary>
public sealed class TimingsCommandTests
{
    private static readonly string[] WhatIfPhases =
    [
        "load template", "run scopes", "validate", "sign-in and preflight", "tenant size (overlapped)", "plan: expand and graph", "what-if reads", "edge pass", "orphan report",
    ];

    private static string Groups(CliSandbox sandbox, int count = 2)
    {
        var resources = Enumerable.Range(0, count).Select(i =>
            "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"timed-group-" + i + "\", \"properties\": { \"displayName\": \"Timed group " + i + "\", \"mailEnabled\": false, \"mailNickname\": \"timed-group-" + i + "\", \"securityEnabled\": true } }");
        return sandbox.WriteTemplate("groups.entra.json", "\"resources\": [ " + string.Join(", ", resources) + " ]");
    }

    /// <summary>The table's lines (from the "Timings" title to the footer), or an empty list when stderr has none.</summary>
    private static IReadOnlyList<string> Table(string stderr)
    {
        var lines = stderr.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int start = Array.IndexOf(lines, "Timings");
        if (start < 0)
        {
            return [];
        }

        int end = Array.FindIndex(lines, start, l => l.StartsWith("  at most ", StringComparison.Ordinal));
        return lines[start..(end + 1)];
    }

    /// <summary>The phase names of the table, in table order (startup and total included).</summary>
    private static List<string> RowNames(IReadOnlyList<string> table)
    {
        return table.Skip(2).Take(table.Count - 3).Select(l => l[2..].Split("  ", StringSplitOptions.RemoveEmptyEntries)[0].Trim()).ToList();
    }

    private static string[] Cells(IReadOnlyList<string> table, string row)
    {
        string line = table.Single(l => l.StartsWith("  " + row + " ", StringComparison.Ordinal));
        return line[(2 + row.Length)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public async Task WhatIfWithTimings_KeepsTheJsonDocumentOnStdout_AndPrintsThePhaseTableOnStderr()
    {
        using var sandbox = new CliSandbox(nameof(WhatIfWithTimings_KeepsTheJsonDocumentOnStdout_AndPrintsThePhaseTableOnStderr));
        sandbox.Handler.Directory = new FakeDirectory();
        string template = Groups(sandbox);

        var result = await CliRunner.RunAsync("deployment", "what-if", "-f", template, "--timings", "-o", "json");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, result.Json["summary"]!["create"]!.GetValue<int>());
        Assert.DoesNotContain("Timings", result.StandardOutput, StringComparison.Ordinal);

        var table = Table(result.StandardError);
        Assert.NotEmpty(table);
        var names = RowNames(table);
        Assert.Equal("startup", names[0]);
        Assert.Equal("total", names[^1]);
        Assert.All(WhatIfPhases, phase => Assert.Contains(phase, names));
        Assert.DoesNotContain("Key Vault secrets", names);
        Assert.DoesNotContain("apply", names);
        Assert.DoesNotContain("first write at", names);

        // The phases that run one after another are listed in that order.
        string[] serial = ["load template", "run scopes", "validate", "sign-in and preflight", "what-if reads", "edge pass", "orphan report"];
        Assert.Equal(serial, names.Where(serial.Contains));
        Assert.True(names.IndexOf("tenant size (overlapped)") > names.IndexOf("sign-in and preflight"), string.Join(" | ", names));

        // The preflight is one request; the total counts every Graph request the fake received.
        Assert.Equal("1", Cells(table, "sign-in and preflight")[1]);
        Assert.Equal(sandbox.RecordedRequests().Count.ToString(CultureInfo.InvariantCulture), Cells(table, "total")[1]);
        Assert.Matches(@"^  at most \d+ Graph request\(s\) in flight; ", table[^1]);
    }

    [Theory]
    [InlineData("what-if")]
    [InlineData("create")]
    public async Task WithoutTimings_NoTableIsPrinted(string verb)
    {
        using var sandbox = new CliSandbox(nameof(WithoutTimings_NoTableIsPrinted) + verb);
        sandbox.Handler.Directory = new FakeDirectory();
        string template = Groups(sandbox);

        var result = verb == "create"
            ? await CliRunner.RunAsync("deployment", verb, "-f", template, "-y", "-o", "json")
            : await CliRunner.RunAsync("deployment", verb, "-f", template, "-o", "json");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(Table(result.StandardError));
        Assert.DoesNotContain("Timings", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Timings", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateWithTimings_AddsTheApplyPhase_AndTheFirstWriteRow()
    {
        using var sandbox = new CliSandbox(nameof(CreateWithTimings_AddsTheApplyPhase_AndTheFirstWriteRow));
        sandbox.Handler.Directory = new FakeDirectory();
        string template = Groups(sandbox);

        var result = await CliRunner.RunAsync("deployment", "create", "-f", template, "-y", "--timings", "-o", "json");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Succeeded", result.Json["status"]!.GetValue<string>());
        var names = RowNames(Table(result.StandardError));
        Assert.Contains("apply", names);
        Assert.Equal(["apply", "first write at", "total"], names[^3..]);
        int writes = sandbox.RecordedRequests().Count(r => r.Method != "GET");
        Assert.True(writes > 0);
        Assert.True(int.Parse(Cells(Table(result.StandardError), "apply")[1], CultureInfo.InvariantCulture) >= writes);
    }

    [Fact]
    public async Task DeleteWithTimings_TimesTheSearchForTheDeclaredObjects()
    {
        using var sandbox = new CliSandbox(nameof(DeleteWithTimings_TimesTheSearchForTheDeclaredObjects));
        sandbox.Handler.Directory = new FakeDirectory();
        string template = Groups(sandbox);
        Assert.Equal(0, (await CliRunner.RunAsync("deployment", "create", "-f", template, "-y", "-o", "json")).ExitCode);

        var result = await CliRunner.RunAsync("deployment", "delete", "-f", template, "-y", "--timings", "-o", "json");

        Assert.Equal(0, result.ExitCode);
        var names = RowNames(Table(result.StandardError));
        Assert.Contains("find declared objects", names);
        Assert.Contains("delete", names);
        Assert.Contains("first write at", names);
        Assert.True(names.IndexOf("find declared objects") < names.IndexOf("delete"), string.Join(" | ", names));
        Assert.Equal(0, sandbox.Handler.Directory!.Count("/groups"));
    }

    // ValidateCommand passes no invocation at the offline level (it makes no Graph request), so --timings prints nothing there.
    [Theory]
    [InlineData("Template", false)]
    [InlineData("Provider", true)]
    [InlineData("ProviderNoRbac", true)]
    public async Task Validate_PrintsTheTableOnlyAtTheOnlineLevels(string level, bool printed)
    {
        using var sandbox = new CliSandbox(nameof(Validate_PrintsTheTableOnlyAtTheOnlineLevels) + level);
        sandbox.Handler.Directory = new FakeDirectory();
        string template = Groups(sandbox);

        var result = await CliRunner.RunAsync("deployment", "validate", "-f", template, "--validation-level", level, "--timings", "-o", "json");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, result.Json["errorCount"]!.GetValue<int>());
        var table = Table(result.StandardError);
        if (printed)
        {
            var names = RowNames(table);
            Assert.Contains("sign-in and preflight", names);
            Assert.Contains("what-if reads", names);
            Assert.NotEmpty(sandbox.RecordedRequests());
        }
        else
        {
            Assert.Empty(table);
            Assert.Empty(sandbox.RecordedRequests());
        }
    }

    [Fact]
    public async Task ValidateWithoutTimings_PrintsNoTable_EvenOnline()
    {
        using var sandbox = new CliSandbox(nameof(ValidateWithoutTimings_PrintsNoTable_EvenOnline));
        sandbox.Handler.Directory = new FakeDirectory();
        string template = Groups(sandbox);

        var result = await CliRunner.RunAsync("deployment", "validate", "-f", template, "--validation-level", "Provider", "-o", "json");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(Table(result.StandardError));
    }

    [Fact]
    public async Task AFailedRun_StillPrintsTheTable()
    {
        using var sandbox = new CliSandbox(nameof(AFailedRun_StillPrintsTheTable));
        sandbox.UseNotLoggedIn();
        string template = Groups(sandbox);

        var result = await CliRunner.RunAsync("deployment", "what-if", "-f", template, "--timings", "-o", "json");

        Assert.Equal(3, result.ExitCode);
        Assert.NotNull(result.Json);
        var names = RowNames(Table(result.StandardError));
        Assert.Contains("load template", names);
        Assert.DoesNotContain("what-if reads", names);
        Assert.Equal("total", names[^1]);
    }
}
