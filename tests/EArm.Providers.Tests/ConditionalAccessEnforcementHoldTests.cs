using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EArm.Core.Diagnostics;
using EArm.Engine.Providers;
using EArm.Engine.WhatIf;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// The <c>--allow-ca-enforce</c> rule end to end: a declared <c>state: enabled</c> without the flag creates a new policy report-only and
/// leaves an existing policy's live state as it is (no <c>state</c> in the PATCH), with a <c>ConditionalAccessEnforcementNotAllowed</c>
/// warning and a noted <c>state</c> delta in what-if; with the flag the declared state is applied; declared <c>disabled</c> /
/// report-only and an undeclared state are not affected.
/// </summary>
public sealed class ConditionalAccessEnforcementHoldTests
{
    private const string PolType = ConditionalAccessPoliciesProvider.ResourceType;
    private const string PolPath = "/identity/conditionalAccess/policies";
    private const string GroupId = "aaaaaaaa-1111-2222-3333-444444444444";
    private const string ReportOnly = ConditionalAccessPoliciesRules.DefaultState;
    private const string Code = ProviderWarnings.ConditionalAccessEnforcementNotAllowed;

    private static string Policy(string? state, string controls = "[\"mfa\"]")
    {
        string stateJson = state is null ? string.Empty : "\"state\": \"" + state + "\", ";
        return Templates.Resource(PolType, "CA-Hold", "{ " + stateJson + "\"conditions\": { \"users\": { \"includeGroups\": [\"" + GroupId + "\"] }, \"applications\": { \"includeApplications\": [\"All\"] } }, \"grantControls\": { \"builtInControls\": " + controls + " } }");
    }

    private static async Task<ProviderHost> WithExistingPolicyAsync(string liveState, bool allow = false)
    {
        var host = new ProviderHost { AllowCaEnforce = true };
        Check.Succeeded(await host.CreateAsync(host.Resources(Policy(liveState))));
        host.AllowCaEnforce = allow;
        return host;
    }

    private static string LiveState(ProviderHost host)
    {
        return host.Directory.Objects(PolPath).Single()["state"]!.GetValue<string>();
    }

    private static bool HasWarning(IEnumerable<Diagnostic> diagnostics)
    {
        return diagnostics.Any(d => d.Code == Code && d.Severity == DiagnosticSeverity.Warning);
    }

    // ---- new policy ----

    [Fact]
    public async Task Create_WithoutTheFlag_IsReportOnly_WithAWarning_InWhatIfAndCreate()
    {
        var host = new ProviderHost();
        var document = host.Resources(Policy("enabled"));

        var whatIf = await host.WhatIfAsync(document);
        var row = Assert.Single(whatIf.Plan.Changes);
        Assert.Equal(ChangeType.Create, row.Type);
        Assert.False(row.HasError);
        Assert.Equal(ReportOnly, row.After!["state"]!.GetValue<string>());
        var delta = Assert.Single(row.Deltas, d => d.Path.ToString() == "properties.state");
        Assert.Equal("declared enabled, kept enabledForReportingButNotEnforced; pass --allow-ca-enforce to enforce", delta.Note);
        Assert.True(HasWarning(whatIf.Plan.Diagnostics));

        var run = await host.CreateAsync(document);
        Check.Succeeded(run);
        Assert.Equal(ReportOnly, Check.Body(Assert.Single(run.Writes))["state"]!.GetValue<string>());
        Assert.Equal(ReportOnly, LiveState(host));
        Assert.True(HasWarning(run.Executed.Diagnostics));
    }

