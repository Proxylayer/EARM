using System;
using System.Linq;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using Xunit;
using static EArm.Tests.Engine.Review1A;

namespace EArm.Tests.Engine;

/// <summary>
/// A create whose call ends with a cancellation the caller did not ask for (a raw HTTP timeout, a <see cref="TaskCanceledException"/> from a client or
/// decorator that does not map it to a transport failure) is an ambiguous write: the key is read again and an object found is adopted, as for a mapped timeout.
/// </summary>
public sealed class CreateTimeoutTests
{
    private static EArm.Core.TemplateDocument Unit(string name)
    {
        return Templates.Resources("{ \"type\": \"Microsoft.Entra/administrativeUnits\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"" + name + "\", \"properties\": { \"displayName\": \"" + name + "\" } }");
    }

    [Fact]
    public async Task APostThatTimesOutWithARawCancellationAfterGraphCommittedIt_IsCountedAsDone()
    {
        var host = new EngineHost(graph => new CommitThenFailGraphClient(
            graph,
            request => request.Path.StartsWith("/directory/administrativeUnits", StringComparison.Ordinal),
            _ => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException()),
            times: 10));

        var (_, result) = await ExecuteAsync(host, Unit("au1"));

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.Equal(1, host.Directory.Count("/directory/administrativeUnits"));
        var warning = Assert.Single(result.Diagnostics, d => d.Code == EngineDiagnosticCodes.CreateOutcomeUncertain);
        Assert.Contains("timed out or was cancelled", warning.Message, StringComparison.Ordinal);
        var op = Assert.Single(result.Operations);
        Assert.Equal(ChangeType.Create, op.ChangeType);
        Assert.Equal(IdOf(host.Directory.Objects("/directory/administrativeUnits").Single()), op.GraphId);
    }

    [Fact]
    public async Task AnUpsertThatTimesOutWithARawCancellationAfterGraphCommittedIt_IsCountedAsDone()
    {
        var host = new EngineHost(graph => new CommitThenFailGraphClient(
            graph,
            request => request.Path.StartsWith("/groups(", StringComparison.Ordinal),
            _ => new OperationCanceledException("The operation was canceled."),
            times: 10));

        var (_, result) = await ExecuteAsync(host, Groups("g1"));

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.Equal(1, host.Directory.Count("/groups"));
        Assert.Contains(result.Diagnostics, d => d.Code == EngineDiagnosticCodes.CreateOutcomeUncertain);
    }

    [Fact]
    public async Task APostThatTimesOutWithARawCancellationBeforeGraphCommittedIt_StillFails()
    {
        var host = new EngineHost();
        host.Fake.Failures.Add("POST", "/directory/administrativeUnits", _ => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."), times: 10);

        var (_, result) = await ExecuteAsync(host, Unit("au1"));

        Assert.False(result.Succeeded);
        Assert.Equal(OperationStatus.Failed, Assert.Single(result.Operations).Status);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == EngineDiagnosticCodes.CreateOutcomeUncertain);
        Assert.Equal(0, host.Directory.Count("/directory/administrativeUnits"));
    }
}
