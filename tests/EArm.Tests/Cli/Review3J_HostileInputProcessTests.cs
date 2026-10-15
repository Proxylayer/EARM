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
/// Review 3J, findings 2, 3, 6 and 8: the hostile inputs of the second security review, each run as <c>earm deployment validate</c> in a child process
/// (a runaway allocation or a hang cannot take the test host down) with the time and the peak working set measured. Before the fixes:
/// <c>format('{0:D999999999}', 5)</c> 22 s / 9.6 GB, three of them 29 s / 11.5 GB, <c>lastIndexOf</c> over 200,000 characters 12 s (1 MiB: more than
/// two minutes), 400 variables of 1,000,000 characters 821 MB, a 38 MB template 35 s / 5 GB.
/// </summary>
public sealed class Review3J_HostileInputProcessTests
{
    private const long ModestCeiling = 250L * 1024 * 1024;

    private static string Run(string testName, string body, out Review1C.ChildResult result, long ceilingBytes, TimeSpan? timeout = null)
    {
        using var directory = new TestDirectory(testName);
        string path = directory.File("hostile.entra.json");
        File.WriteAllText(path, Review1C.Template(body), new UTF8Encoding(false));

        result = Review1C.RunChild(["deployment", "validate", "-f", path, "-o", "json"], timeout ?? TimeSpan.FromSeconds(60));

        Assert.True(result.PeakWorkingSetBytes < ceilingBytes, $"peak working set {result.PeakWorkingSetBytes / (1024 * 1024)} MB (ceiling {ceilingBytes / (1024 * 1024)} MB)");
        Assert.Equal(string.Empty, result.StandardError);
        return result.StandardOutput;
    }

    private static string[] Codes(string output)
    {
        return JsonNode.Parse(output)!["diagnostics"]!.AsArray().Select(d => d!["code"]!.GetValue<string>()).ToArray();
    }

    [Theory]
    [InlineData("{0:D999999999}")]
    [InlineData("{0:N999999999}{0:D999999999}{0:X999999999}")]
    [InlineData("{0:F2147483647}")]
    public void AHugeFormatPrecision_IsAValidationError_WithoutAllocating(string format)
    {
        string body = "\"variables\": { \"a\": \"[format('" + format + "', 5)]\" }, \"resources\": [], \"outputs\": { \"o\": { \"type\": \"int\", \"value\": \"[length(variables('a'))]\" } }";

        string output = Run(nameof(AHugeFormatPrecision_IsAValidationError_WithoutAllocating), body, out var result, ModestCeiling);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("InvalidFunctionArguments", Codes(output));
    }

    [Fact]
    public void ThousandsOfPaddedFormatItems_AreAValidationError_WithoutAllocating()
    {
        string items = string.Concat(Enumerable.Repeat("{0,1000000}", 2000));
        string body = "\"variables\": { \"a\": \"[format('" + items + "', 'x')]\" }, \"resources\": [], \"outputs\": { \"o\": { \"type\": \"int\", \"value\": \"[length(variables('a'))]\" } }";

        string output = Run(nameof(ThousandsOfPaddedFormatItems_AreAValidationError_WithoutAllocating), body, out var result, ModestCeiling);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("EvaluationBudgetExceeded", Codes(output));
    }

