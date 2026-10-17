using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>
/// Verification probe (h): a service principal and a user added to a group in one <c>$batch</c> (what the edge pass of
/// <c>GroupMembershipEdgeKind</c> sends) are both found by the reads <c>GroupMembershipEdgeKind</c> makes, for a plain security
/// group and for a role-assignable one. Observed on a real tenant: v1.0 <c>GET /groups/{id}/members</c> omits service principal
/// members (beta, the type cast <c>/members/microsoft.graph.servicePrincipal</c> and <c>$expand=members</c> list them, and
/// re-adding one answers "already exist"), so the edge kind reads the cast as well; this probe records whether that still holds.
/// </summary>
[Collection(RealTenantCollection.Name)]
[Trait("Category", "Verify")]
public sealed class GroupMembershipProbes : IntegrationTestBase
{
    private const string ServicePrincipalType = "#microsoft.graph.servicePrincipal";

    public GroupMembershipProbes(IntegrationFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task It_Probe_H_ServicePrincipalAndUserBatchAddedToGroupsAreReadBack()
    {
        Fixture.Require(Needs.Domain | Needs.Roles);
        var report = new ProbeReport("h");

        var application = await TenantObjects.CreateApplicationAsync(Fixture, "probe-member-app", null, Ct);
        var servicePrincipal = await Graph.CreateObjectAsync("/servicePrincipals", new JsonObject { ["appId"] = application.Text("appId") }, Ct);
        string spId = servicePrincipal.RequireId();
        Fixture.Track("/servicePrincipals/" + spId);

        string userName = Fixture.Name("probe-member-user");
        var user = await Graph.CreateObjectAsync(
            "/users",
            new JsonObject
            {
                ["accountEnabled"] = false,
                ["displayName"] = userName,
                ["mailNickname"] = userName,
                ["userPrincipalName"] = Fixture.Upn("probe-member-user"),
                ["passwordProfile"] = new JsonObject { ["password"] = NewPassword(), ["forceChangePasswordNextSignIn"] = true },
            },
            Ct);
        string userId = user.RequireId();
        Fixture.Track("/users/" + userId);

        foreach (bool roleAssignable in new[] { false, true })
        {
            string label = roleAssignable ? "role-assignable" : "plain";
            string name = Fixture.Name(roleAssignable ? "probe-member-ra" : "probe-member-plain");
            var body = new JsonObject
            {
                ["displayName"] = name,
                ["mailEnabled"] = false,
                ["mailNickname"] = name,
                ["securityEnabled"] = true,
            };
            if (roleAssignable)
            {
                body["isAssignableToRole"] = true;
            }

            var group = await Graph.CreateObjectAsync("/groups", body, Ct);
            string groupId = group.RequireId();
            Fixture.Track("/groups/" + groupId);
            await EventuallyAsync(() => Graph.GetObjectAsync(new GraphRequest("/groups/" + groupId).WithSelect("id"), Ct), found => found is not null);

            var responses = await Graph.BatchAsync(
                [
                    new BatchRequest("POST", $"/groups/{groupId}/members/$ref", new JsonObject { ["@odata.id"] = $"{GraphApi.V1.BaseUrl()}/directoryObjects/{spId}" }, Id: "sp"),
                    new BatchRequest("POST", $"/groups/{groupId}/members/$ref", new JsonObject { ["@odata.id"] = $"{GraphApi.V1.BaseUrl()}/directoryObjects/{userId}" }, Id: "user"),
                ],
                GraphApi.V1,
                RetryHint.Default,
                Ct);
            report.Observe($"{label}: batch add answers: {string.Join("; ", responses.Select(r => $"{r.Id}={(int)r.Status}{(r.Error is null ? string.Empty : " " + ProbeReport.Describe(r.Error))}"))}");

            var started = DateTime.UtcNow;
            var members = await EventuallyAsync(() => ReadAsync($"/groups/{groupId}/members"), found => found.ContainsKey(userId), seconds: 90, stepSeconds: 2);
            report.Observe($"{label}: v1.0 /members listed the user after {(DateTime.UtcNow - started).TotalSeconds:F0} s");
            started = DateTime.UtcNow;
            var cast = await EventuallyAsync(() => ReadAsync($"/groups/{groupId}/members/microsoft.graph.servicePrincipal"), found => found.ContainsKey(spId), seconds: 150, stepSeconds: 2);
            report.Observe($"{label}: the type cast listed the service principal {(DateTime.UtcNow - started).TotalSeconds:F0} s after that");
            report.Observe($"{label}: v1.0 /members: {Describe(members)}; /members/microsoft.graph.servicePrincipal: {Describe(cast)}");
            report.Observe(members.ContainsKey(spId)
                ? $"{label}: v1.0 /members NOW LISTS the service principal: the extra type-cast read in GroupMembershipEdgeKind.ReadMembersAsync could be dropped."
                : $"{label}: v1.0 /members still omits the service principal member; the type-cast read in GroupMembershipEdgeKind.ReadMembersAsync is required.");

            report.Expect(
                members.ContainsKey(userId),
                $"{label} group: a user batch-added as a member is not listed by v1.0 GET /groups/{{id}}/members after 90 s ({Describe(members)}). See GroupMembershipEdgeKind.ReadMembersAsync.");
            report.Expect(
                cast.ContainsKey(spId) && string.Equals(cast[spId], ServicePrincipalType, StringComparison.OrdinalIgnoreCase),
                $"{label} group: a service principal batch-added as a member is not listed by GET /groups/{{id}}/members/microsoft.graph.servicePrincipal after 90 s ({Describe(cast)}). See GroupMembershipEdgeKind.ReadMembersAsync.");
        }

        report.Complete();
    }

    private async Task<Dictionary<string, string?>> ReadAsync(string path)
    {
        var items = await Graph.ListAsync(new GraphRequest(path).WithSelect("id").WithTop(999), Ct);
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            map[item.Text("id") ?? "(null id)"] = item.Text("@odata.type") ?? (path.EndsWith("servicePrincipal", StringComparison.Ordinal) ? ServicePrincipalType : null);
        }

        return map;
    }

    private static string Describe(Dictionary<string, string?> members)
    {
        return members.Count == 0 ? "(none)" : string.Join(", ", members.Select(m => $"{m.Key} {m.Value}"));
    }
}
