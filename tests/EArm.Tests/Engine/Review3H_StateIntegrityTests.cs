using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Engine.State;
using EArm.Graph;
using EArm.TestSupport;
using Xunit;
using static EArm.Tests.Engine.Review1A;

namespace EArm.Tests.Engine;

/// <summary>
/// Review round 2, findings S2-01 (a tampered state entry deletes an arbitrary object), S2-04 (a portal rename duplicates and orphans), S2-13 (the bulk
/// delete leaves "Running" behind), S2-14 (state read/write collision, size cap), S2-16 (first-party service principals, provenance wording).
/// </summary>
public sealed class Review3H_StateIntegrityTests
{
    private const string UserType = "Microsoft.Entra/users";

    private static string UserId(string upn)
    {
        return "/providers/Microsoft.Entra/users/" + upn;
    }

    private static string Json(string resourceId, string type, string graphId, string keyJson, string extra = "")
    {
        return "{ \"schemaVersion\": 1, \"deploymentName\": \"" + EngineHost.DeploymentName + "\", \"resources\": { \"" + resourceId + "\": { \"type\": \"" + type + "\", \"apiVersion\": \"" + ApiVersion + "\", "
            + (keyJson.Length == 0 ? string.Empty : "\"key\": " + keyJson + ", ") + "\"id\": \"" + graphId + "\", \"provenance\": \"created\"" + extra + " } } }";
    }

    // ---- S2-01: the key of an entry is the key its resource id implies ----

