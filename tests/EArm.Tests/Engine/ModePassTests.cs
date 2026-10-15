using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Engine.State;
using EArm.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EArm.Tests.Engine;

/// <summary>
/// The mode pass and the delete machinery: <see cref="ModePass"/> rules, the planner's
/// what-if rows with a state, the executor's detach / delete behaviour, and <see cref="DeploymentDeleter"/>.
/// </summary>
public sealed class ModePassTests
{
    private const string OldApp = "/providers/Microsoft.Entra/applications/old-app";
    private const string OldFic = OldApp + "/federatedIdentityCredentials/gh";
    private const string OldGroup = "/providers/Microsoft.Entra/groups/old-group";
    private const string OldSpAppId = "99999999-9999-9999-9999-999999999999";
    private const string OldSp = "/providers/Microsoft.Entra/servicePrincipals/" + OldSpAppId;
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StateEntry Entry(string id, string type, string graphId, string keyName, string keyValue, string? parentGraphId = null, params string[] dependsOn)
    {
        return new StateEntry
        {
            Id = StateSerializer.ParseResourceId(id, type)!,
            Type = type,
            ApiVersion = "2026-10-01-preview",
            Key = new Dictionary<string, string> { [keyName] = keyValue },
            GraphId = graphId,
            ParentGraphId = parentGraphId,
            DependsOn = dependsOn,
            LastWriteOn = Start,
        };
    }

    /// <summary>An application, its FIC and its service principal that a previous run created and the template no longer declares.</summary>
    private static StateFile SeedOrphans(EngineHost host, bool live = true)
    {
        var state = StateFile.CreateNew(EngineHost.DeploymentName, EngineHost.Tenant, Start);
        string appId = "OLD-APP";
        string ficId = "OLD-FIC";
        string spId = "OLD-SP";
        if (live)
        {
            host.Directory.Add("/applications", new JsonObject { ["id"] = appId, ["uniqueName"] = "old-app", ["displayName"] = "Old", ["appId"] = OldSpAppId });
            host.Directory.Add("/applications/" + appId + "/federatedIdentityCredentials", new JsonObject { ["id"] = ficId, ["name"] = "gh", ["issuer"] = "i", ["subject"] = "s" });
            host.Directory.Add("/servicePrincipals", new JsonObject { ["id"] = spId, ["appId"] = OldSpAppId, ["displayName"] = "Old" });
        }

        state.Upsert(Entry(OldApp, Fixture.AppType, appId, "uniqueName", "old-app"));
        state.Upsert(Entry(OldFic, Fixture.FicType, ficId, "name", "gh", appId, OldApp));
        state.Upsert(Entry(OldSp, Fixture.SpType, spId, "appId", OldSpAppId, null, OldApp));
        return state;
    }

    private static TemplateDocument Empty()
    {
        return Templates.Resources(string.Empty);
    }

    // ---- OrderForDeletion ----

    [Fact]
    public void OrderForDeletion_IsReverseDependencyOrder_ThenPhase_ThenId()
    {
        var app = Entry(OldApp, Fixture.AppType, "A", "uniqueName", "old-app");
        var sp = Entry(OldSp, Fixture.SpType, "S", "appId", OldSpAppId, null, OldApp);
        var group = Entry(OldGroup, Fixture.GroupType, "G", "uniqueName", "old-group", null, OldSp);
        var fic = Entry(OldFic, Fixture.FicType, "F", "name", "gh", "A", OldApp);
        var policy = Entry("/providers/Microsoft.Entra/conditionalAccess/policies/CA", Fixture.PolicyType, "P", "displayName", "CA");
        var otherGroup = Entry("/providers/Microsoft.Entra/groups/aaa", Fixture.GroupType, "G2", "uniqueName", "aaa");

        var ordered = ModePass.OrderForDeletion([app, sp, group, fic, policy, otherGroup]).Select(e => e.Id.Id).ToList();

        Assert.Equal(["/providers/Microsoft.Entra/conditionalAccess/policies/CA", "/providers/Microsoft.Entra/groups/aaa", OldGroup, OldFic, OldSp, OldApp], ordered);
    }

