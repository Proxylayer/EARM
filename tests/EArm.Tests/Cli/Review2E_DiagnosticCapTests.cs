using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// Review 2E: a hostile template with 12,000 variables that each exceed the depth limit used to print 12,000 identical <c>ValueTooDeep</c> diagnostics.
/// The listing is capped per code (the first 50, then one summary line); the error and warning counts and the JSON <c>suppressedCount</c> keep the truth.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class Review2E_DiagnosticCapTests
{
    private const int Chain = 12_000;

    // v0 is a string, v{k} has depth k: v65 .. v{Chain-1} exceed the limit.
    private const int Errors = Chain - 65;

    private static string Write(CliSandbox sandbox, int length)
    {
        return sandbox.WriteTemplate("chain.entra.json", Review1C.VariableChain(length, "createArray") + ", \"resources\": []");
    }

    [Fact]
    public async Task Json_ListsTheFirstFiftyOfACode_AndKeepsTheCountsAndASuppressedCount()
    {
        using var sandbox = new CliSandbox(nameof(Json_ListsTheFirstFiftyOfACode_AndKeepsTheCountsAndASuppressedCount));
        string template = Write(sandbox, Chain);

        var result = await CliRunner.RunAsync("deployment", "validate", "-f", template, "-o", "json");

        Assert.Equal(2, result.ExitCode);
        var diagnostics = result.Json["diagnostics"]!.AsArray();
        Assert.Equal(50, diagnostics.Count(d => d!["code"]!.GetValue<string>() == "ValueTooDeep"));
        Assert.Equal("variables.v65", diagnostics[0]!["path"]!.GetValue<string>());
        Assert.Equal(Errors, result.Json["errorCount"]!.GetValue<int>());
        Assert.Equal(Errors - 50, result.Json["suppressedCount"]!.GetValue<int>());
        var suppressed = Assert.Single(result.Json["suppressed"]!.AsArray());
        Assert.Equal("ValueTooDeep", suppressed!["code"]!.GetValue<string>());
        Assert.Equal("Error", suppressed["severity"]!.GetValue<string>());
        Assert.Equal(Errors - 50, suppressed["count"]!.GetValue<int>());
        Assert.Equal("Failed", result.Json["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Table_PrintsFiftyLines_ThenOneSummaryLine_AndTheTotalInTheFooter()
    {
        using var sandbox = new CliSandbox(nameof(Table_PrintsFiftyLines_ThenOneSummaryLine_AndTheTotalInTheFooter));
        string template = Write(sandbox, Chain);

        var result = await CliRunner.RunAsync("deployment", "validate", "-f", template);

        Assert.Equal(2, result.ExitCode);
        var lines = result.StandardError.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        Assert.Equal(50, lines.Count(l => l.StartsWith("error ValueTooDeep", StringComparison.Ordinal)));
        string summary = Assert.Single(lines, l => l.Contains("more", StringComparison.Ordinal) && l.Contains("of the same code ValueTooDeep", StringComparison.Ordinal));
        Assert.Contains($"{Errors - 50} more", summary, StringComparison.Ordinal);
        Assert.Contains($"Validation failed: {Errors} error(s), 0 warning(s).", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExactlyFiftyOfACode_AreAllListed_WithNoSummary()
    {
        using var sandbox = new CliSandbox(nameof(ExactlyFiftyOfACode_AreAllListed_WithNoSummary));
        string template = Write(sandbox, 65 + 50);

        var result = await CliRunner.RunAsync("deployment", "validate", "-f", template, "-o", "json");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(50, result.Json["errorCount"]!.GetValue<int>());
        Assert.Equal(50, result.Json["diagnostics"]!.AsArray().Count);
        Assert.Equal(0, result.Json["suppressedCount"]!.GetValue<int>());
        Assert.Null(result.Json["suppressed"]);
    }

    [Fact]
    public async Task ADifferentCodeIsNeverHiddenBehindTheCap()
    {
        using var sandbox = new CliSandbox(nameof(ADifferentCodeIsNeverHiddenBehindTheCap));
        string template = sandbox.WriteTemplate("mixed.entra.json", Review1C.VariableChain(300, "createArray") + ", \"resources\": [ " + Review1C.Group("\"g1\"", "{ \"displayName\": \"[variables('nope')]\" }") + " ]");

        var result = await CliRunner.RunAsync("deployment", "validate", "-f", template, "-o", "json");

        var codes = result.Json["diagnostics"]!.AsArray().Select(d => d!["code"]!.GetValue<string>()).ToList();
        Assert.Equal(50, codes.Count(c => c == "ValueTooDeep"));
        Assert.Contains("UndefinedVariable", codes);
        Assert.Equal(300 - 65 - 50, result.Json["suppressedCount"]!.GetValue<int>());
    }
}
