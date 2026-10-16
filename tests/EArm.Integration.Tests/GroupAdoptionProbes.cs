using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>
/// Verification probe (GA1): adopting a group by display name (<c>GroupsProvider</c>) sets the <c>uniqueName</c> of a group created without one
/// with <c>PATCH /groups/{id}</c>, although Learn documents the property as immutable and read-only. This probe asserts that the first set works
/// and reads back, and records whether a second PATCH to another value is accepted (is it settable once, or freely?).
/// </summary>
[Collection(RealTenantCollection.Name)]
[Trait("Category", "Verify")]
public sealed class GroupAdoptionProbes : IntegrationTestBase
{
    private const string FixHint = "see AdoptUniqueNameAsync in src/EArm.Providers/GroupsProvider.cs and the group adoption notes in docs/providers.md (adoption by display name relies on it)";

    public GroupAdoptionProbes(IntegrationFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task It_Probe_GA1_UniqueNameCanBeSetOnExistingGroup()
    {
        Fixture.Require();
        var report = new ProbeReport("ga1");

        var group = await TenantObjects.CreateGroupAsync(Fixture, "ga1-group", Ct);
        string groupId = group.RequireId();
        report.Observe($"POST /groups answered uniqueName {(group.IsNullOrAbsent("uniqueName") ? "(null)" : "'" + group.Text("uniqueName") + "'")}");
        await EventuallyAsync(() => ReadAsync(groupId), found => found is not null);

        string first = Fixture.Name("ga1");
        bool firstAccepted = await TryPatchAsync(report, groupId, first, "first");
        report.Expect(firstAccepted, $"PATCH /groups/{{id}} {{\"uniqueName\":\"{first}\"}} on a group created without uniqueName was refused: {FixHint}.");

        if (firstAccepted)
        {
            var read = await EventuallyAsync(() => ReadAsync(groupId), found => string.Equals(found.Text("uniqueName"), first, StringComparison.OrdinalIgnoreCase));
            report.Observe($"after the first PATCH the group reads uniqueName '{read.Text("uniqueName")}'");
            report.Expect(
                string.Equals(read.Text("uniqueName"), first, StringComparison.OrdinalIgnoreCase),
                $"the first PATCH of uniqueName was accepted but GET /groups/{{id}} reads '{read.Text("uniqueName")}' instead of '{first}' after 45 s: {FixHint}.");

            // The open question: can it be changed again? Recorded only; GroupsProvider never changes an existing uniqueName.
            string second = Fixture.Name("ga1-second");
            if (await TryPatchAsync(report, groupId, second, "second"))
            {
                var again = await EventuallyAsync(() => ReadAsync(groupId), found => string.Equals(found.Text("uniqueName"), second, StringComparison.OrdinalIgnoreCase), seconds: 30);
                report.Observe($"after the second PATCH the group reads uniqueName '{again.Text("uniqueName")}' ({(string.Equals(again.Text("uniqueName"), second, StringComparison.OrdinalIgnoreCase) ? "uniqueName is freely settable" : "the change did not stick")})");
            }
            else
            {
                report.Observe("uniqueName is settable once: a second PATCH to another value is refused (GroupsProvider treats it as immutable after adoption, which matches).");
            }
        }

        report.Complete();
    }

    private async Task<bool> TryPatchAsync(ProbeReport report, string groupId, string uniqueName, string label)
    {
        try
        {
            var response = await Graph.PatchAsync(new GraphRequest("/groups/" + groupId).WithHint(RetryHint.ExpectReplicationLag), new JsonObject { ["uniqueName"] = uniqueName }, Ct);
            report.Observe($"{label} PATCH uniqueName='{uniqueName}' ACCEPTED (status {(int)response.Status})");
            return true;
        }
        catch (GraphException ex)
        {
            report.Observe($"{label} PATCH uniqueName='{uniqueName}' REJECTED: {ProbeReport.Describe(ex)}");
            return false;
        }
    }

    private Task<JsonObject?> ReadAsync(string groupId)
    {
        return Graph.GetObjectAsync(new GraphRequest("/groups/" + groupId).WithSelect("id", "uniqueName", "displayName"), Ct);
    }
}