    [Fact]
    public void OrderForDeletion_IgnoresDependenciesOutsideTheSet_AndFallsBackOnCycles()
    {
        var a = Entry("/providers/Microsoft.Entra/groups/a", Fixture.GroupType, "A", "uniqueName", "a", null, "/providers/Microsoft.Entra/users/elsewhere@contoso.com", "/providers/Microsoft.Entra/groups/b");
        var b = Entry("/providers/Microsoft.Entra/groups/b", Fixture.GroupType, "B", "uniqueName", "b", null, "/providers/Microsoft.Entra/groups/a");
        var c = Entry("/providers/Microsoft.Entra/groups/c", Fixture.GroupType, "C", "uniqueName", "c", null, "/providers/Microsoft.Entra/users/elsewhere@contoso.com");

        var ordered = ModePass.OrderForDeletion([b, a, c]).Select(e => e.Id.NameLeaf).ToList();

        Assert.Equal(["c", "a", "b"], ordered);
        Assert.Empty(ModePass.OrderForDeletion([]));
    }

    // ---- FindOrphans ----

    [Fact]
    public async Task FindOrphans_ExcludesDeclaredIds_CountsConditionFalse_AndLeavesSymbolicTypesUndecided()
    {
        var host = new EngineHost();
        var plan = await host.ExpandAsync(await Templates.FixtureAsync());
        var state = StateFile.CreateNew(EngineHost.DeploymentName, EngineHost.Tenant, Start);
        state.Upsert(Entry(Fixture.App, Fixture.AppType, "A", "uniqueName", "contoso-dev-api"));
        state.Upsert(Entry(Fixture.Fic0, Fixture.FicType, "F", "name", "github-0", "A", Fixture.App));
        state.Upsert(Entry(Fixture.Policy, Fixture.PolicyType, "P", "displayName", "CA-Orders-Admins-MFA"));
        state.Upsert(Entry(OldGroup, Fixture.GroupType, "G", "uniqueName", "old-group"));
        state.Upsert(Entry(OldSp, Fixture.SpType, "S", "appId", OldSpAppId));
        state.Upsert(Entry("/providers/Microsoft.Entra/roleAssignments/11111111-1111-1111-1111-111111111111", Fixture.RoleAssignmentType, "R", "principalId", "x"));

        var orphans = ModePass.FindOrphans(state, plan, out var undecided);

        Assert.Equal([OldGroup], orphans.Select(o => o.Id.Id).ToList());
        Assert.Equal(2, undecided.Count);
        Assert.Contains(undecided, u => u.Type == Fixture.SpType && u.PlannedId == Fixture.SpSymbolic);
        Assert.Contains(undecided, u => u.Type == Fixture.RoleAssignmentType);
    }

    // ---- planner (what-if) with state ----

    [Fact]
    public async Task WhatIf_ReadsThroughThePinnedGraphIdFirst()
    {
        var host = new EngineHost();
        host.SeedExisting();
        var app = host.Directory.AddApplication("contoso-dev-api", "Orders API");
        var state = StateFile.CreateNew(EngineHost.DeploymentName, EngineHost.Tenant, Start);
        state.Upsert(Entry(Fixture.App, Fixture.AppType, app["id"]!.GetValue<string>(), "uniqueName", "contoso-dev-api"));

        var plan = await host.WhatIfAsync(await Templates.FixtureAsync(), state);

        var appReads = host.Fake.Requests.Where(r => r.Path.StartsWith("/applications", StringComparison.Ordinal) && !r.Path.Contains("federatedIdentityCredentials", StringComparison.Ordinal)).ToList();
        Assert.Equal("/applications/" + app["id"], appReads[0].Path);
        Assert.DoesNotContain(appReads, r => r.Path == "/applications(uniqueName='contoso-dev-api')");
        Assert.Equal(ChangeType.Modify, plan.Changes.Single(c => c.Id.Id == Fixture.App).Type);
        Assert.Empty(host.Fake.Writes);
        Assert.Empty(plan.ModeChanges);
    }

