using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Engine.State;
using EArm.TestSupport;
using Xunit;
using static EArm.Tests.Engine.Review1A;

namespace EArm.Tests.Engine;

/// <summary>
/// Review findings S-01 / E-09 (the state is not trusted blindly), E-01 / I-02 (case-only renames and shared objects) and
/// E-02 (adoption is visible and protected). Every test was written against a repro of the finding.
/// </summary>
public sealed class Review1A_StateTrustTests
{
    // ---- S-01b: a pinned id must still name the object the key names ----

    [Fact]
    public async Task PinnedIdOfAnotherObject_IsIgnored_TheObjectWithTheKeyIsUsed_AndTheOtherObjectIsNeverPatched()
    {
        // Review round 2 (S2-04): when NOTHING has the key the pinned object was renamed, and the resource fails instead of creating a duplicate (see
        // Review3H_StateIntegrityTests); here an object that has the key exists, so the pinned id is ignored and that object is used.
        var host = new EngineHost();
        var victim = host.Directory.AddApplication("victim", "Victim");
        host.Directory.AddApplication("mine", "Mine");
        string victimId = IdOf(victim);
        var state = NewState();
        state.Upsert(Entry(AppId("mine"), Fixture.AppType, victimId, "uniqueName", "mine"));

        var (_, result, _) = await ExecuteAsync(host, state, Apps("mine"));

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.DoesNotContain(host.Fake.Writes, w => w.Path.Contains(victimId, StringComparison.Ordinal) || w.Path.Contains("victim", StringComparison.Ordinal));
        Assert.Equal("Victim", victim["displayName"]!.GetValue<string>());
        Assert.Equal(2, host.Directory.Count("/applications"));
        var warning = Assert.Single(result.Diagnostics, d => d.Code == EngineDiagnosticCodes.StateEntryMismatch);
        Assert.Contains(victimId, warning.Message, StringComparison.Ordinal);
        Assert.Contains(AppId("mine"), warning.Message, StringComparison.Ordinal);
        var op = Assert.Single(result.Operations);
        Assert.Equal(ChangeType.Modify, op.ChangeType);
        Assert.NotEqual(victimId, state.GraphIdOf(AppId("mine")));
    }

    [Fact]
    public async Task WhatIf_PinnedIdOfAnotherObject_UsesTheObjectWithTheKey_WithAMismatchWarning()
    {
        var host = new EngineHost();
        var victim = host.Directory.AddApplication("victim", "Victim");
        host.Directory.AddApplication("mine", "Mine");
        var state = NewState();
        state.Upsert(Entry(AppId("mine"), Fixture.AppType, IdOf(victim), "uniqueName", "mine"));

        var plan = await host.WhatIfAsync(Apps("mine"), state);

        var change = Assert.Single(plan.Changes);
        Assert.Equal(ChangeType.Modify, change.Type);
        Assert.Contains(plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.StateEntryMismatch);
        Assert.Empty(host.Fake.Writes);
    }

    [Fact]
    public async Task PinnedId_OfTheRightObject_IsStillUsed_WithoutAnyWarning()
    {
        var host = new EngineHost();
        var mine = host.Directory.AddApplication("mine", "Old name");
        var state = NewState();
        state.Upsert(Entry(AppId("mine"), Fixture.AppType, IdOf(mine), "uniqueName", "mine"));

        var (_, result, _) = await ExecuteAsync(host, state, Apps("mine"));

        Assert.True(result.Succeeded);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(ChangeType.Modify, Assert.Single(result.Operations).ChangeType);
    }

    // ---- S-01a: the state is only a hint for deletes ----

