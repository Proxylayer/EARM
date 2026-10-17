using System;
using System.Linq;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Graph;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>
/// Adopt-only resources against a real tenant (<c>"x-earm": { "objectId": "…" }</c>, docs/format.md): a group created outside EARM is adopted by
/// displayName only when the pin names it, a missing group is never created, and <c>deployment delete</c> leaves the adopted group alone.
/// Skipped without <c>EARM_IT_TENANT</c>.
/// </summary>
[Collection(RealTenantCollection.Name)]
public sealed class AdoptOnlyScenarioTests : IntegrationTestBase
{
    private const string OtherId = "99999999-8888-7777-6666-555555555555";

    public AdoptOnlyScenarioTests(IntegrationFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task It_Groups_AdoptOnlyWithAPinnedObjectId()
    {
        Fixture.Require();
        string displayName = Fixture.Name("adopt-only");
        string uniqueName = Fixture.Name("adopt-only-u");
        string rowSuffix = "/groups/" + uniqueName;

        // Nothing holds the key yet: adopt-only never creates it.
        var missing = Fixture.NewRun("adopt-only-missing", "group-adopt-only.entra.json")
            .WithParameter("uniqueNameSuffix", "adopt-only-missing-u")
            .WithParameter("displayNameSuffix", "adopt-only-missing")
            .WithParameter("objectId", OtherId);
        var missingCreate = await Fixture.Cli.CreateAsync(missing, Ct);
        Assert.NotEqual(0, missingCreate.ExitCode);
        Assert.Contains(missingCreate.Operations, o => (string?)o["errorCode"] == EngineDiagnosticCodes.AdoptOnlyNotFound);
        Assert.Null(await Graph.GetObjectAsync(new GraphRequest(GraphPath.AlternateKey("/groups", "uniqueName", Fixture.Name("adopt-only-missing-u"))).WithSelect("id"), Ct));

        // A group created outside EARM (no uniqueName), found by displayName.
        string groupId = (await TenantObjects.CreateGroupAsync(Fixture, "adopt-only", Ct)).RequireId();
        await FindOneEventuallyAsync("/groups", "displayName", displayName, found => found is not null, "id");

        var pinnedElsewhere = await Fixture.Cli.WhatIfAsync(Fixture.NewRun("adopt-only", "group-adopt-only.entra.json").WithParameter("objectId", OtherId), Ct, "--exit-code-on-changes");
        var row = pinnedElsewhere.Changes.FirstOrDefault(c => ((string?)c["resourceId"])?.EndsWith(rowSuffix, StringComparison.OrdinalIgnoreCase) == true);
        Assert.True(
            (string?)row?["changeType"] == "Unsupported" && ((string?)row?["unsupportedReason"] ?? string.Empty).Contains(groupId, StringComparison.OrdinalIgnoreCase),
            $"what-if with another pinned id: expected an Unsupported row naming {groupId} but got: {pinnedElsewhere.DescribeChanges()}{Environment.NewLine}{Tail(pinnedElsewhere.Combined)}");

        var run = Fixture.NewRun("adopt-only", "group-adopt-only.entra.json").WithParameter("objectId", groupId);
        AssertChange(await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), rowSuffix, "Modify", "what-if of the pinned adoption");
        AssertCreated(await Fixture.Cli.CreateAsync(run, Ct), "create (pinned adoption)");
        var adopted = await EventuallyAsync(
            () => Graph.GetObjectAsync(new GraphRequest("/groups/" + groupId).WithSelect("id", "uniqueName", "description"), Ct),
            found => string.Equals(found.Text("uniqueName"), uniqueName, StringComparison.OrdinalIgnoreCase) && found.Text("description") is not null);
        Assert.Equal("Adopted (adopt-only) by the EARM integration tests", adopted.Text("description"));

        // deployment delete leaves an adopt-only object alone (the fixture's sweep removes it).
        AssertExit(0, await Fixture.Cli.DeleteAllAsync(run, Ct), "deployment delete");
        Assert.NotNull(await Graph.GetObjectAsync(new GraphRequest("/groups/" + groupId).WithSelect("id"), Ct));
    }
}
