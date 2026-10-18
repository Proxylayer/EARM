using System;
using System.Linq;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using Xunit;
using static EArm.Tests.Engine.Review1A;

namespace EArm.Tests.Engine;

/// <summary>
/// Review findings E-01 / I-02 (case-only renames and shared objects) and E-02 (an object created concurrently is taken over, not
/// created twice). Every test was written against a repro of the finding.
/// </summary>
public sealed class Review1A_IdentityTests
{
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

        var (_, result) = await ExecuteAsync(host, Apps("app-a", "app-b"));

        Assert.False(result.Succeeded);
        var failed = Assert.Single(result.Operations, o => o.Status == OperationStatus.Failed);
        Assert.Equal(EngineDiagnosticCodes.DuplicateGraphObject, failed.ErrorCode);
        Assert.Single(result.Operations, o => o.Status == OperationStatus.Succeeded);
        Assert.DoesNotContain(host.Fake.Writes, w => w.Method == "POST");
    }

    [Fact]
    public async Task TwoDeclaredResources_ThatResolveToOneObject_AreADuplicateInWhatIf()
    {
        var host = new EngineHost(graph => new AliasingGraphClient(graph, "/applications(uniqueName='app-b')", "/applications(uniqueName='app-a')"));
        host.Directory.AddApplication("app-a", "A");

        var plan = await host.WhatIfAsync(Apps("app-a", "app-b"));

        Assert.Single(plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.DuplicateGraphObject);
        Assert.Single(plan.Changes, c => c.HasError);
    }

    [Fact]
    public async Task ResourcesOfDifferentTypes_WithTheSameGraphId_AreNotADuplicate()
    {
        // The claim identity carries the type (IResourceProvider.ClaimIdentity): a Graph id seen by two types is never "one object, two declarations".
        var host = new EngineHost(graph => new AliasingGraphClient(graph, "/groups(uniqueName='team')", "/applications(uniqueName='app-a')"));
        host.Directory.AddApplication("app-a", "A");
        var document = Templates.Resources(
            "{ \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"app-a\", \"properties\": { \"displayName\": \"app-a\" } }, "
            + "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"team\", \"properties\": { \"displayName\": \"team\", \"mailEnabled\": false, \"securityEnabled\": true } }");

        var plan = await host.WhatIfAsync(document);

        Assert.DoesNotContain(plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.DuplicateGraphObject);
        Assert.Equal(host.Directory.Objects("/applications").Single()["id"]!.GetValue<string>(), plan.Changes.Single(c => c.Id.Id == GroupId("team")).GraphId);
    }

    // ---- E-02: adoption ----

    [Fact]
    public async Task AnAlreadyExistsRace_TakesTheObjectOver_NotCreatedTwice()
    {
        // Another deployment creates the application between our read and our create.
        EngineHost? created = null;
        var host = new EngineHost(graph => new RacingApplicationClient(graph, () => created!.Directory.AddApplication("racy", "Racy")));
        created = host;

        var (_, result) = await ExecuteAsync(host, Apps("racy"));

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.Single(host.Directory.Objects("/applications"), a => a["uniqueName"]?.GetValue<string>() == "racy");
    }
}
