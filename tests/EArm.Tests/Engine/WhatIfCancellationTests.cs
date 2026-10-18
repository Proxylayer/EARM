using System;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using Xunit;

namespace EArm.Tests.Engine;

/// <summary>
/// A cancellation that is not the caller's (an HTTP timeout surfacing as <see cref="TaskCanceledException"/>) fails only the row it happened in;
/// the caller's own cancellation still ends the what-if.
/// </summary>
public sealed class WhatIfCancellationTests
{
    private const string HttpTimeout = "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.";

    [Fact]
    public async Task TimeoutOnOneRead_MarksThatRow_AndTheRestOfTheWhatIfCompletes()
    {
        var host = new EngineHost();
        host.SeedExisting();
        host.Fake.Failures.Add("*", "/roleManagement/directory/roleDefinitions", _ => new TaskCanceledException(HttpTimeout), times: -1);

        var plan = await host.WhatIfAsync(await Templates.FixtureAsync());

        Assert.Equal(Fixture.InstanceCount, plan.Changes.Count);
        var row = Assert.Single(plan.Changes, c => c.Id.Id == Fixture.RoleDefinition);
        Assert.True(row.HasError);
        Assert.Contains("could not be read", row.UnsupportedReason);
        Assert.Contains("timed out or was cancelled", row.UnsupportedReason);
        Assert.Contains("HttpClient.Timeout", row.UnsupportedReason);
        Assert.Equal(1, plan.Summary.Unsupported);
        Assert.Equal(ChangeType.Create, Assert.Single(plan.Changes, c => c.Id.Id == Fixture.User).Type);
        Assert.Equal(ChangeType.Create, Assert.Single(plan.Changes, c => c.Id.Id == Fixture.NamedLocation).Type);
    }

    [Fact]
    public async Task TimeoutOfAnExistingResourceLookup_LeavesAPlaceholder_AndIsReportedAsUnresolved()
    {
        var host = new EngineHost();
        host.SeedExisting();
        host.Fake.Failures.Add("*", "cio@contoso.com", _ => new TaskCanceledException(HttpTimeout), times: -1);

        var plan = await host.WhatIfAsync(await Templates.FixtureAsync());

        Assert.Equal(Fixture.InstanceCount, plan.Changes.Count);
        var unresolved = Assert.Single(plan.UnresolvedReferences);
        Assert.Contains("cio@contoso.com", unresolved.Id.Id, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("timed out or was cancelled", unresolved.Reason);
    }

    [Fact]
    public async Task CallerCancellationDuringARead_StillAbortsTheWhatIf()
    {
        var host = new EngineHost();
        host.SeedExisting();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        host.Fake.Failures.Add("*", "/roleManagement/directory/roleDefinitions", _ =>
        {
            cts.Cancel();
            return new OperationCanceledException(cts.Token);
        }, times: -1);
        var document = await Templates.FixtureAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Planner().PlanAsync(document, DeploymentMode.Incremental, PlanMode.WhatIf, cts.Token));
    }
}
