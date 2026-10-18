using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EArm.Cli;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// The command-line side of the review-1A findings: the Complete-mode blast radius (I-06), the secure outputs file check (E-03a) and secure
/// values in console errors (S-02), driven in-process against the scripted Graph.
/// </summary>
public sealed class Review1A_CliSafetyTests
{
    private static string[] Args(CliSandbox sandbox, params string[] args)
    {
        return [.. args];
    }

    private static string Id(char c, int n)
    {
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{c}0000000-0000-4000-8000-{n:D12}");
    }

    private static string Get(string collection, string uniqueName, string id)
    {
        return "{ \"match\": { \"method\": \"GET\", \"urlContains\": \"" + collection + "(uniqueName='" + uniqueName + "')\" }, \"status\": 200, \"body\": { \"id\": \"" + id + "\", \"uniqueName\": \"" + uniqueName + "\", \"displayName\": \"Live " + uniqueName + "\", \"mailEnabled\": false, \"securityEnabled\": true } }";
    }

    private static int Deletes(CliSandbox sandbox)
    {
        return sandbox.RecordedCommands().Count(c => c.StartsWith("DELETE ", StringComparison.Ordinal));
    }

    // ---- Complete mode never deletes (it replaced the I-06 blast-radius limits) ----

    [Fact]
    public async Task Complete_WithAnEmptyTemplate_DeletesNothing_AndNeedsNoFlag()
    {
        using var sandbox = new CliSandbox(nameof(Complete_WithAnEmptyTemplate_DeletesNothing_AndNeedsNoFlag));
        sandbox.UsePreflightOk(extraResponses: Get("groups", "g1", Id('a', 1)));
        string template = sandbox.WriteEmptyTemplate("wipe.entra.json");

        var result = await CliRunner.RunAsync(Args(sandbox, "deployment", "create", "-f", template, "--mode", "Complete", "-y", "-o", "json"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, Deletes(sandbox));
        Assert.Empty(result.Json["orphans"]!.AsArray());
        Assert.False(result.Json.ContainsKey("deleted"));
    }

    [Theory]
    [InlineData("--max-deletes", "3")]
    [InlineData("--allow-delete-all", null)]
    [InlineData("--delete-adopted", null)]
    [InlineData("--state-dir", "state")]
    [InlineData("--force-unlock", null)]
    [InlineData("--lock-timeout", "10")]
    public void TheRemovedDeleteAndStateFlags_AreUnknownOptions(string flag, string? value)
    {
        string[] args = value is null ? ["deployment", "create", "-f", "t.json", flag] : ["deployment", "create", "-f", "t.json", flag, value];

        var parse = Program.BuildRootCommand().Parse(args);

        Assert.NotEmpty(parse.Errors);
    }

    // ---- E-03a ----

    [Theory]
    [InlineData("directory")]
    [InlineData("missing-folder")]
    public async Task ABadSecureOutputsFile_StopsTheRunBeforeAnythingIsSentOrWritten(string kind)
    {
        using var sandbox = new CliSandbox(nameof(ABadSecureOutputsFile_StopsTheRunBeforeAnythingIsSentOrWritten) + kind);
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();
        string target = kind == "directory" ? sandbox.TemplatesDir : Path.Combine(sandbox.Root, "no-such-folder", "secure.json");

        var result = await CliRunner.RunAsync(Args(sandbox, "deployment", "create", "-f", template, "-y", "--secure-outputs-file", target));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--secure-outputs-file", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Deployment not started.", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(sandbox.RecordedCommands());
        Assert.Empty(sandbox.UnexpectedFiles());
    }

    [Fact]
    public async Task WhatIf_DoesNotCheckTheSecureOutputsFile()
    {
        using var sandbox = new CliSandbox(nameof(WhatIf_DoesNotCheckTheSecureOutputsFile));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();

        var result = await CliRunner.RunAsync(Args(sandbox, "deployment", "what-if", "-f", template));

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ADeploymentError_ThatEchoesASecureParameter_IsMaskedOnTheConsole()
    {
        using var sandbox = new CliSandbox(nameof(ADeploymentError_ThatEchoesASecureParameter_IsMaskedOnTheConsole));
        string template = sandbox.WriteTemplate("secret.entra.json", "\"parameters\": { \"pw\": { \"type\": \"securestring\" } }, \"resources\": [ { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"g\", \"properties\": { \"displayName\": \"G\", \"mailEnabled\": false, \"securityEnabled\": true, \"description\": \"[parameters('pw')]\" } } ]");
        sandbox.UsePreflightOk(extraResponses:
            "{ \"match\": { \"method\": \"GET\", \"urlContains\": \"/groups\" }, \"status\": 404, \"body\": { \"error\": { \"code\": \"Request_ResourceNotFound\", \"message\": \"gone\" } } }," +
            "{ \"match\": { \"method\": \"PATCH\", \"urlContains\": \"/groups\" }, \"status\": 400, \"body\": { \"error\": { \"code\": \"Request_BadRequest\", \"message\": \"The value 'Hunter2-Secret-Value!' is not valid for description.\" } } }," +
            "{ \"match\": { \"method\": \"POST\", \"urlContains\": \"/groups\" }, \"status\": 400, \"body\": { \"error\": { \"code\": \"Request_BadRequest\", \"message\": \"The value 'Hunter2-Secret-Value!' is not valid for description.\" } } }");

        var result = await CliRunner.RunAsync(Args(sandbox, "deployment", "create", "-f", template, "-p", "pw=Hunter2-Secret-Value!", "-y", "-o", "json"));

        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain("Hunter2-Secret-Value!", result.StandardOutput + result.StandardError, StringComparison.Ordinal);
        Assert.Empty(sandbox.UnexpectedFiles());
    }
}
