using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using EArm.Tests.Engine;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// Review 1C, findings I-04 and S-04: hostile templates took 8-13 GB of memory (and, for a huge <c>copy.count</c>, the process) before anything
/// stopped them. Each of them now ends as a validation error within a bounded working set. They run in a child process so that a regression
/// (an out-of-memory failure, a stack overflow) cannot take the test host down, and the peak working set of the child is measured.
/// </summary>
public sealed class Review1C_HostileMemoryTests
{
    /// <summary>The ceiling for the child's peak working set: the runtime alone needs ~50 MB, the hostile templates took 8-13 GB.</summary>
    private const long CeilingBytes = 700L * 1024 * 1024;

    private static string Doubling()
    {
        var sb = new StringBuilder("\"variables\": { \"v0\": \"aaaaaaaaaaaaaaaa\"");
        for (int i = 1; i <= 28; i++)
        {
            sb.Append(", \"v").Append(i).Append("\": \"[concat(variables('v").Append(i - 1).Append("'), variables('v").Append(i - 1).Append("'))]\"");
        }

        return sb.Append(" }, \"resources\": [], \"outputs\": { \"o\": { \"type\": \"int\", \"value\": \"[length(variables('v28'))]\" } }").ToString();
    }

    private static string Copy(string count)
    {
        return "\"resources\": [ { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"[concat('g', copyIndex())]\", \"copy\": { \"name\": \"c\", \"count\": " + count + " }, \"properties\": { \"displayName\": \"g\", \"mailEnabled\": false, \"mailNickname\": \"g\", \"securityEnabled\": true } } ]";
    }

    private static string NestedLambdas(int size)
    {
        return "\"variables\": { \"a\": \"[length(flatten(map(range(0," + size + "), lambda('i', map(range(0," + size + "), lambda('j', add(lambdaVariables('i'), lambdaVariables('j'))))))))]\" }, \"resources\": [], \"outputs\": { \"o\": { \"type\": \"int\", \"value\": \"[variables('a')]\" } }";
    }

    public static IEnumerable<object[]> HostileTemplates()
    {
        yield return ["copy-3M", Copy("3000000"), "ExpansionLimitExceeded"];
        yield return ["copy-100M", Copy("100000000"), "ExpansionLimitExceeded"];
        yield return ["string-doubling", Doubling(), "EvaluationBudgetExceeded"];
        yield return ["format-alignment", "\"variables\": { \"a\": \"[format('{0,1000000000}', 'x')]\", \"b\": \"[length(variables('a'))]\" }, \"resources\": [], \"outputs\": { \"o\": { \"type\": \"int\", \"value\": \"[variables('b')]\" } }", "EvaluationBudgetExceeded"];
        yield return ["replace-growth", "\"variables\": { \"a\": \"[replace(replace(replace(replace(replace(replace('aaaaaaaaaaaaaaaa', 'a', 'aaaaaaaaaaaaaaaa'), 'a', 'aaaaaaaaaaaaaaaa'), 'a', 'aaaaaaaaaaaaaaaa'), 'a', 'aaaaaaaaaaaaaaaa'), 'a', 'aaaaaaaaaaaaaaaa'), 'a', 'aaaaaaaaaaaaaaaa')]\" }, \"resources\": [], \"outputs\": { \"o\": { \"type\": \"int\", \"value\": \"[length(variables('a'))]\" } }", "EvaluationBudgetExceeded"];
        yield return ["array-doubling", "\"variables\": { \"v0\": \"[range(0, 10000)]\", \"v1\": \"[concat(variables('v0'), variables('v0'))]\", \"v2\": \"[concat(variables('v1'), variables('v1'))]\", \"v3\": \"[concat(variables('v2'), variables('v2'))]\", \"v4\": \"[concat(variables('v3'), variables('v3'))]\", \"v5\": \"[concat(variables('v4'), variables('v4'))]\", \"v6\": \"[concat(variables('v5'), variables('v5'))]\", \"v7\": \"[concat(variables('v6'), variables('v6'))]\", \"v8\": \"[concat(variables('v7'), variables('v7'))]\", \"v9\": \"[concat(variables('v8'), variables('v8'))]\", \"v10\": \"[concat(variables('v9'), variables('v9'))]\", \"v11\": \"[concat(variables('v10'), variables('v10'))]\", \"v12\": \"[concat(variables('v11'), variables('v11'))]\" }, \"resources\": []", "EvaluationBudgetExceeded"];
        yield return ["nested-lambdas-1000", NestedLambdas(1000), "EvaluationBudgetExceeded"];
    }

