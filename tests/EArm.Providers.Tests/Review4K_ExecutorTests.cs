using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Engine.State;
using EArm.Graph;
using EArm.Providers.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// Review round 4, K7: the decisions of the deployment executor that earlier suites left unpinned (what survives a cancelled token, what a non-token cancellation or a
/// null reference does to one resource, which Graph ids an orphan may not delete, the replacement of an object, the re-read after an uncertain create, the secrets file, the
/// edge records), driven through the real providers over the fake directory.
/// </summary>
public sealed partial class Review4K_ExecutorTests
{
    private const string GroupType = "Microsoft.Entra/groups";
    private const string AppType = ApplicationsProvider.ResourceType;
    private const string LocType = NamedLocationsProvider.ResourceType;
    private const string LocPath = "/identity/conditionalAccess/namedLocations";
    private const string AssignmentType = AppRoleAssignmentsProvider.ResourceType;
    private const string ResourceAppId = "11111111-2222-3333-4444-555555555555";
    private const string UserRoleId = "6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0001";
    private const string BothRoleId = "6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0004";
    private const string OtherRoleId = "6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0002";

    private static CancellationToken Ct => ProviderHost.Ct;

    private static string Group(string name, string extra = "", string? extraResource = null)
    {
        return Templates.Resource(GroupType, name, "{ \"displayName\": \"" + name + "\", \"mailEnabled\": false, \"securityEnabled\": true" + extra + " }", extra: extraResource);
    }

    private static string App(string name, string? extraResource = null)
    {
        return Templates.Resource(AppType, name, "{ \"displayName\": \"" + name + "\" }", extra: extraResource);
    }

    private static string IpLocation(string name = "Contoso offices")
    {
        return Templates.Resource(LocType, name, """{ "@odata.type": "#microsoft.graph.ipNamedLocation", "ipRanges": [ { "cidrAddress": "10.0.0.0/8" } ] }""");
    }

    private static string CountryLocation(string name = "Contoso offices")
    {
        return Templates.Resource(LocType, name, """{ "@odata.type": "#microsoft.graph.countryNamedLocation", "countriesAndRegions": [ "GB" ] }""");
    }

    private static string GroupResourceId(string name)
    {
        return "/providers/Microsoft.Entra/groups/" + name;
    }

    private sealed record Outcome(DeploymentPlan Plan, DeploymentResult Result, StateFile State);

    /// <summary>
    /// Plans (Create mode) and executes with the state sink of <c>deployment create</c> over the host's store, with the hooks a test can ask for.
    /// </summary>
    private static async Task<Outcome> ExecuteAsync(
        ProviderHost host,
        TemplateDocument document,
        DeploymentMode mode = DeploymentMode.Incremental,
        ExecuteOptions? execute = null,
        IGraphClient? graph = null,
        ILogger<DeploymentExecutor>? logger = null,
        Func<IStateSink, IStateSink>? wrapSink = null,
        CancellationToken? token = null,
        Func<PlannerOptions, PlannerOptions>? plannerOptions = null)
    {
        var state = await host.LoadStateAsync();
        var options = plannerOptions is null ? host.Options() : plannerOptions(host.Options());
        var plan = await new DeploymentPlanner(host.Registry, host.Fake, options, host.Logs.For<DeploymentPlanner>()).PlanAsync(document, state, mode, PlanMode.Create, Ct);
        state ??= StateFile.CreateNew(ProviderHost.DeploymentName, host.Directory.TenantId, DateTimeOffset.UtcNow);
        var sink = new FileStateSink(host.Store, state, plan, host.Logs.For<FileStateSink>()) { SaveRetryBaseDelay = TimeSpan.FromMilliseconds(1) };
        await sink.BeginAsync(Ct);
        IStateSink effective = wrapSink is null ? sink : wrapSink(sink);
        var executeOptions = (execute ?? new ExecuteOptions { Parallelism = 1 }) with { StateSink = effective, State = state };
        var executor = new DeploymentExecutor(host.Registry, graph ?? host.Fake, logger ?? host.Logs.For<DeploymentExecutor>());
        var result = await executor.ExecuteAsync(plan, executeOptions, token ?? Ct);
        return new Outcome(plan, result, state);
    }

    private static async Task<StateFile> StateOfAsync(ProviderHost host)
    {
        return await host.LoadStateAsync() ?? throw new InvalidOperationException("no state");
    }

    // ---- DE01 / DE09: a failure that is neither the caller's cancellation nor an expected exception fails one resource ----

    public static TheoryData<string> UnexpectedFailures => new() { "timeout-cancellation", "null-reference" };

