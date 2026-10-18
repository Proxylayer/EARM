using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Graph;
using Xunit;
using static EArm.Tests.Engine.Review1A;

namespace EArm.Tests.Engine;

/// <summary>
/// The executor's engine-level catch-all (review round 2, S2-09): an unexpected exception thrown by the run itself, outside the per-resource catch,
/// aborts the batches but not the end phase. The run returns a failed result (it does not throw), the resources not reached are recorded as skipped
/// with the reason, and the completion is still logged.
/// </summary>
public sealed class ExecutorAbortTests
{
    private const string StoppingAfterBatch = "StoppingAfterBatch";

    private static string Group(string name, string? dependsOn = null)
    {
        string depends = dependsOn is null ? string.Empty : ", \"dependsOn\": [ \"" + dependsOn + "\" ]";
        return "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"" + name + "\"" + depends + ", \"properties\": { \"displayName\": \"" + name + "\", \"mailEnabled\": false, \"securityEnabled\": true } }";
    }

    [Fact]
    public async Task AnExceptionOutsideTheResourceCatch_AbortsTheRun_ButTheEndPhaseStillRuns()
    {
        var host = new EngineHost();
        host.Fake.Failures.Add("PATCH", "/groups", request => new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", "Invalid value.", request.Hint), times: 1);
        var logger = new RecordingLogger<DeploymentExecutor>(StoppingAfterBatch);
        var executor = new DeploymentExecutor(host.Registry, host.Graph, logger, new ReplicationWait { Delay = host.ReplicationDelays }, new KeyAbsenceCheck { Delay = host.AbsenceDelays });
        var plan = await host.ExpandAsync(Templates.Resources(Group("g1") + ", " + Group("g2", GroupId("g1"))));

        var result = await executor.ExecuteAsync(plan, new ExecuteOptions(), Ct);

        Assert.Equal(DeploymentStatus.Failed, result.Status);
        var aborted = Assert.Single(result.Diagnostics, d => d.Message.Contains("aborted by an unexpected error", StringComparison.Ordinal));
        Assert.True(aborted.IsError);
        Assert.Contains("InvalidOperationException", aborted.Message, StringComparison.Ordinal);
        Assert.Equal(OperationStatus.Failed, result.Operations.Single(o => o.Id.Id == GroupId("g1")).Status);
        var skipped = result.Operations.Single(o => o.Id.Id == GroupId("g2"));
        Assert.Equal(OperationStatus.Skipped, skipped.Status);
        Assert.Equal("the run was aborted by an unexpected error", skipped.Error);
        Assert.Contains(logger.Events, e => e.Name == "Completed");
        Assert.Equal(0, host.Directory.Count("/groups"));
    }
}
