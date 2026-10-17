using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Validation;
using EArm.Engine;
using EArm.Graph;
using EArm.Providers;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>
/// PIM role management policies (<c>Microsoft.Entra/roleManagementPolicies</c>) through the CLI, under the restore guard
/// (<see cref="PimPolicyRestoreGuard"/>): needs <c>EARM_IT_ALLOW_PIM=1</c> and <c>EARM_IT_ALLOW_PIM_POLICIES=1</c>. Policies are tenant-wide and cannot be
/// deleted, so only two low-risk scopes are ever touched: the Message Center Reader directory role policy and the PIM for Groups policy of a prefixed test
/// group. Every policy is snapshotted before it changes and restored exactly in a <c>finally</c> (and again at fixture teardown). The values a test
/// deploys are derived from the snapshot so they always differ from the live ones, and never enable multifactor authentication (which may conflict with
/// an authentication context rule).
/// </summary>
[Collection(RealTenantCollection.Name)]
public sealed class PimPolicyScenarioTests : IntegrationTestBase
{
    internal const string ExpirationRule = "Expiration_EndUser_Assignment";
    internal const string EnablementRule = "Enablement_EndUser_Assignment";
    internal const string ApprovalRule = "Approval_EndUser_Assignment";

    private const Needs PolicyNeeds = Needs.Pim | Needs.PimPolicies;

    public PimPolicyScenarioTests(IntegrationFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task It_PimPolicy_DirectoryRolePolicyModifyRerunNoChangeKeptOnDeleteAndRestored()
    {
        Fixture.Require(PolicyNeeds);
        var guard = Fixture.PimPolicies;
        var scope = PimPolicyRestoreGuard.MessageCenterReader;
        string messageCenterReader = await PimReads.RoleTemplateIdAsync(Graph, "Message Center Reader", Ct);
        Assert.True(
            string.Equals(messageCenterReader, PimPolicyRestoreGuard.MessageCenterReaderTemplateId, StringComparison.OrdinalIgnoreCase),
            $"Message Center Reader has template id {messageCenterReader} in this tenant, not {PimPolicyRestoreGuard.MessageCenterReaderTemplateId}; fix PimPolicyRestoreGuard.MessageCenterReaderTemplateId (the guard refuses every other directory role).");

        string approverGroupId = (await TenantObjects.CreateGroupAsync(Fixture, "ppol-approvers", Ct)).RequireId();
        await EventuallyAsync(() => Graph.GetObjectAsync(new GraphRequest("/groups/" + approverGroupId).WithSelect("id"), Ct), found => found is not null);
        string policyName = Guid.NewGuid().ToString("D");
        var log = new List<string>();
        try
        {
            var snapshot = await guard.SnapshotAsync(scope, Ct) ?? throw new InvalidOperationException("Graph lists no PIM policy for Message Center Reader.");
            ObserveRules("snapshot of the Message Center Reader policy", snapshot, ExpirationRule, EnablementRule, ApprovalRule);
            var (duration, enabled) = ChangedValues(snapshot);
            var run = Fixture.NewRun("ppolrole", "pim-policy-role.entra.json")
                .WithParameter("policyName", policyName)
                .WithParameter("approverGroupId", approverGroupId)
                .WithParameter("maximumDuration", duration)
                .WithParameter("enabledRules", string.Join(',', enabled));

            // Never Create: the first run against an existing policy is a Modify of the declared rules.
            var preview = await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes");
            AssertChange(preview, "/" + policyName, "Modify", "what-if of the changed rules");
            AssertCreated(await Fixture.Cli.CreateAsync(run, Ct), "create with the changed rules");

            var rules = await EventuallyAsync(
                async () => PimPolicyRestoreGuard.RulesOf(await guard.ReadAsync(scope, Ct)),
                found => HasValues(found, duration, enabled) && HasApprover(found, approverGroupId),
                seconds: 120,
                stepSeconds: 5);
            ObserveRules("after create", rules, ExpirationRule, EnablementRule, ApprovalRule);
            AssertValues(rules, duration, enabled, "the Message Center Reader policy after create");
            Assert.True(HasApprover(rules, approverGroupId), $"The approval rule does not require approval by the approvers group {approverGroupId}: {rules.GetValueOrDefault(ApprovalRule)?.ToJsonString()}");

            await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "rerun after create");

            // deployment delete keeps the policy (NotDeletable) and leaves its rules as they are.
            var deleted = await Fixture.Cli.DeleteAllAsync(run, Ct);
            AssertExit(0, deleted, "deployment delete");
            AssertKeptNotDeletable(deleted, policyName);
            var afterDelete = PimPolicyRestoreGuard.RulesOf(await guard.ReadAsync(scope, Ct));
            var touched = PimPolicyRestoreGuard.Differences(rules, afterDelete);
            Assert.True(touched.Count == 0, $"deployment delete changed the kept policy's rules: {string.Join(", ", touched)}");
        }
        finally
        {
            await guard.RestoreAsync(scope, log, CancellationToken.None);
            log.ForEach(Observe);
        }

        Assert.True(guard.Pending.Count == 0, "The Message Center Reader policy could not be restored to its snapshot (the fixture retries at teardown):" + Environment.NewLine + string.Join(Environment.NewLine, log));
    }

