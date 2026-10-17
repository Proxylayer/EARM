using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static EArm.Tests.Engine.Review1A;

namespace EArm.Tests.Engine;

/// <summary>
/// <c>create -y</c> (a <see cref="PlanMode.Create"/> plan, nothing read before the run): the keys of the later batches come from a sandboxed dry run
/// (<see cref="SpeculativeKey"/>). It makes no live lookup, and a key that depends on an earlier batch, or that another instance may share, is not prefetched.
/// </summary>
public sealed partial class KeyAbsencePrefetchTests
{
    /// <summary>An administrative unit (its key is its displayName, which is its name) in batch 2 behind g1, or in batch 1 without <paramref name="dependsOnGroup"/>.</summary>
    private static string Unit(string name, string dependsOnGroup = "g1", string extra = "")
    {
        return "{ \"type\": \"Microsoft.Entra/administrativeUnits\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"" + name + "\""
            + (dependsOnGroup.Length == 0 ? string.Empty : ", \"dependsOn\": [\"[resourceId('Microsoft.Entra/groups', '" + dependsOnGroup + "')]\"]")
            + ", \"properties\": { " + extra.TrimStart(',', ' ') + " } }";
    }

    private static readonly string Group1 = "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"g1\", \"properties\": { \"displayName\": \"G1\", \"mailEnabled\": false, \"mailNickname\": \"g1\", \"securityEnabled\": true } }";

    /// <summary>The prefetch a create -y run would start for <paramref name="resources"/>, and whether it took over the confirmation of <paramref name="name"/>.</summary>
    private static async Task<(bool Prefetched, int Reads)> PrefetchOfAsync(string resources, string name)
    {
        var host = new EngineHost();
        var plan = await host.ExpandAsync(Templates.Resources(resources), options: host.Options() with { RetryPolicy = null });
        int reads = 0;
        var check = new KeyAbsenceCheck { Delay = host.AbsenceDelays };
        var prefetch = KeyAbsencePrefetch.Start(plan, check, (_, _, _) => { Interlocked.Increment(ref reads); return Task.FromResult<ExistingResource?>(null); }, 4, NullLogger.Instance, Ct);
        await using (prefetch)
        {
            var instance = plan.Instances.Single(i => i.Name == name);
            var key = SpeculativeKey.Resolve(plan, instance, host.Provider(instance.Type), out _);
            Assert.NotNull(key);
            if (!prefetch.TryTake(instance, key, out var confirmed))
            {
                return (false, Volatile.Read(ref reads));
            }

            Assert.True(await confirmed.WaitAsync(TimeSpan.FromSeconds(30), Ct));
            return (true, Volatile.Read(ref reads));
        }
    }

    [Fact]
    public async Task CreateY_ANewObjectOfALaterBatch_HasItsExtraReadsDoneWhileTheFirstBatchDeploys_AndIsCreatedOnce()
    {
        WriteGate? client = null;
        var host = new EngineHost(graph => client = new WriteGate(graph));
        var heldUntilConfirmed = HoldGroupWriteUntil(client!, () => host.AbsenceDelays.Delays.Count >= Extra);

        var (_, result) = await ExecuteAsync(host, Templates.Resources(GroupThenUnit), planner: host.Options() with { RetryPolicy = null });

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.True(await heldUntilConfirmed(), "the unit's extra reads did not run before the group of the first batch was created");
        Assert.Equal(1, PostsTo(host, Units));
        Assert.Equal(1, host.Directory.Count(Units));
        Assert.Equal(Extra, host.AbsenceDelays.Delays.Count);
    }

    [Fact]
    public async Task CreateY_AnObjectThatAppearsAfterTheEarlyConfirmation_IsFoundByTheFinalRead_AndAdopted_NotCreatedAgain()
    {
        WriteGate? client = null;
        var host = new EngineHost(graph => client = new WriteGate(graph));
        var held = HoldGroupWriteUntil(client!, () => host.AbsenceDelays.Delays.Count >= Extra, () => host.Directory.Add(Units, new JsonObject { ["displayName"] = "au-late" }));

        var (_, result) = await ExecuteAsync(host, Templates.Resources(GroupThenUnit), planner: host.Options() with { RetryPolicy = null });

        Assert.True(await held());
        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.Equal(0, PostsTo(host, Units));
        Assert.Equal(1, host.Directory.Count(Units));
    }

    [Fact]
    public async Task CreateY_TheDryRunMakesNoExistingResourceLookup_BeforeTheEarlierBatchesRan()
    {
        WriteGate? client = null;
        var host = new EngineHost(graph => client = new WriteGate(graph));
        host.SeedExisting();
        int lookupsAtGroupWrite = -1;
        int delaysAtGroupWrite = -1;
        client!.BeforeWrite = request =>
        {
            if (request.Path.StartsWith("/groups", StringComparison.Ordinal))
            {
                lookupsAtGroupWrite = host.Fake.Requests.Count(r => r.Path.Contains("cio@contoso.com", StringComparison.OrdinalIgnoreCase));
                delaysAtGroupWrite = host.AbsenceDelays.Delays.Count;
            }

            return Task.CompletedTask;
        };

        // The unit's description looks an existing user up; its key (displayName) does not depend on it, so the unit is still prefetched.
        string lookup = ", \"description\": \"[reference(resourceId('Microsoft.Entra/users', 'cio@contoso.com'), '" + ApiVersion + "').displayName]\"";
        var (_, result) = await ExecuteAsync(host, Templates.Resources(Group1 + ", " + Unit("au-late", extra: lookup)), planner: host.Options() with { RetryPolicy = null });

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.Equal(0, lookupsAtGroupWrite);
        Assert.True(delaysAtGroupWrite >= 0);
        Assert.Equal("Chief Information Officer", host.Directory.Find(Units, "displayName", "au-late")!["description"]!.GetValue<string>());
        Assert.Equal(1, PostsTo(host, Units));
    }

    [Fact]
    public async Task CreateY_AKeyThatDependsOnAnEarlierBatch_IsNotPrefetched()
    {
        // A unit named after g1's live displayName: its key (its name) is only known once g1 exists.
        string unit = Unit("[concat('au-', reference(resourceId('Microsoft.Entra/groups', 'g1')).displayName)]");

        var (prefetched, reads) = await PrefetchOfAsync(Group1 + ", " + unit + ", " + Unit("au-other"), "au-other");

        // The deploy-time key could be anything, au-other included: no unit is prefetched.
        Assert.False(prefetched);
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task CreateY_AKeyOnlyOneInstanceHas_IsPrefetched()
    {
        var (prefetched, reads) = await PrefetchOfAsync(Group1 + ", " + Unit("au-one", dependsOnGroup: string.Empty) + ", " + Unit("au-two"), "au-two");

        Assert.True(prefetched);
        Assert.Equal(Extra, reads);
    }

    [Fact]
    public async Task CreateY_AnInstanceWhoseConditionIsFalse_IsNotPrefetched()
    {
        string unit = Unit("au-off").Replace("\"name\": \"au-off\"", "\"name\": \"au-off\", \"condition\": false", StringComparison.Ordinal);
        var host = new EngineHost();
        var plan = await host.ExpandAsync(Templates.Resources(Group1 + ", " + unit), options: host.Options() with { RetryPolicy = null });
        var instance = plan.Instances.Single(i => i.Name == "au-off");

        var key = SpeculativeKey.Resolve(plan, instance, host.Provider(instance.Type), out bool conditionFalse);

        Assert.Null(key);
        Assert.True(conditionFalse);
    }
}
