using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using Xunit;
using static EArm.Providers.Tests.Infrastructure.DirectoryTemplates;

namespace EArm.Providers.Tests;

/// <summary>
/// Cancelling a deployment (Ctrl+C in the CLI) at the planning stage, in the middle of an apply and during a retry wait: the engine
/// stops with an <see cref="OperationCanceledException"/>, nothing is half written, and the next run finishes the job without
/// duplicating objects.
/// </summary>
public sealed class CancellationTests
{
    private static string ThreeGroups()
    {
        return Join(
            Group().Replace("test-admins", "first", StringComparison.Ordinal).Replace("Test Admins", "First", StringComparison.Ordinal),
            Group().Replace("test-admins", "second", StringComparison.Ordinal).Replace("Test Admins", "Second", StringComparison.Ordinal),
            Group().Replace("test-admins", "third", StringComparison.Ordinal).Replace("Test Admins", "Third", StringComparison.Ordinal));
    }

    private static async Task<(ProviderHost Host, TemplateDocument Document)> CancelAfterTheFirstWriteAsync()
    {
        var host = new ProviderHost { UseRetry = false };
        var document = host.Resources(ThreeGroups());
        using var cts = new CancellationTokenSource();
        var plan = await host.Planner().PlanAsync(document, DeploymentMode.Incremental, PlanMode.Create, ProviderHost.Ct);
        int applied = 0;
        var graph = new InterceptingGraphClient(host.Fake)
        {
            PatchOverride = (request, body) =>
            {
                // Apply the write for real, then cancel: the first object exists, the rest must never be started.
                var response = host.Fake.PatchAsync(request, body, ProviderHost.Ct).GetAwaiter().GetResult();
                if (Interlocked.Increment(ref applied) == 1)
                {
                    cts.Cancel();
                }

                return response;
            },
        };
        var executor = new DeploymentExecutor(host.Registry, graph, host.Logs.For<DeploymentExecutor>());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(plan, new ExecuteOptions { Parallelism = 1 }, cts.Token));
        return (host, document);
    }

    [Fact]
    public async Task APreCancelledToken_StopsPlanning_BeforeAnyGraphCall()
    {
        var host = new ProviderHost();
        var document = host.Resources(ThreeGroups());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Planner().PlanAsync(document, DeploymentMode.Incremental, PlanMode.WhatIf, cts.Token));

        Assert.Empty(host.Fake.Requests);
    }

    [Fact]
    public async Task CancellingBetweenResources_StopsTheApply_AfterTheFirstWrite_AndTheNextRunAdoptsItWithoutDuplicates()
    {
        var (host, document) = await CancelAfterTheFirstWriteAsync();

        Assert.Single(host.Directory.Objects("/groups"));
        Assert.DoesNotContain(host.Logs.Lines, l => l.Contains("Unhandled", StringComparison.OrdinalIgnoreCase));

        var resumed = await host.CreateAsync(document);

        Check.Succeeded(resumed);
        Assert.Equal(3, host.Directory.Objects("/groups").Count);
        Assert.Equal(3, resumed.Executed.Operations.Count(o => o.Status == OperationStatus.Succeeded));
    }

    [Fact]
    public async Task ACancellationRightAfterAWrite_StillStopsNewWork_AndTheResumedRunFindsTheWrittenObjectByKey()
    {
        var (host, document) = await CancelAfterTheFirstWriteAsync();
        var written = Assert.Single(host.Directory.Objects("/groups"));
        string writtenId = written["id"]!.GetValue<string>();
        string writtenName = written["uniqueName"]!.GetValue<string>();

        var resumed = await host.CreateAsync(document);

        Check.Succeeded(resumed);
        Assert.Equal(3, host.Directory.Objects("/groups").Count);
        Assert.Equal(ChangeType.NoChange, resumed.Op("/" + writtenName).ChangeType);
        Assert.Equal(writtenId, resumed.Op("/" + writtenName).GraphId);
    }

    [Fact]
    public async Task CancellingDuringTheRetryWait_StopsInsteadOfWaiting()
    {
        var host = new ProviderHost();
        host.Fake.Failures.ServerError("/groups", times: -1);
        using var cts = new CancellationTokenSource();
        var policy = new RetryPolicy(new RetryOptions(), new CancellingDelay(cts), host.Logs.For<RetryPolicy>());

        var started = DateTimeOffset.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => policy.ExecuteAsync(async ct => await host.Fake.GetAsync(new GraphRequest("/groups"), ct), RetryHint.Default, null, cts.Token));

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(30), "the wait was not actually slept");
        Assert.Equal(1, host.Fake.Requests.Count(r => r.Path == "/groups"));
    }

    private sealed class CancellingDelay(CancellationTokenSource source) : IDelayProvider
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            source.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