    [Fact]
    public async Task WhatIf_Incremental_ReportsOrphansAsIgnoreRows_WithAResourceDetachedWarning()
    {
        var host = new EngineHost();
        var state = SeedOrphans(host);

        var plan = await host.WhatIfAsync(Empty(), state, DeploymentMode.Incremental);

        Assert.Equal(3, plan.ModeChanges.Count);
        Assert.All(plan.ModeChanges, c =>
        {
            Assert.Equal(ChangeType.Ignore, c.Type);
            Assert.Equal(ChangeMode.None, c.Mode);
            Assert.Equal([ModePass.DetachReasonWhatIf], c.Reasons);
            Assert.False(c.IsWrite);
        });
        Assert.Equal(plan.ModeChanges, plan.Changes);
        Assert.False(plan.HasChanges);
        Assert.Equal(3, plan.Summary.Ignore);
        Assert.Equal(3, plan.Diagnostics.Count(d => d.Code == EngineDiagnosticCodes.ResourceDetached && !d.IsError));
        Assert.Contains(plan.Diagnostics, d => d.Message.Contains(OldFic, StringComparison.Ordinal) && d.Message.Contains("--mode Complete", StringComparison.Ordinal));
        Assert.Empty(host.Fake.Requests);
    }

    [Fact]
    public async Task WhatIf_Complete_ReportsOrphansAsDeleteRows_WithTheLiveObjectAsBefore_InDeleteOrder()
    {
        var host = new EngineHost();
        var state = SeedOrphans(host);

        var plan = await host.WhatIfAsync(Empty(), state, DeploymentMode.Complete);

        Assert.Equal([OldFic, OldSp, OldApp], plan.ModeChanges.Select(c => c.Id.Id).ToList());
        Assert.All(plan.ModeChanges, c =>
        {
            Assert.Equal(ChangeType.Delete, c.Type);
            Assert.Equal(ChangeMode.Delete, c.Mode);
            Assert.True(c.IsWrite);
            Assert.Null(c.UnsupportedReason);
            Assert.NotNull(c.Before);
            Assert.Null(c.After);
            Assert.Equal('-', c.Symbol);
        });
        var app = plan.ModeChanges[2];
        Assert.Equal("OLD-APP", app.GraphId);
        Assert.Equal("\"Old\"", app.Before!["displayName"]!.ToJsonString());
        Assert.False(app.Before.ContainsKey("appId"), "the before view is the stripped diff view");
        Assert.True(plan.HasChanges);
        Assert.Equal(3, plan.Summary.Delete);
        Assert.Empty(host.Fake.Writes);
        // Review finding S-01: the live object is looked up by its KEY (never by the pinned id alone) before a delete is planned.
        Assert.Contains(host.Fake.Requests, r => r.Path == "/applications/OLD-APP/federatedIdentityCredentials(name='gh')");
        Assert.DoesNotContain(plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.ResourceDetached);
    }

    [Fact]
    public async Task WhatIf_Complete_AnObjectAlreadyGone_IsAnIgnoreRow()
    {
        var host = new EngineHost();
        var state = SeedOrphans(host, live: false);

        var plan = await host.WhatIfAsync(Empty(), state, DeploymentMode.Complete);

        Assert.Equal(3, plan.ModeChanges.Count);
        Assert.All(plan.ModeChanges, c =>
        {
            Assert.Equal(ChangeType.Ignore, c.Type);
            Assert.Contains("no longer in the tenant", c.Reasons[0]);
        });
        Assert.False(plan.HasChanges);
    }

