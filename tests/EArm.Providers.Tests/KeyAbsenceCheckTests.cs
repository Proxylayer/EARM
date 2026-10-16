using System;
using System.Linq;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// The list read of a <c>displayName</c>-keyed type (<see cref="IResourceProvider.KeyReadLagsCreate"/>) FLAPS after a write: one replica lists the
/// object, the next does not. An empty read is confirmed by more reads (<see cref="KeyAbsenceCheck"/>) before what-if plans, or create POSTs, a
/// create; and read-your-write after a create needs <see cref="ReplicationWait.ConsecutiveVisibleReads"/> hits in a row. The fake models the
/// flapping list with <c>FlapLists</c> (true = the list shows the collection, false = it lists nothing).
/// </summary>
public sealed class KeyAbsenceCheckTests
{
    private const string LocType = NamedLocationsProvider.ResourceType;
    private const string LocPath = "/identity/conditionalAccess/namedLocations";
    private static readonly int Extra = KeyAbsenceCheck.Default.ExtraReads;

    private static string IpLocation(string cidr = "10.0.0.0/8")
    {
        return "{ \"type\": \"" + LocType + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"Contoso offices\", \"properties\": { \"@odata.type\": \"#microsoft.graph.ipNamedLocation\", \"ipRanges\": [ { \"cidrAddress\": \"" + cidr + "\" } ] } }";
    }

    private static int ListReads(Run run)
    {
        return run.Requests.Count(r => r.Method == "GET" && r.Path == LocPath);
    }

