using System;
using System.Linq;
using System.Threading.Tasks;
using EArm.TestSupport;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// Review 1D / Q-03 and I-01 at the CLI seam: <c>--allow-ca-enforce</c> reaches the planner and the provider (mutants that dropped the
/// flag between the command line and <c>PlanOptions</c> survived), and a securestring parameter cannot smuggle <c>state: enabled</c> past it.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class Review1D_CaFlagTests
{
    private const string PoliciesPath = "/identity/conditionalAccess/policies";

    private static string Policy(string state) =>
        "{ \"type\": \"Microsoft.Entra/conditionalAccess/policies\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"CA-Enforced\", \"properties\": { \"state\": " + state + ", " +
        "\"conditions\": { \"users\": { \"includeGroups\": [\"aaaaaaaa-1111-2222-3333-444444444444\"] }, \"applications\": { \"includeApplications\": [\"All\"] } }, " +
        "\"grantControls\": { \"builtInControls\": [\"mfa\"] } } }";

    private static string[] Deploy(CliSandbox sandbox, string template, string verb, params string[] extra)
    {
        return ["deployment", verb, "-f", template, "-o", "json", .. extra];
    }

    [Fact]
    public async Task StateEnabled_IsRejectedWithoutTheFlag_AndAcceptedWithIt()
    {
        using var sandbox = new CliSandbox(nameof(StateEnabled_IsRejectedWithoutTheFlag_AndAcceptedWithIt));
        var directory = new FakeDirectory();
        sandbox.Handler.Directory = directory;
        string template = sandbox.WriteTemplate("enforced.entra.json", "\"resources\": [ " + Policy("\"enabled\"") + " ]");

        var whatIf = await CliRunner.RunAsync(Deploy(sandbox, template, "what-if"));
        var refused = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y"));

        Assert.Contains("--allow-ca-enforce", whatIf.StandardOutput + whatIf.StandardError, StringComparison.Ordinal);
        Assert.NotEqual(0, refused.ExitCode);
        Assert.Contains("--allow-ca-enforce", refused.StandardOutput + refused.StandardError, StringComparison.Ordinal);
        Assert.Empty(directory.Objects(PoliciesPath));
        Assert.DoesNotContain(sandbox.RecordedRequests(), r => r.Method != "GET");

        var allowed = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y", "--allow-ca-enforce"));

        Assert.Equal(0, allowed.ExitCode);
        Assert.Equal("enabled", Assert.Single(directory.Objects(PoliciesPath))["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task WhatIf_WithTheFlag_NoLongerReportsTheEnforcementRefusal()
    {
        using var sandbox = new CliSandbox(nameof(WhatIf_WithTheFlag_NoLongerReportsTheEnforcementRefusal));
        sandbox.Handler.Directory = new FakeDirectory();
        string template = sandbox.WriteTemplate("enforced.entra.json", "\"resources\": [ " + Policy("\"enabled\"") + " ]");

        var plain = await CliRunner.RunAsync(Deploy(sandbox, template, "what-if"));
        var flagged = await CliRunner.RunAsync(Deploy(sandbox, template, "what-if", "--allow-ca-enforce"));

        Assert.Contains("--allow-ca-enforce", plain.StandardOutput + plain.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("enforces the policy", flagged.StandardOutput + flagged.StandardError, StringComparison.Ordinal);
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

        var refused = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y"));

        Assert.NotEqual(0, refused.ExitCode);
        Assert.Contains("--allow-ca-enforce", refused.StandardOutput + refused.StandardError, StringComparison.Ordinal);
        Assert.Empty(directory.Objects(PoliciesPath));
        Assert.DoesNotContain(sandbox.RecordedRequests(), r => r.Method != "GET");

        var allowed = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y", "--allow-ca-enforce"));

        Assert.Equal(0, allowed.ExitCode);
        Assert.Equal("enabled", Assert.Single(directory.Objects(PoliciesPath))["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReportingOnlyState_NeedsNoFlag()
    {
        using var sandbox = new CliSandbox(nameof(ReportingOnlyState_NeedsNoFlag));
        var directory = new FakeDirectory();
        sandbox.Handler.Directory = directory;
        string template = sandbox.WriteTemplate("report.entra.json", "\"resources\": [ " + Policy("\"enabledForReportingButNotEnforced\"") + " ]");

        var run = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y"));

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("enabledForReportingButNotEnforced", Assert.Single(directory.Objects(PoliciesPath))["state"]!.GetValue<string>());
    }
}