    [Fact]
    public async Task WhatIf_Complete_WithoutAProviderForTheEntryType_IsAnUnsupportedDeleteRow()
    {
        var registry = new ProviderRegistry();
        registry.Register(new EchoProvider(EArm.Core.Types.ResourceTypeCatalog.Default.Find(Fixture.AppType)!));
        var host = new EngineHost(registry: registry);
        var state = StateFile.CreateNew(EngineHost.DeploymentName, EngineHost.Tenant, Start);
        state.Upsert(Entry(OldGroup, Fixture.GroupType, "G", "uniqueName", "old-group"));

        var plan = await host.WhatIfAsync(Empty(), state, DeploymentMode.Complete);

        var row = Assert.Single(plan.ModeChanges);
        Assert.Equal(ChangeType.Delete, row.Type);
        Assert.True(row.HasError);
        Assert.Contains("No resource provider is registered", row.UnsupportedReason);
        Assert.True(plan.HasChanges);
    }

    [Fact]
    public async Task WhatIf_SymbolicTypes_AreLeftAloneWithAWarning_UntilTheNameResolves()
    {
        var unresolved = new EngineHost();
        unresolved.SeedExisting();
        var state = StateFile.CreateNew(EngineHost.DeploymentName, EngineHost.Tenant, Start);
        state.Upsert(Entry(OldSp, Fixture.SpType, "S", "appId", OldSpAppId));

        var undecided = await unresolved.WhatIfAsync(await Templates.FixtureAsync(), state, DeploymentMode.Complete);

        Assert.Empty(undecided.ModeChanges);
        var warnings = undecided.Diagnostics.Where(d => d.Code == EngineDiagnosticCodes.ResourceDetached).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w => Assert.Contains("were not checked against the template", w.Message));
        Assert.Contains(warnings, w => w.Message.Contains(Fixture.SpType, StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Message.Contains(Fixture.RoleAssignmentType, StringComparison.Ordinal));

        var resolved = new EngineHost();
        resolved.SeedExisting();
        resolved.Directory.AddApplication("contoso-dev-api", "Orders API", "aaaaaaaa-1111-2222-3333-444444444444");
        string oldSpId = resolved.Directory.AddServicePrincipal(OldSpAppId, "Old")["id"]!.GetValue<string>();
        state.Upsert(Entry(OldSp, Fixture.SpType, oldSpId, "appId", OldSpAppId, null));
        var decided = await resolved.WhatIfAsync(await Templates.FixtureAsync(), state, DeploymentMode.Complete);

        var row = Assert.Single(decided.ModeChanges);
        Assert.Equal(OldSp, row.Id.Id);
        Assert.Equal(ChangeType.Delete, row.Type);
    }

    // ---- executor with state ----

    private static async Task<(EngineHost Host, StateFile State, InMemoryStateStore Store, DeploymentResult Result)> ExecuteAsync(EngineHost host, StateFile state, TemplateDocument document, DeploymentMode mode, bool continueOnError = false)
    {
        var store = new InMemoryStateStore();
        var plan = await host.ExpandAsync(document, state, mode);
        var sink = new FileStateSink(store, state, plan);
        await sink.BeginAsync(Ct);
        var result = await host.Executor().ExecuteAsync(plan, new ExecuteOptions { StateSink = sink, State = state, ContinueOnError = continueOnError }, Ct);
        return (host, state, store, result);
    }

