using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EntraArm.Engine;
using EntraArm.Engine.Execution;
using EntraArm.Engine.State;
using EntraArm.Providers.Tests.Infrastructure;
using Xunit;
using static EntraArm.Providers.Tests.Infrastructure.DirectoryTemplates;

namespace EntraArm.Providers.Tests;

/// <summary>
/// Failures that only exist while a deployment is being applied (values computed from live objects, conditions that depend on
/// a created resource, outputs that cannot be evaluated): each fails its own resource with a stable code, skips its dependents,
/// leaves the rest of the deployment alone and never records a half-applied state entry.
/// </summary>
public sealed class ApplyTimeFailureTests
{
    private const string Base = "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"base\", \"properties\": { \"displayName\": \"Base\" } }";

    private static string Dependent(string name, string properties, string? extra = null)
    {
        return "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", \"properties\": { " + properties + " }"
            + (extra is null ? string.Empty : ", " + extra) + ", \"dependsOn\": [\"[resourceId('Microsoft.Entra/groups', 'base')]\"] }";
    }

    [Fact]
    public async Task AnExpressionThatFailsAtApplyTime_FailsOnlyThatResource_AndSkipsItsDependents()
    {
        var host = new ProviderHost();
        string failing = Dependent("divides", "\"displayName\": \"[string(div(1, sub(length(reference('base').displayName), 4)))]\"");
        string dependent = "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"after\", \"properties\": { \"displayName\": \"After\" }, \"dependsOn\": [\"[resourceId('Microsoft.Entra/groups', 'divides')]\"] }";
        string independent = Dependent("fine", "\"displayName\": \"Fine\"");

        var run = await host.CreateAsync(host.Resources(Join(Base, failing, dependent, independent)), continueOnError: true);

        Assert.False(run.Succeeded);
        var op = run.Op("divides");
        Assert.Equal(OperationStatus.Failed, op.Status);
        Assert.False(string.IsNullOrEmpty(op.ErrorCode));
        Assert.Contains("divides", op.Error, StringComparison.Ordinal);
        Assert.Equal(OperationStatus.Skipped, run.Op("after").Status);
        Assert.Equal(OperationStatus.Succeeded, run.Op("fine").Status);
        Assert.Null(host.Directory.Find("/groups", "uniqueName", "divides"));
        Assert.DoesNotContain(run.State!.Resources.Keys, k => k.EndsWith("/divides", StringComparison.Ordinal));
        Assert.Contains(run.State.Resources.Keys, k => k.EndsWith("/base", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AConditionThatIsNotABoolean_AtApplyTime_IsConditionUnresolved()
    {
        var host = new ProviderHost();
        string conditional = Dependent("conditional", "\"displayName\": \"C\"", "\"condition\": \"[reference('base').displayName]\"");

        var run = await host.CreateAsync(host.Resources(Join(Base, conditional)));

        Assert.False(run.Succeeded);
        var op = run.Op("conditional");
        Assert.Equal(OperationStatus.Failed, op.Status);
        Assert.Equal(EngineDiagnosticCodes.ConditionUnresolved, op.ErrorCode);
        Assert.Null(host.Directory.Find("/groups", "uniqueName", "conditional"));
    }

    [Fact]
    public async Task AConditionOnALiveValue_IsEvaluatedAfterTheDependencyExists()
    {
        var host = new ProviderHost();
        string yes = Dependent("yes", "\"displayName\": \"Y\"", "\"condition\": \"[equals(reference('base').displayName, 'Base')]\"");
        string no = Dependent("no", "\"displayName\": \"N\"", "\"condition\": \"[equals(reference('base').displayName, 'Other')]\"");

        var run = await host.CreateAsync(host.Resources(Join(Base, yes, no)));

        Check.Succeeded(run);
        Assert.Equal(OperationStatus.Succeeded, run.Op("yes").Status);
        Assert.Equal(OperationStatus.Ignored, run.Op("no").Status);
        Assert.NotNull(host.Directory.Find("/groups", "uniqueName", "yes"));
        Assert.Null(host.Directory.Find("/groups", "uniqueName", "no"));
    }

    [Fact]
    public async Task APropertyReadFromAMissingMemberOfALiveObject_IsAFailureOfThatResource()
    {
        var host = new ProviderHost();
        string reading = Dependent("reading", "\"displayName\": \"[reference('base').noSuchMember]\"");

        var run = await host.CreateAsync(host.Resources(Join(Base, reading)), continueOnError: true);

        Assert.False(run.Succeeded);
        Assert.Equal(OperationStatus.Failed, run.Op("reading").Status);
        Assert.Equal(OperationStatus.Succeeded, run.Op("base").Status);
        Assert.Null(host.Directory.Find("/groups", "uniqueName", "reading"));
    }

    [Fact]
    public async Task OutputsThatCannotBeEvaluated_AreDiagnostics_NotCrashes_AndTheDeploymentStillRecordsItsResources()
    {
        var host = new ProviderHost();
        string outputs =
            "\"badCondition\": { \"type\": \"string\", \"condition\": \"[reference('base').displayName]\", \"value\": \"x\" }, "
            + "\"skipped\": { \"type\": \"string\", \"condition\": \"[equals(1, 2)]\", \"value\": \"never\" }, "
            + "\"included\": { \"type\": \"string\", \"condition\": \"[equals(1, 1)]\", \"value\": \"[reference('base').displayName]\" }, "
            + "\"broken\": { \"type\": \"string\", \"value\": \"[string(div(1, sub(length(reference('base').displayName), 4)))]\" }";

        var run = await host.CreateAsync(host.Resources(Base, outputs));

        Assert.NotNull(run.Result);
        Assert.Contains(run.Executed.Diagnostics, d => d.Message.Contains("the condition does not evaluate to a boolean", StringComparison.Ordinal));
        Assert.Equal("Base", run.Output("included")!.GetValue<string>());
        Assert.Null(run.Executed.Outputs["skipped"]);
        Assert.Contains(run.State!.Resources.Keys, k => k.EndsWith("/base", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailureInTheMiddle_WithoutContinueOnError_SkipsEveryLaterBatch_AndTheNextRunPicksUp()
    {
        var host = new ProviderHost();
        host.Fake.Failures.Forbid("/groups", "*", 1);
        string template = Join(Base, Dependent("later", "\"displayName\": \"Later\""));

        var failed = await host.CreateAsync(host.Resources(template));

        Assert.False(failed.Succeeded);
        Assert.Equal(OperationStatus.Failed, failed.Op("base").Status);
        Assert.Equal(OperationStatus.Skipped, failed.Op("later").Status);
        Assert.Empty(failed.State!.Resources);

        var resumed = await host.CreateAsync(host.Resources(template));

        Check.Succeeded(resumed);
        Assert.Equal(2, resumed.State!.Resources.Count);
    }

    [Fact]
    public async Task APartiallyAppliedDeployment_RecordsExactlyTheResourcesThatWereApplied()
    {
        var host = new ProviderHost();
        string failing = Dependent("blocked", "\"displayName\": \"Blocked\"");
        host.Fake.Failures.Forbid("blocked", "*", -1);

        var run = await host.CreateAsync(host.Resources(Join(Base, failing, Dependent("free", "\"displayName\": \"Free\""))), continueOnError: true);

        Assert.False(run.Succeeded);
        var recorded = (await host.LoadStateAsync())!.Resources.Keys.Select(k => k[(k.LastIndexOf('/') + 1)..]).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["base", "free"], recorded);
        Assert.Equal(StateFile.StatusFailed, (await host.LoadStateAsync())!.LastStatus);
        Assert.Contains((await host.LoadStateAsync())!.Operations, o => o.Status == OperationStatus.Failed && o.ResourceId.EndsWith("/blocked", StringComparison.Ordinal));
    }
}