    [Fact]
    public async Task It_PimPolicy_GroupPolicyWithSameRunEligibilityConvergesInTwoRunsAndIsKeptOnDelete()
    {
        Fixture.Require(Needs.Domain | PolicyNeeds);
        var guard = Fixture.PimPolicies;
        string policyName = Guid.NewGuid().ToString("D");
        string eligibilityName = Guid.NewGuid().ToString("D");
        var run = Fixture.NewRun("ppolgroup", "pim-policy-group.entra.json")
            .WithParameter("domain", Fixture.Domain)
            .WithParameter("policyName", policyName)
            .WithParameter("eligibilityName", eligibilityName);
        var log = new List<string>();
        PolicyScope? scope = null;
        try
        {
            // First run: the group is new and PIM has not onboarded it, so Graph lists no policy; the eligibility of the same run onboards it.
            var first = await Fixture.Cli.CreateAsync(run, Ct);
            AssertCreated(first, "first create");
            string groupId = Output(first, "groupId");
            string userId = Output(first, "userId");
            scope = PimPolicyRestoreGuard.GroupAccess(groupId, "member");
            var policyOperation = first.Operations.FirstOrDefault(o => ((string?)o["resourceId"])?.EndsWith("/" + policyName, StringComparison.OrdinalIgnoreCase) == true);
            string? status = (string?)policyOperation?["status"];
            bool warned = first.Combined.Contains(EngineDiagnosticCodes.PimPolicyNotAvailable, StringComparison.Ordinal);
            bool deferred = status == "Ignored";
            Observe($"first run: the group policy operation is {policyOperation?.ToJsonString() ?? "(not listed)"}; {(deferred ? "DEFERRED to the next run" : "APPLIED in the first run")} (PimPolicyNotAvailable in the output: {warned})");
            Assert.True(
                (deferred && warned) || status == "Succeeded",
                $"The first run neither deferred the group policy with a PimPolicyNotAvailable warning nor applied it (status '{status}', warning {warned}):{Environment.NewLine}{first.DescribeOperations()}{Environment.NewLine}{Tail(first.Combined)}");

            await EventuallyAsync(
                async () => PimReads.OfAccess(await PimReads.ByGroupAsync(Graph, GroupSchedulesLookup.EligibilitySchedulesPath, groupId, Ct), userId, "member"),
                found => found.Count == 1,
                seconds: 120,
                stepSeconds: 5);
            var onboarded = await EventuallyAsync(() => guard.ReadAsync(scope, Ct), found => found is not null, seconds: 180, stepSeconds: 10);
            Assert.True(onboarded is not null, $"{scope.Describe()} is still not listed 180 s after the eligibility onboarded the group; the second run could not apply the policy.");

            // Snapshot the onboarded policy (Microsoft's defaults, or the first run's values) before the second run changes it.
            var snapshot = await guard.SnapshotAsync(scope, Ct) ?? throw new InvalidOperationException($"{scope.Describe()} vanished right after it was listed.");
            ObserveRules("snapshot of the onboarded group policy", snapshot, ExpirationRule, EnablementRule);
            var (duration, enabled) = ChangedValues(snapshot);
            var changed = run.WithParameter("maximumDuration", duration).WithParameter("enabledRules", string.Join(',', enabled));

            var preview = await Fixture.Cli.WhatIfAsync(changed, Ct, "--exit-code-on-changes");
            AssertChange(preview, "/" + policyName, "Modify", "what-if of the second run");
            Assert.Equal("NoChange", preview.ChangeTypeOf("/" + eligibilityName));
            AssertCreated(await Fixture.Cli.CreateAsync(changed, Ct), "second create");
            var rules = await EventuallyAsync(
                async () => PimPolicyRestoreGuard.RulesOf(await guard.ReadAsync(scope, Ct)),
                found => HasValues(found, duration, enabled),
                seconds: 120,
                stepSeconds: 5);
            AssertValues(rules, duration, enabled, "the group policy after the second run");
            await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(changed, Ct, "--exit-code-on-changes"), "rerun after the second create");

            // Restore first (the policy is kept by the delete), then delete: the policy is kept (NotDeletable), the eligibility, user and group go.
            Assert.True(await guard.RestoreAsync(scope, log, Ct), "The group policy could not be restored to its snapshot:" + Environment.NewLine + string.Join(Environment.NewLine, log));
            var deleted = await Fixture.Cli.DeleteAllAsync(changed, Ct);
            AssertExit(0, deleted, "deployment delete");
            AssertKeptNotDeletable(deleted, policyName);
        }
        finally
        {
            if (scope is not null)
            {
                await guard.RestoreAsync(scope, log, CancellationToken.None);
            }

            log.ForEach(Observe);
        }

