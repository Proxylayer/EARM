using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Az;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>
/// <c>state adopt</c> against hand-made objects: the uniqueName backfill (online) and the <c>--object-id</c> pin of a
/// displayName-keyed type (offline), followed by a deployment that must update the adopted object instead of creating a second one.
/// </summary>
[Collection(RealTenantCollection.Name)]
public sealed class AdoptScenarioTests : IntegrationTestBase
{
    public AdoptScenarioTests(IntegrationFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task It_Adopt_UniqueNameBackfillOnHandCreatedApplication()
    {
        Fixture.Require();
        string name = Fixture.Name("adopt-app");

        // A brownfield application: created without a uniqueName, so no template can address it by name yet.
        var application = await Graph.CreateObjectAsync("/applications", new JsonObject { ["displayName"] = name, ["signInAudience"] = "AzureADMyOrg" }, Ct);
        string id = application.RequireId();
        Fixture.Track("/applications/" + id);
        var before = await Graph.GetObjectAsync(new GraphRequest("/applications/" + id).WithSelect("id", "uniqueName"), Ct);
        Assert.True(before.IsNullOrAbsent("uniqueName"), "The hand-made application already has a uniqueName: " + before!.ToJsonString());

        var adopt = await Fixture.Cli.RunAsync(["state", "adopt", "--type", "Microsoft.Entra/applications", "--id", id, "--name", name, "-y"], Ct);
        AssertExit(0, adopt, "state adopt (uniqueName)");
        Assert.Equal(id, (await ApplicationByNameAsync(name, "id"))!.RequireId());

        // Idempotent for the same name, refused for another one (uniqueName is immutable).
        AssertExit(0, await Fixture.Cli.RunAsync(["state", "adopt", "--type", "Microsoft.Entra/applications", "--id", id, "--name", name, "-y"], Ct), "state adopt repeated");
        var refused = await Fixture.Cli.RunAsync(["state", "adopt", "--type", "Microsoft.Entra/applications", "--id", id, "--name", name + "-other", "-y"], Ct);
        AssertExit(1, refused, "state adopt with a different uniqueName");

        // The template now binds to the hand-made application: a Modify, never a Create.
        var run = Fixture.NewRun("adopt", "adopt-application.entra.json");
        AssertChange(await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "/applications/" + name, "Modify", "what-if after adopt");
        AssertCreated(await Fixture.Cli.CreateAsync(run, Ct), "create after adopt");

        var sameName = await Graph.ListAsync(new GraphRequest("/applications").WithFilter($"displayName eq '{name}'").WithSelect("id", "web"), Ct);
        var only = Assert.Single(sameName);
        Assert.Equal(id, only.RequireId());
        Assert.Equal("https://earm-it.example.test/home", only.Nested("web")!.Text("homePageUrl"));
        AssertNoChanges(await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "what-if after create");
    }

    [Fact]
    public async Task It_Adopt_ObjectIdPinsDisplayNameKeyedAdministrativeUnit()
    {
        Fixture.Require(Needs.Roles);
        string name = Fixture.Name("pin-au");
        var run = Fixture.NewRun("pin", "adopt-administrative-unit.entra.json");
        string resourceId = "/providers/Microsoft.Entra/administrativeUnits/" + name;

        var intended = await Graph.CreateObjectAsync("/directory/administrativeUnits", new JsonObject { ["displayName"] = name, ["description"] = "hand-made, intended" }, Ct);
        Fixture.Track("/directory/administrativeUnits/" + intended.RequireId());

        // A second unit with the same display name makes the key ambiguous (when Graph allows the duplicate).
        JsonObject? decoy = null;
        try
        {
            decoy = await Graph.CreateObjectAsync("/directory/administrativeUnits", new JsonObject { ["displayName"] = name, ["description"] = "hand-made, decoy" }, Ct);
            Fixture.Track("/directory/administrativeUnits/" + decoy.RequireId());
        }
        catch (GraphException ex)
        {
            Observe($"Graph refused a second administrative unit named '{name}' ({ex.Code}); the ambiguity part of the scenario is skipped.");
        }

        if (decoy is not null)
        {
            var ambiguous = await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes");
            bool refusedOrUnsupported = ambiguous.ExitCode != 0 && ambiguous.ExitCode != 5
                || ambiguous.Changes.Any(c => c["unsupportedReason"] is not null);
            Assert.True(refusedOrUnsupported, "Two units share the name but the what-if neither failed nor flagged the row: " + ambiguous.DescribeChanges());
        }

        var pin = await Fixture.Cli.RunAsync(["state", "adopt", "-n", run.Name, "--resource-id", resourceId, "--object-id", intended.RequireId(), "-y"], Ct);
        AssertExit(0, pin, "state adopt --object-id");
        var pinned = await Fixture.Store.LoadAsync(run.Name, Ct);
        Assert.Equal(intended.RequireId(), pinned?.GraphIdOf(resourceId));

        AssertChange(await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), resourceId, "Modify", "what-if after the pin");
        AssertCreated(await Fixture.Cli.CreateAsync(run, Ct), "create after the pin");

        var live = await Graph.GetObjectAsync(new GraphRequest("/directory/administrativeUnits/" + intended.RequireId()), Ct);
        Assert.Equal("managed by EARM", live.Text("description"));
        if (decoy is not null)
        {
            var other = await Graph.GetObjectAsync(new GraphRequest("/directory/administrativeUnits/" + decoy.RequireId()), Ct);
            Assert.Equal("hand-made, decoy", other.Text("description"));
        }

        Assert.Equal(intended.RequireId(), (await Fixture.Store.LoadAsync(run.Name, Ct))?.GraphIdOf(resourceId));
    }
}