    [Theory]
    [MemberData(nameof(UnexpectedFailures))]
    public async Task ANonTokenCancellationOrANullReference_FailsOnlyThatResource_AndTheEndPhaseStillRuns(string kind)
    {
        var host = new ProviderHost();
        host.Fake.Failures.Add("PATCH", "uniqueName='boom'", _ => kind == "null-reference" ? new NullReferenceException("a provider bug") : new TaskCanceledException("the HTTP call timed out"), -1);
        var document = host.Resources(Group("ok") + ", " + Group("boom"), outputs: """ "done": { "type": "string", "value": "yes" } """);

        var outcome = await ExecuteAsync(host, document);

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(DeploymentStatus.Failed, outcome.Result.Status);
        Assert.Equal(OperationStatus.Succeeded, outcome.Result.Operations.Single(o => o.Id.Id.EndsWith("/ok", StringComparison.Ordinal)).Status);
        var boom = outcome.Result.Operations.Single(o => o.Id.Id.EndsWith("/boom", StringComparison.Ordinal));
        Assert.Equal(OperationStatus.Failed, boom.Status);
        Assert.Equal(EngineDiagnosticCodes.ResourceApplyFailed, boom.ErrorCode);
        Assert.Contains("unexpected error", boom.Error, StringComparison.Ordinal);
        Assert.Contains(kind == "null-reference" ? "NullReferenceException" : "TaskCanceledException", boom.Error, StringComparison.Ordinal);
        Assert.Equal("yes", outcome.Result.Outputs["done"]!["value"]!.GetValue<string>());
        Assert.Equal(StateFile.StatusFailed, (await StateOfAsync(host)).LastStatus);
    }

    // ---- DE03: a cancelled run judges nothing in the mode pass ----

