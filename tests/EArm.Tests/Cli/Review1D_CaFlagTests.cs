using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.TestSupport;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// Review 1D / Q-03 and I-01 at the CLI seam: <c>--allow-ca-enforce</c> reaches the planner and the provider (mutants that dropped the
/// flag between the command line and <c>PlanOptions</c> survived), and a securestring parameter cannot smuggle <c>state: enabled</c> past it.
/// Without the flag a declared <c>state: enabled</c> is held back, never refused: a new policy is created report-only, an existing policy
/// keeps its live state, and the row carries the <c>ConditionalAccessEnforcementNotAllowed</c> warning.
/// </summary>
public sealed class Review1D_CaFlagTests
{
    private const string PoliciesPath = "/identity/conditionalAccess/policies";
    private const string ReportOnly = "enabledForReportingButNotEnforced";
    private const string WarningCode = "ConditionalAccessEnforcementNotAllowed";
    private const int ChangesDetected = 5;

    private static string Policy(string state, string controls = "[\"mfa\"]") =>
        "{ \"type\": \"Microsoft.Entra/conditionalAccess/policies\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"CA-Enforced\", \"properties\": { \"state\": " + state + ", " +
        "\"conditions\": { \"users\": { \"includeGroups\": [\"aaaaaaaa-1111-2222-3333-444444444444\"] }, \"applications\": { \"includeApplications\": [\"All\"] } }, " +
        "\"grantControls\": { \"builtInControls\": " + controls + " } } }";

    private static string[] Deploy(CliSandbox sandbox, string template, string verb, params string[] extra)
    {
        return ["deployment", verb, "-f", template, "-o", "json", .. extra];
    }

    private static string[] DeployText(string template, string verb, params string[] extra)
    {
        return ["deployment", verb, "-f", template, .. extra];
    }

    private static string LiveState(FakeDirectory directory)
    {
        return Assert.Single(directory.Objects(PoliciesPath))["state"]!.GetValue<string>();
    }

    [Fact]
    public async Task StateEnabled_IsCreatedReportOnlyWithoutTheFlag_AndEnforcedWithIt()
    {
        using var sandbox = new CliSandbox(nameof(StateEnabled_IsCreatedReportOnlyWithoutTheFlag_AndEnforcedWithIt));
        var directory = new FakeDirectory();
        sandbox.Handler.Directory = directory;
        string template = sandbox.WriteTemplate("enforced.entra.json", "\"resources\": [ " + Policy("\"enabled\"") + " ]");

        var whatIf = await CliRunner.RunAsync(Deploy(sandbox, template, "what-if"));
        Assert.Equal(0, whatIf.ExitCode);
        var change = Assert.Single(JsonNode.Parse(whatIf.StandardOutput)!["changes"]!.AsArray())!;
        Assert.Equal("Create", change["changeType"]!.GetValue<string>());
        Assert.Equal(ReportOnly, change["after"]!["properties"]!["state"]!.GetValue<string>());
        Assert.Contains(change["reasons"]!.AsArray(), r => r!.GetValue<string>().StartsWith(WarningCode + ":", StringComparison.Ordinal));

        var created = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y"));
        Assert.Equal(0, created.ExitCode);
        Assert.Contains(WarningCode, created.StandardOutput + created.StandardError, StringComparison.Ordinal);
        Assert.Equal(ReportOnly, LiveState(directory));

        var allowed = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y", "--allow-ca-enforce"));
        Assert.Equal(0, allowed.ExitCode);
        Assert.Equal("enabled", LiveState(directory));
    }