    [Fact]
    public async Task Complete_OrphanPinnedToAnotherObject_IsNotDeleted_TheEntryStays_AndTheMismatchIsReported()
    {
        var host = new EngineHost();
        var victim = host.Directory.AddApplication("victim", "Victim");
        var state = NewState();
        state.Upsert(Entry(AppId("ghost"), Fixture.AppType, IdOf(victim), "uniqueName", "ghost"));

        var (_, result, store) = await ExecuteAsync(host, state, Empty(), DeploymentMode.Complete);

        Assert.False(result.Succeeded);
        var op = Assert.Single(result.Operations);
        Assert.Equal(OperationStatus.Failed, op.Status);
        Assert.Equal(EngineDiagnosticCodes.StateEntryMismatch, op.ErrorCode);
        Assert.Contains(AppId("ghost"), op.Error, StringComparison.Ordinal);
        Assert.Contains(IdOf(victim), op.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(host.Fake.Writes, w => w.Method == "DELETE");
        Assert.Equal(1, host.Directory.Count("/applications"));
        Assert.NotNull(state.Find(AppId("ghost")));
        Assert.Contains(result.Diagnostics, d => d.Code == EngineDiagnosticCodes.StateEntryMismatch && d.IsError);
        Assert.Contains(AppId("ghost"), store.DocumentOf(EngineHost.DeploymentName), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Complete_OrphanWhoseKeyBelongsToAnotherObject_IsNotDeleted()
    {
        var host = new EngineHost();
        var victim = host.Directory.AddApplication("ghost", "Victim");
        var other = host.Directory.AddApplication("other", "Other");
        var state = NewState();

        // The id was edited: the entry's key (ghost) names "victim", the pinned id is "other". (A KEY edited to differ from the resource id is rejected at load: Review3H_StateIntegrityTests.)
        state.Upsert(Entry(AppId("ghost"), Fixture.AppType, IdOf(other), "uniqueName", "ghost"));

        var (_, result, _) = await ExecuteAsync(host, state, Empty(), DeploymentMode.Complete);

        var op = Assert.Single(result.Operations);
        Assert.Equal(EngineDiagnosticCodes.StateEntryMismatch, op.ErrorCode);
        Assert.Contains(IdOf(victim), op.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(host.Fake.Writes, w => w.Method == "DELETE");
        Assert.Equal(2, host.Directory.Count("/applications"));
    }

    [Fact]
    public async Task WhatIf_Complete_MismatchedOrphan_IsAnUnsupportedRow_NotADelete()
    {
        var host = new EngineHost();
        var victim = host.Directory.AddApplication("victim", "Victim");
        var state = NewState();
        state.Upsert(Entry(AppId("ghost"), Fixture.AppType, IdOf(victim), "uniqueName", "ghost"));

        var plan = await host.WhatIfAsync(Empty(), state, DeploymentMode.Complete);

        var row = Assert.Single(plan.ModeChanges);
        Assert.True(row.HasError);
        Assert.Contains(IdOf(victim), row.UnsupportedReason, StringComparison.Ordinal);
        Assert.Null(row.Before);
        Assert.Contains(plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.StateEntryMismatch);
    }

    [Fact]
    public async Task Complete_OrphanThatIsTheSignedInPrincipal_IsNeverDeleted()
    {
        var host = new EngineHost();
        var me = host.Directory.AddUser("me@contoso.com", "Me");
        string myId = IdOf(me);
        var state = NewState();
        state.Upsert(Entry("/providers/Microsoft.Entra/users/me@contoso.com", Fixture.UserType, myId, "userPrincipalName", "me@contoso.com"));
        var planner = host.Options() with { SignedInPrincipalId = myId };

        var (_, result, _) = await ExecuteAsync(host, state, Empty(), DeploymentMode.Complete, planner);

        var op = Assert.Single(result.Operations);
        Assert.Equal(OperationStatus.Failed, op.Status);
        Assert.Equal(EngineDiagnosticCodes.ProtectedObject, op.ErrorCode);
        Assert.DoesNotContain(host.Fake.Writes, w => w.Method == "DELETE");
        Assert.Equal(1, host.Directory.Count("/users"));
        Assert.NotNull(state.Find("/providers/Microsoft.Entra/users/me@contoso.com"));
    }

    [Fact]
    public async Task Complete_OrphanThatIsAFirstPartyServicePrincipal_IsNeverDeleted()
    {
        var host = new EngineHost();
        const string AppId0 = "00000003-0000-0000-c000-000000000000";
        var sp = host.Directory.AddServicePrincipal(AppId0, "Microsoft Graph", s => s["appOwnerOrganizationId"] = "f8cdef31-a31e-4b4a-93e4-5f571e91255a");
        var state = NewState();

        // Review round 2 (S2-16): a first-party service principal that this deployment ADOPTED is protected even with --delete-adopted; one it CREATED is not (Review3H_StateIntegrityTests).
        state.Upsert(Entry("/providers/Microsoft.Entra/servicePrincipals/" + AppId0, Fixture.SpType, IdOf(sp), "appId", AppId0, StateProvenance.Adopted));

        var (_, result, _) = await ExecuteAsync(host, state, Empty(), DeploymentMode.Complete, execute: new ExecuteOptions { DeleteAdopted = true });

        var op = Assert.Single(result.Operations);
        Assert.Equal(EngineDiagnosticCodes.ProtectedObject, op.ErrorCode);
        Assert.DoesNotContain(host.Fake.Writes, w => w.Method == "DELETE");
        Assert.Equal(1, host.Directory.Count("/servicePrincipals"));
    }

    [Fact]
    public async Task Complete_AnOwnTenantServicePrincipal_IsDeletedAsBefore()
    {
        var host = new EngineHost();
        const string AppId0 = "99999999-9999-9999-9999-999999999999";
        var sp = host.Directory.AddServicePrincipal(AppId0, "Mine", s => s["appOwnerOrganizationId"] = EngineHost.Tenant);
        var state = NewState();
        state.Upsert(Entry("/providers/Microsoft.Entra/servicePrincipals/" + AppId0, Fixture.SpType, IdOf(sp), "appId", AppId0));

        var (_, result, _) = await ExecuteAsync(host, state, Empty(), DeploymentMode.Complete);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(0, host.Directory.Count("/servicePrincipals"));
    }

    [Fact]
    public async Task DeploymentDeleter_UsesTheSameChecks_AMismatchedEntryIsKept()
    {
        var host = new EngineHost();
        var victim = host.Directory.AddApplication("victim", "Victim");
        var mine = host.Directory.AddApplication("mine", "Mine");
        var state = NewState();
        state.Upsert(Entry(AppId("ghost"), Fixture.AppType, IdOf(victim), "uniqueName", "ghost"));
        state.Upsert(Entry(AppId("mine"), Fixture.AppType, IdOf(mine), "uniqueName", "mine"));
        var store = new InMemoryStateStore();

        var result = await DeploymentDeleter.DeleteAllAsync(state, store, host.Registry, host.Graph, host.Retry, continueOnError: true, DeleteGuard.Default, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, Ct);

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(EngineDiagnosticCodes.StateEntryMismatch, result.Outcomes.Single(o => o.Status == DeleteStatus.Failed).ErrorCode);
        Assert.Equal(1, host.Directory.Count("/applications"));
        Assert.Equal("Victim", host.Directory.Objects("/applications").Single()["displayName"]!.GetValue<string>());
        Assert.NotNull(state.Find(AppId("ghost")));
        Assert.Null(state.Find(AppId("mine")));
    }

    // ---- S-01c: Graph ids in the state have a shape ----

    [Theory]
    [InlineData("me")]
    [InlineData("../users/00000000-0000-0000-0000-000000000000")]
    [InlineData("a b")]
    [InlineData("1234")]
    [InlineData("00000000-0000-0000-0000-00000000000g")]
    [InlineData("{00000000-0000-0000-0000-000000000000}")]
    [InlineData("A1")]
    public void Load_RejectsAGraphIdThatIsNotAGuid_WithAClearError(string graphId)
    {
        string json = StateJson(Fixture.AppType, AppId("a"), graphId);

        var error = Assert.Throws<StateSchemaUnsupportedException>(() => StateSerializer.Deserialize(json, EngineHost.DeploymentName));

        Assert.Contains(AppId("a"), error.Message, StringComparison.Ordinal);
        Assert.Contains("not a GUID", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_AcceptsTheDocumentedNonGuidIds_AndGuidsInAnyCase()
    {
        var auth = StateSerializer.Deserialize(StateJson("Microsoft.Entra/conditionalAccess/authenticationContextClassReferences", "/providers/Microsoft.Entra/conditionalAccess/authenticationContextClassReferences/c10", "c10"), EngineHost.DeploymentName);
        var role = StateSerializer.Deserialize(StateJson(Fixture.RoleAssignmentType, "/providers/Microsoft.Entra/roleAssignments/" + Gid(1), "AzC8ELHhY0eVMkKAOQtymW1jTUZZ8qNOvDUlRZfSNxQ-1"), EngineHost.DeploymentName);
        var app = StateSerializer.Deserialize(StateJson(Fixture.AppType, AppId("a"), "ABCDEFAB-0000-4000-8000-000000000001"), EngineHost.DeploymentName);

        Assert.Equal("c10", Assert.Single(auth.Resources.Values).GraphId);
        Assert.Single(role.Resources);
        Assert.Single(app.Resources);
    }

    [Theory]
    [InlineData("C10")]
    [InlineData("c0")]
    [InlineData("c100")]
    [InlineData("10")]
    public void Load_RejectsAMalformedAuthenticationContextId(string id)
    {
        string json = StateJson("Microsoft.Entra/conditionalAccess/authenticationContextClassReferences", "/providers/Microsoft.Entra/conditionalAccess/authenticationContextClassReferences/c10", id);

        Assert.Throws<StateSchemaUnsupportedException>(() => StateSerializer.Deserialize(json, EngineHost.DeploymentName));
    }

    [Fact]
    public void Load_RejectsAMalformedParentId_AndResourceIdsThatDifferOnlyByCase()
    {
        string badParent = "{ \"schemaVersion\": 1, \"resources\": { \"/providers/Microsoft.Entra/applications/a/federatedIdentityCredentials/f\": { \"type\": \"Microsoft.Entra/applications/federatedIdentityCredentials\", \"apiVersion\": \"" + ApiVersion + "\", \"id\": \"" + Gid(2) + "\", \"parentId\": \"me\" } } }";
        string twice = "{ \"schemaVersion\": 1, \"resources\": { \"" + AppId("a") + "\": { \"type\": \"" + Fixture.AppType + "\", \"apiVersion\": \"" + ApiVersion + "\", \"id\": \"" + Gid(3) + "\" }, \"" + AppId("A") + "\": { \"type\": \"" + Fixture.AppType + "\", \"apiVersion\": \"" + ApiVersion + "\", \"id\": \"" + Gid(4) + "\" } } }";

        Assert.Contains("parent", Assert.Throws<StateSchemaUnsupportedException>(() => StateSerializer.Deserialize(badParent, EngineHost.DeploymentName)).Message, StringComparison.Ordinal);
        Assert.Contains("more than once", Assert.Throws<StateSchemaUnsupportedException>(() => StateSerializer.Deserialize(twice, EngineHost.DeploymentName)).Message, StringComparison.Ordinal);
    }

    private static string StateJson(string type, string resourceId, string graphId)
    {
        return "{ \"schemaVersion\": 1, \"resources\": { \"" + resourceId + "\": { \"type\": \"" + type + "\", \"apiVersion\": \"" + ApiVersion + "\", \"id\": \"" + graphId + "\" } } }";
    }

    // ---- E-01 / I-02: case-only renames ----

    [Fact]
    public async Task Complete_ACaseOnlyRename_NeverDeletesTheLiveObject_AndReKeysTheStateEntry()
    {
        var host = new EngineHost();
        var live = host.Directory.AddApplication("MyApp", "My app");
        var state = NewState();
        state.Upsert(Entry(AppId("MyApp"), Fixture.AppType, IdOf(live), "uniqueName", "MyApp"));

        var (_, result, store) = await ExecuteAsync(host, state, Apps("myapp"), DeploymentMode.Complete);

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.DoesNotContain(host.Fake.Writes, w => w.Method == "DELETE");
        Assert.Equal(1, host.Directory.Count("/applications"));
        Assert.Equal(IdOf(live), state.GraphIdOf(AppId("myapp")));
        var entry = Assert.Single(state.Resources.Values);
        Assert.Equal(AppId("myapp"), entry.Id.Id);
        Assert.Equal(AppId("myapp"), Assert.Single(state.Resources.Keys));
        Assert.DoesNotContain("MyApp", store.DocumentOf(EngineHost.DeploymentName), StringComparison.Ordinal);
        Assert.Equal(0, result.DeletedCount);
    }

    [Fact]
    public async Task FindOrphans_ComparesResourceIdsCaseInsensitively()
    {
        var host = new EngineHost();
        var state = NewState();
        state.Upsert(Entry(AppId("MyApp"), Fixture.AppType, Gid(10), "uniqueName", "MyApp"));
        state.Upsert(Entry(GroupId("gone"), Fixture.GroupType, Gid(11), "uniqueName", "gone"));

        var plan = await host.ExpandAsync(Apps("myapp"), state);
        var orphans = ModePass.FindOrphans(state, plan, out _);

        Assert.Equal([GroupId("gone")], orphans.Select(o => o.Id.Id).ToList());
    }

    [Fact]
    public async Task Complete_AnOrphanThatSharesTheGraphIdOfADeclaredResource_IsNotDeleted_AndIsDetached()
    {
        var host = new EngineHost();
        var live = host.Directory.AddApplication("new-name", "App");
        string id = IdOf(live);
        var state = NewState();
        state.Upsert(Entry(AppId("old-name"), Fixture.AppType, id, "uniqueName", "old-name"));

        var (_, result, _) = await ExecuteAsync(host, state, Apps("new-name"), DeploymentMode.Complete);

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.DoesNotContain(host.Fake.Writes, w => w.Method == "DELETE");
        Assert.Equal(1, host.Directory.Count("/applications"));
        Assert.Contains(result.Diagnostics, d => d.Code == EngineDiagnosticCodes.OrphanSharesObject && !d.IsError);
        Assert.Null(state.Find(AppId("old-name")));
        Assert.Equal(id, state.GraphIdOf(AppId("new-name")));
        Assert.Contains(result.Operations, o => o.Id.Id == AppId("old-name") && o.Status == OperationStatus.Detached);
    }

    [Fact]
    public async Task WhatIf_Complete_AnOrphanThatSharesAnObject_IsAnIgnoreRowWithAWarning()
    {
        var host = new EngineHost();
        var live = host.Directory.AddApplication("new-name", "App");
        var state = NewState();
        state.Upsert(Entry(AppId("old-name"), Fixture.AppType, IdOf(live), "uniqueName", "old-name"));

        var plan = await host.WhatIfAsync(Apps("new-name"), state, DeploymentMode.Complete);

        var row = Assert.Single(plan.ModeChanges);
        Assert.Equal(ChangeType.Ignore, row.Type);
        Assert.Contains(plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.OrphanSharesObject);
    }

    [Fact]
    public async Task TwoDeclaredResources_ThatDifferOnlyByCase_AreADuplicateResourceId()
    {
        var host = new EngineHost();

        var error = await Assert.ThrowsAsync<ValidationFailedException>(() => host.ExpandAsync(Groups("Team", "team")));

        Assert.Contains(error.Diagnostics, d => d.Code == EArm.Core.Diagnostics.DiagnosticCodes.DuplicateResourceId);
    }

    [Fact]
    public async Task TwoNamesComputedAtExpansion_ThatDifferOnlyByCase_AreADuplicateResourceId()
    {
        var host = new EngineHost();
        var document = Templates.Parse(
            "\"parameters\": { \"a\": { \"type\": \"string\" }, \"b\": { \"type\": \"string\" } }, \"resources\": [ "
            + "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"[parameters('a')]\", \"properties\": { \"displayName\": \"x\", \"mailEnabled\": false, \"securityEnabled\": true } }, "
            + "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"[parameters('b')]\", \"properties\": { \"displayName\": \"y\", \"mailEnabled\": false, \"securityEnabled\": true } } ]",
            ("a", "Team"),
            ("b", "TEAM"));

        var error = await Assert.ThrowsAsync<ValidationFailedException>(() => host.ExpandAsync(document));

        Assert.Contains(error.Diagnostics, d => d.Code == EArm.Core.Diagnostics.DiagnosticCodes.DuplicateResourceId && d.Message.Contains("TEAM", StringComparison.Ordinal));
    }

    [Fact]
    public void GuidNamedSegments_AreNormalisedToLowerCase()
    {
        var upper = ResourceIdentifier.Create(Fixture.AppType, "ABCDEFAB-0000-4000-8000-00000000AAAA");
        var lower = ResourceIdentifier.Create(Fixture.AppType, "abcdefab-0000-4000-8000-00000000aaaa");

        Assert.Equal(lower, upper);
        Assert.Equal(lower.Id, upper.Id);
        Assert.Equal("abcdefab-0000-4000-8000-00000000aaaa", upper.NameLeaf);
        Assert.Equal("Orders-API", ResourceIdentifier.Create(Fixture.AppType, "Orders-API").NameLeaf);
    }

    [Fact]
    public async Task TwoDeclaredResources_ThatResolveToOneObject_FailTheSecondAtApplyTime()
    {
        var host = new EngineHost(graph => new AliasingGraphClient(graph, "/applications(uniqueName='app-b')", "/applications(uniqueName='app-a')"));
        host.Directory.AddApplication("app-a", "A");
        var state = NewState();

        var (_, result, _) = await ExecuteAsync(host, state, Apps("app-a", "app-b"));

        Assert.False(result.Succeeded);
        var failed = Assert.Single(result.Operations, o => o.Status == OperationStatus.Failed);
        Assert.Equal(EngineDiagnosticCodes.DuplicateGraphObject, failed.ErrorCode);
        Assert.Single(result.Operations, o => o.Status == OperationStatus.Succeeded);
        Assert.DoesNotContain(host.Fake.Writes, w => w.Method == "POST");
    }

    // ---- E-02: adoption ----

    [Fact]
    public async Task AnExistingObjectWithoutAStateEntry_IsAdopted_WithAWarning_AndRecordedAsAdopted()
    {
        var host = new EngineHost();
        var live = host.Directory.AddApplication("old-app", "Old");

        var state = NewState();
        var (_, result, store) = await ExecuteAsync(host, state, Apps("old-app", "fresh-app"));

        Assert.True(result.Succeeded);
        var warning = Assert.Single(result.Diagnostics, d => d.Code == EngineDiagnosticCodes.AdoptedExistingObject);
        Assert.Contains(AppId("old-app"), warning.Message, StringComparison.Ordinal);
        Assert.Contains(IdOf(live), warning.Message, StringComparison.Ordinal);
        Assert.Equal(StateProvenance.Adopted, state.Find(AppId("old-app"))!.Provenance);
        Assert.Equal(StateProvenance.Created, state.Find(AppId("fresh-app"))!.Provenance);
        string document = store.DocumentOf(EngineHost.DeploymentName)!;
        Assert.Contains("\"provenance\": \"adopted\"", document, StringComparison.Ordinal);
        Assert.Contains("\"provenance\": \"created\"", document, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARerun_KeepsTheProvenance_AndDoesNotWarnAgain()
    {
        var host = new EngineHost();
        host.Directory.AddApplication("old-app", "Old");
        var state = NewState();
        await ExecuteAsync(host, state, Apps("old-app", "fresh-app"));

        var (_, second, _) = await ExecuteAsync(host, state, Apps("old-app", "fresh-app"));

        Assert.DoesNotContain(second.Diagnostics, d => d.Code == EngineDiagnosticCodes.AdoptedExistingObject);
        Assert.Equal(StateProvenance.Adopted, state.Find(AppId("old-app"))!.Provenance);
        Assert.Equal(StateProvenance.Created, state.Find(AppId("fresh-app"))!.Provenance);
    }

    [Fact]
    public async Task AnAlreadyExistsRace_IsAdopted_NotCreated()
    {
        // Another deployment creates the application between our read and our create.
        EngineHost? created = null;
        var host = new EngineHost(graph => new RacingApplicationClient(graph, () => created!.Directory.AddApplication("racy", "Racy")));
        created = host;

        var state = NewState();
        var (_, result, _) = await ExecuteAsync(host, state, Apps("racy"));

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.Equal(StateProvenance.Adopted, state.Find(AppId("racy"))!.Provenance);
        Assert.Contains(result.Diagnostics, d => d.Code == EngineDiagnosticCodes.AdoptedExistingObject);
    }

    [Fact]
    public async Task WhatIf_ShowsTheAdoption()
    {
        var host = new EngineHost();
        host.Directory.AddApplication("old-app", "Old");

        var plan = await host.WhatIfAsync(Apps("old-app"), NewState());

        Assert.Contains(plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.AdoptedExistingObject && d.Message.Contains(AppId("old-app"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Complete_AnAdoptedOrphan_IsNotDeleted_TheEntryStays_AndTheRunStillSucceeds()
    {
        var host = new EngineHost();
        var live = host.Directory.AddApplication("brownfield", "Brownfield");
        var state = NewState();
        state.Upsert(Entry(AppId("brownfield"), Fixture.AppType, IdOf(live), "uniqueName", "brownfield", StateProvenance.Adopted));

        var (_, result, _) = await ExecuteAsync(host, state, Empty(), DeploymentMode.Complete);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(host.Fake.Writes, w => w.Method == "DELETE");
        Assert.Equal(1, host.Directory.Count("/applications"));
        Assert.NotNull(state.Find(AppId("brownfield")));
        Assert.Contains(result.Diagnostics, d => d.Code == EngineDiagnosticCodes.AdoptedObjectNotDeleted && !d.IsError);
        var op = Assert.Single(result.Operations);
        Assert.Equal(OperationStatus.Ignored, op.Status);
    }

    [Fact]
    public async Task Complete_AnAdoptedOrphan_IsDeleted_WithDeleteAdopted()
    {
        var host = new EngineHost();
        var live = host.Directory.AddApplication("brownfield", "Brownfield");
        var state = NewState();
        state.Upsert(Entry(AppId("brownfield"), Fixture.AppType, IdOf(live), "uniqueName", "brownfield", StateProvenance.Adopted));

        var (_, result, _) = await ExecuteAsync(host, state, Empty(), DeploymentMode.Complete, execute: new ExecuteOptions { DeleteAdopted = true });

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(0, host.Directory.Count("/applications"));
        Assert.Null(state.Find(AppId("brownfield")));
    }

    [Fact]
    public async Task WhatIf_Complete_AnAdoptedOrphan_IsAnIgnoreRow_UnlessDeleteAdoptedIsSet()
    {
        var host = new EngineHost();
        var live = host.Directory.AddApplication("brownfield", "Brownfield");
        var state = NewState();
        state.Upsert(Entry(AppId("brownfield"), Fixture.AppType, IdOf(live), "uniqueName", "brownfield", StateProvenance.Adopted));

        var kept = await host.WhatIfAsync(Empty(), state, DeploymentMode.Complete);
        var deleted = await host.WhatIfAsync(Empty(), state, DeploymentMode.Complete, host.Options() with { DeleteAdopted = true });

        Assert.Equal(ChangeType.Ignore, Assert.Single(kept.ModeChanges).Type);
        Assert.Contains(kept.Diagnostics, d => d.Code == EngineDiagnosticCodes.AdoptedObjectNotDeleted);
        Assert.Equal(ChangeType.Delete, Assert.Single(deleted.ModeChanges).Type);
    }

    [Fact]
    public async Task DeploymentDeleter_KeepsAdoptedEntries_AndRemovesTheStateOnlyWhenNothingIsLeft()
    {
        var host = new EngineHost();
        var created = host.Directory.AddApplication("created", "Created");
        var adopted = host.Directory.AddApplication("adopted", "Adopted");
        var state = NewState();
        state.Upsert(Entry(AppId("created"), Fixture.AppType, IdOf(created), "uniqueName", "created"));
        state.Upsert(Entry(AppId("adopted"), Fixture.AppType, IdOf(adopted), "uniqueName", "adopted", StateProvenance.Adopted));
        var store = new InMemoryStateStore();
        await store.SaveAsync(state, Ct);
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        var first = await DeploymentDeleter.DeleteAllAsync(state, store, host.Registry, host.Graph, host.Retry, false, DeleteGuard.Default, logger, Ct);

        Assert.True(first.Succeeded);
        Assert.False(first.StateRemoved);
        Assert.Equal(1, first.DeletedCount);
        Assert.Equal(1, first.RefusedCount);
        Assert.Equal(1, host.Directory.Count("/applications"));
        Assert.NotNull(store.DocumentOf(EngineHost.DeploymentName));
        Assert.Equal(StateFile.StatusSucceeded, state.LastStatus);

        var second = await DeploymentDeleter.DeleteAllAsync(state, store, host.Registry, host.Graph, host.Retry, false, new DeleteGuard { DeleteAdopted = true }, logger, Ct);

        Assert.True(second.StateRemoved);
        Assert.Equal(0, host.Directory.Count("/applications"));
    }

    [Fact]
    public void Provenance_RoundTrips_AndAnAbsentOrUnknownValueIsAdopted()
    {
        var state = NewState();
        state.Upsert(Entry(AppId("a"), Fixture.AppType, Gid(20), "uniqueName", "a", StateProvenance.Created));
        state.Upsert(Entry(AppId("b"), Fixture.AppType, Gid(21), "uniqueName", "b", StateProvenance.Adopted));

        var loaded = StateSerializer.Deserialize(StateSerializer.Serialize(state), EngineHost.DeploymentName);
        Assert.Equal(StateProvenance.Created, loaded.Find(AppId("a"))!.Provenance);
        Assert.Equal(StateProvenance.Adopted, loaded.Find(AppId("b"))!.Provenance);

        // A file written before provenance existed: safe default, nothing is deleted without --delete-adopted.
        string old = StateJson(Fixture.AppType, AppId("old"), Gid(22));
        Assert.Equal(StateProvenance.Adopted, StateSerializer.Deserialize(old, EngineHost.DeploymentName).Find(AppId("old"))!.Provenance);
        string odd = old.Replace("\"id\":", "\"provenance\": \"whatever\", \"id\":", StringComparison.Ordinal);
        Assert.Equal(StateProvenance.Adopted, StateSerializer.Deserialize(odd, EngineHost.DeploymentName).Find(AppId("old"))!.Provenance);
        Assert.Equal(1, StateFile.CurrentSchemaVersion);
    }
}
