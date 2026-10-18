using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Providers;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>Probes PP1 and PP7 of <see cref="PimPolicyProbes"/>: when a group's policy is listed, before and after onboarding, through each lookup.</summary>
public sealed partial class PimPolicyProbes
{
    [Fact]
    public async Task It_Probe_PP1_GroupPolicyIsNotListedBeforeOnboardingAndIsAfterwards()
    {
        Fixture.Require(Needs.Domain | PolicyNeeds);
        var report = new ProbeReport("pp1");
        string userId = await NewUserAsync("probe-ppol-onb");
        string groupId = await NewGroupAsync("probe-ppol-onb-grp");
        var scope = PimPolicyRestoreGuard.GroupAccess(groupId, "member");

        JsonObject? before = null;
        try
        {
            before = await Fixture.PimPolicies.ReadAsync(scope, Ct);
            report.Observe(before is null
                ? "before onboarding Graph lists NO member policy for the new group (the provider defers the policy row with PimPolicyNotAvailable when a request of the same run onboards the group)"
                : $"before onboarding Graph LISTS a member policy for the new group (policy id {RoleManagementPolicyLookup.PolicyId(before)}, {PimPolicyRestoreGuard.RulesOf(before).Count} rules): the provider then PATCHes it at once; the 404 re-id retry covers an id change on onboarding");
        }
        catch (GraphException ex)
        {
            report.Expect(false, $"the policy assignment lookup of a group PIM has not onboarded is refused ({ProbeReport.Describe(ex)}): RoleManagementPolicyLookup.FindAsync would fail instead of reporting PimPolicyNotAvailable. Treat that answer as 'not listed' in RoleManagementPolicyLookup.FindAsync.");
        }

        try
        {
            var loose = await Graph.ListAsync(new GraphRequest(RoleManagementPolicyLookup.AssignmentsPath).WithFilter($"scopeId eq '{groupId}' and scopeType eq 'Group'"), Ct);
            report.Observe($"before onboarding, $filter=scopeId+scopeType (no roleDefinitionId) lists {loose.Count} assignment(s)");
        }
        catch (GraphException ex)
        {
            report.Observe("before onboarding, $filter=scopeId+scopeType (no roleDefinitionId): " + ProbeReport.Describe(ex));
        }

        await PimReads.SendAsync(Graph, GroupSchedulesLookup.EligibilityRequestsPath, PimReads.GroupRequest(PimScheduleRequests.AdminAssign, groupId, userId, "member", PimReads.ScheduleInfo(duration: "P7D")), Ct);
        var started = DateTime.UtcNow;
        var after = await EventuallyAsync(() => Fixture.PimPolicies.ReadAsync(scope, Ct), found => found is not null, seconds: 180, stepSeconds: 10);
        report.Expect(
            after is not null,
            "180 s after the first eligibility request of a group its member policy is still not listed: neither the end-of-run retry (up to 90 s) nor the next run of a template that onboards the group would find it, and both would report PimPolicyNotAvailable. Revisit RoleManagementPoliciesProvider.PlanMissing and docs/pim.md (the 'applied at the end of the same run' claim).");
        if (after is not null)
        {
            string? newId = RoleManagementPolicyLookup.PolicyId(after);
            report.Observe($"after onboarding the member policy is listed after about {(DateTime.UtcNow - started).TotalSeconds:F0} s (the end-of-run retry waits up to 90 s): policy id {newId}, {PimPolicyRestoreGuard.RulesOf(after).Count} rules");
            if (before is not null)
            {
                report.Observe($"the policy id {(string.Equals(newId, RoleManagementPolicyLookup.PolicyId(before), StringComparison.OrdinalIgnoreCase) ? "did NOT change" : "CHANGED")} on onboarding ({RoleManagementPolicyLookup.PolicyId(before)} -> {newId})");
            }
        }

        report.Complete();
    }

