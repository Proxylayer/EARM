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

/// <summary>Probes PP2, PP3, PP4 and PP6 of <see cref="PimPolicyProbes"/>: rule PATCH bodies, approvers and echoes, target casing per scope, and multifactor authentication with an authentication context.</summary>
public sealed partial class PimPolicyProbes
{
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
                report.Expect(false, $"the provider's PATCH body of the live expiration rule is refused ({ProbeReport.Describe(ex)}): every policy update would fail. Fix RoleManagementPolicyShape.ToGraph.");
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
                    $"the approval rule read back after the PATCH does not compare equal to the declaration (want {want}, have {have}): every rerun would show a Modify. Fix RoleManagementPolicyShape.Comparable / ApproverToArm (description, isBackup).");
            }
            else
            {
                report.Expect(false, "the approval rule is missing after the PATCH; RoleManagementPolicyLookup.FindAsync / LiveView.");
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
                report.Expect(false, $"the {label} policy is not listed; the casing cannot be checked (RoleManagementPolicyLookup.FindAsync).");
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
                    $"{label} rule {rule.Text("id")}: the target {target?.ToJsonString()} does not normalise to ARM's casing (got {arm?.ToJsonString()}): a declared target would always differ. Fix RoleManagementPolicyRules.NormalizeRules or RoleManagementPolicyShape.ToArm.");
            }

            report.Observe($"{label}: live target spellings: {string.Join(" | ", raw)}");
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
}