    [Fact]
    public async Task ACancelledRun_NeverRunsTheModePass_SoNoOrphanIsDeleted()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(Group("orphan"))));
        using var source = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var hooked = new HookedGraphClient(host.Fake) { After = (method, request) => method == "PATCH" ? source.CancelAsync() : Task.CompletedTask };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExecuteAsync(host, host.Resources(Group("keep")), DeploymentMode.Complete, graph: hooked, token: source.Token));

        Assert.DoesNotContain(host.Fake.Writes, w => w.Method == "DELETE");
        Assert.NotNull(host.Directory.Find("/groups", "uniqueName", "orphan"));
        var state = await StateOfAsync(host);
        Assert.NotNull(state.Find(GroupResourceId("orphan")));
        Assert.Equal(StateFile.StatusCancelled, state.LastStatus);
    }

    // ---- DE06 / DE07 / DE08 / DE25: recording what already happened never observes the cancelled token ----

    [Fact]
    public async Task ADetachRecorded_JustAsTheRunIsCancelled_IsStillRecorded_AndTheRestIsLeftAlone()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(Group("orphan-a") + ", " + Group("orphan-b"))));
        using var source = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var logger = new HookLogger<DeploymentExecutor>(id =>
        {
            // The line "... detached from the state" is written right before the detach is recorded.
            if (id.Id == 2041)
            {
                source.Cancel();
            }
        });
        TokenStrictSink? strict = null;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExecuteAsync(host, host.Resources(Group("keep")), logger: logger, token: source.Token, wrapSink: sink => strict = new TokenStrictSink(sink)));

        Assert.Single(strict!.Calls, c => c.StartsWith("Detached:", StringComparison.Ordinal));
        Assert.Contains("Completed:Cancelled", strict.Calls);
        var state = await StateOfAsync(host);
        Assert.Equal(1, new[] { "orphan-a", "orphan-b" }.Count(n => state.Find(GroupResourceId(n)) is not null));
    }

    [Fact]
    public async Task ADeleteRecorded_JustAsTheRunIsCancelled_IsStillRecorded_AndTheNextOrphanIsNotDeleted()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(Group("orphan-a") + ", " + Group("orphan-b"))));
        using var source = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var hooked = new HookedGraphClient(host.Fake) { After = (method, _) => method == "DELETE" ? source.CancelAsync() : Task.CompletedTask };
        TokenStrictSink? strict = null;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExecuteAsync(host, host.Resources(Group("keep")), DeploymentMode.Complete, graph: hooked, token: source.Token, wrapSink: sink => strict = new TokenStrictSink(sink)));

        Assert.Single(strict!.Calls, c => c.StartsWith("Deleted:", StringComparison.Ordinal));
        Assert.Contains("Completed:Cancelled", strict.Calls);
        Assert.Equal(1, host.Directory.Count("/groups") - 1);
        var state = await StateOfAsync(host);
        Assert.Equal(1, new[] { "orphan-a", "orphan-b" }.Count(n => state.Find(GroupResourceId(n)) is not null));
    }

    [Fact]
    public async Task ADeleteFailureRecorded_JustAsTheRunIsCancelled_IsStillRecorded()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(Group("orphan-a"))));
        using var source = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var hooked = new HookedGraphClient(host.Fake)
        {
            Before = async (method, request) =>
            {
                if (method == "DELETE")
                {
                    await source.CancelAsync();
                    throw new GraphException(HttpStatusCode.Forbidden, "Authorization_RequestDenied", "denied", request.Hint);
                }
            },
        };
        TokenStrictSink? strict = null;

        var outcome = await ExecuteAsync(host, host.Resources(Group("keep")), DeploymentMode.Complete, graph: hooked, token: source.Token, wrapSink: sink => strict = new TokenStrictSink(sink));

        Assert.Contains("DeleteFailed:" + GroupResourceId("orphan-a"), strict!.Calls);
        Assert.Contains(outcome.Result.Operations, o => o.Status == OperationStatus.Failed && o.Id.Id == GroupResourceId("orphan-a"));
        Assert.NotNull((await StateOfAsync(host)).Find(GroupResourceId("orphan-a")));
    }

    [Fact]
    public async Task TheEdgesAddedBeforeACancellation_AreRecorded_WithoutTheCancelledToken()
    {
        var host = new ProviderHost();
        string first = host.Directory.AddGroup("team-a", "Team A")["id"]!.GetValue<string>();
        string second = host.Directory.AddGroup("team-b", "Team B")["id"]!.GetValue<string>();
        using var source = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var hooked = new HookedGraphClient(host.Fake) { After = (method, request) => method == "POST" && request.Path.EndsWith("/members/$ref", StringComparison.Ordinal) ? source.CancelAsync() : Task.CompletedTask };
        TokenStrictSink? strict = null;
        var document = host.Resources(DirectoryTemplates.Alice("\"memberOf\": [\"" + first + "\", \"" + second + "\"]"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExecuteAsync(host, document, graph: hooked, token: source.Token, wrapSink: sink => strict = new TokenStrictSink(sink)));

        Assert.Equal(1, host.Directory.References("/groups/" + first + "/members").Count + host.Directory.References("/groups/" + second + "/members").Count);
        Assert.Contains("EdgesApplied:" + DirectoryTemplates.AliceId, strict!.Calls);
        Assert.Contains("EdgesFailed:" + DirectoryTemplates.AliceId, strict.Calls);
        var recorded = (await StateOfAsync(host)).Find(DirectoryTemplates.AliceId)!.Relationships;
        Assert.NotNull(recorded);
        Assert.Single(recorded!["memberOf"]!.AsArray());
    }
    // ---- DE40: a row with validation errors records no edges ----

    [Fact]
    public async Task ARowThatFailedValidation_RecordsNoEdges()
    {
        var host = new ProviderHost();
        TokenStrictSink? strict = null;
        string ghostGroup = Guid.NewGuid().ToString("D");

        var outcome = await ExecuteAsync(host, host.Resources(DirectoryTemplates.Alice("\"memberOf\": [\"" + ghostGroup + "\"]")), wrapSink: sink => strict = new TokenStrictSink(sink));

        Assert.Contains(outcome.Result.Operations, o => o.Status == OperationStatus.Failed && o.ErrorCode == EngineDiagnosticCodes.EdgeValidationFailed);
        Assert.DoesNotContain(strict!.Calls, c => c.StartsWith("EdgesApplied:", StringComparison.Ordinal));
        Assert.Contains("EdgesFailed:" + DirectoryTemplates.AliceId, strict.Calls);
    }

    // ---- DE10: ExecuteOptions.Parallelism wins over the plan's ----

    [Theory]
    [InlineData(1, 4, 1, 1)]
    [InlineData(3, 1, 2, 3)]
    [InlineData(null, 2, 2, 2)]
    public async Task TheExecutorsParallelism_IsTheOptionsValue_ElseThePlans(int? optionsParallelism, int planParallelism, int minObserved, int maxObserved)
    {
        var host = new ProviderHost();
        var hooked = new HookedGraphClient(host.Fake) { HoldReads = TimeSpan.FromMilliseconds(150) };
        var document = host.Resources(string.Join(", ", Enumerable.Range(1, 6).Select(i => Group("p" + i))));

        var outcome = await ExecuteAsync(host, document, execute: new ExecuteOptions { Parallelism = optionsParallelism }, graph: hooked, plannerOptions: o => o with { Parallelism = planParallelism });

        Check.Succeeded(new Run(outcome.Plan, outcome.Result, outcome.State, []));
        Assert.InRange(hooked.MaxInFlight, minObserved, maxObserved);
    }

    // ---- DE12 / DE14 / DE15: what an orphan may not delete ----

    [Fact]
    public async Task AnOrphanThatIsTheSignedInPrincipal_IsAProtectedObject_AndAnErrorDiagnostic()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(Group("me"))));
        string id = host.IdOf("/groups", "uniqueName", "me");
        host.SignedInPrincipalId = id.ToUpperInvariant();

        var run = await host.CreateAsync(host.Resources(Group("keep")), DeploymentMode.Complete);

        Assert.NotNull(host.Directory.Find("/groups", "uniqueName", "me"));
        Assert.DoesNotContain(run.Writes, w => w.Method == "DELETE");
        var diagnostic = Assert.Single(run.Executed.Diagnostics, d => d.Code == EngineDiagnosticCodes.ProtectedObject);
        Assert.True(diagnostic.IsError);
        Assert.Contains(run.Executed.Operations, o => o.Id.Id == GroupResourceId("me") && o.Status == OperationStatus.Failed && o.ErrorCode == EngineDiagnosticCodes.ProtectedObject);
    }

    private static async Task<(ProviderHost Host, string KeepId)> HostWithStaleDuplicateAsync()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(Group("keep"))));
        string keepId = host.IdOf("/groups", "uniqueName", "keep");

        // A stale entry of another resource id points at the object "keep" owns.
        var state = (await host.LoadStateAsync())!;
        var keep = state.Find(GroupResourceId("keep"))!;
        state.Upsert(keep with { Id = ResourceIdentifier.Create(GroupType, "old"), Key = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["uniqueName"] = "old" } });
        await host.Store.SaveAsync(state, Ct);
        return (host, keepId);
    }

    [Fact]
    public async Task AnOrphanThatPointsAtAnObjectADeclaredResourceClaimedInThisRun_IsDetached_NotDeleted()
    {
        var (host, keepId) = await HostWithStaleDuplicateAsync();

        var run = await host.CreateAsync(host.Resources(Group("keep")), DeploymentMode.Complete);

        Check.Succeeded(run);
        Assert.DoesNotContain(run.Writes, w => w.Method == "DELETE");
        Assert.NotNull(host.Directory.Find("/groups", "uniqueName", "keep"));
        var old = run.Executed.Operations.Single(o => o.Id.Id == GroupResourceId("old"));
        Assert.Equal(OperationStatus.Detached, old.Status);
        Assert.Contains(run.Executed.Diagnostics, d => d.Code == EngineDiagnosticCodes.OrphanSharesObject);
        Assert.Null((await StateOfAsync(host)).Find(GroupResourceId("old")));
        Assert.Equal(keepId, (await StateOfAsync(host)).Find(GroupResourceId("keep"))!.GraphId);
    }

    [Fact]
    public async Task AnOrphanThatPointsAtAnObjectTheStatePinsForADeclaredResource_IsDetached_EvenWhenThatResourceWasSkipped()
    {
        var (host, keepId) = await HostWithStaleDuplicateAsync();

        // "keep" depends on "bad", which fails: "keep" is skipped, never claims its object, but the state still pins it to "keep".
        host.Fake.Failures.Forbid("uniqueName='bad'", "PATCH");
        var document = host.Resources(App("bad") + ", " + Group("keep", extraResource: Templates.DependsOn(AppType, "bad")));

        var run = await host.CreateAsync(document, DeploymentMode.Complete, continueOnError: true);

        Assert.DoesNotContain(run.Writes, w => w.Method == "DELETE");
        Assert.Equal(OperationStatus.Skipped, run.Executed.Operations.Single(o => o.Id.Id == GroupResourceId("keep")).Status);
        var old = run.Executed.Operations.Single(o => o.Id.Id == GroupResourceId("old"));
        Assert.Equal(OperationStatus.Detached, old.Status);
        Assert.Contains(run.Executed.Diagnostics, d => d.Code == EngineDiagnosticCodes.OrphanSharesObject);
        Assert.NotNull(host.Directory.Find("/groups", "uniqueName", "keep"));
        Assert.Equal(keepId, (await StateOfAsync(host)).Find(GroupResourceId("keep"))!.GraphId);
    }

    // ---- DE49: the batch of a mode-pass operation ----

    [Fact]
    public async Task AModePassOperation_IsNumberedAfterTheLastBatch()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(Group("orphan"))));

        var run = await host.CreateAsync(host.Resources(Group("keep")), DeploymentMode.Complete);

        Check.Succeeded(run);
        var deleted = run.Executed.Operations.Single(o => o.Id.Id == GroupResourceId("orphan"));
        Assert.Equal(OperationStatus.Succeeded, deleted.Status);
        Assert.Equal(run.Plan.Batches.Count + 1, deleted.Batch);
        Assert.All(run.Executed.Operations.Where(o => o.Id.Id != GroupResourceId("orphan")), o => Assert.True(o.Batch <= run.Plan.Batches.Count));
    }
}
