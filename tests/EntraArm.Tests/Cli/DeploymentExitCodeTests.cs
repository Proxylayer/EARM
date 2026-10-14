using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace EntraArm.Tests.Cli;

/// <summary>
/// Exit codes of <c>deployment what-if / create</c> that the other CLI suites leave out: unreadable templates and parameter files
/// (1), an invalid template in every output format (2), a missing toolchain or Key Vault secret (3), and the output switches
/// (<c>-o json / none</c>) on each failure. All but the Key Vault case stop before any Azure CLI process starts.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class DeploymentExitCodeTests
{
    private const string NoSuchAz = "C:\\nonexistent\\entraarm-no-such-az.ps1";

    private static string[] Offline(CliSandbox sandbox, params string[] args)
    {
        return [.. args, "--az-command", NoSuchAz, "--temp-dir", sandbox.TempDir, "--state-dir", sandbox.StateDir];
    }

    [Theory]
    [InlineData("what-if")]
    [InlineData("create")]
    public async Task AMissingTemplateFile_ExitsOne_WithAFileNotFoundDiagnostic(string verb)
    {
        using var sandbox = new CliSandbox(nameof(AMissingTemplateFile_ExitsOne_WithAFileNotFoundDiagnostic) + verb);
        string missing = Path.Combine(sandbox.TemplatesDir, "nope.entra.json");

        var json = await CliRunner.RunAsync(Offline(sandbox, "deployment", verb, "-f", missing, "-o", "json"));
        var table = await CliRunner.RunAsync(Offline(sandbox, "deployment", verb, "-f", missing));
        var none = await CliRunner.RunAsync(Offline(sandbox, "deployment", verb, "-f", missing, "-o", "none"));

        Assert.Equal(1, json.ExitCode);
        Assert.Contains(json.Diagnostics, d => d!["code"]!.GetValue<string>() == "FileNotFound");
        Assert.Equal(1, table.ExitCode);
        Assert.Contains("nope.entra.json", table.StandardError + table.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(1, none.ExitCode);
        Assert.Equal(string.Empty, none.StandardOutput + none.StandardError);
        Assert.Empty(sandbox.RecordedCommands());
    }

    [Theory]
    [InlineData("what-if")]
    [InlineData("create")]
    public async Task AnUnparsableTemplate_ExitsOne_AndNamesThePosition(string verb)
    {
        using var sandbox = new CliSandbox(nameof(AnUnparsableTemplate_ExitsOne_AndNamesThePosition) + verb);
        string path = Path.Combine(sandbox.TemplatesDir, "broken.entra.json");
        File.WriteAllText(path, "{ \"resources\": [ \n  { \"type\": }\n ] }");

        var json = await CliRunner.RunAsync(Offline(sandbox, "deployment", verb, "-f", path, "-o", "json"));

        Assert.Equal(1, json.ExitCode);
        var diagnostic = Assert.Single(json.Diagnostics);
        Assert.Equal("InvalidJson", diagnostic!["code"]!.GetValue<string>());
        Assert.Equal(2, diagnostic["line"]!.GetValue<int>());
        Assert.Empty(sandbox.RecordedCommands());
    }

    [Theory]
    [InlineData("what-if")]
    [InlineData("create")]
    public async Task AnEmptyOrBomOnlyTemplate_ExitsOne(string verb)
    {
        using var sandbox = new CliSandbox(nameof(AnEmptyOrBomOnlyTemplate_ExitsOne) + verb);
        string empty = Path.Combine(sandbox.TemplatesDir, "empty.entra.json");
        string bomOnly = Path.Combine(sandbox.TemplatesDir, "bom.entra.json");
        File.WriteAllBytes(empty, []);
        File.WriteAllBytes(bomOnly, [0xEF, 0xBB, 0xBF]);

        foreach (string path in new[] { empty, bomOnly })
        {
            var result = await CliRunner.RunAsync(Offline(sandbox, "deployment", verb, "-f", path, "-o", "json"));

            Assert.Equal(1, result.ExitCode);
            Assert.Contains(result.Diagnostics, d => d!["code"]!.GetValue<string>() == "InvalidJson");
        }
    }

    [Theory]
    [InlineData("what-if")]
    [InlineData("create")]
    public async Task AnUnreadableParameterFile_ExitsOne_BeforeTheTemplateIsLoaded(string verb)
    {
        using var sandbox = new CliSandbox(nameof(AnUnreadableParameterFile_ExitsOne_BeforeTheTemplateIsLoaded) + verb);
        string template = sandbox.WriteEmptyTemplate();
        string badParameters = Path.Combine(sandbox.TemplatesDir, "params.json");
        File.WriteAllText(badParameters, "{ \"parameters\": { \"p\": { \"value\": 1, \"reference\": {} } } }");

        var conflicting = await CliRunner.RunAsync(Offline(sandbox, "deployment", verb, "-f", template, "-p", "@" + badParameters, "-o", "json"));
        var missing = await CliRunner.RunAsync(Offline(sandbox, "deployment", verb, "-f", template, "-p", "@" + Path.Combine(sandbox.TemplatesDir, "absent.json"), "-o", "json"));

        Assert.Equal(1, conflicting.ExitCode);
        Assert.Contains(conflicting.Diagnostics, d => d!["code"]!.GetValue<string>() == "InvalidParameterFile");
        Assert.Equal(1, missing.ExitCode);
        Assert.NotEmpty(missing.Diagnostics);
        Assert.Empty(sandbox.RecordedCommands());
    }

    [Theory]
    [InlineData("what-if")]
    [InlineData("create")]
    public async Task ASemanticallyInvalidTemplate_ExitsTwo_InEveryOutputFormat(string verb)
    {
        using var sandbox = new CliSandbox(nameof(ASemanticallyInvalidTemplate_ExitsTwo_InEveryOutputFormat) + verb);
        string template = sandbox.WriteTemplate("dup.entra.json", "\"resources\": [ "
            + "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01\", \"name\": \"same\", \"properties\": { \"displayName\": \"A\" } }, "
            + "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01\", \"name\": \"same\", \"properties\": { \"displayName\": \"B\" } } ]");

        var json = await CliRunner.RunAsync(Offline(sandbox, "deployment", verb, "-f", template, "-o", "json"));
        var table = await CliRunner.RunAsync(Offline(sandbox, "deployment", verb, "-f", template));
        var none = await CliRunner.RunAsync(Offline(sandbox, "deployment", verb, "-f", template, "-o", "none"));

        Assert.Equal(2, json.ExitCode);
        Assert.Equal("Failed", json.Json["status"]!.GetValue<string>());
        Assert.NotEmpty(json.Diagnostics);
        Assert.Equal(2, table.ExitCode);
        Assert.NotEqual(string.Empty, table.StandardError + table.StandardOutput);
        Assert.Equal(2, none.ExitCode);
        Assert.Equal(string.Empty, none.StandardOutput + none.StandardError);
        Assert.Empty(sandbox.RecordedCommands());
    }

    [Fact]
    public async Task AMissingPowerShell_ExitsThree_WithThePreflightMessage_AndNeverTouchesTheState()
    {
        using var sandbox = new CliSandbox(nameof(AMissingPowerShell_ExitsThree_WithThePreflightMessage_AndNeverTouchesTheState));
        string template = sandbox.WriteEmptyTemplate();

        var whatIf = await CliRunner.RunAsync("deployment", "what-if", "-f", template, "--pwsh-path", "entraarm-no-such-pwsh", "--temp-dir", sandbox.TempDir, "--state-dir", sandbox.StateDir);
        var create = await CliRunner.RunAsync("deployment", "create", "-f", template, "-y", "--pwsh-path", "C:\\nonexistent\\pwsh.exe", "--temp-dir", sandbox.TempDir, "--state-dir", sandbox.StateDir);

        Assert.Equal(3, whatIf.ExitCode);
        Assert.Contains("Preflight failed:", whatIf.StandardError, StringComparison.Ordinal);
        Assert.Contains("--pwsh-path", whatIf.StandardError, StringComparison.Ordinal);
        Assert.Equal(3, create.ExitCode);
        Assert.False(File.Exists(sandbox.StatePath("empty")));
        Assert.False(File.Exists(sandbox.LockPath("empty")));
    }

    [Fact]
    public async Task AKeyVaultSecretThatCannotBeRead_ExitsThree_WithTheParameterNamed_AndNoSecretTextAnywhere()
    {
        using var sandbox = new CliSandbox(nameof(AKeyVaultSecretThatCannotBeRead_ExitsThree_WithTheParameterNamed_AndNoSecretTextAnywhere));
        string template = sandbox.WriteTemplate("kv.entra.json", "\"parameters\": { \"pw\": { \"type\": \"securestring\" } }, \"resources\": [], \"outputs\": { \"o\": { \"type\": \"string\", \"value\": \"x\" } }");
        string parameters = Path.Combine(sandbox.TemplatesDir, "kv.parameters.json");
        File.WriteAllText(parameters, "{ \"parameters\": { \"pw\": { \"reference\": { \"keyVault\": { \"id\": \"/subscriptions/0/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/contoso-kv\" }, \"secretName\": \"orders-secret\" } } } }");
        sandbox.UsePreflightOk(extraResponses: "{ \"match\": { \"argvContains\": \"keyvault secret show\" }, \"exitCode\": 3, \"stderr\": \"ERROR: (SecretNotFound) A secret with (name/id) orders-secret was not found in this key vault.\" }");

        var result = await CliRunner.RunAsync([.. sandbox.AzArgs, "deployment", "create", "-f", template, "-p", "@" + parameters, "-y", "-o", "json"]);

        Assert.Equal(3, result.ExitCode);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Contains("pw", diagnostic!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("orders-secret", diagnostic["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(File.Exists(sandbox.StatePath("kv")));
        Assert.False(File.Exists(sandbox.LockPath("kv")));
    }
}
