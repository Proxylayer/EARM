using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Cli;
using EArm.Cli.Commands;
using EArm.Engine;
using EArm.TestSupport;
using EArm.Tests.Engine;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// The CLI side of the review fixes: <c>create -c -o json</c> prints exactly one JSON document (B6), <c>--exit-code-on-changes</c> applies to <c>create</c>
/// (B16), a secret-bearing property does not make every rerun a change (B14), and the choice options are case-insensitive like the Azure CLI's.
/// Driven in-process against an in-memory directory.
/// </summary>
public sealed class ReviewFixesCliTests
{
    private const string LocType = "Microsoft.Entra/conditionalAccess/namedLocations";
    private const string LocPath = "/identity/conditionalAccess/namedLocations";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (CliSandbox Sandbox, FakeDirectory Directory) Setup(string test)
    {
        var sandbox = new CliSandbox(test);
        var directory = new FakeDirectory();
        sandbox.Handler.Directory = directory;
        sandbox.Tokens.TenantId = CliSandbox.Tenant;
        return (sandbox, directory);
    }

    /// <summary>A template that creates one country named location (nothing seeded).</summary>
    private static string NewLocationTemplate(CliSandbox sandbox)
    {
        return sandbox.WriteTemplate("loc.entra.json", "\"resources\": [ { \"type\": \"" + LocType + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"Contoso offices\", \"properties\": { \"@odata.type\": \"#microsoft.graph.countryNamedLocation\", \"countriesAndRegions\": [ \"GB\" ] } } ]");
    }

