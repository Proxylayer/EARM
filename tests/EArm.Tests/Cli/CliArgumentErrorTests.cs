using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// Argument and input errors of <c>permissions</c>, <c>deployment delete</c>, the removed state commands and the output switches
/// (<c>-o none</c>, <c>-o json</c>, a bad <c>-o</c>): the exit code (1 for anything the user can fix on the command line, 2 for an
/// invalid template), where the message goes, and that nothing is written when it fails.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class CliArgumentErrorTests
{
    // ---- permissions ----

    [Fact]
    public async Task Permissions_WithAMalformedParameterToken_OrAnUnreadableParameterFile_ExitsOne()
    {
        using var sandbox = new CliSandbox(nameof(Permissions_WithAMalformedParameterToken_OrAnUnreadableParameterFile_ExitsOne));
        string template = sandbox.WriteEmptyTemplate();
        string badFile = Path.Combine(sandbox.TemplatesDir, "bad.parameters.json");
        File.WriteAllText(badFile, "{ \"parameters\": { \"p\": 5 } }");

        var token = await CliRunner.RunAsync("permissions", "-f", template, "-p", "notatoken", "-o", "json");
        var file = await CliRunner.RunAsync("permissions", "-f", template, "-p", "@" + badFile, "-o", "json");
        var absent = await CliRunner.RunAsync("permissions", "-f", template, "-p", "@" + Path.Combine(sandbox.TemplatesDir, "absent.json"), "-o", "json");

        Assert.Equal(1, token.ExitCode);
        Assert.Contains(token.Diagnostics, d => d!["code"]!.GetValue<string>() == "InvalidArgument");
        Assert.Equal(1, file.ExitCode);
        Assert.Contains(file.Diagnostics, d => d!["code"]!.GetValue<string>() == "InvalidParameterFile");
        Assert.Equal(1, absent.ExitCode);
    }

    [Fact]
    public async Task Permissions_OfATemplateWithoutResources_ListsNone_InEveryModeAndFormat()
    {
        using var sandbox = new CliSandbox(nameof(Permissions_OfATemplateWithoutResources_ListsNone_InEveryModeAndFormat));
        string template = sandbox.WriteEmptyTemplate();

        var app = await CliRunner.RunAsync("permissions", "-f", template);
        var delegated = await CliRunner.RunAsync("permissions", "-f", template, "--mode", "delegated");
        var json = await CliRunner.RunAsync("permissions", "-f", template, "-o", "json");
        var none = await CliRunner.RunAsync("permissions", "-f", template, "-o", "none");

        Assert.Equal(0, app.ExitCode);
        Assert.Contains("Organization.Read.All", app.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(0, delegated.ExitCode);
        Assert.Contains("Entra roles", delegated.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("  none", delegated.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(["Organization.Read.All"], json.Json["permissions"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()).ToArray());
        Assert.Equal(string.Empty, none.StandardOutput);
    }

    [Fact]
    public async Task Permissions_OfAnInvalidTemplate_ExitsTwo_AndOfAMissingOneExitsOne()
    {
        using var sandbox = new CliSandbox(nameof(Permissions_OfAnInvalidTemplate_ExitsTwo_AndOfAMissingOneExitsOne));
        string duplicates = sandbox.WriteTemplate("dup.entra.json", "\"resources\": [ "
            + "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"x\", \"properties\": { \"displayName\": \"A\" } }, "
            + "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"x\", \"properties\": { \"displayName\": \"B\" } } ]");

        var invalid = await CliRunner.RunAsync("permissions", "-f", duplicates, "-o", "json");
        var missing = await CliRunner.RunAsync("permissions", "-f", Path.Combine(sandbox.TemplatesDir, "nope.entra.json"), "-o", "json");

        Assert.Equal(2, invalid.ExitCode);
        Assert.NotEmpty(invalid.Diagnostics);
        Assert.Equal(1, missing.ExitCode);
        Assert.Contains(missing.Diagnostics, d => d!["code"]!.GetValue<string>() == "FileNotFound");
    }

    [Fact]
    public async Task Permissions_WithAnUnknownMode_OrOutputFormat_IsAUsageError()
    {
        using var sandbox = new CliSandbox(nameof(Permissions_WithAnUnknownMode_OrOutputFormat_IsAUsageError));
        string template = sandbox.WriteEmptyTemplate();

        var mode = await CliRunner.RunAsync("permissions", "-f", template, "--mode", "everything");
        var format = await CliRunner.RunAsync("permissions", "-f", template, "-o", "yaml");
        var noTemplate = await CliRunner.RunAsync("permissions");

        Assert.Equal(1, mode.ExitCode);
        Assert.Contains("everything", mode.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, format.ExitCode);
        Assert.Equal(1, noTemplate.ExitCode);
        Assert.Contains("--template-file", noTemplate.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeploymentDelete_WithoutATemplate_OrOfAMissingOne_ExitsOne_AndTheOutputSwitchIsRejectedWhenUnknown()
    {
        using var sandbox = new CliSandbox(nameof(DeploymentDelete_WithoutATemplate_OrOfAMissingOne_ExitsOne_AndTheOutputSwitchIsRejectedWhenUnknown));

        var noTemplate = await CliRunner.RunAsync("deployment", "delete", "-n", "dep", "-y");
        var ghost = await CliRunner.RunAsync("deployment", "delete", "-f", Path.Combine(sandbox.Root, "ghost.entra.json"), "-y");
        var badOutput = await CliRunner.RunAsync("deployment", "delete", "-f", Path.Combine(sandbox.Root, "ghost.entra.json"), "-o", "xml");

        Assert.Equal(1, noTemplate.ExitCode);
        Assert.Contains("--template-file", noTemplate.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, ghost.ExitCode);
        Assert.Contains("ghost.entra.json", ghost.StandardError + ghost.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(1, badOutput.ExitCode);
        Assert.Contains("xml", badOutput.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRemovedStateCommands_AreUnknown_AndTheHelpOfEveryGroupExitsZero()
    {
        foreach (string[] removed in new[] { new[] { "state", "unlock", "-n", "dep" }, new[] { "deployment", "operation", "list", "-n", "dep" }, new[] { "deployment", "show", "-n", "dep" }, new[] { "deployment", "list" } })
        {
            var result = await CliRunner.RunAsync(removed);
            Assert.Equal(1, result.ExitCode);
        }

        foreach (string[] help in new[] { new[] { "deployment", "--help" }, new[] { "auth", "--help" }, new[] { "schema", "--help" }, new[] { "permissions", "--help" } })
        {
            var result = await CliRunner.RunAsync(help);
            Assert.Equal(0, result.ExitCode);
            Assert.NotEqual(string.Empty, result.StandardOutput);
        }
    }

    [Fact]
    public async Task AnUnknownCommand_OrOption_IsAUsageError()
    {
        var command = await CliRunner.RunAsync("deployment", "explode");
        var option = await CliRunner.RunAsync("deployment", "validate", "--no-such-option");
        var version = await CliRunner.RunAsync("--version");

        Assert.Equal(1, command.ExitCode);
        Assert.Contains("explode", command.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, option.ExitCode);
        Assert.Equal(0, version.ExitCode);
        Assert.Matches(@"^\d+\.\d+\.\d+", version.StandardOutput.Trim());
    }
}