    [Fact]
    public async Task Create_WithTheFlag_IsEnforced_WithoutAWarning()
    {
        var host = new ProviderHost { AllowCaEnforce = true };

        var run = await host.CreateAsync(host.Resources(Policy("enabled")));

        Check.Succeeded(run);
        Assert.Equal("enabled", Check.Body(Assert.Single(run.Writes))["state"]!.GetValue<string>());
        Assert.False(HasWarning(run.Executed.Diagnostics));
        Assert.False(HasWarning(run.Plan.Diagnostics));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData(ReportOnly)]
    public async Task Create_OfAStateThatDoesNotEnforce_IsAppliedAsDeclared(string state)
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(Policy(state)));

        Check.Succeeded(run);
        Assert.Equal(state, Check.Body(Assert.Single(run.Writes))["state"]!.GetValue<string>());
        Assert.False(HasWarning(run.Executed.Diagnostics));
    }

    [Fact]
    public async Task Create_WithAnUndeclaredState_KeepsTheReportOnlyDefault_WithoutAWarning()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(Policy(null)));

        Check.Succeeded(run);
        Assert.Equal(ReportOnly, Check.Body(Assert.Single(run.Writes))["state"]!.GetValue<string>());
        Assert.False(HasWarning(run.Executed.Diagnostics));
    }

    // ---- existing policy ----

    [Theory]
    [InlineData("disabled")]
    [InlineData(ReportOnly)]
    public async Task Existing_WithoutTheFlag_KeepsTheLiveState_AsNoChange_WithANotedDelta(string liveState)
    {
        var host = await WithExistingPolicyAsync(liveState);
        var document = host.Resources(Policy("enabled"));

        var whatIf = await host.WhatIfAsync(document);
        var row = Assert.Single(whatIf.Plan.Changes);
        Assert.Equal(ChangeType.NoChange, row.Type);
        Assert.False(row.IsWrite);
        Assert.False(whatIf.Plan.HasChanges);
        var delta = Assert.Single(row.Deltas, d => d.Note is not null);
        Assert.Equal(PropertyChangeType.NoEffect, delta.Kind);
        Assert.Equal(liveState, delta.Before!.GetValue<string>());
        Assert.Equal("enabled", delta.After!.GetValue<string>());
        Assert.Contains(row.Reasons, r => r.StartsWith(Code + ":", StringComparison.Ordinal) && r.Contains(liveState, StringComparison.Ordinal));
        Assert.True(HasWarning(whatIf.Plan.Diagnostics));

        var run = await host.CreateAsync(document);
        Check.Succeeded(run);
        Check.NoWrites(run);
        Assert.False(run.Executed.HasChanges);
        Assert.Equal(liveState, LiveState(host));
        Assert.True(HasWarning(run.Executed.Diagnostics));
    }

    [Fact]
    public async Task Existing_WithoutTheFlag_StillUpdatesTheOtherProperties_WithoutStateInThePatch()
    {
        var host = await WithExistingPolicyAsync("disabled");

        var run = await host.CreateAsync(host.Resources(Policy("enabled", "[\"mfa\", \"compliantDevice\"]")));

        Check.Succeeded(run);
        var patch = Check.Body(Assert.Single(run.Writes));
        Assert.False(patch.ContainsKey("state"));
        Assert.True(patch.ContainsKey("grantControls"));
        Assert.Equal("disabled", LiveState(host));
        Assert.Equal(ChangeType.Modify, Assert.Single(run.Changes).Type);
        Assert.True(HasWarning(run.Executed.Diagnostics));
    }

    [Fact]
    public async Task Existing_WithTheFlag_IsEnforced()
    {
        var host = await WithExistingPolicyAsync(ReportOnly, allow: true);

        var run = await host.CreateAsync(host.Resources(Policy("enabled")));

        Check.Succeeded(run);
        Assert.Equal("enabled", Check.Body(Assert.Single(run.Writes))["state"]!.GetValue<string>());
        Assert.Equal("enabled", LiveState(host));
        Assert.False(HasWarning(run.Executed.Diagnostics));
    }

    [Fact]
    public async Task Existing_AlreadyEnabledLive_WithoutTheFlag_IsNoChange_WithoutAWarning()
    {
        var host = await WithExistingPolicyAsync("enabled");

        var run = await host.CreateAsync(host.Resources(Policy("enabled")));

        Check.Succeeded(run);
        Check.NoWrites(run);
        Assert.False(HasWarning(run.Executed.Diagnostics));
    }

    [Theory]
    [InlineData("enabled", "disabled")]
    [InlineData("enabled", ReportOnly)]
    [InlineData(ReportOnly, "disabled")]
    public async Task Existing_DeclaredStateThatDoesNotEnforce_IsAppliedWithoutTheFlag(string liveState, string declared)
    {
        var host = await WithExistingPolicyAsync(liveState);

        var run = await host.CreateAsync(host.Resources(Policy(declared)));

        Check.Succeeded(run);
        Assert.Equal(declared, Check.Body(Assert.Single(run.Writes))["state"]!.GetValue<string>());
        Assert.Equal(declared, LiveState(host));
        Assert.False(HasWarning(run.Executed.Diagnostics));
    }

    [Fact]
    public async Task Existing_WithAnUndeclaredState_NeverWritesTheState()
    {
        var host = await WithExistingPolicyAsync("enabled");

        var run = await host.CreateAsync(host.Resources(Policy(null, "[\"mfa\", \"compliantDevice\"]")));

        Check.Succeeded(run);
        Assert.False(Check.Body(Assert.Single(run.Writes)).ContainsKey("state"));
        Assert.Equal("enabled", LiveState(host));
        Assert.False(HasWarning(run.Executed.Diagnostics));
    }

    // ---- executor path ----

    [Fact]
    public async Task DirectUpdate_ReappliesTheRule_ToAFreshlyEvaluatedState()
    {
        // The executor plans again against a fresh read; a direct UpdateAsync of re-evaluated properties (state back to "enabled")
        // must still leave the live state alone.
        var host = await WithExistingPolicyAsync("disabled");
        var whatIf = await host.WhatIfAsync(host.Resources(Policy("enabled", "[\"mfa\", \"compliantDevice\"]")));
        var instance = whatIf.Plan.Instances.Single();
        var change = Assert.Single(whatIf.Plan.Changes);
        instance.Properties!["state"] = "enabled";
        var provider = host.Provider<ConditionalAccessPoliciesProvider>(PolType);
        provider.PlanOptions = PlanOptions.Default;
        var existing = await provider.ReadAsync(provider.ResolveKey(instance), host.Fake, ProviderHost.Ct);
        host.ClearRequests();

        var applied = await provider.UpdateAsync(instance, existing!, change, host.Fake, ProviderHost.Ct);

        Assert.False(Check.Body(Assert.Single(host.Fake.Writes)).ContainsKey("state"));
        Assert.Equal(Code, Assert.Single(applied.Warnings).Code);
        Assert.Equal("disabled", LiveState(host));
    }

    // ---- what-if rendering ----

    [Fact]
    public async Task WhatIfText_AndJson_ShowTheHeldBackStateMarker()
    {
        var host = await WithExistingPolicyAsync("disabled");
        var stateOnly = await host.WhatIfAsync(host.Resources(Policy("enabled")));
        var modify = await host.WhatIfAsync(host.Resources(Policy("enabled", "[\"mfa\", \"compliantDevice\"]")));

        var text = new StringWriter();
        WhatIfText.Render(stateOnly.Plan, text);
        var modifyText = new StringWriter();
        WhatIfText.Render(modify.Plan, modifyText);
        const string Marker = "= properties.state: \"disabled\" => \"enabled\" (declared enabled, live value kept; pass --allow-ca-enforce to enforce)";
        Assert.Contains(Marker, text.ToString(), StringComparison.Ordinal);
        Assert.Contains("(" + Code + ": state: declared enabled, live value kept (disabled)", text.ToString(), StringComparison.Ordinal);
        Assert.Contains("warning " + Code, text.ToString(), StringComparison.Ordinal);
        Assert.Contains(Marker, modifyText.ToString(), StringComparison.Ordinal);
        Assert.False(WhatIfReport.From(stateOnly.Plan).HasChanges);
        Assert.True(WhatIfReport.From(modify.Plan).HasChanges);

        var json = WhatIfJson.Build(stateOnly.Plan);
        var row = Assert.Single(json["changes"]!.AsArray())!;
        Assert.Equal("NoChange", row["changeType"]!.GetValue<string>());
        var delta = Assert.Single(row["delta"]!.AsArray())!;
        Assert.Equal("NoEffect", delta["propertyChangeType"]!.GetValue<string>());
        Assert.Equal("declared enabled, live value kept; pass --allow-ca-enforce to enforce", delta["note"]!.GetValue<string>());
        Assert.Contains(json["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == Code);

        var modifyRow = Assert.Single(WhatIfJson.Build(modify.Plan)["changes"]!.AsArray())!;
        Assert.Contains(modifyRow["delta"]!.AsArray(), d => d!["note"] is not null && d["path"]!.GetValue<string>() == "properties.state");
    }

    [Fact]
    public async Task WhatIfText_ForACreate_ShowsTheReportOnlyPayload_AndTheWarning()
    {
        var host = new ProviderHost();
        var whatIf = await host.WhatIfAsync(host.Resources(Policy("enabled")));

        var text = new StringWriter();
        WhatIfText.Render(whatIf.Plan, text);

        Assert.Contains("state: \"" + ReportOnly + "\"", text.ToString(), StringComparison.Ordinal);
        Assert.Contains("(" + Code + ": state: declared enabled, kept " + ReportOnly, text.ToString(), StringComparison.Ordinal);
    }
}
