using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>Probes AZ3, AZ4 and AZ6 of <see cref="AzurePimPolicyProbes"/>: the rules PATCH of the configured policy (partial array, partial rule body, unchanged rule), each under the restore guard.</summary>
public sealed partial class AzurePimPolicyProbes
{
    [Fact]
    public async Task It_Probe_AZ3_PartialRulesPatchKeepsTheOtherRules()
    {
        Fixture.Require(AzureNeeds);
        var report = new ProbeReport("az3");
        var guard = Guard;
        var log = new List<string>();
        try
        {
            var snapshot = await guard.SnapshotAsync(Ct);
            var live = RuleOf(snapshot, ExpirationRule);
            string? from = live.Text("maximumDuration");
            string to = AlternateDuration(from);
            var changed = (JsonObject)live.DeepClone();
            changed["maximumDuration"] = to;

            int mark = Responses.Count;
            JsonObject? answer = null;
            bool accepted = true;
            try
            {
                answer = await guard.PatchRulesAsync([changed], Ct);
            }
            catch (GraphException ex)
            {
                accepted = false;
                report.Observe($"PATCH with only {ExpirationRule}: {DescribeResponses(mark)}");
                report.Expect(false, $"a rules PATCH naming only {ExpirationRule} is refused ({ProbeReport.Describe(ex)}): the provider must PATCH the complete rules array (the live rules with the declared ones merged in). Fix {ProviderFile}.");
            }

            if (accepted)
            {
                await CheckPartialPatchAsync(report, snapshot, answer, mark, from, to);
            }
        }
        finally
        {
            await guard.RestoreAsync(log, CancellationToken.None);
            log.ForEach(report.Observe);
        }

        report.Expect(guard.Pending.Count == 0, "the Azure PIM policy could not be restored after the probe (the fixture retries at teardown): " + string.Join(" | ", log));
        report.Complete();
    }

    /// <summary>AZ3 after an accepted PATCH: the changed rule reads back, and no other rule changed.</summary>
    private async Task CheckPartialPatchAsync(ProbeReport report, IReadOnlyDictionary<string, JsonObject> snapshot, JsonObject? answer, int mark, string? from, string to)
    {
        var guard = Guard;
        report.Observe($"PATCH properties.rules with only {ExpirationRule} (maximumDuration {from} -> {to}): {DescribeResponses(mark)}; the answer is {DescribeAnswer(answer, snapshot)}");

        var after = await EventuallyAsync(() => guard.ReadAsync(Ct), state => Duration(state, ExpirationRule) == to, seconds: 60);
        var afterRules = AzurePimPolicyRestoreGuard.RulesOf(after?.Policy);
        report.Observe($"re-read: {afterRules.Count} rules (snapshot {snapshot.Count}); {ExpirationRule}.maximumDuration {Duration(after, ExpirationRule) ?? "(absent)"}; rule ids added {Describe(afterRules.Keys.Except(snapshot.Keys, StringComparer.OrdinalIgnoreCase))}, missing {Describe(snapshot.Keys.Except(afterRules.Keys, StringComparer.OrdinalIgnoreCase))}");
        report.Expect(Duration(after, ExpirationRule) == to, $"the PATCH of {ExpirationRule} was accepted but maximumDuration reads back as {Duration(after, ExpirationRule) ?? "(absent)"}, not {to}, within 60 s; the provider cannot confirm its write. Fix {ProviderFile} (re-read and compare after the PATCH).");

        var others = AzurePimPolicyRestoreGuard.Differences(Without(snapshot, ExpirationRule), afterRules);
        foreach (string ruleId in others)
        {
            report.Observe($"  CHANGED by the partial PATCH: {ruleId}: was {snapshot[ruleId].ToJsonString()}, now {(afterRules.TryGetValue(ruleId, out var now) ? now.ToJsonString() : "(absent)")}");
        }

        report.Expect(
            others.Count == 0,
            $"a rules PATCH naming one rule changed or reset {others.Count} rule(s) it did not name ({string.Join(", ", others)}): the provider must PATCH the complete rules array (the live rules with the declared ones merged in), never only the declared rules. Fix {ProviderFile}.");
    }