    [Fact]
    public async Task It_Probe_PP7_GroupPolicyAppearsOnlyOnOnboarding()
    {
        Fixture.Require(Needs.Domain | PolicyNeeds);
        var report = new ProbeReport("pp7");
        string groupId = await NewGroupAsync("probe-ppol-onb7-grp");
        string userId = await NewUserAsync("probe-ppol-onb7");
        var scope = PimPolicyRestoreGuard.GroupAccess(groupId, "member");
        var lookups = OnboardingLookups(scope);
        var created = DateTime.UtcNow;
        var duration = TimeSpan.FromMinutes(PreOnboardingPollMinutes);
        var interval = TimeSpan.FromSeconds(PreOnboardingPollSeconds);
        report.Observe($"group {scope.ScopeId} created; polling three lookups every {PreOnboardingPollSeconds} s for up to {PreOnboardingPollMinutes} min before any eligibility request: {string.Join(" | ", lookups.Values.Select(l => $"{l.Label} = GET {l.Path}?$filter={l.Filter}"))}");

        // Before onboarding: every lookup every PreOnboardingPollSeconds, until one of them returns a policy or the time is up.
        Dictionary<string, LookupResult> last;
        while (true)
        {
            last = await RunLookupsAsync(lookups);
            report.Observe($"t+{(DateTime.UtcNow - created).TotalSeconds:F0} s: {DescribeLookups(last)}");
            if (last.Values.Any(r => r.PolicyIds.Count > 0) || DateTime.UtcNow - created + interval > duration)
            {
                break;
            }

            await Task.Delay(interval, Ct);
        }

        var listedBefore = last.Values.Where(r => r.PolicyIds.Count > 0).Select(r => r.Label).ToList();
        report.Observe(listedBefore.Count == 0
            ? $"no lookup returned a policy in {(DateTime.UtcNow - created).TotalMinutes:F1} min before any eligibility request: the group's policy is NOT discoverable before onboarding"
            : $"before any eligibility request, {string.Join(" and ", listedBefore)} returned a policy {(DateTime.UtcNow - created).TotalSeconds:F0} s after the group was created");

        // When the policies collection returns the policy: write one harmless rule back to its own value and watch the member assignment lookup.
        if (last[CollectionLookup].PolicyIds.FirstOrDefault() is { } collectionPolicyId)
        {
            string outcome = await PatchRuleToItselfAsync(collectionPolicyId, PimPolicyScenarioTests.ExpirationRule);
            report.Observe($"PATCH of {PimPolicyScenarioTests.ExpirationRule} on policy {collectionPolicyId} (from the policies collection) to its current value: {outcome}");
            var patched = DateTime.UtcNow;
            var member = await EventuallyAsync(() => LookupAsync(lookups[MemberLookup]), r => r.PolicyIds.Count > 0, seconds: OnboardingWaitSeconds, stepSeconds: 5);
            report.Observe(member.PolicyIds.Count > 0
                ? $"after the rule PATCH the member assignment lookup lists policy {string.Join(",", member.PolicyIds)} after about {(DateTime.UtcNow - patched).TotalSeconds:F0} s: a policy PATCH ONBOARDS the group (policy id {(member.PolicyIds.Contains(collectionPolicyId, StringComparer.OrdinalIgnoreCase) ? "unchanged" : "CHANGED from " + collectionPolicyId)})"
                : $"{OnboardingWaitSeconds} s after the rule PATCH the member assignment lookup still lists no policy ({member.Describe()}): a policy PATCH does NOT onboard the group");
        }
        else
        {
            report.Observe("the policies collection returned no policy before onboarding, so the rule PATCH route could not be tried");
        }

        // The first eligibility request (also after a PATCH, so there is an eligibility whose removal tests persistence): how long until the policy is listed.
        var requested = PimReads.ScheduleInfo(duration: "P7D");
        await PimReads.SendAsync(Graph, GroupSchedulesLookup.EligibilityRequestsPath, PimReads.GroupRequest(PimScheduleRequests.AdminAssign, groupId, userId, "member", requested), Ct);
        var assigned = DateTime.UtcNow;
        TimeSpan? memberAt = null;
        TimeSpan? collectionAt = null;
        while (true)
        {
            last = await RunLookupsAsync(lookups, MemberLookup, CollectionLookup);
            memberAt ??= last[MemberLookup].PolicyIds.Count > 0 ? (TimeSpan?)(DateTime.UtcNow - assigned) : null;
            collectionAt ??= last[CollectionLookup].PolicyIds.Count > 0 ? (TimeSpan?)(DateTime.UtcNow - assigned) : null;
            if ((memberAt is not null && collectionAt is not null) || (DateTime.UtcNow - assigned).TotalSeconds + 5 > OnboardingWaitSeconds)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), Ct);
        }

        report.Observe($"after the adminAssign (P7D): the member assignment lookup lists a policy {(memberAt is { } m ? $"after about {m.TotalSeconds:F0} s" : $"NOT within {OnboardingWaitSeconds} s")}, the policies collection {(collectionAt is { } c ? $"after about {c.TotalSeconds:F0} s" : $"NOT within {OnboardingWaitSeconds} s")}; last answers: {DescribeLookups(last)}");
        var onboardedIds = last[MemberLookup].PolicyIds;

        // Remove the only eligibility, wait, and check the policy is still listed (onboarding persists).
        string removal = await RemoveEligibilityAsync(groupId, userId, requested, assigned);
        report.Observe("adminRemove of the only eligibility: " + removal);
        if (removal.StartsWith("ACCEPTED", StringComparison.Ordinal))
        {
            var remaining = await EventuallyAsync(
                async () => PimReads.OfAccess(await PimReads.ByGroupAsync(Graph, GroupSchedulesLookup.EligibilitySchedulesPath, groupId, Ct), userId, "member"),
                found => found.Count == 0,
                seconds: 120,
                stepSeconds: 5);
            report.Observe(remaining.Count == 0 ? "the eligibility schedule is no longer listed" : $"the eligibility schedule is STILL listed 120 s after the adminRemove ({string.Join("; ", remaining.Select(PimReads.DescribeSchedule))})");
            await Task.Delay(TimeSpan.FromSeconds(PersistenceWaitSeconds), Ct);
            var afterRemove = await RunLookupsAsync(lookups, MemberLookup, CollectionLookup);
            var member = afterRemove[MemberLookup];
            report.Observe($"{PersistenceWaitSeconds} s after the eligibility was removed: {DescribeLookups(afterRemove)}");
            report.Observe(member.PolicyIds.Count > 0
                ? $"onboarding PERSISTS without any eligibility: the member assignment lookup still lists policy {string.Join(",", member.PolicyIds)} ({(member.PolicyIds.SequenceEqual(onboardedIds, StringComparer.OrdinalIgnoreCase) ? "the same id as after onboarding" : "after onboarding it listed " + (onboardedIds.Count == 0 ? "none" : string.Join(",", onboardedIds)))})"
                : $"onboarding does NOT persist: the member assignment lookup lists no policy once the only eligibility is gone ({member.Describe()})");
        }
        else
        {
            report.Observe("the eligibility was not removed, so whether onboarding persists stays UNVERIFIED; the cleanup guard ends it with the user's deletion");
        }

        report.Complete();
    }

    /// <summary>One GET of a PP7 lookup: the collection, its <c>$filter</c>, and whether the items are policies (else policy assignments).</summary>
    private sealed record OnboardingLookup(string Label, string Path, string Filter, bool ListsPolicies);

    /// <summary>What a PP7 lookup answered: <c>200</c> or the error, the item count and the distinct policy ids.</summary>
    private sealed record LookupResult(string Label, string Status, int Count, IReadOnlyList<string> PolicyIds)
    {
        public string Describe()
        {
            return $"{Label}: {Status}, {Count} item(s){(PolicyIds.Count > 0 ? ", policy " + string.Join(",", PolicyIds) : string.Empty)}";
        }
    }

    /// <summary>The three lookups of PP7 for a group's member policy, by label.</summary>
    private static Dictionary<string, OnboardingLookup> OnboardingLookups(PolicyScope scope)
    {
        string byScope = "scopeId eq '" + PimScheduleRequests.Quote(scope.ScopeId) + "' and scopeType eq '" + PimScheduleRequests.Quote(scope.ScopeType) + "'";
        return new Dictionary<string, OnboardingLookup>(StringComparer.Ordinal)
        {
            [MemberLookup] = new(MemberLookup, RoleManagementPolicyLookup.AssignmentsPath, RoleManagementPolicyLookup.Filter(scope), ListsPolicies: false),
            [ScopeLookup] = new(ScopeLookup, RoleManagementPolicyLookup.AssignmentsPath, byScope, ListsPolicies: false),
            [CollectionLookup] = new(CollectionLookup, RoleManagementPolicyLookup.PoliciesPath, byScope, ListsPolicies: true),
        };
    }

    /// <summary>Runs one lookup; an error is the answer (recorded), not a failure.</summary>
    private async Task<LookupResult> LookupAsync(OnboardingLookup lookup)
    {
        try
        {
            var items = await Graph.ListAsync(new GraphRequest(lookup.Path).WithFilter(lookup.Filter), Ct);
            var ids = items
                .Select(i => lookup.ListsPolicies ? i.Text("id") : RoleManagementPolicyLookup.PolicyId(i))
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new LookupResult(lookup.Label, "200", items.Count, ids);
        }
        catch (GraphException ex)
        {
            return new LookupResult(lookup.Label, ProbeReport.Describe(ex), 0, []);
        }
    }

    /// <summary>Runs the named lookups (all when none is named), in order.</summary>
    private async Task<Dictionary<string, LookupResult>> RunLookupsAsync(Dictionary<string, OnboardingLookup> lookups, params string[] only)
    {
        var results = new Dictionary<string, LookupResult>(StringComparer.Ordinal);
        foreach (var (label, lookup) in lookups)
        {
            if (only.Length == 0 || only.Contains(label, StringComparer.Ordinal))
            {
                results[label] = await LookupAsync(lookup);
            }
        }

        return results;
    }

    private static string DescribeLookups(Dictionary<string, LookupResult> results)
    {
        return string.Join(" | ", results.Values.Select(r => r.Describe()));
    }
}
