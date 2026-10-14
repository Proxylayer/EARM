using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EntraArm.Tests.Engine;
using Xunit;

namespace EntraArm.Tests;

/// <summary>
/// Hostile but well-formed template text through the real command line: an expression nested thousands of levels deep, JSON nested past
/// the reader limit, and a lone surrogate escape. Each must end as a diagnostic with a normal exit code, never a stack overflow or an
/// unhandled exception.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class HostileTemplateCliTests
{
    private const string Header = "{ \"$schema\": \"https://entraarm.dev/schemas/2026-10-01/deploymentTemplate.json#\", \"contentVersion\": \"1.0.0.0\", ";

    private static string GroupTemplate(string displayNameJson)
    {
        return Header + "\"resources\": [ { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01\", \"name\": \"g\", \"properties\": { \"displayName\": " + displayNameJson + " } } ] }";
    }

    private static string NestedCalls(int depth)
    {
        return "[" + string.Concat(Enumerable.Repeat("or(", depth)) + "true()" + new string(')', depth) + "]";
    }

    private static async Task<CliResult> ValidateAsync(string testName, string templateText, byte[]? bytes = null)
    {
        using var directory = new TestDirectory(testName);
        string path = directory.File("template.entra.json");
        if (bytes is null)
        {
            await File.WriteAllTextAsync(path, templateText, new UTF8Encoding(false), TestContext.Current.CancellationToken);
        }
        else
        {
            await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        }

        return await CliRunner.ValidateJsonAsync(path);
    }

    [Theory]
    [InlineData(150)]
    [InlineData(1_300)]
    [InlineData(5_000)]
    public async Task ADeeplyNestedExpression_ExitsTwo_WithAnExpressionTooDeepDiagnostic(int depth)
    {
        var result = await ValidateAsync(nameof(ADeeplyNestedExpression_ExitsTwo_WithAnExpressionTooDeepDiagnostic) + depth, GroupTemplate("\"" + NestedCalls(depth) + "\""));

        Assert.Equal(2, result.ExitCode);
        var diagnostic = Assert.Single(result.Diagnostics, d => d!["code"]!.GetValue<string>() == "ExpressionTooDeep");
        Assert.Contains("maximum depth of 100", diagnostic!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("displayName", diagnostic["path"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task AnExpressionAtAReasonableDepth_IsNotRejected()
    {
        var result = await ValidateAsync(nameof(AnExpressionAtAReasonableDepth_IsNotRejected), GroupTemplate("\"" + NestedCalls(60) + "\""));

        Assert.DoesNotContain(result.Diagnostics, d => d!["code"]!.GetValue<string>() == "ExpressionTooDeep");
    }

    [Fact]
    public async Task JsonNestedPastTheReaderLimit_IsAnInvalidJsonDiagnostic_NotACrash()
    {
        string nested = string.Concat(Enumerable.Repeat("{ \"n\": ", 5_000)) + "1" + string.Concat(Enumerable.Repeat(" }", 5_000));

        var result = await ValidateAsync(nameof(JsonNestedPastTheReaderLimit_IsAnInvalidJsonDiagnostic_NotACrash), Header + "\"resources\": [], \"metadata\": " + nested + " }");

        Assert.Equal(1, result.ExitCode);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("InvalidJson", diagnostic!["code"]!.GetValue<string>());
        Assert.Contains("depth", diagnostic["message"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task ALoneSurrogateEscape_ExitsTwo_WithAnInvalidJsonDiagnosticNamingThePathAndPosition()
    {
        string template = GroupTemplate("\"bad \\ud800 surrogate\"");

        var result = await ValidateAsync(nameof(ALoneSurrogateEscape_ExitsTwo_WithAnInvalidJsonDiagnosticNamingThePathAndPosition), template);

        Assert.Equal(2, result.ExitCode);
        var diagnostic = Assert.Single(result.Diagnostics, d => d!["code"]!.GetValue<string>() == "InvalidJson");
        Assert.Contains("lone surrogate", diagnostic!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("displayName", diagnostic["path"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(1, diagnostic["line"]!.GetValue<int>());
        Assert.Equal(template.IndexOf("\\ud800", StringComparison.Ordinal) + 1, diagnostic["column"]!.GetValue<int>());
        Assert.DoesNotContain("Unhandled", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task ALoneSurrogateEscape_OnALaterLine_ReportsThatLine()
    {
        string template = GroupTemplate("\"fine\"").Replace(", \"properties\"", ",\n  \"properties\"", StringComparison.Ordinal).Replace("\"fine\"", "\"x\\udfff\"", StringComparison.Ordinal);

        var result = await ValidateAsync(nameof(ALoneSurrogateEscape_OnALaterLine_ReportsThatLine), template);

        Assert.Equal(2, result.ExitCode);
        var diagnostic = Assert.Single(result.Diagnostics, d => d!["code"]!.GetValue<string>() == "InvalidJson");
        Assert.Equal(2, diagnostic!["line"]!.GetValue<int>());
    }

    [Fact]
    public async Task AValidSurrogatePairEscape_IsNotReported()
    {
        var result = await ValidateAsync(nameof(AValidSurrogatePairEscape_IsNotReported), GroupTemplate("\"smile \\ud83d\\ude00 ok\""));

        Assert.DoesNotContain(result.Diagnostics, d => d!["code"]!.GetValue<string>() == "InvalidJson");
        Assert.NotEqual(1, result.ExitCode);
    }

    [Fact]
    public async Task AnEscapedBackslashBeforeU_IsNotASurrogateEscape()
    {
        // The text is backslash-backslash-u-d800 inside the JSON string: a literal backslash followed by the characters "ud800".
        var result = await ValidateAsync(nameof(AnEscapedBackslashBeforeU_IsNotASurrogateEscape), GroupTemplate("\"path \\\\ud800 literal\""));

        Assert.DoesNotContain(result.Diagnostics, d => d!["code"]!.GetValue<string>() == "InvalidJson");
    }

    [Fact]
    public async Task ALoneSurrogateEscape_InAPropertyName_IsReported()
    {
        string template = Header + "\"resources\": [], \"metadata\": { \"na\\ud800me\": 1 } }";

        var result = await ValidateAsync(nameof(ALoneSurrogateEscape_InAPropertyName_IsReported), template);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d!["code"]!.GetValue<string>() == "InvalidJson" && d["message"]!.GetValue<string>().Contains("lone surrogate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALoneSurrogateEscape_InAParameterFile_ExitsOneWithAnInvalidJsonDiagnostic()
    {
        using var directory = new TestDirectory(nameof(ALoneSurrogateEscape_InAParameterFile_ExitsOneWithAnInvalidJsonDiagnostic));
        string template = directory.File("template.entra.json");
        string parameters = directory.File("template.parameters.json");
        await File.WriteAllTextAsync(template, Header + "\"parameters\": { \"p\": { \"type\": \"string\" } }, \"resources\": [] }", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(parameters, "{ \"parameters\": { \"p\": { \"value\": \"a\\ud800b\" } } }", TestContext.Current.CancellationToken);

        var result = await CliRunner.ValidateJsonAsync(template, "-p", "@" + parameters);

        Assert.Equal(1, result.ExitCode);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("InvalidJson", diagnostic!["code"]!.GetValue<string>());
        Assert.Contains("lone surrogate", diagnostic["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(parameters, diagnostic["source"]!.GetValue<string>());
        Assert.Equal(string.Empty, result.StandardError);
    }
}
