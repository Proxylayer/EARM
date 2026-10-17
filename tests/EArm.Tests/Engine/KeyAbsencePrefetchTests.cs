using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using Xunit;
using static EArm.Tests.Engine.Review1A;

namespace EArm.Tests.Engine;

/// <summary>
/// <see cref="KeyAbsencePrefetch"/>: the absence confirmation (<see cref="KeyAbsenceCheck"/>) of a planned create in a later batch runs while the earlier
/// batches deploy, instead of after them, and never at the price of a duplicate. The template is a group (an alternate key: no confirmation of its own)
/// in batch 1 and an administrative unit (a displayName key whose read lags) that depends on it, in batch 2. The group's write (an upsert by its alternate key) is held until the unit's
/// early extra reads have run: without the overlap they would only start after that write, and the hold would time out. Nothing sleeps: the delay
/// providers record and return.
/// </summary>
public sealed class KeyAbsencePrefetchTests
{
    private const string Units = "/directory/administrativeUnits";
    private static readonly int Extra = KeyAbsenceCheck.Default.ExtraReads;

    private static readonly string GroupThenUnit = "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"g1\", \"properties\": { \"displayName\": \"G1\", \"mailEnabled\": false, \"mailNickname\": \"g1\", \"securityEnabled\": true } }, "
        + "{ \"type\": \"Microsoft.Entra/administrativeUnits\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"au-late\", \"dependsOn\": [\"[resourceId('Microsoft.Entra/groups', 'g1')]\"], \"properties\": { \"displayName\": \"au-late\" } }";

    /// <summary>The CLI's <c>create</c> without <c>-y</c>: the what-if pass plans on the first read (no confirmation), then the executor runs that plan.</summary>
    private static async Task<(DeploymentPlan Plan, DeploymentResult Result)> WhatIfThenCreateAsync(EngineHost host)
    {
        var plan = await host.WhatIfAsync(Templates.Resources(GroupThenUnit), options: host.Options() with { ConfirmKeyAbsence = false, RetryPolicy = null });
        var result = await host.Executor().ExecuteAsync(plan, new ExecuteOptions(), Ct);
        return (plan, result);
    }

    /// <summary>
    /// Holds the group's write (an upsert by its alternate key) until <paramref name="ready"/> is true (or ten seconds passed); the returned task says
    /// whether it became true in time (and fails the test instead of hanging when the write never came).
    /// </summary>
    private static Func<Task<bool>> HoldGroupWriteUntil(WriteGate client, Func<bool> ready, Action? then = null)
    {
        var outcome = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.BeforeWrite = async request =>
        {
            if (!request.Path.StartsWith("/groups", StringComparison.Ordinal))
            {
                return;
            }

            var clock = Stopwatch.StartNew();
            while (!ready() && clock.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(5, Ct);
            }

            bool inTime = ready();
            if (inTime)
            {
                then?.Invoke();
            }

            outcome.TrySetResult(inTime);
        };
        return () => outcome.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
    }

    /// <summary>Runs a hook before every POST and PATCH.</summary>
    private sealed class WriteGate : GraphClientDecorator
    {
        public WriteGate(EArm.Graph.IGraphClient inner)
            : base(inner)
        {
        }

        public Func<EArm.Graph.GraphRequest, Task>? BeforeWrite { get; set; }

        public override async Task<EArm.Graph.GraphResponse> PostAsync(EArm.Graph.GraphRequest request, JsonNode? body, System.Threading.CancellationToken cancellationToken)
        {
            await (BeforeWrite?.Invoke(request) ?? Task.CompletedTask);
            return await Inner.PostAsync(request, body, cancellationToken);
        }

        public override async Task<EArm.Graph.GraphResponse> PatchAsync(EArm.Graph.GraphRequest request, JsonNode body, System.Threading.CancellationToken cancellationToken)
        {
            await (BeforeWrite?.Invoke(request) ?? Task.CompletedTask);
            return await Inner.PatchAsync(request, body, cancellationToken);
        }
    }

    private static int PostsTo(EngineHost host, string path)
    {
        return host.Fake.Writes.Count(w => w.Method == "POST" && w.Path == path);
    }

    [Fact]
    public async Task ANewObjectOfALaterBatch_HasItsExtraReadsDoneWhileTheFirstBatchDeploys_AndIsCreatedOnce()
    {
        WriteGate? client = null;
        var host = new EngineHost(graph => client = new WriteGate(graph));
        var heldUntilConfirmed = HoldGroupWriteUntil(client!, () => host.AbsenceDelays.Delays.Count >= Extra);

        var (_, result) = await WhatIfThenCreateAsync(host);

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.True(await heldUntilConfirmed(), "the unit's extra reads did not run before the group of the first batch was created");
        Assert.Equal(1, PostsTo(host, Units));
        Assert.Equal(1, host.Directory.Count(Units));

        // The early extra reads count for the unit: the executor does not wait them a second time, it reads once more right before the POST.
        Assert.Equal(Extra, host.AbsenceDelays.Delays.Count);
    }

