using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// Exit codes of <c>deployment what-if / create</c> that the other CLI suites leave out: unreadable templates and parameter files
/// (1), an invalid template in every output format (2), no sign-in or an unreadable Key Vault secret (3), and the output switches
/// (<c>-o json / none</c>) on each failure. All but the sign-in and Key Vault cases stop before any request is sent.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class DeploymentExitCodeTests
{
    private static string[] Offline(CliSandbox sandbox, params string[] args)
    {
        return [.. args];
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
            + "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"same\", \"properties\": { \"displayName\": \"A\" } }, "
            + "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"same\", \"properties\": { \"displayName\": \"B\" } } ]");

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
    public async Task NoSignIn_ExitsThree_WithTheGuidance_AndNeverWritesOrSendsARequest()
    {
        using var sandbox = new CliSandbox(nameof(NoSignIn_ExitsThree_WithTheGuidance_AndNeverWritesOrSendsARequest));
        string template = sandbox.WriteEmptyTemplate();
        sandbox.UseNotLoggedIn();

        var whatIf = await CliRunner.RunAsync("deployment", "what-if", "-f", template);
        var create = await CliRunner.RunAsync("deployment", "create", "-f", template, "-y");

        foreach (var result in new[] { whatIf, create })
        {
            Assert.Equal(3, result.ExitCode);
            Assert.Contains("Preflight failed:", result.StandardError, StringComparison.Ordinal);
            Assert.Contains("Not signed in", result.StandardError, StringComparison.Ordinal);
            Assert.Contains("AZURE_TENANT_ID", result.StandardError, StringComparison.Ordinal);
            Assert.Contains("AZURE_CLIENT_SECRET", result.StandardError, StringComparison.Ordinal);
            Assert.Contains("earm auth login", result.StandardError, StringComparison.Ordinal);
            Assert.DoesNotContain("az login", result.StandardError, StringComparison.Ordinal);
        }

        Assert.Empty(sandbox.RecordedCommands());
        Assert.Empty(sandbox.UnexpectedFiles());
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("https://evil.example")]
    public async Task AnUnknownCloud_ExitsOne_AndNamesTheAllowedValues(string cloud)
    {
        using var sandbox = new CliSandbox(nameof(AnUnknownCloud_ExitsOne_AndNamesTheAllowedValues));
        string template = sandbox.WriteEmptyTemplate();
        sandbox.UsePreflightOk();
        string? saved = Environment.GetEnvironmentVariable("EARM_CLOUD");
        try
        {
            Environment.SetEnvironmentVariable("EARM_CLOUD", cloud);

            var result = await CliRunner.RunAsync("deployment", "what-if", "-f", template);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("public, usgov, usgovdod, china", result.StandardError, StringComparison.Ordinal);
            Assert.Empty(sandbox.RecordedCommands());
        }
        finally
        {
            Environment.SetEnvironmentVariable("EARM_CLOUD", saved);
        }
    }

    private static string KeyVaultTemplate(CliSandbox sandbox, string vaultId, string secretName, out string parameters)
    {
        string template = sandbox.WriteTemplate("kv.entra.json", "\"parameters\": { \"pw\": { \"type\": \"securestring\" } }, \"resources\": [], \"outputs\": { \"o\": { \"type\": \"string\", \"value\": \"x\" } }");
        parameters = Path.Combine(sandbox.TemplatesDir, "kv.parameters.json");
        File.WriteAllText(parameters, "{ \"parameters\": { \"pw\": { \"reference\": { \"keyVault\": { \"id\": \"" + vaultId + "\" }, \"secretName\": \"" + secretName + "\" } } } }");
        return template;
    }

    [Fact]
    public async Task AKeyVaultSecretThatCannotBeRead_ExitsThree_WithTheParameterNamed_AndNoSecretTextAnywhere()
    {
        using var sandbox = new CliSandbox(nameof(AKeyVaultSecretThatCannotBeRead_ExitsThree_WithTheParameterNamed_AndNoSecretTextAnywhere));
        string template = KeyVaultTemplate(sandbox, "/subscriptions/0/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/contoso-kv", "orders-secret", out string parameters);
        sandbox.UsePreflightOk(extraResponses: "{ \"match\": { \"method\": \"GET\", \"urlContains\": \"/secrets/orders-secret\" }, \"status\": 404, \"body\": { \"error\": { \"code\": \"SecretNotFound\", \"message\": \"A secret with (name/id) orders-secret was not found in this key vault.\" } } }");

        var result = await CliRunner.RunAsync(["deployment", "create", "-f", template, "-p", "@" + parameters, "-y", "-o", "json"]);

        Assert.Equal(3, result.ExitCode);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Contains("pw", diagnostic!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("orders-secret", diagnostic["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("SecretNotFound", diagnostic["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty(sandbox.UnexpectedFiles(parameters));
        var vaultCall = Assert.Single(sandbox.RecordedRequests(), static r => r.Uri.Host.StartsWith("contoso-kv.", StringComparison.Ordinal));
        Assert.Equal("https://contoso-kv.vault.azure.net/secrets/orders-secret?api-version=7.4", vaultCall.Uri.AbsoluteUri);
        Assert.Contains("https://vault.azure.net/.default", sandbox.Tokens.RequestedScopes);
    }

    [Theory]
    [InlineData("evil.example/x#", "orders-secret")]
    [InlineData("contoso-kv", "../../x")]
    [InlineData("contoso-kv", "a.b")]
    public async Task AKeyVaultNameThatBreaksTheRules_ExitsThree_BeforeAnyVaultRequest(string vaultId, string secretName)
    {
        using var sandbox = new CliSandbox(nameof(AKeyVaultNameThatBreaksTheRules_ExitsThree_BeforeAnyVaultRequest));
        string template = KeyVaultTemplate(sandbox, vaultId, secretName, out string parameters);
        sandbox.UsePreflightOk();

        var result = await CliRunner.RunAsync(["deployment", "create", "-f", template, "-p", "@" + parameters, "-y", "-o", "json"]);

        Assert.Equal(3, result.ExitCode);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Contains("not a valid Key Vault", diagnostic!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.All(sandbox.RecordedRequests(), static r => Assert.EndsWith("graph.microsoft.com", r.Uri.Host, StringComparison.Ordinal));
        Assert.Empty(sandbox.UnexpectedFiles(parameters));
    }

    [Fact]
    public async Task AKeyVaultSecret_IsResolvedOverHttp_TaintedAndNeverPrinted()
    {
        using var sandbox = new CliSandbox(nameof(AKeyVaultSecret_IsResolvedOverHttp_TaintedAndNeverPrinted));
        string template = sandbox.WriteTemplate("kv.entra.json", "\"parameters\": { \"pw\": { \"type\": \"securestring\" } }, \"resources\": [], \"outputs\": { \"echo\": { \"type\": \"securestring\", \"value\": \"[parameters('pw')]\" } }");
        string parameters = Path.Combine(sandbox.TemplatesDir, "kv.parameters.json");
        File.WriteAllText(parameters, "{ \"parameters\": { \"pw\": { \"reference\": { \"keyVault\": { \"id\": \"contoso-kv\" }, \"secretName\": \"orders-secret\" } } } }");
        sandbox.UsePreflightOk(extraResponses: "{ \"match\": { \"method\": \"GET\", \"urlContains\": \"/secrets/orders-secret\" }, \"status\": 200, \"body\": { \"value\": \"V4ult-S3cret-Value\" } }");

        var result = await CliRunner.RunAsync(["deployment", "create", "-f", template, "-p", "@" + parameters, "-y", "-o", "json"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("***", result.Json["outputs"]!["echo"]!["value"]!.GetValue<string>());
        Assert.DoesNotContain("V4ult-S3cret-Value", result.StandardOutput + result.StandardError, StringComparison.Ordinal);
        Assert.Empty(sandbox.UnexpectedFiles(parameters));
    }
}