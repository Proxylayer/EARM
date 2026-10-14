using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Az;
using EntraArm.Core.Secrets;
using EntraArm.Engine;
using EntraArm.Engine.Execution;
using EntraArm.Engine.Planning;
using EntraArm.Engine.Providers;
using EntraArm.Engine.State;
using EntraArm.TestSupport;
using Xunit;

namespace EntraArm.Tests.Engine;

/// <summary><see cref="FileStateSink"/> over <see cref="InMemoryStateStore"/>: one save per resource, concrete ids, hashes, failures, tenant pin.</summary>
public sealed class FileStateSinkTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Run(EngineHost Host, InMemoryStateStore Store, StateFile State, DeploymentPlan Plan, DeploymentResult Result);

    private static async Task<Run> ExecuteAsync(string? password = Templates.DefaultPassword, StateFile? state = null, Action<EngineHost>? configure = null, bool continueOnError = false)
    {
        var host = new EngineHost();
        host.SeedExisting();
        configure?.Invoke(host);
        var store = new InMemoryStateStore();
        var plan = await host.ExpandAsync(await Templates.FixtureAsync(password: password));
        state ??= StateFile.CreateNew(EngineHost.DeploymentName, null, Start);
        var sink = new FileStateSink(store, state, plan, timeProvider: new FakeTimeProvider(Start));
        await sink.BeginAsync(Ct);
        var result = await host.Executor().ExecuteAsync(plan, new ExecuteOptions { StateSink = sink, State = state, ContinueOnError = continueOnError }, Ct);
        return new Run(host, store, state, plan, result);
    }

    [Fact]
    public async Task EveryResourceIsSavedAsItLands_AndTheRunMetadataIsRecorded()
    {
        var run = await ExecuteAsync();

        Assert.True(run.Result.Succeeded);
        Assert.True(run.Store.Saves.Count >= 1 + 15 + 1 + 1, "begin + one save per applied resource + ignored + completed (+ edge recordings)");
        Assert.Contains("\"lastStatus\": \"Running\"", run.Store.Saves[0]);
        Assert.Contains("\"tenantId\": \"" + EngineHost.Tenant + "\"", run.Store.Saves[0]);
        Assert.Equal(15, run.State.Resources.Count);
        Assert.Equal(StateFile.StatusSucceeded, run.State.LastStatus);
        Assert.Equal("Incremental", run.State.LastMode);
        Assert.Equal(run.Plan.TemplateHash, run.State.LastTemplateHash);
        Assert.Equal(Start, run.State.LastDeployedOn);
        Assert.Equal(Start, run.State.CreatedOn);
        Assert.Equal(EngineHost.Tenant, run.State.TenantId);
        Assert.Equal(Fixture.InstanceCount, run.State.Operations.Count);

        // Saves grow monotonically: each one carries at least the entries of the previous one.
        var counts = run.Store.Saves.Select(s => JsonNode.Parse(s)!["resources"]!.AsObject().Count).ToList();
        Assert.True(counts.Zip(counts.Skip(1)).All(pair => pair.Second >= pair.First), string.Join(",", counts));
        Assert.Equal(15, counts.Zip(counts.Skip(1)).Count(pair => pair.Second == pair.First + 1));
        Assert.Equal(15, counts[^1]);

        string final = run.Store.DocumentOf(EngineHost.DeploymentName)!;
        Assert.DoesNotContain(Templates.DefaultPassword, final, StringComparison.Ordinal);
        Assert.DoesNotContain("fake-secret-", final, StringComparison.Ordinal);
        Assert.DoesNotContain("ref(", final, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntriesUseConcreteIds_ForCopyIterationsAndSymbolicNames_AndMapDependsOn()
    {
        var run = await ExecuteAsync();
        string appId = Assert.Single(run.Host.Directory.Objects("/applications"))["appId"]!.GetValue<string>();
        string appGraphId = Assert.Single(run.Host.Directory.Objects("/applications"))["id"]!.GetValue<string>();

        Assert.Contains(Fixture.Fic0, run.State.Resources.Keys);
        Assert.Contains(Fixture.Fic1, run.State.Resources.Keys);
        Assert.Contains(Fixture.Sp(appId), run.State.Resources.Keys);
        Assert.DoesNotContain(Fixture.Policy, run.State.Resources.Keys);
        Assert.DoesNotContain(run.State.Resources.Keys, k => k.Contains("ref(", StringComparison.Ordinal));

        var app = run.State.Resources[Fixture.App];
        Assert.Equal(appGraphId, app.GraphId);
        Assert.Equal([Fixture.User], app.DependsOn);
        Assert.Equal("contoso-dev-api", app.Key["uniqueName"]);
        Assert.Equal("2026-10-01", app.ApiVersion);
        Assert.Equal(Start, app.LastWriteOn);
        Assert.StartsWith("sha256:", app.ContentHash, StringComparison.Ordinal);

        var sp = run.State.Resources[Fixture.Sp(appId)];
        Assert.Equal(appId, sp.Key["appId"]);
        Assert.Equal([Fixture.App], sp.DependsOn);

        var group = run.State.Resources[Fixture.Group];
        Assert.Equal([Fixture.Sp(appId)], group.DependsOn);

        var fic = run.State.Resources[Fixture.Fic0];
        Assert.Equal(appGraphId, fic.ParentGraphId);
        Assert.Equal([Fixture.App], fic.DependsOn);
        Assert.Equal("github-0", fic.Key["name"]);

        var password = run.State.Resources[Fixture.Password];
        Assert.Equal(appGraphId, password.ParentGraphId);
        Assert.NotNull(password.Extra);
        Assert.Equal(password.GraphId, password.Extra["keyId"]!.GetValue<string>());
        Assert.Equal("2027-10-01T00:00:00Z", password.Extra["endDateTime"]!.GetValue<string>());
        Assert.False(password.Extra.ContainsKey("secretText"));

        var assignment = run.State.Resources.Values.Single(e => e.Type == Fixture.AppRoleAssignmentType);
        Assert.Equal(3, assignment.Key.Count);
        Assert.Equal([Fixture.App, Fixture.Group, Fixture.Sp(appId)], assignment.DependsOn);
    }

    [Fact]
    public async Task ContentHash_IsStable_AndIndependentOfSecretValues()
    {
        var first = await ExecuteAsync(password: "first-P@ss1");
        var second = await ExecuteAsync(password: "second-P@ss2");

        Assert.Equal(first.State.Resources[Fixture.User].ContentHash, second.State.Resources[Fixture.User].ContentHash);
        Assert.Equal(first.State.Resources[Fixture.App].ContentHash, second.State.Resources[Fixture.App].ContentHash);
        Assert.NotEqual(first.State.Resources[Fixture.User].ContentHash, first.State.Resources[Fixture.App].ContentHash);
        Assert.Equal(first.Plan.TemplateHash, second.Plan.TemplateHash);
    }

    [Fact]
    public async Task Operations_RecordStatusChangeTypeBatchAndTimes()
    {
        var run = await ExecuteAsync();

        var user = Assert.Single(run.State.Operations, o => o.ResourceId == Fixture.User);
        Assert.Equal(OperationStatus.Succeeded, user.Status);
        Assert.Equal(ChangeType.Create, user.ChangeType);
        Assert.Equal(1, user.Batch);
        Assert.Equal(Start, user.StartedOn);
        Assert.Equal(Start, user.FinishedOn);
        Assert.Equal(run.State.Resources[Fixture.User].GraphId, user.GraphId);

        var policy = Assert.Single(run.State.Operations, o => o.ResourceId == Fixture.Policy);
        Assert.Equal(OperationStatus.Ignored, policy.Status);
        Assert.Equal(ChangeType.Ignore, policy.ChangeType);
        Assert.Equal(5, policy.Batch);
        Assert.Equal("condition evaluated to false", policy.Error);
        Assert.Null(policy.ErrorCode);
    }

    [Fact]
    public async Task Failure_KeepsThePreviousEntry_AndRecordsARedactedError()
    {
        var state = StateFile.CreateNew(EngineHost.DeploymentName, EngineHost.Tenant, Start);
        state.Upsert(new StateEntry
        {
            Id = EntraArm.Core.ResourceIdentifier.Create(Fixture.UserType, "bg-orders@contoso.com"),
            Type = Fixture.UserType,
            ApiVersion = "2026-10-01",
            Key = new Dictionary<string, string> { ["userPrincipalName"] = "bg-orders@contoso.com" },
            GraphId = "OLD-ID",
            ContentHash = "sha256:old",
            LastWriteOn = Start.AddDays(-1),
        });

        // Fail fast: every later batch is skipped through the sink (eleven Skipped operations, no entries for them).
        var run = await ExecuteAsync(state: state, continueOnError: false, configure: host => host.Fake.Failures.Add("POST", "/users", request => new GraphException(HttpStatusCode.Forbidden, "Authorization_RequestDenied", "Insufficient privileges; password=hunter2 token=eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJlLXBhcnQ", request.Hint)));

        Assert.False(run.Result.Succeeded);
        var kept = run.State.Resources[Fixture.User];
        Assert.Equal("OLD-ID", kept.GraphId);
        Assert.Equal("sha256:old", kept.ContentHash);
        Assert.Equal(Start.AddDays(-1), kept.LastWriteOn);
        Assert.Equal(StateFile.StatusFailed, run.State.LastStatus);

        var operation = Assert.Single(run.State.Operations, o => o.ResourceId == Fixture.User);
        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.Equal(EngineDiagnosticCodes.ResourceApplyFailed, operation.ErrorCode);
        Assert.Equal("OLD-ID", operation.GraphId);
        Assert.Contains("Authorization_RequestDenied", operation.Error);
        Assert.DoesNotContain("hunter2", operation.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGciOiJSUzI1NiJ9", operation.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", run.Store.DocumentOf(EngineHost.DeploymentName), StringComparison.Ordinal);

        // Skipped dependents are recorded as operations but never as entries.
        Assert.Contains(run.State.Operations, o => o.ResourceId == Fixture.App && o.Status == OperationStatus.Skipped && o.ErrorCode == EngineDiagnosticCodes.ResourceSkipped);
        Assert.DoesNotContain(Fixture.App, run.State.Resources.Keys);
        Assert.Equal(4, run.State.Operations.Count(o => o.Status == OperationStatus.Succeeded));
        Assert.Equal(1, run.State.Operations.Count(o => o.Status == OperationStatus.Failed));
        Assert.Equal(11, run.State.Operations.Count(o => o.Status == OperationStatus.Skipped));
        Assert.Equal(1 + run.State.Operations.Count + 1, run.Store.Saves.Count);
    }

    [Fact]
    public async Task Drift_RePinsTheEntryToTheLiveObject()
    {
        var state = StateFile.CreateNew(EngineHost.DeploymentName, EngineHost.Tenant, Start);
        state.Upsert(new StateEntry
        {
            Id = EntraArm.Core.ResourceIdentifier.Create(Fixture.UserType, "bg-orders@contoso.com"),
            Type = Fixture.UserType,
            ApiVersion = "2026-10-01",
            Key = new Dictionary<string, string> { ["userPrincipalName"] = "bg-orders@contoso.com" },
            GraphId = "STALE",
        });
        JsonObject? live = null;

        var run = await ExecuteAsync(state: state, configure: host => live = host.Directory.AddUser("bg-orders@contoso.com", "Break Glass (Orders)", u =>
        {
            u["mailNickname"] = "bg-orders";
            u["usageLocation"] = "GB";
        }));

        Assert.True(run.Result.Succeeded);
        Assert.Equal(live!["id"]!.GetValue<string>(), run.State.GraphIdOf(Fixture.User));
        Assert.Equal(ChangeType.NoChange, Assert.Single(run.Result.Operations, o => o.Id.Id == Fixture.User).ChangeType);
        Assert.Contains(run.Host.Fake.Requests, r => r.Method == "GET" && r.Path == "/users/STALE");
        Assert.Equal(Start, run.State.Resources[Fixture.User].LastWriteOn);
    }

    [Fact]
    public async Task LastWriteOn_IsKeptForUnchangedResources()
    {
        var first = await ExecuteAsync();
        var sink2Clock = new FakeTimeProvider(Start.AddHours(1));
        var host = new EngineHost();
        host.SeedExisting();
        var plan = await host.ExpandAsync(await Templates.FixtureAsync());
        var state = StateFile.CreateNew(EngineHost.DeploymentName, EngineHost.Tenant, Start);
        var stored = first.State.Resources[Fixture.User];
        host.Directory.AddUser("bg-orders@contoso.com", "Break Glass (Orders)", u =>
        {
            u["mailNickname"] = "bg-orders";
            u["usageLocation"] = "GB";
        });
        state.Upsert(stored);
        var sink = new FileStateSink(new InMemoryStateStore(), state, plan, timeProvider: sink2Clock);
        await sink.BeginAsync(Ct);

        var result = await host.Executor().ExecuteAsync(plan, new ExecuteOptions { StateSink = sink, State = state }, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(Start, state.Resources[Fixture.User].LastWriteOn);
        Assert.Equal(Start.AddHours(1), state.Resources[Fixture.App].LastWriteOn);
        Assert.Equal(Start.AddHours(1), state.LastDeployedOn);
    }

    [Fact]
    public async Task TenantPin_AdoptsTheTenant_AndRefusesAnotherOne()
    {
        var host = new EngineHost();
        var plan = await host.ExpandAsync(Templates.Resources(string.Empty));
        var adopted = StateFile.CreateNew("x", null, Start);
        var foreign = StateFile.CreateNew("x", "22222222-2222-2222-2222-222222222222", Start);
        var store = new InMemoryStateStore();

        await new FileStateSink(store, adopted, plan).BeginAsync(Ct);
        var error = await Assert.ThrowsAsync<TenantMismatchException>(() => new FileStateSink(store, foreign, plan).BeginAsync(Ct));

        Assert.Equal(EngineHost.Tenant, adopted.TenantId);
        Assert.Equal(EngineHost.Tenant, error.CurrentTenantId);
        Assert.Equal("22222222-2222-2222-2222-222222222222", error.StateTenantId);
        Assert.Single(store.Saves);
    }

    [Fact]
    public async Task BeginAsync_IsIdempotent()
    {
        var host = new EngineHost();
        var plan = await host.ExpandAsync(Templates.Resources(string.Empty));
        var store = new InMemoryStateStore();
        var sink = new FileStateSink(store, StateFile.CreateNew("x", null, Start), plan);

        await sink.BeginAsync(Ct);
        await sink.BeginAsync(Ct);

        Assert.Single(store.Saves);
    }

    [Fact]
    public async Task ResourceAppliedAsync_RefusesSymbolicInstances_AndSecretExtraData()
    {
        var host = new EngineHost();
        var plan = await host.ExpandAsync(await Templates.FixtureAsync());
        var sink = new FileStateSink(new InMemoryStateStore(), StateFile.CreateNew(EngineHost.DeploymentName, null, Start), plan);
        var sp = EngineHost.Instance(plan, Fixture.SpSymbolic);
        var user = EngineHost.Evaluated(plan, Fixture.User);
        var key = host.Provider(Fixture.UserType).ResolveKey(user);
        var change = new ResourceChange { Id = user.Id, ResourceType = Fixture.UserType, Type = ChangeType.Create, Mode = ChangeMode.Create };

        var symbolic = await Assert.ThrowsAsync<InvalidOperationException>(() => sink.ResourceAppliedAsync(sp, change with { Id = sp.Id, ResourceType = Fixture.SpType }, new AppliedResource(sp.Id, "S1", new JsonObject(), key), Ct));
        var secret = await Assert.ThrowsAsync<InvalidOperationException>(() => sink.ResourceAppliedAsync(user, change, new AppliedResource(user.Id, "U1", new JsonObject(), key, new JsonObject { ["secretText"] = SecretJson.CreateString("s") }), Ct));

        Assert.Contains("deploy-time name", symbolic.Message);
        Assert.Contains("secret", secret.Message);
    }

    [Fact]
    public async Task InMemoryStateStore_RoundTripsThroughTheSerializer_AndLocks()
    {
        var store = new InMemoryStateStore();
        var state = StateFile.CreateNew("mem", EngineHost.Tenant, Start);
        state.Upsert(StateSerializerTests.Entry("/providers/Microsoft.Entra/applications/app", Fixture.AppType, "A1"));

        await store.SaveAsync(state, Ct);
        var loaded = await store.LoadAsync("mem", Ct);
        await using (await store.LockAsync("mem", TimeSpan.Zero, Ct))
        {
            await Assert.ThrowsAsync<StateLockedException>(() => store.LockAsync("mem", TimeSpan.Zero, Ct));
            Assert.NotNull(await store.GetLockAsync("mem", Ct));
        }

        Assert.NotSame(state, loaded);
        Assert.Equal("A1", loaded!.GraphIdOf("/providers/Microsoft.Entra/applications/app"));
        Assert.Equal(["mem"], await store.ListAsync(Ct));
        Assert.Null(await store.GetLockAsync("mem", Ct));
        Assert.True(await store.DeleteAsync("mem", Ct));
        Assert.Null(await store.LoadAsync("mem", Ct));
    }
}