    [Theory]
    [InlineData(200_000)]
    [InlineData(1_000_000)]
    [Trait("Category", "Timing")]
    public void ALastIndexOf_OverALongString_FinishesQuickly(int length)
    {
        // The needle is half the length of the text and differs from it only at its end: the quadratic search compared it at every position.
        string body = "\"variables\": { \"s\": \"[format('{0," + length + "}', '')]\", \"needle\": \"[concat(format('{0," + (length / 2) + "}', ''), 'b')]\", \"r\": \"[lastIndexOf(variables('s'), variables('needle'))]\" }, \"resources\": [], \"outputs\": { \"o\": { \"type\": \"int\", \"value\": \"[variables('r')]\" } }";

        Run(nameof(ALastIndexOf_OverALongString_FinishesQuickly) + length, body, out var result, ModestCeiling);

        Assert.Equal(0, result.ExitCode);
        // Category=Timing: the quadratic search needs hours here, the linear one a fraction of a second; the bound only has to separate the two.
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(120), "took " + result.Elapsed);
    }

    [Fact]
    public void FourHundredVariablesOfAMillionCharacters_StayUnderFourHundredMegabytes()
    {
        var variables = string.Join(", ", Enumerable.Range(1, 400).Select(i => $"\"v{i}\": \"[format('{{0,1000000}}', 'x')]\""));
        var outputs = string.Join(", ", Enumerable.Range(1, 400).Select(i => $"\"o{i}\": {{ \"type\": \"int\", \"value\": \"[length(variables('v{i}'))]\" }}"));
        string body = "\"variables\": { " + variables + " }, \"resources\": [], \"outputs\": { " + outputs + " }";

        string output = Run(nameof(FourHundredVariablesOfAMillionCharacters_StayUnderFourHundredMegabytes), body, out var result, 400L * 1024 * 1024);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("EvaluationBudgetExceeded", Codes(output));
    }

    // ---- file size ----

    [Fact]
    public void ATemplateOverEightMebibytes_IsRefusedBeforeItIsRead()
    {
        using var directory = new TestDirectory(nameof(ATemplateOverEightMebibytes_IsRefusedBeforeItIsRead));
        string path = directory.File("huge.entra.json");
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            byte[] head = Encoding.UTF8.GetBytes(Review1C.Template("\"resources\": []").TrimEnd('}', ' ') + " ");
            stream.Write(head);
            byte[] chunk = Encoding.UTF8.GetBytes(new string(' ', 1 << 20));
            for (int i = 0; i < 10; i++)
            {
                stream.Write(chunk);
            }

            stream.Write("}"u8);
        }

        var result = Review1C.RunChild(["deployment", "validate", "-f", path, "-o", "json"], TimeSpan.FromSeconds(60));

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("InputTooLarge", JsonNode.Parse(result.StandardOutput)!["diagnostics"]![0]!["code"]!.GetValue<string>());
        Assert.True(result.PeakWorkingSetBytes < ModestCeiling, "peak " + result.PeakWorkingSetBytes / (1024 * 1024) + " MB");
    }

    [Fact]
    public void ADirectoryAsTheTemplate_SaysItIsADirectory()
    {
        using var directory = new TestDirectory(nameof(ADirectoryAsTheTemplate_SaysItIsADirectory));

        var result = Review1C.RunChild(["deployment", "validate", "-f", directory.Path], TimeSpan.FromSeconds(60));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("is a directory", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("was not found", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void AUtf16Template_IsNamedAsUtf16_NotAsAByteLevelSyntaxError()
    {
        using var directory = new TestDirectory(nameof(AUtf16Template_IsNamedAsUtf16_NotAsAByteLevelSyntaxError));
        string path = directory.File("wide.entra.json");
        File.WriteAllText(path, Review1C.Template("\"resources\": []"), Encoding.Unicode);

        var result = Review1C.RunChild(["deployment", "validate", "-f", path], TimeSpan.FromSeconds(60));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("must be UTF-8 (UTF-16 detected)", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("0xFF", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void AMistypedTimeout_IsAUsageError_InARealProcessToo()
    {
        using var directory = new TestDirectory(nameof(AMistypedTimeout_IsAUsageError_InARealProcessToo));
        string path = directory.File("ok.entra.json");
        File.WriteAllText(path, Review1C.Template("\"resources\": []"), new UTF8Encoding(false));
        var environment = new Dictionary<string, string> { ["EARM_EVALUATION_TIMEOUT"] = "1e18" };

        var flag = Review1C.RunChild(["deployment", "validate", "-f", path, "--evaluation-timeout", "abc"], TimeSpan.FromSeconds(60));
        var variable = Review1C.RunChild(["deployment", "validate", "-f", path], TimeSpan.FromSeconds(60), environment);

        Assert.Equal(1, flag.ExitCode);
        Assert.DoesNotContain("Unexpected error", flag.StandardError, StringComparison.Ordinal);
        Assert.Equal(0, variable.ExitCode);
    }
}
