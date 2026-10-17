using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EArm.Cli;
using EArm.Cli.Commands;
using EArm.Cli.Hosting;
using EArm.Core;
using EArm.Core.Json;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// The <c>az deployment</c> options added for fidelity: <c>-x</c> / <c>--exclude-change-types</c> / <c>--what-if-exclude-change-types</c>, <c>-r</c> /
/// <c>--what-if-result-format</c>, <c>-w</c>, <c>--proceed-if-no-change</c>, <c>--no-prompt</c> (with <see cref="ParameterPrompt"/>) and
/// <c>validate --validation-level</c>.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class AzureCliParityOptionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WhatIf_AcceptsExcludeChangeTypes_CaseInsensitively_AndTheShortResultFormat()
    {
        using var sandbox = new CliSandbox(nameof(WhatIf_AcceptsExcludeChangeTypes_CaseInsensitively_AndTheShortResultFormat));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();

        var result = await CliRunner.RunAsync("deployment", "what-if", "-f", template, "-x", "create", "NoChange", "-r", "resourceidonly", "-o", "json");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, result.Json["summary"]!["deploy"]!.GetValue<int>());
    }

    [Fact]
    public async Task AnUnknownChangeType_IsAUsageError_ListingTheArmNames()
    {
        var result = await CliRunner.RunAsync("deployment", "what-if", "-f", "t.entra.json", "-x", "Recreate");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Recreate", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("'Deploy'", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_TakesTheAzureCliNames_OfTheWhatIfOptions_AndW()
    {
        using var sandbox = new CliSandbox(nameof(Create_TakesTheAzureCliNames_OfTheWhatIfOptions_AndW));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();

        var result = await CliRunner.RunAsync("deployment", "create", "-f", template, "-w", "--what-if-exclude-change-types", "Ignore", "--what-if-result-format", "ResourceIdOnly", "-o", "json");

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.Json["changes"]);
        Assert.All(sandbox.RecordedCommands(), c => Assert.StartsWith("GET ", c, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProceedIfNoChange_DeploysWithoutAsking_WhenTheWhatIfShowsNoChange()
    {
        using var sandbox = new CliSandbox(nameof(ProceedIfNoChange_DeploysWithoutAsking_WhenTheWhatIfShowsNoChange));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();
        var invocation = new DeploymentInvocation
        {
            TemplateFile = template,
            ConfirmWithWhatIf = true,
            ProceedIfNoChange = true,
            Output = OutputFormat.Json,
            Connection = sandbox.Connection,
        };
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        // The reader would decline: it must not be asked.
        int exit = await DeploymentRunner.CreateAsync(invocation, stdout, stderr, () => "n", Ct);

        Assert.Equal(0, exit);
        Assert.Contains("deploying without confirmation (--proceed-if-no-change)", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Are you sure", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"status\": \"Succeeded\"", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidationLevelProvider_SignsInAndPlansReadOnly_TemplateStaysOffline()
    {
        using var sandbox = new CliSandbox(nameof(ValidationLevelProvider_SignsInAndPlansReadOnly_TemplateStaysOffline));
        string template = sandbox.WriteEmptyTemplate();

        var offline = await CliRunner.RunAsync("deployment", "validate", "-f", template, "-o", "json");
        Assert.Equal(0, offline.ExitCode);
        Assert.Empty(sandbox.RecordedCommands());

        sandbox.UsePreflightOk();
        var online = await CliRunner.RunAsync("deployment", "validate", "-f", template, "--validation-level", "provider", "-o", "json");

        Assert.Equal(0, online.ExitCode);
        Assert.Equal(0, online.Json["errorCount"]!.GetValue<int>());
        Assert.NotEmpty(sandbox.RecordedCommands());
        Assert.All(sandbox.RecordedCommands(), c => Assert.StartsWith("GET ", c, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidationLevelProvider_WithoutASignIn_ExitsThree()
    {
        using var sandbox = new CliSandbox(nameof(ValidationLevelProvider_WithoutASignIn_ExitsThree));
        sandbox.UseNotLoggedIn();
        string template = sandbox.WriteEmptyTemplate();

        var result = await CliRunner.RunAsync("deployment", "validate", "-f", template, "--validation-level", "ProviderNoRbac", "-o", "json");

        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task NoPrompt_IsAcceptedAsAFlagOrWithAValue()
    {
        using var sandbox = new CliSandbox(nameof(NoPrompt_IsAcceptedAsAFlagOrWithAValue));
        string template = sandbox.WriteEmptyTemplate();

        var flag = await CliRunner.RunAsync("deployment", "validate", "-f", template, "--no-prompt", "-o", "json");
        var valued = await CliRunner.RunAsync("deployment", "validate", "-f", template, "--no-prompt", "true", "-o", "json");

        Assert.Equal(0, flag.ExitCode);
        Assert.Equal(0, valued.ExitCode);
    }

    [Fact]
    public void ParameterPrompt_AsksEachMissingParameter_ReadsSecureOnesHidden_AndExplainsOnQuestionMark()
    {
        var answers = new Queue<string?>(["?", "dev"]);
        var secrets = new Queue<string?>(["s3cret"]);
        using var stderr = new StringWriter();
        var prompt = new ParameterPrompt(stderr, () => answers.Dequeue(), () => secrets.Dequeue());

        var result = prompt.Ask([Definition("env", "string", "The environment."), Definition("password", "securestring")]);

        Assert.NotNull(result);
        Assert.Equal([new KeyValuePair<string, string>("env", "dev"), new KeyValuePair<string, string>("password", "s3cret")], result);
        string text = stderr.ToString();
        Assert.Contains("Please provide string value for 'env' (? for help): ", text, StringComparison.Ordinal);
        Assert.Contains("The environment.", text, StringComparison.Ordinal);
        Assert.Contains("Please provide securestring value for 'password'", text, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ParameterPrompt_ReturnsNull_WhenInputEnds()
    {
        using var stderr = new StringWriter();
        var prompt = new ParameterPrompt(stderr, () => null, () => null);

        Assert.Null(prompt.Ask([Definition("env", "string")]));
    }

    private static ParameterDefinition Definition(string name, string type, string? description = null)
    {
        var source = new System.Text.Json.Nodes.JsonObject { ["type"] = type };
        return new ParameterDefinition
        {
            Name = name,
            Type = type,
            Source = source,
            Path = JsonPath.Of("parameters").Append(name),
            Metadata = description is null ? null : new System.Text.Json.Nodes.JsonObject { ["description"] = description },
        };
    }
}
