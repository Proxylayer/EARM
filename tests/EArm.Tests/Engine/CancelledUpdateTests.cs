using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Graph;
using Xunit;
using static EArm.Tests.Engine.Review1A;

namespace EArm.Tests.Engine;

/// <summary>
/// An in-place update (PATCH) cut short by a cancellation after the write may have committed is reported as it is: the object is read back once by key
/// and planned again. It is applied when the object now matches the template, and failed with the object recorded when it does not; it is never
/// reported as skipped (Ctrl+C) or as an unexpected error (a raw HTTP timeout).
/// </summary>
public sealed class CancelledUpdateTests
{
    private static string SeedOutdatedGroup(EngineHost host)
    {
        var group = host.Directory.AddGroup("g1", "old name", g => { g["mailEnabled"] = false; g["securityEnabled"] = true; });
        return IdOf(group);
    }

    [Fact]
    public async Task APatchThatTimesOutAfterGraphCommittedIt_IsReportedApplied()
    {
        var host = new EngineHost(graph => new PatchThenCancelClient(graph, commit: true, caller: null));
        string id = SeedOutdatedGroup(host);

        var (_, result) = await ExecuteAsync(host, Groups("g1"));

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        var op = Assert.Single(result.Operations);
        Assert.Equal(ChangeType.Modify, op.ChangeType);
        Assert.Equal(id, op.GraphId);
    }

    [Fact]
    public async Task APatchThatTimesOutBeforeGraphCommittedIt_FailsTruthfully_WithTheObjectRecorded()
    {
        var host = new EngineHost(graph => new PatchThenCancelClient(graph, commit: false, caller: null));
        string id = SeedOutdatedGroup(host);

        var (_, result) = await ExecuteAsync(host, Groups("g1"));

        var op = Assert.Single(result.Operations);
        Assert.Equal(OperationStatus.Failed, op.Status);
        Assert.Equal(id, op.GraphId);
        Assert.Contains("unknown outcome", op.Error, StringComparison.Ordinal);
        Assert.Contains("does not match the template yet", op.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("unexpected error", op.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACtrlCAfterThePatchCommitted_ReportsTheUpdateApplied_NotSkipped()
    {
        using var cts = new CancellationTokenSource();
        var host = new EngineHost(graph => new PatchThenCancelClient(graph, commit: true, caller: cts));
        SeedOutdatedGroup(host);
        var logger = new RecordingLogger<DeploymentExecutor>();
        var executor = new DeploymentExecutor(host.Registry, host.Graph, logger, new ReplicationWait { Delay = host.ReplicationDelays }, new KeyAbsenceCheck { Delay = host.AbsenceDelays });
        var plan = await host.ExpandAsync(Groups("g1"), options: host.Options() with { RetryPolicy = null });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(plan, new ExecuteOptions(), cts.Token));

        Assert.DoesNotContain(logger.Events, e => e.Name == "Skipped");
        Assert.Contains(logger.Events, e => e.Name == "Applied" && e.Message.Contains(GroupId("g1"), StringComparison.Ordinal));
        Assert.Equal("g1", host.Directory.Objects("/groups").Single()["displayName"]!.GetValue<string>());
    }

    [Fact]
    public async Task ACtrlCBeforeThePatchCommitted_ReportsTheResourceFailedWithItsObject_NotSkipped()
    {
        using var cts = new CancellationTokenSource();
        var host = new EngineHost(graph => new PatchThenCancelClient(graph, commit: false, caller: cts));
        string id = SeedOutdatedGroup(host);
        var logger = new RecordingLogger<DeploymentExecutor>();
        var executor = new DeploymentExecutor(host.Registry, host.Graph, logger, new ReplicationWait { Delay = host.ReplicationDelays }, new KeyAbsenceCheck { Delay = host.AbsenceDelays });
        var plan = await host.ExpandAsync(Groups("g1"), options: host.Options() with { RetryPolicy = null });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(plan, new ExecuteOptions(), cts.Token));

        Assert.DoesNotContain(logger.Events, e => e.Name == "Skipped");
        var failed = Assert.Single(logger.Events, e => e.Name == "ApplyFailed");
        Assert.Contains("cancelled while the object was being updated", failed.Message, StringComparison.Ordinal);
        Assert.Contains(id, failed.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The PATCH of an existing group (optionally committed first) ends with a cancellation: the caller's Ctrl+C between two requests of the provider
    /// when <c>caller</c> is set, otherwise the <see cref="TaskCanceledException"/> of a raw HTTP timeout.
    /// </summary>
    private sealed class PatchThenCancelClient(IGraphClient inner, bool commit, CancellationTokenSource? caller) : GraphClientDecorator(inner)
    {
        private int _fired;

        public override async Task<GraphResponse> PatchAsync(GraphRequest request, JsonNode body, CancellationToken cancellationToken)
        {
            if (!request.Path.StartsWith("/groups", StringComparison.Ordinal) || Interlocked.Exchange(ref _fired, 1) == 1)
            {
                return await base.PatchAsync(request, body, cancellationToken);
            }

            if (commit)
            {
                await base.PatchAsync(request, body, CancellationToken.None);
            }

            if (caller is null)
            {
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException());
            }

            await caller.CancelAsync();
            throw new OperationCanceledException(caller.Token);
        }
    }
}