        Assert.True(guard.Pending.Count == 0, "The group policy could not be restored to its snapshot (the fixture retries at teardown):" + Environment.NewLine + string.Join(Environment.NewLine, log));
    }

    [Fact]
    public async Task It_PimPolicy_GroupPolicyWithOnboardingOptInIsAppliedInOneRunAndRemovesItsEligibility()
    {
        Fixture.Require(Needs.Domain | PolicyNeeds);
        var guard = Fixture.PimPolicies;
        string policyName = Guid.NewGuid().ToString("D");
        var run = Fixture.NewRun("ppolonb", "pim-policy-group-onboarding.entra.json")
            .WithParameter("domain", Fixture.Domain)
            .WithParameter("policyName", policyName);

        // What-if: the group does not exist yet, so the policy row is unknown until deployment; nothing is written.
        var preview = await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes");
        AssertExit(5, preview, "what-if before the first create");
        Observe($"what-if before the first create: the policy row is {preview.ChangeTypeOf("/" + policyName)}");

        // One run: the temporary eligibility onboards the group, is removed, and the rules are applied to the post-onboarding policy.
        var created = await Fixture.Cli.CreateAsync(run, Ct);
        AssertCreated(created, "create with the onboarding opt-in");
        string groupId = Output(created, "groupId");
        string userId = Output(created, "userId");
        var scope = PimPolicyRestoreGuard.GroupAccess(groupId, "member");
        Assert.False(
            created.Combined.Contains(EngineDiagnosticCodes.PimOnboardingEligibilityNotRemoved, StringComparison.Ordinal),
            $"The create left the onboarding eligibility behind:{Environment.NewLine}{Tail(created.Combined)}");

        var rules = await EventuallyAsync(
            async () => PimPolicyRestoreGuard.RulesOf(await guard.ReadAsync(scope, Ct)),
            found => HasValues(found, "PT3H", ["Justification", "Ticketing"]),
            seconds: 120,
            stepSeconds: 5);
        ObserveRules("after the onboarding create", rules, ExpirationRule, EnablementRule);
        AssertValues(rules, "PT3H", ["Justification", "Ticketing"], "the group policy after the onboarding create");

        var remaining = await EventuallyAsync(
            async () => PimReads.OfAccess(await PimReads.ByGroupAsync(Graph, GroupSchedulesLookup.EligibilitySchedulesPath, groupId, Ct), userId, "member"),
            found => found.Count == 0,
            seconds: 120,
            stepSeconds: 5);
        Assert.True(remaining.Count == 0, $"The temporary onboarding eligibility of {userId} on group {groupId} is still listed: {string.Join("; ", remaining.Select(PimReads.DescribeSchedule))}");

        // The rerun finds the onboarded group's policy: no changes, no eligibility requested again.
        await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "rerun after the onboarding create");

        // deployment delete keeps the policy (NotDeletable); the user and the group (and with it the policy) go.
        var deleted = await Fixture.Cli.DeleteAllAsync(run, Ct);
        AssertExit(0, deleted, "deployment delete");
        AssertKeptNotDeletable(deleted, policyName);
    }

    // ---- helpers (shared with PimPolicyProbes) ----

    /// <summary>
    /// A maximum activation duration and a set of enabled rules that both differ from the snapshot's (never with multifactor authentication, which can
    /// conflict with a live authentication context rule).
    /// </summary>
    internal static (string Duration, string[] EnabledRules) ChangedValues(IReadOnlyDictionary<string, JsonObject> snapshot)
    {
        string? liveDuration = snapshot.GetValueOrDefault(ExpirationRule).Text("maximumDuration");
        string duration = new[] { "PT3H", "PT2H" }.First(d => !SameDuration(d, liveDuration));
        var liveEnabled = snapshot.GetValueOrDefault(EnablementRule).Strings("enabledRules");
        string[] enabled = new[] { new[] { "Justification", "Ticketing" }, new[] { "Justification" } }.First(c => !SameSet(c, liveEnabled));
        return (duration, enabled);
    }

    internal static bool SameDuration(string expected, string? actual)
    {
        return PimScheduleRules.TryParseDuration(expected, out var left) && actual is not null && PimScheduleRules.TryParseDuration(actual, out var right) && left == right;
    }

    internal static bool SameSet(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        return new HashSet<string>(expected, StringComparer.OrdinalIgnoreCase).SetEquals(actual);
    }

    /// <summary>The rules (in the provider's ARM shape) of a policy as one line each, for observations.</summary>
    internal static void ObserveRules(string what, IReadOnlyDictionary<string, JsonObject> rules, params string[] ids)
    {
        foreach (string id in ids)
        {
            Observe($"{what}: {id} = {rules.GetValueOrDefault(id)?.ToJsonString() ?? "(missing)"}");
        }
    }

    private static bool HasValues(IReadOnlyDictionary<string, JsonObject> rules, string duration, string[] enabled)
    {
        return SameDuration(duration, rules.GetValueOrDefault(ExpirationRule).Text("maximumDuration"))
            && SameSet(enabled, rules.GetValueOrDefault(EnablementRule).Strings("enabledRules"));
    }

    private static void AssertValues(IReadOnlyDictionary<string, JsonObject> rules, string duration, string[] enabled, string what)
    {
        Assert.True(
            HasValues(rules, duration, enabled),
            $"{what}: expected maximumDuration {duration} and enabledRules [{string.Join(", ", enabled)}] but read {rules.GetValueOrDefault(ExpirationRule)?.ToJsonString()} / {rules.GetValueOrDefault(EnablementRule)?.ToJsonString()}");
    }

    private static bool HasApprover(IReadOnlyDictionary<string, JsonObject> rules, string groupId)
    {
        var setting = rules.GetValueOrDefault(ApprovalRule)?["setting"];
        return (bool?)setting?["isApprovalRequired"] == true
            && setting?["approvalStages"] is JsonArray stages
            && stages.OfType<JsonObject>().Any(s => s["primaryApprovers"] is JsonArray approvers
                && approvers.OfType<JsonObject>().Any(a => string.Equals(a.Text("groupId"), groupId, StringComparison.OrdinalIgnoreCase)));
    }

    private static void AssertKeptNotDeletable(CliResult deleted, string name)
    {
        var outcome = deleted.Json["outcomes"]?.AsArray().OfType<JsonObject>()
            .FirstOrDefault(o => ((string?)o["resourceId"])?.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase) == true);
        Assert.True(
            outcome is not null && (string?)outcome["status"] == "Refused" && (string?)outcome["errorCode"] == EngineDiagnosticCodes.NotDeletable,
            $"deployment delete did not keep the policy as NotDeletable: {outcome?.ToJsonString() ?? "(no outcome for it)"}{Environment.NewLine}{Tail(deleted.Combined)}");
        Assert.True(deleted.Json["kept"]?.GetValue<int>() >= 1, $"deployment delete reports no kept object: {Tail(deleted.StandardOutput)}");
    }

    private static string Output(CliResult created, string name)
    {
        return (string?)created.Json["outputs"]?[name]?["value"] ?? throw new InvalidOperationException($"No '{name}' output: {Tail(created.StandardOutput)}");
    }
}
