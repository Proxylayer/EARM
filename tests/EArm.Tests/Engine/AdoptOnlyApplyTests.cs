using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using Xunit;
using static EArm.Tests.Engine.Review1A;

namespace EArm.Tests.Engine;

/// <summary>
/// An adopt-only resource at apply time: it is never created, so a missing object is decided by one key read, without the extra reads that confirm the
/// absence of a lagging type (<see cref="KeyAbsenceCheck"/>) before a create.
/// </summary>
public sealed class AdoptOnlyApplyTests
{
    private const string Units = "/directory/administrativeUnits";

    private static readonly string AdoptOnlyUnit = "{ \"type\": \"Microsoft.Entra/administrativeUnits\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"au-adopted\", \"properties\": { \"displayName\": \"au-adopted\" }, \"x-earm\": { \"adoptOnly\": true } }";

    private static int KeyReads(EngineHost host)
    {
        return host.Fake.Requests.Count(r => r.Method == "GET" && r.Path.StartsWith(Units, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMissingAdoptOnlyObjectOfALaggingType_FailsAfterOneRead_WithoutTheExtraReads()
    {
        var host = new EngineHost();

        var (_, result) = await ExecuteAsync(host, Templates.Resources(AdoptOnlyUnit), planner: host.Options() with { RetryPolicy = null });

        var operation = Assert.Single(result.Operations);
        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.Equal(EngineDiagnosticCodes.AdoptOnlyNotFound, operation.ErrorCode);
        Assert.Empty(host.AbsenceDelays.Delays);
        Assert.Equal(1, KeyReads(host));
        Assert.Empty(host.Fake.Writes);
    }

    [Fact]
    public async Task AnExistingAdoptOnlyObject_IsAdoptedAsBefore()
    {
        var host = new EngineHost();
        host.Directory.Add(Units, new JsonObject { ["displayName"] = "au-adopted" });

        var (_, result) = await ExecuteAsync(host, Templates.Resources(AdoptOnlyUnit), planner: host.Options() with { RetryPolicy = null });

        var operation = Assert.Single(result.Operations);
        Assert.Equal(OperationStatus.Succeeded, operation.Status);
        Assert.Empty(host.AbsenceDelays.Delays);
        Assert.Equal(1, host.Directory.Count(Units));
        Assert.DoesNotContain(host.Fake.Writes, w => w.Method == "POST");
    }
}