    [Fact]
    public async Task Incremental_DetachesOrphansFromTheState_AndLeavesTheObjects()
    {
        var host = new EngineHost();
        var state = SeedOrphans(host);

        var (_, _, _, result) = await ExecuteAsync(host, state, Empty(), DeploymentMode.Incremental);

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.DetachedCount);
        Assert.Equal(0, result.DeletedCount);
        Assert.Empty(state.Resources);
        Assert.Equal(3, result.Diagnostics.Count(d => d.Code == EngineDiagnosticCodes.ResourceDetached && !d.IsError));
        Assert.All(result.Operations, o =>
        {
            Assert.Equal(OperationStatus.Detached, o.Status);
            Assert.Equal(ChangeType.Ignore, o.ChangeType);
            Assert.Equal('*', o.Symbol);
        });
        Assert.All(result.Changes, c => Assert.Equal([ModePass.DetachReason], c.Reasons));
        Assert.Empty(host.Fake.Writes);
        Assert.Equal(1, host.Directory.Count("/applications"));
        Assert.Equal(1, host.Directory.Count("/servicePrincipals"));
        Assert.Equal(3, state.Operations.Count(o => o.Status == OperationStatus.Detached));
    }

    [Fact]
    public async Task Complete_DeletesOrphansInReverseDependencyOrder_AndDropsTheEntries()
    {
        var host = new EngineHost();
        var state = SeedOrphans(host);

        var (_, _, store, result) = await ExecuteAsync(host, state, Empty(), DeploymentMode.Complete);

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.DeletedCount);
        Assert.Equal(0, result.SucceededCount);
        Assert.Empty(state.Resources);
        Assert.Equal(StateFile.StatusSucceeded, state.LastStatus);
        Assert.Equal(["DELETE /applications/OLD-APP/federatedIdentityCredentials/OLD-FIC", "DELETE /servicePrincipals/OLD-SP", "DELETE /applications/OLD-APP"], host.Fake.Writes.Select(w => w.ToString()).ToList());
        Assert.Equal([OldFic, OldSp, OldApp], result.Operations.Select(o => o.Id.Id).ToList());
        Assert.All(result.Operations, o =>
        {
            Assert.Equal(OperationStatus.Succeeded, o.Status);
            Assert.Equal(ChangeType.Delete, o.ChangeType);
            Assert.Equal('-', o.Symbol);
        });
        Assert.Equal(0, host.Directory.Count("/applications"));
        Assert.Equal(0, host.Directory.Count("/servicePrincipals"));
        Assert.Equal(1 + 3 + 1, store.Saves.Count);
        Assert.All(result.Changes, c => Assert.Equal([ModePass.DeleteReason], c.Reasons));
    }

    [Fact]
    public async Task Complete_AnObjectAlreadyGone_CountsAsDeleted()
    {
        var host = new EngineHost();
        var state = SeedOrphans(host, live: false);

        var (_, _, _, result) = await ExecuteAsync(host, state, Empty(), DeploymentMode.Complete);

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.DeletedCount);
        Assert.Empty(state.Resources);

        // The objects are found gone by the key re-read, so no DELETE is sent at all.
        Assert.Empty(host.Fake.Writes);
    }

    [Fact]
    public async Task Complete_DeleteFailure_StopsThePass_UnlessContinueOnError()
    {
        var stopped = new EngineHost();
        stopped.Fake.Failures.Forbid("/servicePrincipals/OLD-SP", "DELETE");
        var (_, stoppedState, _, stop) = await ExecuteAsync(stopped, SeedOrphans(stopped), Empty(), DeploymentMode.Complete);

        Assert.False(stop.Succeeded);
        Assert.Equal(1, stop.DeletedCount);
        Assert.Equal(1, stop.FailedCount);
        Assert.Equal(1, stop.SkippedCount);
        Assert.Equal([OldApp, OldSp], stoppedState.Resources.Keys.Order(StringComparer.Ordinal).ToList());
        var failed = Assert.Single(stop.Operations, o => o.Status == OperationStatus.Failed);
        Assert.Equal(OldSp, failed.Id.Id);
        Assert.Equal(EngineDiagnosticCodes.ResourceDeleted, failed.ErrorCode);
        Assert.Contains("Authorization_RequestDenied", failed.Error);
        var skipped = Assert.Single(stop.Operations, o => o.Status == OperationStatus.Skipped);
        Assert.Equal(OldApp, skipped.Id.Id);
        Assert.Equal("an earlier operation failed", skipped.Error);
        Assert.Equal(StateFile.StatusFailed, stoppedState.LastStatus);
        Assert.Contains(stoppedState.Operations, o => o.ResourceId == OldSp && o.Status == OperationStatus.Failed && o.ErrorCode == EngineDiagnosticCodes.ResourceDeleted);

        var continued = new EngineHost();
        continued.Fake.Failures.Forbid("/servicePrincipals/OLD-SP", "DELETE");
        var (_, continuedState, _, go) = await ExecuteAsync(continued, SeedOrphans(continued), Empty(), DeploymentMode.Complete, continueOnError: true);

        Assert.False(go.Succeeded);
        Assert.Equal(2, go.DeletedCount);
        Assert.Equal(0, go.SkippedCount);
        Assert.Equal([OldSp], continuedState.Resources.Keys.ToList());
    }

    [Fact]
    public async Task Complete_ModePassIsSkipped_WhenTheRunStoppedAtABatchBoundary()
    {
        var host = new EngineHost();
        host.SeedExisting();
        var state = SeedOrphans(host);
        host.Fake.Failures.Forbid("/users", "POST");

        var (_, _, _, result) = await ExecuteAsync(host, state, await Templates.FixtureAsync(), DeploymentMode.Complete);

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.DeletedCount);

        // Batch 1 applied four resources before the user failed; the three seeded entries stay (the service
        // principal entry is undecided because the symbolic instance never resolved, the other two were skipped).
        Assert.Equal(3 + 4, state.Resources.Count);
        Assert.Contains(OldApp, state.Resources.Keys);
        Assert.Contains(OldFic, state.Resources.Keys);
        Assert.Contains(OldSp, state.Resources.Keys);
        Assert.Equal(2, result.Operations.Count(o => o.Status == OperationStatus.Skipped && o.ChangeType == ChangeType.Delete && o.Error == "an earlier operation failed"));
        Assert.DoesNotContain(host.Fake.Writes, w => w.Method == "DELETE");
    }

    [Fact]
    public async Task Incremental_ModePassStillRuns_WhenTheRunStoppedEarly()
    {
        var host = new EngineHost();
        host.SeedExisting();
        var state = SeedOrphans(host);
        host.Fake.Failures.Forbid("/users", "POST");

        var (_, _, _, result) = await ExecuteAsync(host, state, await Templates.FixtureAsync(), DeploymentMode.Incremental);

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.DetachedCount);
        Assert.DoesNotContain(OldApp, state.Resources.Keys);
        Assert.DoesNotContain(OldFic, state.Resources.Keys);
        Assert.Contains(OldSp, state.Resources.Keys);
    }

    [Fact]
    public async Task ExecutorWithoutAState_SkipsTheModePass()
    {
        var host = new EngineHost();
        var state = SeedOrphans(host);
        var plan = await host.ExpandAsync(Empty(), state, DeploymentMode.Complete);

        var result = await host.Executor().ExecuteAsync(plan, ExecuteOptions.Default, Ct);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Operations);
        Assert.Empty(host.Fake.Writes);
    }

    // ---- DeploymentDeleter ----

    [Fact]
    public async Task DeleteAll_DeletesEveryEntryInOrder_SavingAfterEach_ThenRemovesTheState()
    {
        var host = new EngineHost();
        var state = SeedOrphans(host);
        var store = new InMemoryStateStore();
        await store.SaveAsync(state, Ct);

        var result = await DeploymentDeleter.DeleteAllAsync(state, store, host.Registry, host.Graph, host.Retry, continueOnError: false, NullLogger.Instance, Ct);

        Assert.True(result.Succeeded);
        Assert.True(result.StateRemoved);
        Assert.Equal(3, result.DeletedCount);
        Assert.Equal(0, result.AlreadyGoneCount);
        Assert.Equal([OldFic, OldSp, OldApp], result.Outcomes.Select(o => o.Entry.Id.Id).ToList());
        Assert.All(result.Outcomes, o => Assert.Equal(DeleteStatus.Deleted, o.Status));
        Assert.Null(store.DocumentOf(EngineHost.DeploymentName));
        Assert.Equal(1 + 3, store.Saves.Count);
        Assert.Contains("\"lastStatus\": \"Running\"", store.Saves[1]);
        Assert.Empty(state.Resources);
        Assert.Equal(0, host.Directory.Count("/applications"));
    }

    [Fact]
    public async Task DeleteAll_AlreadyGoneObjects_CountSeparately()
    {
        var host = new EngineHost();
        var state = SeedOrphans(host, live: false);
        var store = new InMemoryStateStore();

        var result = await DeploymentDeleter.DeleteAllAsync(state, store, host.Registry, host.Graph, null, continueOnError: false, NullLogger.Instance, Ct);

        Assert.True(result.Succeeded);
        Assert.True(result.StateRemoved);
        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(3, result.AlreadyGoneCount);
    }

    [Fact]
    public async Task DeleteAll_FailureStops_KeepsTheRemainingEntries_AndMarksTheStateFailed()
    {
        var host = new EngineHost();
        host.Fake.Failures.Forbid("/servicePrincipals/OLD-SP", "DELETE");
        var state = SeedOrphans(host);
        var store = new InMemoryStateStore();

        var result = await DeploymentDeleter.DeleteAllAsync(state, store, host.Registry, host.Graph, host.Retry, continueOnError: false, NullLogger.Instance, Ct);

        Assert.False(result.Succeeded);
        Assert.False(result.StateRemoved);
        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(DeleteStatus.Failed, result.Outcomes[1].Status);
        Assert.Equal(EngineDiagnosticCodes.ResourceDeleted, result.Outcomes[1].ErrorCode);
        Assert.Equal(DeleteStatus.Skipped, result.Outcomes[2].Status);
        Assert.Equal([OldApp, OldSp], state.Resources.Keys.Order(StringComparer.Ordinal).ToList());
        Assert.Equal(StateFile.StatusFailed, state.LastStatus);
        Assert.NotNull(store.DocumentOf(EngineHost.DeploymentName));

        // One save per attempted entry (the skipped one records nothing new) plus the final failed-state save.
        Assert.Equal(2 + 1, store.Saves.Count);
        Assert.Contains(state.Operations, o => o.ResourceId == OldApp && o.Status == OperationStatus.Skipped && o.ErrorCode == EngineDiagnosticCodes.ResourceSkipped);

        var continued = new EngineHost();
        continued.Fake.Failures.Forbid("/servicePrincipals/OLD-SP", "DELETE");
        var continuedState = SeedOrphans(continued);
        var go = await DeploymentDeleter.DeleteAllAsync(continuedState, new InMemoryStateStore(), continued.Registry, continued.Graph, null, continueOnError: true, NullLogger.Instance, Ct);

        Assert.Equal(2, go.DeletedCount);
        Assert.Equal(0, go.SkippedCount);
        Assert.Equal([OldSp], continuedState.Resources.Keys.ToList());
    }

    [Fact]
    public async Task DeleteAll_WithoutAProvider_FailsTheEntry()
    {
        var registry = new ProviderRegistry();
        var host = new EngineHost(registry: registry);
        var state = StateFile.CreateNew(EngineHost.DeploymentName, EngineHost.Tenant, Start);
        state.Upsert(Entry(OldGroup, Fixture.GroupType, "G", "uniqueName", "old-group"));

        var result = await DeploymentDeleter.DeleteAllAsync(state, new InMemoryStateStore(), registry, host.Graph, null, continueOnError: false, NullLogger.Instance, Ct);

        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(DeleteStatus.Failed, outcome.Status);
        Assert.Contains("No resource provider is registered", outcome.Error);
        Assert.False(result.StateRemoved);
        Assert.Single(state.Resources);
    }

    [Fact]
    public async Task StateDeletion_RetriesTransientFailures_ThroughThePolicy()
    {
        var host = new EngineHost();
        var state = SeedOrphans(host);
        host.Fake.Failures.ServerError("/applications/OLD-APP/federatedIdentityCredentials/OLD-FIC", times: 2, method: "DELETE");

        var outcome = await StateDeletion.DeleteAsync(state.Resources[OldFic], host.Registry, host.Graph, host.Retry, Ct);

        Assert.Equal(DeleteStatus.Deleted, outcome.Status);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], host.Delays.Delays);
        Assert.Equal(0, host.Directory.Count("/applications/OLD-APP/federatedIdentityCredentials"));
    }
}