    /// <summary>An IP location in the tenant and a template that declares it as a country location: the kind is immutable, so the object is replaced.</summary>
    private static string SeedReplacement(CliSandbox sandbox, FakeDirectory directory)
    {
        directory.Add(LocPath, new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.ipNamedLocation",
            ["displayName"] = "Contoso offices",
            ["isTrusted"] = false,
            ["ipRanges"] = new JsonArray(new JsonObject { ["@odata.type"] = "#microsoft.graph.iPv4CidrRange", ["cidrAddress"] = "10.0.0.0/8" }),
        });
        return NewLocationTemplate(sandbox);
    }

    private static DeploymentInvocation Invocation(CliSandbox sandbox, string template, bool confirmWithWhatIf = false, bool yes = false, bool exitCodeOnChanges = false)
    {
        return new DeploymentInvocation
        {
            TemplateFile = template,
            ConfirmWithWhatIf = confirmWithWhatIf,
            Yes = yes,
            ExitCodeOnChanges = exitCodeOnChanges,
            Output = OutputFormat.Json,
            Connection = sandbox.Connection,
        };
    }

    private static int Writes(CliSandbox sandbox)
    {
        return sandbox.RecordedCommands().Count(c => c.StartsWith("DELETE ", StringComparison.Ordinal) || c.StartsWith("POST ", StringComparison.Ordinal) || c.StartsWith("PATCH ", StringComparison.Ordinal));
    }

    /// <summary>Parses standard output as exactly ONE JSON document: a second document (or trailing text) fails the parse.</summary>
    private static JsonObject SingleDocument(string stdout)
    {
        using var document = JsonDocument.Parse(stdout);
        return JsonNode.Parse(stdout)!.AsObject();
    }

    // ---- B6: create -c -o json is one document ----

    [Fact]
    public async Task CreateConfirmWithWhatIf_Yes_Json_PrintsOneDocument_TheResultWithTheWhatIfEmbedded()
    {
        var (sandbox, directory) = Setup(nameof(CreateConfirmWithWhatIf_Yes_Json_PrintsOneDocument_TheResultWithTheWhatIfEmbedded));
        using var _ = sandbox;
        string template = NewLocationTemplate(sandbox);

        var result = await CliRunner.RunAsync("deployment", "create", "-f", template, "-c", "-y", "-o", "json");

        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        var json = SingleDocument(result.StandardOutput);
        Assert.Equal("Succeeded", json["status"]!.GetValue<string>());
        Assert.NotNull(json["operations"]);
        var whatIf = json["whatIf"]!.AsObject();
        Assert.Equal("Create", Assert.Single(whatIf["changes"]!.AsArray())!["changeType"]!.GetValue<string>());
        Assert.Equal(1, whatIf["summary"]!["create"]!.GetValue<int>());
        Assert.NotNull(directory.Find(LocPath, "displayName", "Contoso offices"));
    }

    [Fact]
    public async Task CreateConfirmWithWhatIf_Json_Confirmed_ShowsTheWhatIfOnStderr_AndPrintsOneDocument()
    {
        var (sandbox, _) = Setup(nameof(CreateConfirmWithWhatIf_Json_Confirmed_ShowsTheWhatIfOnStderr_AndPrintsOneDocument));
        using var __ = sandbox;
        string template = NewLocationTemplate(sandbox);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exit = await DeploymentRunner.CreateAsync(Invocation(sandbox, template, confirmWithWhatIf: true), stdout, stderr, () => "y", Ct);

        Assert.True(exit == ExitCodes.Ok, stdout + stderr.ToString());
        var json = SingleDocument(stdout.ToString());
        Assert.Equal("Succeeded", json["status"]!.GetValue<string>());
        Assert.NotNull(json["whatIf"]);
        string err = stderr.ToString();
        Assert.Contains("+ /providers/" + LocType + "/Contoso offices", err, StringComparison.Ordinal);
        Assert.Contains("Are you sure you want to execute the deployment? (y/n):", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateConfirmWithWhatIf_Json_Declined_PrintsOneFailedDocumentWithTheWhatIf_AndWritesNothing()
    {
        var (sandbox, directory) = Setup(nameof(CreateConfirmWithWhatIf_Json_Declined_PrintsOneFailedDocumentWithTheWhatIf_AndWritesNothing));
        using var _ = sandbox;
        string template = NewLocationTemplate(sandbox);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exit = await DeploymentRunner.CreateAsync(Invocation(sandbox, template, confirmWithWhatIf: true), stdout, stderr, () => "n", Ct);

        Assert.Equal(ExitCodes.Failure, exit);
        var json = SingleDocument(stdout.ToString());
        Assert.Equal("Failed", json["status"]!.GetValue<string>());
        var diagnostic = Assert.Single(json["diagnostics"]!.AsArray())!;
        Assert.Equal("NotConfirmed", diagnostic["code"]!.GetValue<string>());
        Assert.Equal("Deployment not started.", diagnostic["message"]!.GetValue<string>());
        Assert.Equal(1, json["whatIf"]!["summary"]!["create"]!.GetValue<int>());
        Assert.Equal(0, Writes(sandbox));
        Assert.Null(directory.Find(LocPath, "displayName", "Contoso offices"));
    }

    [Fact]
    public async Task PlainCreate_WithoutYes_Json_ConfirmedReplacement_PrintsOnlyTheResultDocument()
    {
        // A create without -y plans with the read-only what-if pass first; that pass is never printed.
        var (sandbox, directory) = Setup(nameof(PlainCreate_WithoutYes_Json_ConfirmedReplacement_PrintsOnlyTheResultDocument));
        using var _ = sandbox;
        string template = SeedReplacement(sandbox, directory);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exit = await DeploymentRunner.CreateAsync(Invocation(sandbox, template), stdout, stderr, () => "y", Ct);

        Assert.True(exit == ExitCodes.Ok, stdout + stderr.ToString());
        var json = SingleDocument(stdout.ToString());
        Assert.Equal("Succeeded", json["status"]!.GetValue<string>());
        Assert.False(json.ContainsKey("whatIf"));
        Assert.Contains("Replace them? (y/n):", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlainCreate_WithoutYes_Json_NoReplacement_PrintsOnlyTheResultDocument()
    {
        var (sandbox, _) = Setup(nameof(PlainCreate_WithoutYes_Json_NoReplacement_PrintsOnlyTheResultDocument));
        using var __ = sandbox;
        string template = NewLocationTemplate(sandbox);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exit = await DeploymentRunner.CreateAsync(Invocation(sandbox, template), stdout, stderr, () => throw new InvalidOperationException("no prompt expected"), Ct);

        Assert.True(exit == ExitCodes.Ok, stdout + stderr.ToString());
        var json = SingleDocument(stdout.ToString());
        Assert.False(json.ContainsKey("whatIf"));
        Assert.Equal(string.Empty, stderr.ToString());
    }

    // ---- B16: --exit-code-on-changes on create ----

    [Fact]
    public async Task Create_ExitCodeOnChanges_ExitsFiveWhenTheRunWroteSomething_AndZeroOnAnUnchangedRerun()
    {
        var (sandbox, _) = Setup(nameof(Create_ExitCodeOnChanges_ExitsFiveWhenTheRunWroteSomething_AndZeroOnAnUnchangedRerun));
        using var __ = sandbox;
        string template = NewLocationTemplate(sandbox);

        var first = await CliRunner.RunAsync("deployment", "create", "-f", template, "-y", "--exit-code-on-changes", "-o", "json");
        var rerun = await CliRunner.RunAsync("deployment", "create", "-f", template, "-y", "--exit-code-on-changes", "-o", "json");
        var withoutFlag = await CliRunner.RunAsync("deployment", "create", "-f", template, "-y", "-o", "json");

        Assert.True(first.ExitCode == ExitCodes.WhatIfChangesDetected, first.StandardOutput + first.StandardError);
        Assert.Equal("Succeeded", first.Json["status"]!.GetValue<string>());
        Assert.True(rerun.ExitCode == ExitCodes.Ok, rerun.StandardOutput + rerun.StandardError);
        Assert.Equal(ExitCodes.Ok, withoutFlag.ExitCode);
    }

    [Fact]
    public async Task Create_ExitCodeOnChanges_WithConfirmWithWhatIf_ReflectsTheAppliedChanges()
    {
        var (sandbox, _) = Setup(nameof(Create_ExitCodeOnChanges_WithConfirmWithWhatIf_ReflectsTheAppliedChanges));
        using var __ = sandbox;
        string template = NewLocationTemplate(sandbox);

        var first = await CliRunner.RunAsync("deployment", "create", "-f", template, "-c", "-y", "--exit-code-on-changes", "-o", "json");
        var rerun = await CliRunner.RunAsync("deployment", "create", "-f", template, "-c", "-y", "--exit-code-on-changes", "-o", "json");

        Assert.Equal(ExitCodes.WhatIfChangesDetected, first.ExitCode);
        Assert.Equal(ExitCodes.Ok, rerun.ExitCode);
        Assert.Equal(0, rerun.Json["whatIf"]!["summary"]!["create"]!.GetValue<int>());
    }

    [Fact]
    public async Task Create_ExitCodeOnChanges_EmptyTemplate_ExitsZero()
    {
        using var sandbox = new CliSandbox(nameof(Create_ExitCodeOnChanges_EmptyTemplate_ExitsZero));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();

        var result = await CliRunner.RunAsync("deployment", "create", "-f", template, "-y", "--exit-code-on-changes", "-o", "json");

        Assert.True(result.ExitCode == ExitCodes.Ok, result.StandardOutput + result.StandardError);
    }

    // ---- B14: a secret-bearing property is not a change on every rerun ----

    [Fact]
    public async Task ASecretBearingProperty_IsNotAChangeOnARerun_SoExitCodeOnChangesStaysZero()
    {
        var (sandbox, _) = Setup(nameof(ASecretBearingProperty_IsNotAChangeOnARerun_SoExitCodeOnChangesStaysZero));
        using var __ = sandbox;
        string template = sandbox.WriteTemplate("grp.entra.json",
            "\"parameters\": { \"note\": { \"type\": \"securestring\" } }, \"resources\": [ { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"contoso-secret-note\", \"properties\": { \"displayName\": \"Secret note\", \"mailEnabled\": false, \"securityEnabled\": true, \"mailNickname\": \"contoso-secret-note\", \"description\": \"[parameters('note')]\" } } ]");

        var create = await CliRunner.RunAsync("deployment", "create", "-f", template, "-p", "note=s3cret-value", "-y", "-o", "json");
        var whatIf = await CliRunner.RunAsync("deployment", "what-if", "-f", template, "-p", "note=s3cret-value", "--exit-code-on-changes", "-o", "json");
        int writesBefore = Writes(sandbox);
        var rerun = await CliRunner.RunAsync("deployment", "create", "-f", template, "-p", "note=s3cret-value", "-y", "--exit-code-on-changes", "-o", "json");

        Assert.True(create.ExitCode == ExitCodes.Ok, create.StandardOutput + create.StandardError);
        Assert.True(whatIf.ExitCode == ExitCodes.Ok, whatIf.StandardOutput);
        Assert.Equal("NoChange", Assert.Single(whatIf.Json["changes"]!.AsArray())!["changeType"]!.GetValue<string>());
        Assert.DoesNotContain("s3cret-value", whatIf.StandardOutput, StringComparison.Ordinal);
        Assert.True(rerun.ExitCode == ExitCodes.Ok, rerun.StandardOutput + rerun.StandardError);
        Assert.Equal(writesBefore, Writes(sandbox));
    }

    // ---- choice options are case-insensitive (as in the Azure CLI) ----

    [Theory]
    [InlineData("--mode", "complete")]
    [InlineData("--mode", "INCREMENTAL")]
    [InlineData("--result-format", "resourceidonly")]
    [InlineData("--on-soft-deleted-conflict", "Restore")]
    [InlineData("--auth", "Auto")]
    public async Task ChoiceOptions_AcceptAnyCase(string option, string value)
    {
        using var sandbox = new CliSandbox(nameof(ChoiceOptions_AcceptAnyCase));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();

        var result = await CliRunner.RunAsync("deployment", "what-if", "-f", template, option, value, "-o", "json");

        Assert.True(result.ExitCode == ExitCodes.Ok, result.StandardOutput + result.StandardError);
        if (option == "--mode")
        {
            Assert.Equal(value.Equals("complete", StringComparison.OrdinalIgnoreCase) ? "Complete" : "Incremental", result.Json["mode"]!.GetValue<string>());
        }
    }

    [Theory]
    [InlineData("JSON")]
    [InlineData("Json")]
    public async Task OutputOption_AcceptsAnyCase(string value)
    {
        using var sandbox = new CliSandbox(nameof(OutputOption_AcceptsAnyCase));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();

        var result = await CliRunner.RunAsync("deployment", "what-if", "-f", template, "-o", value);

        Assert.True(result.ExitCode == ExitCodes.Ok, result.StandardOutput + result.StandardError);
        Assert.Equal("Succeeded", result.Json["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task PermissionsModeOption_AcceptsAnyCase()
    {
        var result = await CliRunner.RunAsync("permissions", "-f", Templates.FixturePath, "-p", "env=dev", "-p", "initialPassword=x", "--mode", "Delegated", "-o", "json");

        Assert.True(result.ExitCode == ExitCodes.Ok, result.StandardOutput + result.StandardError);
        Assert.Equal("delegated", result.Json["mode"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("--mode", "sometimes")]
    [InlineData("-o", "xml")]
    public async Task ChoiceOptions_StillRejectAnUnknownValue(string option, string value)
    {
        using var sandbox = new CliSandbox(nameof(ChoiceOptions_StillRejectAnUnknownValue));
        string template = sandbox.WriteEmptyTemplate();

        var result = await CliRunner.RunAsync("deployment", "what-if", "-f", template, option, value);

        Assert.Equal(ExitCodes.Failure, result.ExitCode);
        Assert.Contains($"Argument '{value}' not recognized", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(sandbox.RecordedCommands());
    }
}