    [Fact]
    public async Task It_Probe_AZ4_PartialRuleBodyIsRecorded()
    {
        Fixture.Require(AzureNeeds);
        var report = new ProbeReport("az4");
        var guard = Guard;
        var log = new List<string>();
        try
        {
            var snapshot = await guard.SnapshotAsync(Ct);
            var live = RuleOf(snapshot, ExpirationRule);
            string? from = live.Text("maximumDuration");
            string to = AlternateDuration(from);

            // Only the id, the type and the one changed value: no target, no isExpirationRequired.
            var partial = new JsonObject
            {
                ["id"] = ExpirationRule,
                ["ruleType"] = live.Text("ruleType"),
                ["maximumDuration"] = to,
            };

            int mark = Responses.Count;
            bool accepted = true;
            try
            {
                var answer = await guard.PatchRulesAsync([partial], Ct);
                report.Observe($"PATCH of {ExpirationRule} with only id, ruleType and maximumDuration ({from} -> {to}): ACCEPTED, {DescribeResponses(mark)}; the answer is {DescribeAnswer(answer, snapshot)}");
            }
            catch (GraphException ex)
            {
                accepted = false;
                report.Observe($"PATCH of {ExpirationRule} with only id, ruleType and maximumDuration: REFUSED {ProbeReport.Describe(ex)}, {DescribeResponses(mark)} (the provider must send every member of a rule)");
            }

            if (accepted)
            {
                await RecordPartialRuleAsync(report, snapshot, live, to);
            }
        }
        finally
        {
            await guard.RestoreAsync(log, CancellationToken.None);
            log.ForEach(report.Observe);
        }

        report.Expect(guard.Pending.Count == 0, "the Azure PIM policy could not be restored after the probe (the fixture retries at teardown): " + string.Join(" | ", log));
        report.Complete();
    }

