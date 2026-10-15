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
    public async Task ARecordedIdOfAnotherObject_IsNeverRead_TheObjectWithTheKeyIsUsed_AndTheOtherObjectIsNeverPatched()
    {
        // Identity is resolved live by key: the recorded id is never consulted, so the object that has the key is used.
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
        Assert.DoesNotContain(host.Fake.Requests, r => r.Path.Contains(victimId, StringComparison.Ordinal));
        var op = Assert.Single(result.Operations);
        Assert.Equal(ChangeType.Modify, op.ChangeType);
        Assert.NotEqual(victimId, state.GraphIdOf(AppId("mine")));
    }

    [Fact]
    public async Task WhatIf_ARecordedIdOfAnotherObject_UsesTheObjectWithTheKey()
    {
        var host = new EngineHost();
        var victim = host.Directory.AddApplication("victim", "Victim");
        host.Directory.AddApplication("mine", "Mine");
        var state = NewState();
        state.Upsert(Entry(AppId("mine"), Fixture.AppType, IdOf(victim), "uniqueName", "mine"));

        var plan = await host.WhatIfAsync(Apps("mine"), state);

        var change = Assert.Single(plan.Changes);
        Assert.Equal(ChangeType.Modify, change.Type);
        Assert.DoesNotContain(host.Fake.Requests, r => r.Path.Contains(IdOf(victim), StringComparison.Ordinal));
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
    public async Task AnAlreadyExistsRace_TakesTheObjectOver_NotCreatedTwice()
    {
        // Another deployment creates the application between our read and our create.
        EngineHost? created = null;
        var host = new EngineHost(graph => new RacingApplicationClient(graph, () => created!.Directory.AddApplication("racy", "Racy")));
        created = host;

        var state = NewState();
        var (_, result, _) = await ExecuteAsync(host, state, Apps("racy"));

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.Single(host.Directory.Objects("/applications"), a => a["uniqueName"]?.GetValue<string>() == "racy");
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