    private static async Task<ProviderHost> HostWithExistingLocationAsync()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(IpLocation())));
        return host;
    }

    private static bool[] Repeat(bool value, int count)
    {
        return Enumerable.Repeat(value, count).ToArray();
    }

    // ---- what-if (planner) ----

    [Fact]
    public async Task WhatIf_AGenuinelyNewObject_IsPlannedAsCreate_OnlyAfterEveryExtraReadIsEmpty()
    {
        var host = new ProviderHost();

        var run = await host.WhatIfAsync(host.Resources(IpLocation()));

        Assert.Equal(ChangeType.Create, run.Change("Contoso offices").Type);
        Assert.Equal(1 + Extra, ListReads(run));
        Assert.Equal(Extra, host.AbsenceDelays.Count);
    }

    [Fact]
    public async Task WhatIf_AnExistingObjectTheListOmitsOnce_IsNoChange_NotCreate()
    {
        var host = await HostWithExistingLocationAsync();
        int delaysBefore = host.AbsenceDelays.Count;
        host.Fake.FlapLists(LocPath, false);

        var run = await host.WhatIfAsync(host.Resources(IpLocation()));

        Assert.Equal(ChangeType.NoChange, run.Change("Contoso offices").Type);
        Assert.Equal(2, ListReads(run));
        Assert.Equal(1, host.AbsenceDelays.Count - delaysBefore);
    }

    [Fact]
    public async Task WhatIf_AnObjectVisibleOnlyOnTheLastExtraRead_IsStillFound()
    {
        var host = await HostWithExistingLocationAsync();
        host.Fake.FlapLists(LocPath, Repeat(false, Extra));

        var run = await host.WhatIfAsync(host.Resources(IpLocation()));

        Assert.Equal(ChangeType.NoChange, run.Change("Contoso offices").Type);
        Assert.Equal(1 + Extra, ListReads(run));
    }

    [Fact]
    public async Task WhatIf_AnObjectFoundAtOnce_CostsNoExtraRead()
    {
        var host = await HostWithExistingLocationAsync();
        int delaysBefore = host.AbsenceDelays.Count;

        var run = await host.WhatIfAsync(host.Resources(IpLocation()));

        Assert.Equal(ChangeType.NoChange, run.Change("Contoso offices").Type);
        Assert.Equal(1, ListReads(run));
        Assert.Equal(0, host.AbsenceDelays.Count - delaysBefore);
    }

    [Fact]
    public async Task WhatIf_ATypeKeyedByAnAlternateKey_IsNeverReadAgain()
    {
        var host = new ProviderHost();

        var run = await host.WhatIfAsync(host.Resources("{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"absence-group\", \"properties\": { \"displayName\": \"Absence\", \"mailEnabled\": false, \"mailNickname\": \"absence\", \"securityEnabled\": true } }"));

        Assert.Equal(ChangeType.Create, run.Change("absence-group").Type);
        Assert.Equal(0, host.AbsenceDelays.Count);
    }

    [Fact]
    public async Task WhatIf_WithTheCheckDisabled_PlansCreateOnOneEmptyRead()
    {
        var host = await HostWithExistingLocationAsync();
        host.AbsenceCheck = new KeyAbsenceCheck { ExtraReads = 0, Delay = host.AbsenceDelays };
        host.Fake.FlapLists(LocPath, false);

        var run = await host.WhatIfAsync(host.Resources(IpLocation()));

        Assert.Equal(ChangeType.Create, run.Change("Contoso offices").Type);
    }

    // ---- planning without the confirmation (what-if and the planning pass of create) ----

    [Fact]
    public async Task WhatIf_WithoutConfirmation_PlansANewObjectOnTheFirstRead_WithNoExtraRead()
    {
        var host = new ProviderHost { ConfirmKeyAbsence = false };

        var run = await host.WhatIfAsync(host.Resources(IpLocation()));

        Assert.Equal(ChangeType.Create, run.Change("Contoso offices").Type);
        Assert.Equal(1, ListReads(run));
        Assert.Equal(0, host.AbsenceDelays.Count);
        Assert.False(host.Logs.Contains("absent after"), string.Join("\n", host.Logs.Lines));
    }

    [Fact]
    public async Task WhatIfThenCreate_ANewObject_IsPostedOnlyAfterTheExecutorsFullCheck()
    {
        var host = new ProviderHost { ConfirmKeyAbsence = false };

        var run = await WhatIfThenCreateAsync(host, IpLocation());

        Check.Succeeded(run);
        var post = Assert.Single(run.Writes);
        Assert.Equal("POST", post.Method);

        // The planner's one read, then the executor's read and its extra reads, all before the POST.
        int postIndex = run.Requests.ToList().IndexOf(post);
        Assert.Equal(1 + 1 + Extra, run.Requests.Take(postIndex).Count(r => r.Method == "GET" && r.Path == LocPath));
        Assert.Equal(Extra, host.AbsenceDelays.Count);
    }

    [Fact]
    public async Task WhatIfThenCreate_AnExistingObjectBothFirstReadsOmit_IsAdoptedByTheExecutor_NotCreatedAgain()
    {
        var host = await HostWithExistingLocationAsync();
        host.ConfirmKeyAbsence = false;
        int delaysBefore = host.AbsenceDelays.Count;

        // The planner's only read and the executor's first read miss the object; the executor's first extra read finds it.
        host.Fake.FlapLists(LocPath, false, false);

        var run = await WhatIfThenCreateAsync(host, IpLocation());

        Assert.Equal(ChangeType.Create, run.Plan.Changes.Single(c => c.Id.Id.Contains("Contoso offices", StringComparison.Ordinal)).Type);
        Check.Succeeded(run);
        Assert.Equal(ChangeType.NoChange, run.Executed.Operations.Single().ChangeType);
        Check.NoWrites(run);
        Assert.Single(host.Directory.Objects(LocPath));
        Assert.Equal(1, host.AbsenceDelays.Count - delaysBefore);
    }

    /// <summary>The CLI's <c>create</c> without <c>-y</c>: a what-if plan (the confirmation pass), then the executor on that plan.</summary>
    private static async Task<Run> WhatIfThenCreateAsync(ProviderHost host, string resources)
    {
        int before = host.Fake.Requests.Count;
        var plan = await host.Planner().PlanAsync(host.Resources(resources), DeploymentMode.Incremental, PlanMode.WhatIf, ProviderHost.Ct);
        var result = await host.Executor().ExecuteAsync(plan, new ExecuteOptions { Parallelism = 1 }, ProviderHost.Ct);
        return new Run(plan, result, host.RequestsSince(before));
    }

    // ---- delete (planner, confirmation kept) ----

    [Fact]
    public async Task Delete_AnExistingObjectTheListOmitsOnce_IsStillFoundAndDeleted()
    {
        var host = await HostWithExistingLocationAsync();
        Assert.True(host.ConfirmKeyAbsence);
        int delaysBefore = host.AbsenceDelays.Count;
        host.Fake.FlapLists(LocPath, false);

        await host.DeleteDeclaredAsync(host.Resources(IpLocation()));

        Assert.Empty(host.Directory.Objects(LocPath));
        Assert.Equal(1, host.AbsenceDelays.Count - delaysBefore);
    }

    // ---- logging ----

    private static string NamedIpLocation(string name)
    {
        return IpLocation().Replace("\"name\": \"Contoso offices\"", "\"name\": \"" + name + "\"", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logs_EachExtraReadAtDebug_AndOneInformationSummaryForTheBatch()
    {
        var host = new ProviderHost();

        await host.WhatIfAsync(host.Resources(NamedIpLocation("Office A") + ", " + NamedIpLocation("Office B")));

        var lines = host.Logs.Lines;
        Assert.Equal(2 * Extra, lines.Count(l => l.StartsWith("Debug DeploymentPlanner: Nothing found for", StringComparison.Ordinal)));
        Assert.DoesNotContain(lines, l => l.Contains("Nothing found for", StringComparison.Ordinal) && !l.StartsWith("Debug ", StringComparison.Ordinal));
        var summary = Assert.Single(lines, l => l.Contains("absent after", StringComparison.Ordinal));
        Assert.StartsWith("Information DeploymentPlanner: Batch ", summary, StringComparison.Ordinal);
        Assert.Contains("confirmed 2 object(s) absent after 3 extra read(s) (12 s)", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logs_TheExecutorsCheck_AsOneInformationSummaryLine()
    {
        var host = new ProviderHost();

        Check.Succeeded(await host.CreateAsync(host.Resources(IpLocation())));

        var lines = host.Logs.Lines;
        Assert.Equal(Extra, lines.Count(l => l.StartsWith("Debug DeploymentExecutor: Nothing found for", StringComparison.Ordinal)));
        var summary = Assert.Single(lines, l => l.Contains("absent after", StringComparison.Ordinal));
        Assert.StartsWith("Information DeploymentExecutor: Confirmed ", summary, StringComparison.Ordinal);
        Assert.Contains("Contoso offices", summary, StringComparison.Ordinal);
        Assert.Contains("absent after 3 extra read(s) (12 s)", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logs_AnObjectFoundOnAnExtraRead_AsAWarning_WithoutAnAbsenceSummary()
    {
        var host = await HostWithExistingLocationAsync();
        int mark = host.Logs.Lines.Count;
        host.Fake.FlapLists(LocPath, false);

        await host.WhatIfAsync(host.Resources(IpLocation()));

        var lines = host.Logs.Lines.Skip(mark).ToList();
        var found = Assert.Single(lines, l => l.Contains("on extra read", StringComparison.Ordinal));
        Assert.StartsWith("Warning DeploymentPlanner: ", found, StringComparison.Ordinal);
        Assert.DoesNotContain(lines, l => l.Contains("absent after", StringComparison.Ordinal));
    }

    // ---- create (executor) ----

    [Fact]
    public async Task Create_AnExistingObjectTheListOmits_IsNotPostedAgain()
    {
        var host = await HostWithExistingLocationAsync();
        host.Fake.FlapLists(LocPath, false, false);

        var run = await host.CreateAsync(host.Resources(IpLocation()));

        Check.Succeeded(run);
        Assert.Equal(ChangeType.NoChange, run.Executed.Operations.Single().ChangeType);
        Check.NoWrites(run);
        Assert.Single(host.Directory.Objects(LocPath));
    }

    [Fact]
    public async Task Create_AChangedObjectTheListOmits_IsPatched_NotCreatedAgain()
    {
        var host = await HostWithExistingLocationAsync();
        host.Fake.FlapLists(LocPath, false);

        var run = await host.CreateAsync(host.Resources(IpLocation("192.168.0.0/16")));

        Check.Succeeded(run);
        Assert.Equal(ChangeType.Modify, run.Executed.Operations.Single().ChangeType);
        Assert.DoesNotContain(run.Writes, w => w.Method == "POST");
        Assert.Single(host.Directory.Objects(LocPath));
    }

    // ---- read-your-write after a create ----

    [Fact]
    public async Task ReadYourWrite_AHitThenAMiss_StartsTheCountAgain_UntilTwoHitsInARow()
    {
        var host = new ProviderHost();

        // The plan read and its extra reads (the collection is empty, listed normally), then read-your-write: hit, miss, hit, hit.
        host.Fake.FlapLists(LocPath, [.. Repeat(true, 1 + Extra), true, false, true, true]);

        var run = await host.CreateAsync(host.Resources(IpLocation()));

        Check.Succeeded(run);
        Check.NoDiagnostic(run, EngineDiagnosticCodes.CreatedObjectNotVisible);
        Assert.Single(run.Writes);
        Assert.Equal(1 + Extra + 4, ListReads(run));

        // A short gap after each hit that is not yet the second in a row, an interval after the miss.
        Assert.Equal(3, host.ReplicationDelays.Count);
    }

    [Fact]
    public async Task ReadYourWrite_AListThatNeverShowsTheObjectTwiceInARow_SucceedsWithTheWarning()
    {
        var host = new ProviderHost();
        bool[] alternating = [.. Enumerable.Range(0, 200).Select(i => i % 2 == 0)];
        host.Fake.FlapLists(LocPath, [.. Repeat(true, 1 + Extra), .. alternating]);

        var run = await host.CreateAsync(host.Resources(IpLocation()));

        Check.Succeeded(run);
        Assert.Single(run.Writes);
        var warning = Check.Diagnostic(run, EngineDiagnosticCodes.CreatedObjectNotVisible);
        Assert.Contains(run.Executed.Operations.Single().GraphId!, warning.Message, StringComparison.Ordinal);

        // Bounded by the window: each hit-miss pair schedules one gap and one interval.
        var wait = ReplicationWait.Default;
        int pairs = (int)(wait.Window / (wait.StableGap + wait.Interval));
        Assert.InRange(host.ReplicationDelays.Count, 2 * pairs, (2 * pairs) + 1);
    }

    [Fact]
    public void TheDefaults_AreThreeExtraReadsFourSecondsApart_AndTwoVisibleReadsInARow()
    {
        Assert.Equal(3, KeyAbsenceCheck.Default.ExtraReads);
        Assert.Equal(TimeSpan.FromSeconds(4), KeyAbsenceCheck.Default.Interval);
        Assert.Equal(2, ReplicationWait.Default.ConsecutiveVisibleReads);
        Assert.Equal(TimeSpan.FromSeconds(2), ReplicationWait.Default.StableGap);
        Assert.Throws<ArgumentOutOfRangeException>(() => new KeyAbsenceCheck { ExtraReads = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new KeyAbsenceCheck { Interval = TimeSpan.Zero });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplicationWait { ConsecutiveVisibleReads = 0 });
    }
}