    /// <summary>AZ4 after an accepted PATCH: which members of the rule came back changed or defaulted, and whether any other rule changed.</summary>
    private async Task RecordPartialRuleAsync(ProbeReport report, IReadOnlyDictionary<string, JsonObject> snapshot, JsonObject live, string to)
    {
        var guard = Guard;
        var after = await EventuallyAsync(() => guard.ReadAsync(Ct), state => Duration(state, ExpirationRule) == to, seconds: 60);
        var afterRules = AzurePimPolicyRestoreGuard.RulesOf(after?.Policy);
        if (!afterRules.TryGetValue(ExpirationRule, out var rule))
        {
            report.Observe($"{ExpirationRule} is MISSING from the policy after the partial-body PATCH");
        }
        else
        {
            foreach (string member in live.Select(p => p.Key).Union(rule.Select(p => p.Key), StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                string before = live[member]?.ToJsonString() ?? "(absent)";
                string now = rule[member]?.ToJsonString() ?? "(absent)";
                report.Observe($"  {ExpirationRule}.{member}: {(string.Equals(AuthMethodRestoreGuard.Canonical(live[member]), AuthMethodRestoreGuard.Canonical(rule[member]), StringComparison.Ordinal) ? "unchanged" : "CHANGED")} ({before} -> {now})");
            }
        }

        report.Observe($"other rules changed by the partial-body PATCH: {Describe(AzurePimPolicyRestoreGuard.Differences(Without(snapshot, ExpirationRule), afterRules))}");
    }

    [Fact]
    public async Task It_Probe_AZ6_UnchangedRulePatchIsRecorded()
    {
        Fixture.Require(AzureNeeds);
        var report = new ProbeReport("az6");
        var guard = Guard;
        var log = new List<string>();
        try
        {
            var snapshot = await guard.SnapshotAsync(Ct);
            var before = await guard.ReadAsync(Ct) ?? throw new InvalidOperationException($"Azure Resource Manager lists no policy for {Target.Describe()}.");
            var live = RuleOf(snapshot, ExpirationRule);

            int mark = Responses.Count;
            try
            {
                var answer = await guard.PatchRulesAsync([(JsonObject)live.DeepClone()], Ct);
                report.Observe($"PATCH of {ExpirationRule} with its live value: {DescribeResponses(mark)}; the answer is {DescribeAnswer(answer, snapshot)}");
            }
            catch (GraphException ex)
            {
                report.Observe($"PATCH of {ExpirationRule} with its live value: REFUSED {ProbeReport.Describe(ex)}, {DescribeResponses(mark)}");
            }

            var after = await guard.ReadAsync(Ct);
            report.Observe($"policy lastModifiedDateTime {LastModified(before.Policy)} -> {LastModified(after?.Policy)}; assignment policy lastModifiedDateTime {AssignmentLastModified(before.Assignment)} -> {AssignmentLastModified(after?.Assignment)} ({(LastModified(before.Policy) == LastModified(after?.Policy) ? "unchanged" : "CHANGED: a no-op PATCH is a visible write, so NoChange must be decided client-side")})");
            var differing = AzurePimPolicyRestoreGuard.Differences(snapshot, AzurePimPolicyRestoreGuard.RulesOf(after?.Policy));
            report.Expect(
                differing.Count == 0,
                $"PATCHing {ExpirationRule} with its own live value changed {differing.Count} rule(s) ({string.Join(", ", differing)}); a rerun with no declared change would not be a no-op. Fix {ProviderFile} (decide NoChange before sending, and PATCH the complete rules array).");
        }
        finally
        {
            await guard.RestoreAsync(log, CancellationToken.None);
            log.ForEach(report.Observe);
        }

        report.Expect(guard.Pending.Count == 0, "the Azure PIM policy could not be restored after the probe (the fixture retries at teardown): " + string.Join(" | ", log));
        report.Complete();
    }

    // ---- helpers of the writing probes ----

    /// <summary>
    /// A maximum activation duration that differs from <paramref name="live"/> and is valid for <see cref="ExpirationRule"/> (an activation lasts at most
    /// 24 hours, so a duration in days would be refused): <c>PT4H</c>, or <c>PT3H</c> when the live value already is <c>PT4H</c>.
    /// </summary>
    private static string AlternateDuration(string? live)
    {
        return string.Equals(live, "PT4H", StringComparison.OrdinalIgnoreCase) ? "PT3H" : "PT4H";
    }

    private JsonObject RuleOf(IReadOnlyDictionary<string, JsonObject> rules, string ruleId)
    {
        return rules.TryGetValue(ruleId, out var rule)
            ? rule
            : throw new InvalidOperationException($"{Target.Describe()} has no rule '{ruleId}' (rules: {string.Join(", ", rules.Keys)}).");
    }

    private static string? Duration(AzurePolicyState? state, string ruleId)
    {
        return AzurePimPolicyRestoreGuard.RulesOf(state?.Policy).TryGetValue(ruleId, out var rule) ? rule.Text("maximumDuration") : null;
    }

    private static Dictionary<string, JsonObject> Without(IReadOnlyDictionary<string, JsonObject> rules, string ruleId)
    {
        return rules.Where(p => !string.Equals(p.Key, ruleId, StringComparison.OrdinalIgnoreCase)).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Whether a PATCH answer is the whole policy (every snapshotted rule id present), part of it, or empty.</summary>
    private static string DescribeAnswer(JsonObject? answer, IReadOnlyDictionary<string, JsonObject> snapshot)
    {
        if (answer is null)
        {
            return "an empty body";
        }

        var rules = AzurePimPolicyRestoreGuard.RulesOf(answer);
        if (rules.Count == 0)
        {
            return $"a body without properties.rules (members: {string.Join(", ", answer.Select(p => p.Key))})";
        }

        return snapshot.Keys.All(rules.ContainsKey)
            ? $"the full policy ({rules.Count} rules)"
            : $"a PARTIAL policy ({rules.Count} rules; missing {string.Join(", ", snapshot.Keys.Where(k => !rules.ContainsKey(k)))})";
    }

    private static string LastModified(JsonObject? policy)
    {
        return policy?["properties"].Text("lastModifiedDateTime") ?? "(absent)";
    }

    private static string AssignmentLastModified(JsonObject? assignment)
    {
        return assignment?["properties"]?["policyAssignmentProperties"]?["policy"].Text("lastModifiedDateTime") ?? "(absent)";
    }

    private static string Describe(IEnumerable<string> ids)
    {
        var list = ids.ToList();
        return list.Count == 0 ? "none" : string.Join(", ", list);
    }
}
