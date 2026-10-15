using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Cli;
using EArm.Cli.Commands;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Providers;
using EArm.Tests.Engine;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// <c>earm deployment what-if / create</c> driven in-process against a fake Microsoft Graph over HTTP and a fake sign-in (the exit
/// codes of the CLI). Templates without resources go through the whole pipeline (preflight, plan, execute) without
/// needing a resource provider; the engine fixture covers the offline paths that stop before any request.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class DeploymentCommandTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string[] Args(CliSandbox sandbox, params string[] args)
    {
        return [.. args];
    }

    // ---- offline failures (never send a request) ----

    [Fact]
    public async Task WhatIf_WithoutParameterValues_FailsValidationBeforeAnyRequest()
    {
        using var sandbox = new CliSandbox(nameof(WhatIf_WithoutParameterValues_FailsValidationBeforeAnyRequest));

        var result = await CliRunner.RunAsync("deployment", "what-if", "-f", Templates.FixturePath, "-o", "json");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("Failed", result.Json["status"]!.GetValue<string>());
        var missing = result.Diagnostics.Where(d => d!["code"]!.GetValue<string>() == "MissingParameterValue").ToList();
        Assert.Equal(2, missing.Count);
        Assert.All(missing, d => Assert.Equal("Error", d!["severity"]!.GetValue<string>()));
        Assert.Contains(missing, d => d!["message"]!.GetValue<string>().Contains("Supply it with -p name=value", StringComparison.Ordinal));
        Assert.Empty(sandbox.RecordedCommands());
        Assert.Empty(sandbox.UnexpectedFiles());
    }

    [Fact]
    public async Task WhatIf_WithAMalformedParameterToken_ExitsOneBeforeAnyRequest()
    {
        using var sandbox = new CliSandbox(nameof(WhatIf_WithAMalformedParameterToken_ExitsOneBeforeAnyRequest));

        var result = await CliRunner.RunAsync("deployment", "what-if", "-f", Templates.FixturePath, "-p", "notatoken", "-o", "json");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d!["code"]!.GetValue<string>() == "InvalidArgument");
        Assert.Empty(sandbox.RecordedCommands());
    }

    [Theory]
    [InlineData("bad name")]
    [InlineData("a/b")]
    [InlineData("CON")]
    public async Task TheDeploymentName_IsOnlyALabel_AnyTextIsAccepted(string name)
    {
        using var sandbox = new CliSandbox(nameof(TheDeploymentName_IsOnlyALabel_AnyTextIsAccepted));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();

        var result = await CliRunner.RunAsync("deployment", "what-if", "-f", template, "-n", name, "-o", "json");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(name, result.Json["deploymentName"]!.GetValue<string>());
        Assert.Empty(sandbox.UnexpectedFiles());
    }

    // ---- preflight ----

    [Fact]
    public async Task WhatIf_NotSignedIn_ExitsThreeWithTheSignInGuidance()
    {
        using var sandbox = new CliSandbox(nameof(WhatIf_NotSignedIn_ExitsThreeWithTheSignInGuidance));
        sandbox.UseNotLoggedIn();

        var result = await CliRunner.RunAsync(Args(sandbox, "deployment", "what-if", "-f", Templates.FixturePath, "-p", "env=dev", "-p", "initialPassword=P@ss-w0rd1"));

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("Preflight failed: Not signed in", result.StandardError);
        Assert.Contains("earm auth login", result.StandardError);
        Assert.Contains("AZURE_CLIENT_SECRET", result.StandardError);
        Assert.DoesNotContain("az login", result.StandardError);
        Assert.Empty(sandbox.RecordedCommands());
        Assert.Empty(sandbox.UnexpectedFiles());
    }

    // ---- what-if ----

    [Fact]
    public async Task WhatIf_EmptyTemplate_ReportsNoChanges_AndNeverWrites()
    {
        using var sandbox = new CliSandbox(nameof(WhatIf_EmptyTemplate_ReportsNoChanges_AndNeverWrites));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();

        var json = await CliRunner.RunAsync(Args(sandbox, "deployment", "what-if", "-f", template, "-o", "json", "--exit-code-on-changes"));
        sandbox.UsePreflightOk();
        var table = await CliRunner.RunAsync(Args(sandbox, "deployment", "what-if", "-f", template, "-n", "named"));

        Assert.Equal(0, json.ExitCode);
        Assert.Equal("Succeeded", json.Json["status"]!.GetValue<string>());
        Assert.Equal("empty", json.Json["deploymentName"]!.GetValue<string>());
        Assert.Equal(CliSandbox.Tenant, json.Json["tenantId"]!.GetValue<string>());
        Assert.Equal("Incremental", json.Json["mode"]!.GetValue<string>());
        Assert.Empty(json.Json["changes"]!.AsArray());
        Assert.Equal(0, json.Json["summary"]!["create"]!.GetValue<int>());
        Assert.Equal(string.Empty, json.StandardError);

        Assert.Equal(0, table.ExitCode);
        Assert.Contains("Deployment 'named' (Incremental) on tenant " + CliSandbox.Tenant, table.StandardOutput);
        Assert.Contains("Resource changes: 0 to create, 0 to modify, 0 to delete, 0 unchanged, 0 ignored.", table.StandardOutput);
        Assert.Contains("The template declares no resources.", table.StandardOutput);

        Assert.Empty(sandbox.UnexpectedFiles());
        // Per run: one GET /organization (the preflight; it also feeds tenant()); what-if never writes.
        Assert.Equal(2, sandbox.RecordedCommands().Count);
        Assert.All(sandbox.RecordedCommands(), c => Assert.StartsWith("GET ", c, StringComparison.Ordinal));
    }

    // ---- create ----

    [Fact]
    public async Task Create_EmptyTemplate_ReportsTheOutputs_AndWritesNothingToDisk()
    {
        using var sandbox = new CliSandbox(nameof(Create_EmptyTemplate_ReportsTheOutputs_AndWritesNothingToDisk));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();

        var json = await CliRunner.RunAsync(Args(sandbox, "deployment", "create", "-f", template, "-y", "-o", "json"));
        sandbox.UsePreflightOk();
        var table = await CliRunner.RunAsync(Args(sandbox, "deployment", "create", "-f", template, "-y", "-n", "custom-name"));

        Assert.Equal(0, json.ExitCode);
        Assert.Equal("Succeeded", json.Json["status"]!.GetValue<string>());
        Assert.Equal("empty", json.Json["deploymentName"]!.GetValue<string>());
        Assert.Equal("hello", json.Json["outputs"]!["greeting"]!["value"]!.GetValue<string>());
        Assert.Equal(0, json.Json["succeeded"]!.GetValue<int>());
        Assert.Empty(json.Json["operations"]!.AsArray());
        Assert.Null(json.Json["secureOutputsFile"]);


        Assert.Equal(0, table.ExitCode);
        Assert.Contains("Deployment 'custom-name' Succeeded", table.StandardOutput);
        Assert.Contains("greeting: \"hello\"", table.StandardOutput);
        Assert.Empty(sandbox.UnexpectedFiles());
    }

    [Fact]
    public async Task Create_SecureOutputs_AreRedactedUnlessRevealed_AndWrittenToTheFile()
    {
        using var sandbox = new CliSandbox(nameof(Create_SecureOutputs_AreRedactedUnlessRevealed_AndWrittenToTheFile));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteTemplate("secret.entra.json", "\"parameters\": { \"pw\": { \"type\": \"securestring\" } }, \"resources\": [], \"outputs\": { \"echo\": { \"type\": \"securestring\", \"value\": \"[parameters('pw')]\" } }");
        string secureFile = Path.Combine(sandbox.Root, "secure-outputs.json");

        var redacted = await CliRunner.RunAsync(Args(sandbox, "deployment", "create", "-f", template, "-p", "pw=hunter2", "-y", "-o", "json", "--secure-outputs-file", secureFile));
        sandbox.UsePreflightOk();
        var revealed = await CliRunner.RunAsync(Args(sandbox, "deployment", "create", "-f", template, "-p", "pw=hunter2", "-y", "-o", "json", "--reveal-secure-outputs"));

        Assert.Equal(0, redacted.ExitCode);
        Assert.Equal("***", redacted.Json["outputs"]!["echo"]!["value"]!.GetValue<string>());
        Assert.Equal(secureFile, redacted.Json["secureOutputsFile"]!.GetValue<string>());
        Assert.DoesNotContain("hunter2", redacted.StandardOutput, StringComparison.Ordinal);
        Assert.Equal("hunter2", JsonNode.Parse(await File.ReadAllTextAsync(secureFile, Ct))!["echo"]!["value"]!.GetValue<string>());
        Assert.Equal(0, revealed.ExitCode);
        Assert.Equal("hunter2", revealed.Json["outputs"]!["echo"]!["value"]!.GetValue<string>());
        Assert.Empty(sandbox.UnexpectedFiles(secureFile));
    }

    [Fact]
    public async Task Create_ConfirmWithWhatIf_DeclinedAtThePrompt_ExitsOneWithoutDeploying()
    {
        using var sandbox = new CliSandbox(nameof(Create_ConfirmWithWhatIf_DeclinedAtThePrompt_ExitsOneWithoutDeploying));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();
        var invocation = new DeploymentInvocation
        {
            TemplateFile = template,
            ConfirmWithWhatIf = true,
            Output = OutputFormat.Json,
            Connection = sandbox.Connection,
        };
        using var declinedOut = new StringWriter();
        using var declinedErr = new StringWriter();

        int declined = await DeploymentRunner.CreateAsync(invocation, declinedOut, declinedErr, () => "n", Ct);

        Assert.Equal(1, declined);
        Assert.Contains("Deployment not started.", declinedErr.ToString());
        Assert.Contains("Are you sure you want to execute the deployment?", declinedErr.ToString());
        Assert.Contains("\"changes\"", declinedOut.ToString());
        Assert.All(sandbox.RecordedCommands(), c => Assert.StartsWith("GET ", c, StringComparison.Ordinal));
        Assert.Empty(sandbox.UnexpectedFiles());

        sandbox.UsePreflightOk();
        using var acceptedOut = new StringWriter();
        using var acceptedErr = new StringWriter();
        int accepted = await DeploymentRunner.CreateAsync(invocation, acceptedOut, acceptedErr, () => "yes", Ct);

        Assert.Equal(0, accepted);
        Assert.Empty(sandbox.UnexpectedFiles());
        Assert.Contains("\"status\": \"Succeeded\"", acceptedOut.ToString());
    }

    [Fact]
    public async Task Create_ConfirmWithWhatIf_OnRedirectedStdin_RefusesWithTheYesHint()
    {
        Assert.SkipUnless(Console.IsInputRedirected, "standard input is interactive in this test host; the prompt would block.");
        using var sandbox = new CliSandbox(nameof(Create_ConfirmWithWhatIf_OnRedirectedStdin_RefusesWithTheYesHint));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();

        var result = await CliRunner.RunAsync(Args(sandbox, "deployment", "create", "-f", template, "-c"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("pass --yes to confirm", result.StandardError);
        Assert.Contains("Deployment not started.", result.StandardError);
    }

    // ---- help ----

    public static TheoryData<string[], string[]> HelpCases()
    {
        return new TheoryData<string[], string[]>
        {
            {
                new[] { "deployment", "create", "--help" },
                new[] { "--template-file", "-f", "--parameters", "-p", "--name", "-n", "--mode", "--parallelism", "--allow-ca-enforce", "--on-soft-deleted-conflict", "--what-if", "--confirm-with-what-if", "-c", "--yes", "-y", "--continue-on-error", "--reveal-secure-outputs", "--secure-outputs-file", "--result-format", "--no-pretty-print", "--auth", "--tenant", "--client-id", "--output", "-o", "--verbose", "--debug" }
            },
            {
                new[] { "deployment", "what-if", "--help" },
                new[] { "--template-file", "--parameters", "--name", "--mode", "--parallelism", "--allow-ca-enforce", "--on-soft-deleted-conflict", "--result-format", "--exit-code-on-changes", "--no-pretty-print", "--auth" }
            },
            {
                new[] { "deployment", "delete", "--help" },
                new[] { "--template-file", "--parameters", "--name", "--parallelism", "--yes", "--continue-on-error" }
            },
            {
                new[] { "deployment", "--help" },
                new[] { "validate", "what-if", "create", "delete" }
            },
        };
    }

    [Theory]
    [MemberData(nameof(HelpCases))]
    public async Task Help_ListsEveryFlag(string[] args, string[] expected)
    {
        var result = await CliRunner.RunAsync(args);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        foreach (var flag in expected)
        {
            Assert.Contains(flag, result.StandardOutput);
        }
    }

    [Fact]
    public async Task Help_DocumentsTheExitCodes()
    {
        var result = await CliRunner.RunAsync("deployment", "what-if", "--help");

        Assert.Contains("Exit with code 5", result.StandardOutput);
    }

    [Theory]
    [InlineData("deployment", "create", "--help")]
    [InlineData("deployment", "what-if", "--help")]
    [InlineData("deployment", "delete", "--help")]
    [InlineData("deployment", "--help")]
    [InlineData("--help")]
    public async Task Help_OffersNoStateOptionsOrCommands(params string[] args)
    {
        var result = await CliRunner.RunAsync(args);

        Assert.Equal(0, result.ExitCode);
        foreach (string removed in new[] { "--state-dir", "--force-unlock", "--lock-timeout", "EARM_STATE_DIR", "operation", "adopt" })
        {
            Assert.DoesNotContain(removed, result.StandardOutput, StringComparison.Ordinal);
        }

        Assert.DoesNotMatch(@"(?m)^\s+(state|show|list)\s", result.StandardOutput);
    }
}