    [Fact]
    public async Task WhatIfText_ShowsTheWarningAndTheHeldBackStateDelta_ForAnExistingPolicy()
    {
        using var sandbox = new CliSandbox(nameof(WhatIfText_ShowsTheWarningAndTheHeldBackStateDelta_ForAnExistingPolicy));
        var directory = new FakeDirectory();
        sandbox.Handler.Directory = directory;
        string disabled = sandbox.WriteTemplate("disabled.entra.json", "\"resources\": [ " + Policy("\"disabled\"") + " ]");
        Assert.Equal(0, (await CliRunner.RunAsync(Deploy(sandbox, disabled, "create", "-y"))).ExitCode);
        string enforced = sandbox.WriteTemplate("enforced.entra.json", "\"resources\": [ " + Policy("\"enabled\"") + " ]");

        var text = await CliRunner.RunAsync(DeployText(enforced, "what-if"));

        Assert.Equal(0, text.ExitCode);
        Assert.Contains("= properties.state: \"disabled\" => \"enabled\" (declared enabled, live value kept; pass --allow-ca-enforce to enforce)", text.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("warning " + WarningCode, text.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("0 to create, 0 to modify", text.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCodeOnChanges_IgnoresAHeldBackState_ButNotARealChange()
    {
        using var sandbox = new CliSandbox(nameof(ExitCodeOnChanges_IgnoresAHeldBackState_ButNotARealChange));
        var directory = new FakeDirectory();
        sandbox.Handler.Directory = directory;
        string disabled = sandbox.WriteTemplate("disabled.entra.json", "\"resources\": [ " + Policy("\"disabled\"") + " ]");
        Assert.Equal(0, (await CliRunner.RunAsync(Deploy(sandbox, disabled, "create", "-y"))).ExitCode);
        string stateOnly = sandbox.WriteTemplate("state-only.entra.json", "\"resources\": [ " + Policy("\"enabled\"") + " ]");
        string withControl = sandbox.WriteTemplate("with-control.entra.json", "\"resources\": [ " + Policy("\"enabled\"", "[\"mfa\", \"compliantDevice\"]") + " ]");

        var whatIfStateOnly = await CliRunner.RunAsync(Deploy(sandbox, stateOnly, "what-if", "--exit-code-on-changes"));
        var createStateOnly = await CliRunner.RunAsync(Deploy(sandbox, stateOnly, "create", "-y", "--exit-code-on-changes"));
        var whatIfWithControl = await CliRunner.RunAsync(Deploy(sandbox, withControl, "what-if", "--exit-code-on-changes"));

        Assert.Equal(0, whatIfStateOnly.ExitCode);
        var row = Assert.Single(JsonNode.Parse(whatIfStateOnly.StandardOutput)!["changes"]!.AsArray())!;
        Assert.Equal("NoChange", row["changeType"]!.GetValue<string>());
        var delta = Assert.Single(row["delta"]!.AsArray())!;
        Assert.Equal("properties.state", delta["path"]!.GetValue<string>());
        Assert.Equal("NoEffect", delta["propertyChangeType"]!.GetValue<string>());
        Assert.Contains("live value kept", delta["note"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(0, createStateOnly.ExitCode);
        Assert.Equal(ChangesDetected, whatIfWithControl.ExitCode);
        Assert.Equal("disabled", LiveState(directory));
    }

    [Fact]
    public async Task ASecurestringParameter_CannotSmuggleStateEnabledPastTheFlag()
    {
        using var sandbox = new CliSandbox(nameof(ASecurestringParameter_CannotSmuggleStateEnabledPastTheFlag));
        var directory = new FakeDirectory();
        sandbox.Handler.Directory = directory;
        string template = sandbox.WriteTemplate(
            "smuggled.entra.json",
            "\"parameters\": { \"s\": { \"type\": \"securestring\", \"defaultValue\": \"enabled\" } }, \"resources\": [ " + Policy("\"[parameters('s')]\"") + " ]");

        var heldBack = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y"));

        Assert.Equal(0, heldBack.ExitCode);
        Assert.Contains(WarningCode, heldBack.StandardOutput + heldBack.StandardError, StringComparison.Ordinal);
        Assert.Equal(ReportOnly, LiveState(directory));

        // A secure value never updates an existing object (it is create-only), so the flagged run starts from an empty tenant.
        var fresh = new FakeDirectory();
        sandbox.Handler.Directory = fresh;
        var allowed = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y", "--allow-ca-enforce"));

        Assert.Equal(0, allowed.ExitCode);
        Assert.Equal("enabled", LiveState(fresh));
    }

    [Fact]
    public async Task ReportingOnlyState_NeedsNoFlag_AndRaisesNoWarning()
    {
        using var sandbox = new CliSandbox(nameof(ReportingOnlyState_NeedsNoFlag_AndRaisesNoWarning));
        var directory = new FakeDirectory();
        sandbox.Handler.Directory = directory;
        string template = sandbox.WriteTemplate("report.entra.json", "\"resources\": [ " + Policy("\"" + ReportOnly + "\"") + " ]");

        var run = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y"));

        Assert.Equal(0, run.ExitCode);
        Assert.DoesNotContain(WarningCode, run.StandardOutput + run.StandardError, StringComparison.Ordinal);
        Assert.Equal(ReportOnly, LiveState(directory));
    }
}