    [Fact]
    public async Task AnObjectThatAppearsAfterTheEarlyConfirmation_IsFoundByTheFinalRead_AndAdopted_NotCreatedAgain()
    {
        WriteGate? client = null;
        var host = new EngineHost(graph => client = new WriteGate(graph));

        // Somebody else creates the unit after the early extra reads were all empty, before its batch comes up.
        var held = HoldGroupWriteUntil(client!, () => host.AbsenceDelays.Delays.Count >= Extra, () => host.Directory.Add(Units, new JsonObject { ["displayName"] = "au-late" }));

        var (_, result) = await WhatIfThenCreateAsync(host);

        Assert.True(await held());
        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.Equal(0, PostsTo(host, Units));
        Assert.Equal(1, host.Directory.Count(Units));
        Assert.NotEqual(ChangeType.Create, result.Operations.Single(o => o.Id.Id.EndsWith("/au-late", StringComparison.Ordinal)).ChangeType);
    }

    [Fact]
    public async Task AnObjectAnEarlyExtraReadFinds_GetsTheFullCheck_AndIsAdopted()
    {
        var host = new EngineHost();
        host.Directory.Add(Units, new JsonObject { ["displayName"] = "au-late" });

        // The what-if read misses the existing unit (the list flaps), so the plan shows a create; the first early extra read lists it.
        host.Fake.FlapLists(Units, false);

        var (plan, result) = await WhatIfThenCreateAsync(host);

        Assert.Equal(ChangeType.Create, plan.Changes.Single(c => c.Id.Id.EndsWith("/au-late", StringComparison.Ordinal)).Type);
        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.Equal(0, PostsTo(host, Units));
        Assert.Equal(1, host.Directory.Count(Units));

        // One early extra read found it; the executor's own first read then finds it too, so it waits nothing more.
        Assert.Single(host.AbsenceDelays.Delays);
    }

    [Fact]
    public async Task APlanWithoutTheWhatIfReads_PrefetchesNothing_AndKeepsTheFullCheckBeforeThePost()
    {
        WriteGate? client = null;
        var host = new EngineHost(graph => client = new WriteGate(graph));
        int delaysAtGroupWrite = -1;
        client!.BeforeWrite = request =>
        {
            if (request.Path.StartsWith("/groups", StringComparison.Ordinal))
            {
                delaysAtGroupWrite = host.AbsenceDelays.Delays.Count;
            }

            return Task.CompletedTask;
        };

        // create -y: a Create-mode plan has evaluated no key before the run.
        var (_, result) = await ExecuteAsync(host, Templates.Resources(GroupThenUnit), planner: host.Options() with { RetryPolicy = null });

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.Equal(0, delaysAtGroupWrite);
        Assert.Equal(Extra, host.AbsenceDelays.Delays.Count);
        Assert.Equal(1, PostsTo(host, Units));
    }

    [Fact]
    public void TheKeyIdentity_SeparatesTypeValuesAndParent_AndIgnoresTheCaseOfNames()
    {
        var a = new ResourceKey { Type = "Microsoft.Entra/administrativeUnits", Values = new System.Collections.Generic.Dictionary<string, string> { ["displayName"] = "x" } };

        Assert.Equal(KeyAbsencePrefetch.Identity(a), KeyAbsencePrefetch.Identity(a with { Type = "microsoft.entra/AdministrativeUnits", Values = new System.Collections.Generic.Dictionary<string, string> { ["DISPLAYNAME"] = "x" } }));
        Assert.NotEqual(KeyAbsencePrefetch.Identity(a), KeyAbsencePrefetch.Identity(a with { Values = new System.Collections.Generic.Dictionary<string, string> { ["displayName"] = "X" } }));
        Assert.NotEqual(KeyAbsencePrefetch.Identity(a), KeyAbsencePrefetch.Identity(a with { ParentGraphId = "p" }));
        Assert.NotEqual(KeyAbsencePrefetch.Identity(a), KeyAbsencePrefetch.Identity(a with { ParentKey = a }));
        Assert.NotEqual(KeyAbsencePrefetch.Identity(a), KeyAbsencePrefetch.Identity(a with { Type = "Microsoft.Entra/groups" }));
    }
}
