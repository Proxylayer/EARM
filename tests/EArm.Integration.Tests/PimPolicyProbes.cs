using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Providers;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>
/// Verification probes for the open questions of <c>Microsoft.Entra/roleManagementPolicies</c> (phase 5; <c>EARM_IT_ALLOW_PIM=1</c> and
/// <c>EARM_IT_ALLOW_PIM_POLICIES=1</c>). A probe that writes only ever writes the Message Center Reader policy, snapshotted first and restored in a
/// <c>finally</c> (<see cref="PimPolicyRestoreGuard"/>), or the policy of a prefixed group it creates itself. Each probe records what Graph answers
/// (<see cref="ProbeReport"/>) and asserts only the assumption the code relies on, naming the file to change when it fails.
/// </summary>
[Collection(RealTenantCollection.Name)]
[Trait("Category", "Verify")]
public sealed partial class PimPolicyProbes : IntegrationTestBase
{
    private const Needs PolicyNeeds = Needs.Pim | Needs.PimPolicies;
    private const string AuthenticationContextRule = "AuthenticationContext_EndUser_Assignment";

    // PP7 timings: shorten the first two to iterate faster (the probe then answers only "not within N minutes").
    private const int PreOnboardingPollMinutes = 15;
    private const int PreOnboardingPollSeconds = 30;
    private const int OnboardingWaitSeconds = 180;
    private const int PersistenceWaitSeconds = 60;

    private const string MemberLookup = "(i) assignments by member role";
    private const string ScopeLookup = "(ii) assignments by scope";
    private const string CollectionLookup = "(iii) policies collection";

    public PimPolicyProbes(IntegrationFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task It_Probe_PP5_AssignmentLookupExpandsPolicyAndRules()
    {
        Fixture.Require(PolicyNeeds);
        var report = new ProbeReport("pp5");
        var scope = PimPolicyRestoreGuard.MessageCenterReader;
        string filter = RoleManagementPolicyLookup.Filter(scope);

        try
        {
            var expanded = await Graph.ListAsync(new GraphRequest(RoleManagementPolicyLookup.AssignmentsPath).WithFilter(filter).WithExpand("policy($expand=rules)"), Ct);
            var first = expanded.FirstOrDefault(a => RoleManagementPolicyLookup.Matches(a, scope));
            int rules = PimPolicyRestoreGuard.RulesOf(first).Count;
            report.Observe($"$expand=policy($expand=rules): {expanded.Count} assignment(s), the matching one carries {rules} rules (policy id {(first is null ? null : RoleManagementPolicyLookup.PolicyId(first))})");
            report.Expect(
                rules > 0,
                $"$expand=policy($expand=rules) does not return the rules of the Message Center Reader policy ({expanded.Count} assignment(s)); the provider reads no live rule and would report every declared rule as missing. Fix RoleManagementPolicyLookup.FindAsync (read GET /policies/roleManagementPolicies/{{id}}?$expand=rules after the assignment).");
        }
        catch (GraphException ex)
        {
            report.Expect(false, $"$expand=policy($expand=rules) on the assignment list is refused ({ProbeReport.Describe(ex)}). Fix RoleManagementPolicyLookup.FindAsync (read the policy with GET /policies/roleManagementPolicies/{{id}}?$expand=rules).");
        }

        try
        {
            var plain = await Graph.ListAsync(new GraphRequest(RoleManagementPolicyLookup.AssignmentsPath).WithFilter(filter).WithExpand("policy"), Ct);
            report.Observe($"$expand=policy (no nested expand): the policy {(plain.FirstOrDefault()?["policy"]?["rules"] is JsonArray { Count: > 0 } ? "CARRIES" : "does not carry")} its rules");
            if (plain.FirstOrDefault() is { } assignment && RoleManagementPolicyLookup.PolicyId(assignment) is { } policyId)
            {
                var policy = await Graph.GetObjectAsync(new GraphRequest(RoleManagementPolicyLookup.PoliciesPath + "/" + GraphPath.Segment(policyId)).WithExpand("rules"), Ct);
                report.Observe($"GET /policies/roleManagementPolicies/{{id}}?$expand=rules: {(policy?["rules"] as JsonArray)?.Count ?? 0} rules");
            }
        }
        catch (GraphException ex)
        {
            report.Observe("$expand=policy alone or the policy read: " + ProbeReport.Describe(ex));
        }

        report.Complete();
    }

    // ---- helpers ----

    /// <summary>
    /// adminRemove of the group member eligibility with the listed schedule (else the one requested). An ActiveDurationTooShort answer is repeated every
    /// 30 s until 7 minutes after the adminAssign (PIM refuses removals within 5 minutes); returns ACCEPTED or REFUSED with the details.
    /// </summary>
    private async Task<string> RemoveEligibilityAsync(string groupId, string userId, JsonObject requested, DateTime assigned)
    {
        var listed = PimReads.OfAccess(await PimReads.ByGroupAsync(Graph, GroupSchedulesLookup.EligibilitySchedulesPath, groupId, Ct), userId, "member");
        var schedule = listed.Count > 0 ? PimScheduleComparison.ScheduleView(listed[0]) : requested;
        var body = PimReads.GroupRequest(PimScheduleRequests.AdminRemove, groupId, userId, "member", schedule);
        string source = listed.Count > 0 ? "the listed schedule" : "the requested schedule (none listed)";
        int tooNew = 0;
        while (true)
        {
            try
            {
                await PimReads.SendAsync(Graph, GroupSchedulesLookup.EligibilityRequestsPath, body, Ct, expectReplicationLag: false);
                return $"ACCEPTED {(DateTime.UtcNow - assigned).TotalSeconds:F0} s after the adminAssign, after {tooNew} ActiveDurationTooShort answer(s), with {source}";
            }
            catch (GraphException ex) when (PimScheduleRequests.IsTooNewToRemove(ex) && DateTime.UtcNow - assigned < TimeSpan.FromMinutes(7))
            {
                tooNew++;
                await Task.Delay(TimeSpan.FromSeconds(30), Ct);
            }
            catch (GraphException ex)
            {
                return $"REFUSED {(DateTime.UtcNow - assigned).TotalSeconds:F0} s after the adminAssign, after {tooNew} ActiveDurationTooShort answer(s), with {source}: {ProbeReport.Describe(ex)}";
            }
        }
    }

    private async Task<string> PolicyIdAsync(PolicyScope scope)
    {
        var assignment = await Fixture.PimPolicies.ReadAsync(scope, Ct) ?? throw new InvalidOperationException($"Graph lists no policy for {scope.Describe()}.");
        return RoleManagementPolicyLookup.PolicyId(assignment) ?? throw new InvalidOperationException($"{scope.Describe()} was read without a policy id.");
    }

    private async Task<string> NewUserAsync(string suffix)
    {
        string id = (await TenantObjects.CreateDisabledUserAsync(Fixture, suffix, Ct)).RequireId();
        await EventuallyAsync(() => Graph.GetObjectAsync(new GraphRequest("/users/" + id).WithSelect("id"), Ct), found => found is not null);
        return id;
    }

    private async Task<string> NewGroupAsync(string suffix)
    {
        string id = (await TenantObjects.CreateGroupAsync(Fixture, suffix, Ct)).RequireId();
        await EventuallyAsync(() => Graph.GetObjectAsync(new GraphRequest("/groups/" + id).WithSelect("id"), Ct), found => found is not null);
        return id;
    }
}