    [Theory]
    [InlineData("{ \"securityEnabled\": \"true\" }")]
    [InlineData("{ \"displayName\": \"Victim Group\" }")]
    [InlineData("{ \"uniqueName\": \"somebody-else\" }")]
    [InlineData("{ \"uniqueName\": \"orphan\", \"displayName\": \"Victim Group\" }")]
    [InlineData("{ \"foo\": \"bar\" }")]
    public void Load_RejectsAGroupEntry_WhoseKeyIsNotItsResourceIdsKey_NamingTheEntry(string key)
    {
        string json = Json(GroupId("orphan"), Fixture.GroupType, Gid(1), key);

        var error = Assert.Throws<StateEntryInvalidException>(() => StateSerializer.Deserialize(json, EngineHost.DeploymentName));

        Assert.Equal(EngineDiagnosticCodes.StateEntryInvalid, error.Code);
        Assert.Equal(GroupId("orphan"), error.ResourceId);
        Assert.Contains(GroupId("orphan"), error.Message, StringComparison.Ordinal);
        Assert.Contains("earm state forget", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_AcceptsTheKeyTheResourceIdImplies_IncludingTheApplicationsAppIdAndCaseDifferences()
    {
        var group = StateSerializer.Deserialize(Json(GroupId("orphan"), Fixture.GroupType, Gid(1), "{ \"uniqueName\": \"ORPHAN\" }"), EngineHost.DeploymentName);
        var app = StateSerializer.Deserialize(Json(AppId("a"), Fixture.AppType, Gid(2), "{ \"uniqueName\": \"a\", \"appId\": \"" + Gid(3) + "\" }"), EngineHost.DeploymentName);

        Assert.Single(group.Resources);
        Assert.Single(app.Resources);
    }

    [Fact]
    public void Load_DerivesTheKeyOfAnEntryWithoutOne_AndKeepsATypeOutsideTheCatalogAsItIs()
    {
        var state = StateSerializer.Deserialize(Json(UserId("u@contoso.com"), UserType, Gid(1), string.Empty), EngineHost.DeploymentName);
        Assert.Equal("u@contoso.com", state.Find(UserId("u@contoso.com"))!.Key["userPrincipalName"]);

        var unknown = StateSerializer.Deserialize(Json("/providers/Microsoft.Entra/futureThings/x", "Microsoft.Entra/futureThings", Gid(2), "{ \"whatever\": \"1\" }"), EngineHost.DeploymentName);
        Assert.Single(unknown.Resources);
    }

    [Theory]
    [InlineData("{ \"principalId\": \"p\" }")]
    [InlineData("{ \"name\": \"x\", \"principalId\": \"p\", \"resourceId\": \"r\", \"appRoleId\": \"a\" }")]
    public void Load_RejectsACompositeEntry_WhoseKeyNamesAreNotTheCatalogsKeyProperties(string key)
    {
        string json = Json("/providers/Microsoft.Entra/appRoleAssignments/" + Gid(4), "Microsoft.Entra/appRoleAssignments", "AzC8ELHhY0eVMkKAOQtymW1jTUZZ8qNOvDUlRZfSNxQ", key);

        Assert.Throws<StateEntryInvalidException>(() => StateSerializer.Deserialize(json, EngineHost.DeploymentName));
    }

    [Fact]
    public void Load_AcceptsACompositeEntry_WithAllItsKeyProperties()
    {
        string json = Json("/providers/Microsoft.Entra/appRoleAssignments/" + Gid(4), "Microsoft.Entra/appRoleAssignments", "AzC8ELHhY0eVMkKAOQtymW1jTUZZ8qNOvDUlRZfSNxQ", "{ \"principalId\": \"" + Gid(5) + "\", \"resourceId\": \"" + Gid(6) + "\", \"appRoleId\": \"" + Gid(7) + "\" }");

        Assert.Single(StateSerializer.Deserialize(json, EngineHost.DeploymentName).Resources);
    }

    // ---- S2-02: ids nested in the recorded relationships ----

    [Theory]
    [InlineData("{ \"memberOf\": [ \"me\" ] }")]
    [InlineData("{ \"memberOf\": [ \"../users/x\" ] }")]
    [InlineData("{ \"directoryRoles\": [ { \"roleDefinitionId\": \"" + "62e90394-69f5-4237-9190-012177145e10" + "\", \"id\": \"a/b\" } ] }")]
    [InlineData("{ \"appRoleAssignments\": [ { \"principalId\": \"x y\", \"id\": \"abc\" } ] }")]
    [InlineData("{ \"directoryRoles\": [ { \"directoryScopeId\": \"/../x\", \"id\": \"abc\" } ] }")]
    public void Load_RejectsARecordedRelationshipWithAMalformedId(string relationships)
    {
        string json = Json(GroupId("g"), Fixture.GroupType, Gid(1), "{ \"uniqueName\": \"g\" }", ", \"relationships\": " + relationships);

        var error = Assert.Throws<StateEntryInvalidException>(() => StateSerializer.Deserialize(json, EngineHost.DeploymentName));
        Assert.Contains("relationships.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_AcceptsTheRelationshipRecordsTheEdgePassWrites()
    {
        string relationships = "{ \"memberOf\": [ \"" + Gid(8) + "\" ], \"manager\": \"" + Gid(9) + "\", \"directoryRoles\": [ { \"roleDefinitionId\": \"62e90394-69f5-4237-9190-012177145e10\", \"principalId\": \"" + Gid(1) + "\", \"directoryScopeId\": \"/\", \"id\": \"AzC8ELHhY0eVMkKAOQ_x-1\" } ] }";
        string json = Json(GroupId("g"), Fixture.GroupType, Gid(1), "{ \"uniqueName\": \"g\" }", ", \"relationships\": " + relationships);

        Assert.NotNull(StateSerializer.Deserialize(json, EngineHost.DeploymentName).Find(GroupId("g"))!.Relationships);
    }

    [Fact]
    public async Task ALenientLoad_ReadsAnInvalidEntry_SoItCanBeForgotten_AndAStrictLoadRefusesIt()
    {
        using var directory = new TestDirectory(nameof(ALenientLoad_ReadsAnInvalidEntry_SoItCanBeForgotten_AndAStrictLoadRefusesIt));
        var store = new FileStateStore(directory.Path);
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(store.PathFor(EngineHost.DeploymentName), Json(GroupId("orphan"), Fixture.GroupType, Gid(1), "{ \"securityEnabled\": \"true\" }"), Ct);

        await Assert.ThrowsAsync<StateEntryInvalidException>(() => store.LoadAsync(EngineHost.DeploymentName, Ct));
        var lenient = await store.LoadAsync(EngineHost.DeploymentName, lenient: true, Ct);

        Assert.NotNull(lenient!.Find(GroupId("orphan")));
    }

    // ---- S2-01: the same rules guard a delete even when the entry was built in memory ----

    [Fact]
    public async Task Complete_AnOrphanEntryWithAForgedKey_NeverDeletesTheObject_EvenWhenTheFileCheckIsBypassed()
    {
        var host = new EngineHost();
        var victim = host.Directory.AddGroup("victim", "Victim Group");
        var state = NewState();
        state.Upsert(Entry(GroupId("orphan"), Fixture.GroupType, IdOf(victim), "displayName", "Victim Group"));

        var (_, result, _) = await ExecuteAsync(host, state, Empty(), DeploymentMode.Complete);

        Assert.DoesNotContain(host.Fake.Writes, w => w.Method == "DELETE");
        Assert.Equal(1, host.Directory.Count("/groups"));
        var op = Assert.Single(result.Operations);
        Assert.Equal(OperationStatus.Failed, op.Status);
        Assert.Equal(EngineDiagnosticCodes.StateEntryInvalid, op.ErrorCode);
        Assert.Contains(GroupId("orphan"), op.Error, StringComparison.Ordinal);
        Assert.NotNull(state.Find(GroupId("orphan")));
    }

    [Fact]
    public async Task Deleter_RefusesAnEntryWithAForgedKey_AndRelationships()
    {
        var host = new EngineHost();
        var victim = host.Directory.AddGroup("victim", "Victim Group");
        var state = NewState();
        state.Upsert(Entry(GroupId("orphan"), Fixture.GroupType, IdOf(victim), "uniqueName", "orphan") with { Relationships = new JsonObject { ["memberOf"] = new JsonArray("me") } });
        var store = new InMemoryStateStore();

        var result = await DeploymentDeleter.DeleteAllAsync(state, store, host.Registry, host.Graph, host.Retry, continueOnError: true, DeleteGuard.Default, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, Ct);

        Assert.Equal(1, result.FailedCount);
        Assert.Equal(EngineDiagnosticCodes.StateEntryInvalid, result.Outcomes.Single().ErrorCode);
        Assert.Equal(1, host.Directory.Count("/groups"));
    }

    // ---- S2-01: the createdDateTime fingerprint ----

    [Fact]
    public async Task ACreate_RecordsTheCreatedDateTimeOfTheObject_AndItSurvivesTheFile()
    {
        var host = new EngineHost();
        var state = NewState();

        var (_, result, store) = await ExecuteAsync(host, state, Apps("fp-app"));

        Assert.True(result.Succeeded);
        var entry = state.Find(AppId("fp-app"))!;
        Assert.NotNull(entry.CreatedDateTime);
        Assert.Equal(entry.CreatedDateTime, ProviderReads.CreatedDateTimeOf(host.Directory.Find("/applications", "uniqueName", "fp-app")));
        var reloaded = StateSerializer.Deserialize(store.DocumentOf(EngineHost.DeploymentName)!, EngineHost.DeploymentName);
        Assert.Equal(entry.CreatedDateTime, reloaded.Find(AppId("fp-app"))!.CreatedDateTime);
        Assert.Contains("\"createdDateTime\"", store.DocumentOf(EngineHost.DeploymentName), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Complete_AnOrphanWhoseObjectWasCreatedAtAnotherTimeThanTheEntryRecorded_IsNotDeleted()
    {
        var host = new EngineHost();
        var live = host.Directory.AddApplication("ghost", "Ghost");
        var state = NewState();
        state.Upsert(Entry(AppId("ghost"), Fixture.AppType, IdOf(live), "uniqueName", "ghost") with { CreatedDateTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero) });

        var (_, result, _) = await ExecuteAsync(host, state, Empty(), DeploymentMode.Complete);

        Assert.DoesNotContain(host.Fake.Writes, w => w.Method == "DELETE");
        var op = Assert.Single(result.Operations);
        Assert.Equal(EngineDiagnosticCodes.StateEntryMismatch, op.ErrorCode);
        Assert.Contains("different object", op.Error, StringComparison.Ordinal);
        Assert.Equal(1, host.Directory.Count("/applications"));
    }

    [Fact]
    public async Task Complete_AnOrphanWhoseObjectMatchesTheRecordedFingerprint_IsDeleted()
    {
        var host = new EngineHost();
        var live = host.Directory.AddApplication("ghost", "Ghost");
        var state = NewState();
        state.Upsert(Entry(AppId("ghost"), Fixture.AppType, IdOf(live), "uniqueName", "ghost") with { CreatedDateTime = ProviderReads.CreatedDateTimeOf(live) });

        var (_, result, _) = await ExecuteAsync(host, state, Empty(), DeploymentMode.Complete);

        Assert.True(result.Succeeded);
        Assert.Equal(0, host.Directory.Count("/applications"));
    }

    [Fact]
    public async Task ARunNeverPatchesAnObject_WhoseCreatedDateTimeIsNotTheOneTheEntryRecorded()
    {
        var host = new EngineHost();
        var other = host.Directory.AddApplication("other", "Other");
        var state = NewState();
        state.Upsert(Entry(AppId("mine"), Fixture.AppType, IdOf(other), "uniqueName", "other") with { CreatedDateTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero) });

        // The template names "mine"; the recorded id points at "other". Identity is resolved live by key, so "other" is never read or patched and "mine" is created.
        var (_, result, _) = await ExecuteAsync(host, state, Apps("mine"));

        Assert.DoesNotContain(host.Fake.Writes, w => w.Path.Contains(IdOf(other), StringComparison.Ordinal));
        Assert.Equal("Other", other["displayName"]!.GetValue<string>());
        Assert.Equal(ChangeType.Create, Assert.Single(result.Operations).ChangeType);
    }

    // ---- a rename outside the template: identity is the live key (ARM rule), so the declared name is a new object ----

    [Fact]
    public async Task AUserWhoseUpnWasRenamedInThePortal_IsNotTheDeclaredUser_TheDeclaredUpnIsCreated()
    {
        var host = new EngineHost();
        var renamed = host.Directory.AddUser("renamed@contoso.com", "Renamed");
        var state = NewState();
        state.Upsert(Entry(UserId("bg@contoso.com"), UserType, IdOf(renamed), "userPrincipalName", "bg@contoso.com"));
        var document = Templates.Resources("{ \"type\": \"" + UserType + "\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"bg@contoso.com\", \"properties\": { \"displayName\": \"Break glass\", \"accountEnabled\": true, \"passwordProfile\": { \"password\": \"Initial-P@ssw0rd-1\" } } }");

        var (_, result, _) = await ExecuteAsync(host, state, document);

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.Equal(ChangeType.Create, Assert.Single(result.Operations).ChangeType);
        Assert.DoesNotContain(host.Fake.Writes, w => w.Path.Contains(IdOf(renamed), StringComparison.Ordinal));
        Assert.Equal(2, host.Directory.Count("/users"));
        Assert.Equal("Renamed", renamed["displayName"]!.GetValue<string>());
        Assert.NotEqual(IdOf(renamed), state.GraphIdOf(UserId("bg@contoso.com")));
    }

    [Fact]
    public async Task ANamedLocationWhoseDisplayNameWasRenamed_IsNotTheDeclaredOne_TheDeclaredNameIsCreated()
    {
        var host = new EngineHost();
        var renamed = host.Directory.Add("/identity/conditionalAccess/namedLocations", new JsonObject { ["@odata.type"] = "#microsoft.graph.ipNamedLocation", ["displayName"] = "HQ (renamed in the portal)", ["isTrusted"] = true, ["ipRanges"] = new JsonArray(new JsonObject { ["@odata.type"] = "#microsoft.graph.iPv4CidrRange", ["cidrAddress"] = "10.0.0.0/8" }) });
        var state = NewState();
        state.Upsert(Entry("/providers/Microsoft.Entra/conditionalAccess/namedLocations/HQ", Fixture.NamedLocationType, IdOf(renamed), "displayName", "HQ"));
        var document = Templates.Resources("{ \"type\": \"" + Fixture.NamedLocationType + "\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"HQ\", \"properties\": { \"@odata.type\": \"#microsoft.graph.ipNamedLocation\", \"isTrusted\": true, \"ipRanges\": [ { \"@odata.type\": \"#microsoft.graph.iPv4CidrRange\", \"cidrAddress\": \"10.0.0.0/8\" } ] } }");

        var plan = await host.WhatIfAsync(document, state);
        var row = Assert.Single(plan.Changes);
        Assert.False(row.HasError);
        Assert.Equal(ChangeType.Create, row.Type);

        var (_, result, _) = await ExecuteAsync(host, state, document);

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.Equal(ChangeType.Create, Assert.Single(result.Operations).ChangeType);
        Assert.DoesNotContain(host.Fake.Writes, w => w.Path.Contains(IdOf(renamed), StringComparison.Ordinal));
        Assert.Equal(2, host.Directory.Count("/identity/conditionalAccess/namedLocations"));
    }

    // ---- S2-16: first-party service principals and the provenance wording ----

    [Fact]
    public async Task Complete_AFirstPartyServicePrincipalThisDeploymentCreated_CanBeDeleted()
    {
        var host = new EngineHost();
        const string AppId0 = "00000003-0000-0000-c000-000000000000";
        var sp = host.Directory.AddServicePrincipal(AppId0, "Microsoft Graph", s => s["appOwnerOrganizationId"] = "f8cdef31-a31e-4b4a-93e4-5f571e91255a");
        var state = NewState();
        state.Upsert(Entry("/providers/Microsoft.Entra/servicePrincipals/" + AppId0, Fixture.SpType, IdOf(sp), "appId", AppId0, StateProvenance.Created));

        var (_, result, _) = await ExecuteAsync(host, state, Empty(), DeploymentMode.Complete);

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.Equal(0, host.Directory.Count("/servicePrincipals"));
    }

    [Fact]
    public async Task AnEntryWithoutAProvenanceRecord_IsExplainedAsAnOlderStateFile_NotAsExistedBefore()
    {
        var host = new EngineHost();
        var live = host.Directory.AddApplication("legacy", "Legacy");
        string json = Json(AppId("legacy"), Fixture.AppType, IdOf(live), "{ \"uniqueName\": \"legacy\" }").Replace(", \"provenance\": \"created\"", string.Empty, StringComparison.Ordinal);
        var state = StateSerializer.Deserialize(json, EngineHost.DeploymentName);

        var (_, result, _) = await ExecuteAsync(host, state, Empty(), DeploymentMode.Complete);

        var warning = Assert.Single(result.Diagnostics, d => d.Code == EngineDiagnosticCodes.AdoptedObjectNotDeleted);
        Assert.Contains("no provenance record", warning.Message, StringComparison.Ordinal);
        Assert.Contains("older state file", warning.Message, StringComparison.Ordinal);
        Assert.Contains("set-provenance", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("existed before", warning.Message, StringComparison.Ordinal);
        Assert.Equal(1, host.Directory.Count("/applications"));
    }

    [Fact]
    public async Task TheDetachedWarning_DoesNotPromiseThatCompleteModeDeletesWhatIsNoLongerInTheState()
    {
        var host = new EngineHost();
        var live = host.Directory.AddApplication("gone-from-template", "Gone");
        var state = NewState();
        state.Upsert(Entry(AppId("gone-from-template"), Fixture.AppType, IdOf(live), "uniqueName", "gone-from-template"));

        var (_, result, _) = await ExecuteAsync(host, state, Empty());

        var warning = Assert.Single(result.Diagnostics, d => d.Code == EngineDiagnosticCodes.ResourceDetached);
        Assert.DoesNotContain("use --mode Complete to delete it", warning.Message, StringComparison.Ordinal);
        Assert.Contains("not managed by this deployment any more", warning.Message, StringComparison.Ordinal);
    }

    // ---- S2-13: the bulk delete never leaves "Running" ----

    private sealed class CancelAfterFirstDelete : GraphClientDecorator
    {
        private readonly CancellationTokenSource _source;
        private int _deletes;

        public CancelAfterFirstDelete(IGraphClient inner, CancellationTokenSource source)
            : base(inner)
        {
            _source = source;
        }

        public override async Task DeleteAsync(GraphRequest request, CancellationToken cancellationToken)
        {
            await base.DeleteAsync(request, cancellationToken);
            if (Interlocked.Increment(ref _deletes) == 1)
            {
                await _source.CancelAsync();
            }
        }
    }

    [Fact]
    public async Task DeploymentDelete_CancelledAfterTheFirstObject_SavesCancelledNotRunning_WithTheDeletedEntryGone()
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var host = new EngineHost(graph => new CancelAfterFirstDelete(graph, source));
        var state = NewState();
        foreach (string name in new[] { "a", "b", "c" })
        {
            state.Upsert(Entry(AppId(name), Fixture.AppType, IdOf(host.Directory.AddApplication(name, name)), "uniqueName", name));
        }

        var store = new InMemoryStateStore();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DeploymentDeleter.DeleteAllAsync(state, store, host.Registry, host.Graph, host.Retry, continueOnError: false, DeleteGuard.Default, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, source.Token));

        Assert.Equal(StateFile.StatusCancelled, state.LastStatus);
        Assert.Equal(2, state.Resources.Count);
        var saved = StateSerializer.Deserialize(store.DocumentOf(EngineHost.DeploymentName)!, EngineHost.DeploymentName);
        Assert.Equal(StateFile.StatusCancelled, saved.LastStatus);
        Assert.Equal(2, saved.Resources.Count);
    }

    // ---- S2-14: reading while a writer replaces, and the size caps ----

    [Fact]
    public async Task ReadersAndWriters_OfOneStateFile_NeverCollide()
    {
        using var directory = new TestDirectory(nameof(ReadersAndWriters_OfOneStateFile_NeverCollide));
        var store = new FileStateStore(directory.Path);
        var state = NewState();
        for (int i = 0; i < 40; i++)
        {
            string name = "app-" + i;
            state.Upsert(Entry(AppId(name), Fixture.AppType, Gid(100 + i), "uniqueName", name));
        }

        state.LastTemplateHash = "v000000";
        await store.SaveAsync(state, Ct);
        using var stop = new CancellationTokenSource();
        var failures = new List<Exception>();

        // One writer, so the template hash is a version that must never go backwards for any reader; the readers are hot (no delay) so the
        // writer is moved into place while a read is in flight. No wall-clock bound: the loop ends when the writer has made its saves.
        async Task Writer()
        {
            for (int i = 1; i <= 60; i++)
            {
                state.LastTemplateHash = "v" + i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
                await store.SaveAsync(state, Ct);
            }
        }

        async Task Reader()
        {
            string last = string.Empty;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var loaded = await store.LoadAsync(EngineHost.DeploymentName, Ct);
                    Assert.NotNull(loaded);
                    string version = loaded.LastTemplateHash!;
                    Assert.True(string.CompareOrdinal(version, last) >= 0, $"version went backwards: {last} then {version}");
                    last = version;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lock (failures)
                    {
                        failures.Add(ex);
                    }
                }

                await Task.Yield();
            }
        }

        var readers = new[] { Task.Run(Reader, Ct), Task.Run(Reader, Ct), Task.Run(Reader, Ct) };
        await Task.Run(Writer, Ct);
        await stop.CancelAsync();
        await Task.WhenAll(readers);

        Assert.Empty(failures);
        Assert.Equal("v000060", (await store.LoadAsync(EngineHost.DeploymentName, Ct))!.LastTemplateHash);
    }

    [Fact]
    public async Task AStateFileOverTheCap_IsRefusedWithAClearError()
    {
        using var directory = new TestDirectory(nameof(AStateFileOverTheCap_IsRefusedWithAClearError));
        var store = new FileStateStore(directory.Path);
        Directory.CreateDirectory(directory.Path);
        await using (var stream = new FileStream(store.PathFor(EngineHost.DeploymentName), FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(FileStateStore.MaxStateFileBytes + 1);
        }

        var error = await Assert.ThrowsAsync<StateStoreException>(() => store.LoadAsync(EngineHost.DeploymentName, Ct));

        Assert.Contains("16 MiB", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALockFileOverTheCap_IsNotReadAsALock_AndIsTakenOverWhenOld()
    {
        using var directory = new TestDirectory(nameof(ALockFileOverTheCap_IsNotReadAsALock_AndIsTakenOverWhenOld));
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new FileStateStore(directory.Path, clock);
        Directory.CreateDirectory(directory.Path);
        string lockPath = store.LockPathFor(EngineHost.DeploymentName);
        await File.WriteAllTextAsync(lockPath, new string('x', (int)FileStateStore.MaxLockFileBytes + 10), Ct);
        File.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow.AddHours(-3));

        await using var lease = await store.LockAsync(EngineHost.DeploymentName, TimeSpan.Zero, Ct);

        Assert.True(File.Exists(lockPath));
        Assert.Contains("lockId", await File.ReadAllTextAsync(lockPath, Ct), StringComparison.Ordinal);
    }
}
