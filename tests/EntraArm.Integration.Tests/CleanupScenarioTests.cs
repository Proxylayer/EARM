using System;
using System.Threading.Tasks;
using EntraArm.Az;
using Xunit;

namespace EntraArm.Integration.Tests;

/// <summary>
/// The cleanup guard on its own: deletes what a crashed run left behind. The fixture teardown does the same after every
/// run; this test exists to run it by hand for a known prefix (README.md, "Manual cleanup").
/// </summary>
[Collection(RealTenantCollection.Name)]
public sealed class CleanupScenarioTests : IntegrationTestBase
{
    public CleanupScenarioTests(IntegrationFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task It_Cleanup_ManualSweepOfTheConfiguredPrefix()
    {
        Fixture.Require();
        Assert.SkipUnless(
            Fixture.Settings.SweepRequested,
            $"{IntegrationSettings.SweepVariable}=1 is not set; this test only runs the cleanup guard for the prefix in {IntegrationSettings.PrefixVariable} (see README.md, 'Manual cleanup').");

        await Fixture.SweepNowAsync(Ct);
        Observe(string.Join(Environment.NewLine, Fixture.CleanupLog));
        Assert.True(Fixture.Leftovers.Count == 0, "The sweep left objects behind:" + Environment.NewLine + string.Join(Environment.NewLine, Fixture.Leftovers));
    }

    [Fact]
    public async Task It_Cleanup_SweepRemovesTrackedGroupWithThePrefix()
    {
        Fixture.Require();

        // The direct-created path: a tracked entity path with a prefixed name is deleted (and purged) by the sweep.
        // The rest of the guard is exercised by the teardown of every other scenario.
        var group = await TenantObjects.CreateGroupAsync(Fixture, "cleanup-probe", Ct);
        string id = group.RequireId();
        Assert.NotNull(await Graph.GetObjectAsync(new GraphRequest("/groups/" + id), Ct));

        await Fixture.SweepNowAsync(Ct);

        Assert.Null(await Graph.GetObjectAsync(new GraphRequest("/groups/" + id), Ct));
        Assert.Empty(Fixture.Leftovers);
    }
}
