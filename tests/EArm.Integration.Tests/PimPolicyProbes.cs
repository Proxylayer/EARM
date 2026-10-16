using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Validation;
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
public sealed class PimPolicyProbes : IntegrationTestBase
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
            report.Expect(false, $"the policy assignment lookup of a group PIM has not onboarded is refused ({ProbeReport.Describe(ex)}): RoleManagementPolicyLookup.FindAsync would fail instead of reporting PimPolicyNotAvailable. Treat that answer as 'not listed' in src/EArm.Providers/RoleManagementPolicyLookup.cs (FindAsync).");
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
            "180 s after the first eligibility request of a group its member policy is still not listed: the second run of a template that onboards the group would still report PimPolicyNotAvailable. Revisit PlanMissing in src/EArm.Providers/RoleManagementPoliciesProvider.cs and docs/pim.md (the 'converges in two runs' claim).");
        if (after is not null)
        {
            string? newId = RoleManagementPolicyLookup.PolicyId(after);
            report.Observe($"after onboarding the member policy is listed after about {(DateTime.UtcNow - started).TotalSeconds:F0} s: policy id {newId}, {PimPolicyRestoreGuard.RulesOf(after).Count} rules");
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

    [Fact]
    public async Task It_Probe_PP2_RulePatchWithoutODataTypeIsRefusedAndTheProviderBodyIsAccepted()
    {
        Fixture.Require(PolicyNeeds);
        var report = new ProbeReport("pp2");
        var guard = Fixture.PimPolicies;
        var scope = PimPolicyRestoreGuard.MessageCenterReader;
        var log = new List<string>();
        try
        {
            var snapshot = await guard.SnapshotAsync(scope, Ct) ?? throw new InvalidOperationException("Graph lists no PIM policy for Message Center Reader.");
            string policyId = await PolicyIdAsync(scope);
            var live = snapshot[PimPolicyScenarioTests.ExpirationRule];
            string path = PimPolicyRestoreGuard.RulePath(policyId, PimPolicyScenarioTests.ExpirationRule);

            // The live values written back without @odata.type: a no-op if Graph accepts it.
            var untyped = PimPolicyRestoreGuard.RestoreBody(live);
            untyped.Remove(RoleManagementPolicyShape.ODataTypeProperty);
            try
            {
                await Graph.PatchAsync(new GraphRequest(path), untyped, Ct);
                report.Observe("a rule PATCH WITHOUT @odata.type was ACCEPTED (the provider sends it anyway; harmless)");
            }
            catch (GraphException ex)
            {
                report.Observe("a rule PATCH without @odata.type: " + ProbeReport.Describe(ex) + $" (raw error: {ex.Message})");
            }

            // The provider's body for the same (live) values: must be accepted.
            var providerBody = RoleManagementPolicyShape.ToGraph(RoleManagementPolicyShape.ToArm(live));
            report.Observe("provider body: " + providerBody.ToJsonString());
            try
            {
                await Graph.PatchAsync(new GraphRequest(path).WithHint(RetryHint.ExpectReplicationLag), providerBody, Ct);
                report.Observe("the provider's rule PATCH body (@odata.type first, ruleType dropped, nulls left out) was accepted");
            }
            catch (GraphException ex)
            {
                report.Expect(false, $"the provider's PATCH body of the live expiration rule is refused ({ProbeReport.Describe(ex)}): every policy update would fail. Fix RoleManagementPolicyShape.ToGraph in src/EArm.Providers/RoleManagementPolicyShape.cs.");
            }
        }
        finally
        {
            await guard.RestoreAsync(scope, log, CancellationToken.None);
            log.ForEach(report.Observe);
        }

        report.Expect(guard.Pending.Count == 0, "the Message Center Reader policy could not be restored after the probe (the fixture retries at teardown): " + string.Join(" | ", log));
        report.Complete();
    }

    [Fact]
    public async Task It_Probe_PP3_ApproverIsBackupAndDescriptionEchoAndCompareEqual()
    {
        Fixture.Require(PolicyNeeds);
        var report = new ProbeReport("pp3");
        var guard = Fixture.PimPolicies;
        var scope = PimPolicyRestoreGuard.MessageCenterReader;
        string approverGroupId = await NewGroupAsync("probe-ppol-appr");
        var log = new List<string>();
        try
        {
            var snapshot = await guard.SnapshotAsync(scope, Ct) ?? throw new InvalidOperationException("Graph lists no PIM policy for Message Center Reader.");
            string policyId = await PolicyIdAsync(scope);
            report.Observe("live approval rule: " + snapshot[PimPolicyScenarioTests.ApprovalRule].ToJsonString());

            // The declared rule in ARM's shape, as a template would write it (description and isBackup on the approver).
            var declared = new JsonObject
            {
                ["id"] = PimPolicyScenarioTests.ApprovalRule,
                ["ruleType"] = "RoleManagementPolicyApprovalRule",
                ["setting"] = new JsonObject
                {
                    ["isApprovalRequired"] = true,
                    ["isApprovalRequiredForExtension"] = false,
                    ["isRequestorJustificationRequired"] = true,
                    ["approvalMode"] = "SingleStage",
                    ["approvalStages"] = new JsonArray(new JsonObject
                    {
                        ["approvalStageTimeOutInDays"] = 1,
                        ["isApproverJustificationRequired"] = true,
                        ["escalationTimeInMinutes"] = 0,
                        ["isEscalationEnabled"] = false,
                        ["primaryApprovers"] = new JsonArray(new JsonObject { ["id"] = approverGroupId, ["userType"] = "Group", ["description"] = "EARM probe approvers", ["isBackup"] = false }),
                        ["escalationApprovers"] = new JsonArray(),
                    }),
                },
            };

            var liveArm = RoleManagementPolicyShape.ToArm(snapshot[PimPolicyScenarioTests.ApprovalRule]);
            var toSend = (JsonObject)declared.DeepClone();
            RoleManagementPolicyShape.FillFromLive(toSend, liveArm);
            var body = RoleManagementPolicyShape.ToGraph(RoleManagementPolicyShape.Comparable(toSend));

            // Graph's subject set with the description too (the provider never sends it; does Graph accept and echo it?).
            var withDescription = (JsonObject)body.DeepClone();
            if (withDescription["setting"]?["approvalStages"]?[0]?["primaryApprovers"]?[0] is JsonObject subject)
            {
                subject["description"] = "EARM probe approvers";
            }

            string path = PimPolicyRestoreGuard.RulePath(policyId, PimPolicyScenarioTests.ApprovalRule);
            try
            {
                await Graph.PatchAsync(new GraphRequest(path).WithHint(RetryHint.ExpectReplicationLag), withDescription, Ct);
                report.Observe("an approver subject set WITH description was accepted");
            }
            catch (GraphException ex)
            {
                report.Observe("an approver subject set with description: " + ProbeReport.Describe(ex) + " -> sending the provider's body (no description)");
                await Graph.PatchAsync(new GraphRequest(path).WithHint(RetryHint.ExpectReplicationLag), body, Ct);
            }

            var rules = await EventuallyAsync(
                async () => PimPolicyRestoreGuard.RulesOf(await guard.ReadAsync(scope, Ct)),
                found => found.GetValueOrDefault(PimPolicyScenarioTests.ApprovalRule)?["setting"]?["isApprovalRequired"]?.GetValue<bool>() == true,
                seconds: 90,
                stepSeconds: 5);
            var echoed = rules.GetValueOrDefault(PimPolicyScenarioTests.ApprovalRule);
            var approver = echoed?["setting"]?["approvalStages"]?[0]?["primaryApprovers"]?[0] as JsonObject;
            report.Observe("approval rule as read back: " + echoed?.ToJsonString());
            report.Observe($"approver echo: isBackup {(approver?.ContainsKey("isBackup") == true ? "PRESENT (" + approver["isBackup"]?.ToJsonString() + ")" : "ABSENT")}, description {(approver?.ContainsKey("description") == true ? "PRESENT (" + approver["description"]?.ToJsonString() + ")" : "ABSENT")}");

            if (echoed is not null)
            {
                // What Plan does: fill the declared rule from the live one and compare both comparable views.
                var planned = (JsonObject)declared.DeepClone();
                var echoedArm = RoleManagementPolicyShape.ToArm(echoed);
                RoleManagementPolicyShape.FillFromLive(planned, echoedArm);
                string want = AuthMethodRestoreGuard.Canonical(RoleManagementPolicyShape.Comparable(planned));
                string have = AuthMethodRestoreGuard.Canonical(echoedArm);
                report.Expect(
                    string.Equals(want, have, StringComparison.Ordinal),
                    $"the approval rule read back after the PATCH does not compare equal to the declaration (want {want}, have {have}): every rerun would show a Modify. Fix RoleManagementPolicyShape.Comparable / ApproverToArm (description, isBackup) in src/EArm.Providers/RoleManagementPolicyShape.cs.");
            }
            else
            {
                report.Expect(false, "the approval rule is missing after the PATCH; RoleManagementPolicyLookup.FindAsync / LiveView in src/EArm.Providers/RoleManagementPolicyLookup.cs.");
            }
        }
        finally
        {
            await guard.RestoreAsync(scope, log, CancellationToken.None);
            log.ForEach(report.Observe);
        }

        report.Expect(guard.Pending.Count == 0, "the Message Center Reader policy could not be restored after the probe (the fixture retries at teardown): " + string.Join(" | ", log));
        report.Complete();
    }

    [Fact]
    public async Task It_Probe_PP4_TargetCasingPerScope()
    {
        Fixture.Require(Needs.Domain | PolicyNeeds);
        var report = new ProbeReport("pp4");

        // Read-only for the directory role; the group policy is the one of a prefixed group this probe onboards itself.
        var role = await Fixture.PimPolicies.ReadAsync(PimPolicyRestoreGuard.MessageCenterReader, Ct);
        string userId = await NewUserAsync("probe-ppol-case");
        string groupId = await NewGroupAsync("probe-ppol-case-grp");
        await PimReads.SendAsync(Graph, GroupSchedulesLookup.EligibilityRequestsPath, PimReads.GroupRequest(PimScheduleRequests.AdminAssign, groupId, userId, "member", PimReads.ScheduleInfo(duration: "P7D")), Ct);
        var group = await EventuallyAsync(() => Fixture.PimPolicies.ReadAsync(PimPolicyRestoreGuard.GroupAccess(groupId, "member"), Ct), found => found is not null, seconds: 180, stepSeconds: 10);

        foreach (var (label, assignment) in new[] { ("directory role (Message Center Reader)", role), ("group member", group) })
        {
            if (assignment is null)
            {
                report.Expect(false, $"the {label} policy is not listed; the casing cannot be checked (RoleManagementPolicyLookup.FindAsync in src/EArm.Providers/RoleManagementPolicyLookup.cs).");
                continue;
            }

            report.Observe($"{label}: assignment scopeType '{assignment.Text("scopeType")}', scopeId '{assignment.Text("scopeId")}', roleDefinitionId '{assignment.Text("roleDefinitionId")}'");
            var raw = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var rule in PimPolicyRestoreGuard.RulesOf(assignment).Values)
            {
                var target = rule["target"] as JsonObject;
                raw.Add($"caller={target.Text("caller")} level={target.Text("level")} operations={target?["operations"]?.ToJsonString()}");
                var arm = RoleManagementPolicyShape.ToArm(rule)["target"] as JsonObject;
                var operations = arm.Strings("operations");
                bool canonical = operations.All(o => RoleManagementPolicyRules.Operations.Contains(o, StringComparer.Ordinal))
                    && (arm.Text("caller") is not { } caller || RoleManagementPolicyRules.Callers.Contains(caller, StringComparer.Ordinal))
                    && (arm.Text("level") is not { } level || RoleManagementPolicyRules.Levels.Contains(level, StringComparer.Ordinal));
                report.Expect(
                    canonical,
                    $"{label} rule {rule.Text("id")}: the target {target?.ToJsonString()} does not normalise to ARM's casing (got {arm?.ToJsonString()}): a declared target would always differ. Fix RoleManagementPolicyRules.NormalizeRules (src/EArm.Core/Validation/RoleManagementPolicyRules.cs) or RoleManagementPolicyShape.ToArm.");
            }

            report.Observe($"{label}: live target spellings: {string.Join(" | ", raw)}");
        }

        report.Complete();
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
                $"$expand=policy($expand=rules) does not return the rules of the Message Center Reader policy ({expanded.Count} assignment(s)); the provider reads no live rule and would report every declared rule as missing. Fix FindAsync in src/EArm.Providers/RoleManagementPolicyLookup.cs (read GET /policies/roleManagementPolicies/{{id}}?$expand=rules after the assignment).");
        }
        catch (GraphException ex)
        {
            report.Expect(false, $"$expand=policy($expand=rules) on the assignment list is refused ({ProbeReport.Describe(ex)}). Fix FindAsync in src/EArm.Providers/RoleManagementPolicyLookup.cs (read the policy with GET /policies/roleManagementPolicies/{{id}}?$expand=rules).");
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

    [Fact]
    public async Task It_Probe_PP6_MultifactorAuthenticationTogetherWithAuthenticationContext()
    {
        Fixture.Require(PolicyNeeds);
        var report = new ProbeReport("pp6");
        var guard = Fixture.PimPolicies;
        var scope = PimPolicyRestoreGuard.MessageCenterReader;

        // An existing, available authentication context (read-only); a context is never created here.
        var contexts = await Graph.ListAsync(new GraphRequest("/identity/conditionalAccess/authenticationContextClassReferences"), Ct);
        var context = contexts.FirstOrDefault(c => (bool?)c["isAvailable"] == true);
        if (context is null)
        {
            report.Observe($"the tenant has no available authentication context ({contexts.Count} listed); MFA together with an authentication context stays UNVERIFIED (publish one context, for example with EARM_IT_ALLOW_CA, and re-run)");
            report.Complete();
            return;
        }

        string claimValue = context.RequireId();
        var log = new List<string>();
        try
        {
            var snapshot = await guard.SnapshotAsync(scope, Ct) ?? throw new InvalidOperationException("Graph lists no PIM policy for Message Center Reader.");
            string policyId = await PolicyIdAsync(scope);
            report.Observe($"live: {PimPolicyScenarioTests.EnablementRule} = {snapshot.GetValueOrDefault(PimPolicyScenarioTests.EnablementRule)?.ToJsonString()}; {AuthenticationContextRule} = {snapshot.GetValueOrDefault(AuthenticationContextRule)?.ToJsonString()}");
            if (!snapshot.ContainsKey(AuthenticationContextRule))
            {
                report.Observe($"the Message Center Reader policy has no {AuthenticationContextRule} rule; nothing to probe");
                return;
            }

            // MFA first, then the authentication context: the second request is the one a conflict refuses.
            var enablement = RoleManagementPolicyShape.ToArm(snapshot[PimPolicyScenarioTests.EnablementRule]);
            var enabled = enablement.Strings("enabledRules");
            if (!enabled.Contains("MultiFactorAuthentication", StringComparer.OrdinalIgnoreCase))
            {
                enabled.Add("MultiFactorAuthentication");
            }

            enablement["enabledRules"] = new JsonArray([.. enabled.Select(r => (JsonNode?)JsonValue.Create(r))]);
            string mfa = await TryPatchAsync(policyId, enablement);
            report.Observe($"step 1, enabledRules [{string.Join(", ", enabled)}]: {mfa}");

            var authContext = RoleManagementPolicyShape.ToArm(snapshot[AuthenticationContextRule]);
            authContext["isEnabled"] = true;
            authContext["claimValue"] = claimValue;
            string both = await TryPatchAsync(policyId, authContext);
            report.Observe($"step 2, {AuthenticationContextRule} isEnabled true claimValue {claimValue} with MFA enabled: {both}");

            var after = PimPolicyRestoreGuard.RulesOf(await guard.ReadAsync(scope, Ct));
            report.Observe($"after: {PimPolicyScenarioTests.EnablementRule} = {after.GetValueOrDefault(PimPolicyScenarioTests.EnablementRule)?.ToJsonString()}; {AuthenticationContextRule} = {after.GetValueOrDefault(AuthenticationContextRule)?.ToJsonString()}");
            report.Observe("EARM's offline checks " + (OfflineAccepts(enabled, claimValue) ? "ACCEPT" : "REFUSE") + " a template declaring both (compare with step 2)");
        }
        finally
        {
            await guard.RestoreAsync(scope, log, CancellationToken.None);
            log.ForEach(report.Observe);
        }

        report.Expect(guard.Pending.Count == 0, "the Message Center Reader policy could not be restored after the probe (the fixture retries at teardown): " + string.Join(" | ", log));
        report.Complete();
    }

    // ---- helpers ----

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

    /// <summary>
    /// Reads the policy with its rules and PATCHes <paramref name="ruleId"/> back to the value it has (the restore guard's body, the provider's body when
    /// that is refused with 400); returns ACCEPTED, REFUSED or NOT TRIED with the details.
    /// </summary>
    private async Task<string> PatchRuleToItselfAsync(string policyId, string ruleId)
    {
        try
        {
            var policy = await Graph.GetObjectAsync(new GraphRequest(RoleManagementPolicyLookup.PoliciesPath + "/" + GraphPath.Segment(policyId)).WithExpand("rules"), Ct);
            var rules = policy?["rules"] as JsonArray;
            var rule = rules?.OfType<JsonObject>().FirstOrDefault(r => string.Equals(r.Text("id"), ruleId, StringComparison.OrdinalIgnoreCase));
            if (rule is null)
            {
                return $"NOT TRIED: the policy read ({(policy is null ? "not found" : (rules?.Count ?? 0) + " rules")}) has no {ruleId} rule";
            }

            string path = PimPolicyRestoreGuard.RulePath(policyId, ruleId);
            try
            {
                await Graph.PatchAsync(new GraphRequest(path), PimPolicyRestoreGuard.RestoreBody(rule), Ct);
                return "ACCEPTED (the rule as read: " + rule.ToJsonString() + ")";
            }
            catch (GraphException ex) when (ex.Status == System.Net.HttpStatusCode.BadRequest)
            {
                await Graph.PatchAsync(new GraphRequest(path), RoleManagementPolicyShape.ToGraph(RoleManagementPolicyShape.ToArm(rule)), Ct);
                return $"ACCEPTED with the provider's body (the rule as read was refused: {ProbeReport.Describe(ex)})";
            }
        }
        catch (GraphException ex)
        {
            return "REFUSED " + ProbeReport.Describe(ex);
        }
    }

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

    /// <summary>PATCHes an ARM-shaped rule with the provider's body; returns "accepted" or the error (status, code, message).</summary>
    private async Task<string> TryPatchAsync(string policyId, JsonObject armRule)
    {
        try
        {
            await RoleManagementPolicyLookup.PatchRuleAsync(Graph, policyId, armRule, GraphApi.V1, RetryHint.ExpectReplicationLag, Ct);
            return "ACCEPTED";
        }
        catch (GraphException ex)
        {
            return "REJECTED " + ProbeReport.Describe(ex);
        }
    }

    /// <summary>Whether the offline rule checks accept a Message Center Reader policy declaring MFA and an authentication context together.</summary>
    private static bool OfflineAccepts(IEnumerable<string> enabledRules, string claimValue)
    {
        var properties = new JsonObject
        {
            ["role"] = "Message Center Reader",
            ["rules"] = new JsonArray(
                new JsonObject { ["id"] = PimPolicyScenarioTests.EnablementRule, ["enabledRules"] = new JsonArray([.. enabledRules.Select(r => (JsonNode?)JsonValue.Create(r))]) },
                new JsonObject { ["id"] = AuthenticationContextRule, ["isEnabled"] = true, ["claimValue"] = claimValue }),
        };
        return RoleManagementPolicyRules.Check(properties, static _ => false).Count == 0;
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