    [Theory]
    [MemberData(nameof(HostileTemplates))]
    public void AHostileTemplate_EndsAsAValidationError_WithinTheMemoryCeiling(string name, string body, string expectedCode)
    {
        using var directory = new TestDirectory("hostile-" + name);
        string path = directory.File(name + ".entra.json");
        File.WriteAllText(path, Review1C.Template(body), new UTF8Encoding(false));

        var result = Review1C.RunChild(["deployment", "validate", "-f", path, "-o", "json"], TimeSpan.FromSeconds(90));

        Assert.True(result.PeakWorkingSetBytes < CeilingBytes, $"{name}: peak working set {result.PeakWorkingSetBytes / (1024 * 1024)} MB (ceiling {CeilingBytes / (1024 * 1024)} MB)");
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        var report = JsonNode.Parse(result.StandardOutput)!.AsObject();
        Assert.Contains(report["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == expectedCode);
        Assert.DoesNotContain("OutOfMemory", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Timing")]
    public void ANestedLambdaLoop_IsStoppedByTheTimeoutOption_WithinTheMemoryCeiling()
    {
        using var directory = new TestDirectory(nameof(ANestedLambdaLoop_IsStoppedByTheTimeoutOption_WithinTheMemoryCeiling));
        string path = directory.File("loop.entra.json");
        File.WriteAllText(path, Review1C.Template(NestedLambdas(10_000)), new UTF8Encoding(false));

        var result = Review1C.RunChild(["deployment", "validate", "-f", path, "-o", "json", "--evaluation-timeout", "2"], TimeSpan.FromSeconds(90));

        Assert.True(result.PeakWorkingSetBytes < CeilingBytes, "peak working set " + result.PeakWorkingSetBytes / (1024 * 1024) + " MB");
        Assert.Equal(2, result.ExitCode);
        var report = JsonNode.Parse(result.StandardOutput)!.AsObject();
        var budget = report["diagnostics"]!.AsArray().Select(d => d!.AsObject()).First(d => d["code"]!.GetValue<string>() == "EvaluationBudgetExceeded");
        Assert.Contains("2 seconds", budget["message"]!.GetValue<string>(), StringComparison.Ordinal);
        // Category=Timing: the default budget is 60 s, so finishing well before the 90 s child limit proves the 2 s option was applied.
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(80), "took " + result.Elapsed);
    }

    [Fact]
    public void TheTimeoutEnvironmentVariable_IsHonoured_AndTheOptionWins()
    {
        using var directory = new TestDirectory(nameof(TheTimeoutEnvironmentVariable_IsHonoured_AndTheOptionWins));
        string path = directory.File("loop.entra.json");
        File.WriteAllText(path, Review1C.Template(NestedLambdas(10_000)), new UTF8Encoding(false));
        var environment = new Dictionary<string, string> { ["EARM_EVALUATION_TIMEOUT"] = "1" };

        var fromEnvironment = Review1C.RunChild(["deployment", "validate", "-f", path, "-o", "json"], TimeSpan.FromSeconds(90), environment);
        var optionWins = Review1C.RunChild(["deployment", "validate", "-f", path, "-o", "json", "--evaluation-timeout", "3"], TimeSpan.FromSeconds(90), environment);

        Assert.Contains("1 seconds", fromEnvironment.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("3 seconds", optionWins.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExitedHostileRun_ReportsNoStackTrace_AndNoBuildPath()
    {
        using var directory = new TestDirectory(nameof(AnExitedHostileRun_ReportsNoStackTrace_AndNoBuildPath));
        string path = directory.File("copy.entra.json");
        File.WriteAllText(path, Review1C.Template(Copy("3000000")), new UTF8Encoding(false));

        var result = Review1C.RunChild(["deployment", "validate", "-f", path], TimeSpan.FromSeconds(60));

        Assert.Equal(2, result.ExitCode);
        string all = result.StandardOutput + result.StandardError;
        Assert.DoesNotContain("   at ", all, StringComparison.Ordinal);
        Assert.DoesNotContain(".cs:line", all, StringComparison.Ordinal);
        Assert.Contains("ExpansionLimitExceeded", all, StringComparison.Ordinal);
    }
}
