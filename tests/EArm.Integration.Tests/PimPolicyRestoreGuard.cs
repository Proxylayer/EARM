using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Validation;
using EArm.Graph;
using EArm.Providers;

namespace EArm.Integration.Tests;

/// <summary>
/// The restore guarantee of the PIM policy scenarios and probes (<c>EARM_IT_ALLOW_PIM_POLICIES=1</c>). A role management policy is tenant-wide and cannot be
/// deleted, so every test snapshots the live rules of a policy BEFORE it changes anything (<see cref="SnapshotAsync"/>) and restores them in a
/// <c>finally</c> (<see cref="RestoreAsync"/>); the fixture restores whatever is still pending at teardown (<see cref="RestoreAllAsync"/>), before the
/// prefix sweep removes the approver groups the changed rules may name. The first snapshot of a policy wins until it is restored. Each snapshot is also
/// written to <c>%TEMP%\earm-it\&lt;prefix&gt;\pim-policies\&lt;scopeType&gt;-&lt;scopeId&gt;-&lt;roleDefinitionId&gt;.json</c> (deleted once restored), for a
/// manual restore after a crash.
/// <para>
/// Only low-risk scopes are accepted: the directory role policy of Message Center Reader (<see cref="MessageCenterReaderTemplateId"/>) and the PIM for
/// Groups policies of groups whose displayName starts with the run prefix. Any other scope (Global Administrator, Privileged Role Administrator, ...) is
/// refused before a single request is sent.
/// </para>
/// <para>
/// A restore PATCHes (<c>/policies/roleManagementPolicies/{id}/rules/{ruleId}</c>) every snapshotted rule whose live value differs, with the snapshot's own
/// Graph body (OData annotations other than <c>@odata.type</c>, the read-only approver <c>description</c> and null members dropped; if Graph refuses that
/// with 400, the provider's body for the same rule), then re-reads the policy and compares every snapshotted rule in the provider's comparable shape
/// (<see cref="RoleManagementPolicyShape.ToArm"/>, arrays as sets). A rule that still differs is reported and keeps the snapshot pending.
/// </para>
/// </summary>
internal sealed class PimPolicyRestoreGuard
{
    /// <summary>The template id of the built-in Message Center Reader role: the only directory role whose policy the tests may change.</summary>
    internal const string MessageCenterReaderTemplateId = "790c1fb9-7f7d-4f88-86a1-ef1f95c05c1b";

    /// <summary>Global Administrator and Privileged Role Administrator: never touched, whatever a test asks (named explicitly in the refusal).</summary>
    private static readonly string[] NeverTouched = ["62e90394-69f5-4237-9190-012177145e10", "e8611ab8-c189-46e8-94e1-60213ab1f814"];

    private static readonly string[] ApproverArrays = ["primaryApprovers", "escalationApprovers"];

    private readonly IGraphClient _graph;
    private readonly string _prefix;
    private readonly string _directory;
    private readonly ConcurrentDictionary<string, Snapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);

    public PimPolicyRestoreGuard(IGraphClient graph, string prefix, string runDirectory)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(runDirectory);
        _graph = graph;
        _prefix = prefix;
        _directory = Path.Combine(runDirectory, "pim-policies");
    }

    /// <summary>The policies (scope keys) whose snapshot has not been restored yet.</summary>
    public IReadOnlyList<string> Pending => [.. _snapshots.Keys.Order(StringComparer.Ordinal)];

    /// <summary>The policy scope of the Message Center Reader directory role.</summary>
    public static PolicyScope MessageCenterReader => new(RoleManagementPolicyRules.DirectoryRoleScope, RoleManagementPolicyRules.RootScopeId, MessageCenterReaderTemplateId);

    /// <summary>The policy scope of a group's member or owner access.</summary>
    public static PolicyScope GroupAccess(string groupId, string accessId)
    {
        return new PolicyScope(RoleManagementPolicyRules.GroupScope, groupId.ToLowerInvariant(), accessId);
    }

    /// <summary>The policy assignment of <paramref name="scope"/> with its policy and rules expanded, or null when Graph lists none.</summary>
    public Task<JsonObject?> ReadAsync(PolicyScope scope, CancellationToken cancellationToken)
    {
        return RoleManagementPolicyLookup.FindAsync(_graph, scope, GraphApi.V1, RetryHint.Default, cancellationToken);
    }

    /// <summary>The live Graph rules of a policy assignment by rule id (from <c>policy.rules</c>).</summary>
    public static Dictionary<string, JsonObject> RulesOf(JsonObject? assignment)
    {
        var rules = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        if (assignment?["policy"]?["rules"] is JsonArray array)
        {
            foreach (var rule in array.OfType<JsonObject>())
            {
                if (rule.Text("id") is { } id)
                {
                    rules[id] = rule;
                }
            }
        }

        return rules;
    }

    /// <summary>
    /// Checks the scope is one the tests may change, reads the live policy and keeps its rules as the snapshot (unless one is already pending). Returns the
    /// pending snapshot's rules by id (copies), or null when Graph lists no policy for the scope (a group PIM has not onboarded yet: nothing to restore).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, JsonObject>?> SnapshotAsync(PolicyScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await EnsureAllowedAsync(scope, cancellationToken).ConfigureAwait(false);
        string key = KeyOf(scope);
        if (!_snapshots.TryGetValue(key, out var snapshot))
        {
            var live = await ReadAsync(scope, cancellationToken).ConfigureAwait(false);
            if (live is null)
            {
                return null;
            }

            var rules = RulesOf(live);
            if (rules.Count == 0)
            {
                throw new InvalidOperationException($"{scope.Describe()} was read without rules ($expand=policy($expand=rules)); nothing can be snapshotted, so the test must not change it.");
            }

            snapshot = _snapshots.GetOrAdd(key, new Snapshot(scope, RoleManagementPolicyLookup.PolicyId(live), rules.ToDictionary(p => p.Key, p => (JsonObject)p.Value.DeepClone(), StringComparer.OrdinalIgnoreCase)));
            Persist(key, snapshot);
        }

        return snapshot.Rules.ToDictionary(p => p.Key, p => (JsonObject)p.Value.DeepClone(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Restores one policy's pending snapshot (a no-op when none is pending). Returns true when every snapshotted rule equals the snapshot afterwards (or the
    /// policy went away with its group); the lines of <paramref name="log"/> say what was sent and what still differs.
    /// </summary>
    public async Task<bool> RestoreAsync(PolicyScope scope, IList<string> log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(log);
        string key = KeyOf(scope);
        if (!_snapshots.TryGetValue(key, out var snapshot))
        {
            return true;
        }

        try
        {
            var live = await ReadAsync(scope, cancellationToken).ConfigureAwait(false);
            if (live is null)
            {
                // A group's policy is listed only while the group exists: deleting a prefixed group takes its policies with it.
                Forget(key, log, $"pim policies: {scope.Describe()} is no longer listed (its group is gone); nothing to restore");
                return true;
            }

            string policyId = RoleManagementPolicyLookup.PolicyId(live) ?? throw new InvalidOperationException($"{scope.Describe()} was read without a policy id.");
            var differing = Differences(snapshot.Rules, RulesOf(live));
            foreach (string ruleId in differing)
            {
                await PatchBackAsync(policyId, snapshot.Rules[ruleId], log, scope, cancellationToken).ConfigureAwait(false);
            }

            // Rule changes are eventually consistent: re-read until the snapshot is visible or the time is up.
            var deadline = DateTime.UtcNow.AddSeconds(60);
            List<string> remaining;
            while (true)
            {
                var after = await ReadAsync(scope, cancellationToken).ConfigureAwait(false);
                remaining = after is null ? ["(policy not listed)"] : Differences(snapshot.Rules, RulesOf(after));
                if (remaining.Count == 0 || DateTime.UtcNow >= deadline)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }

            if (remaining.Count > 0)
            {
                log.Add($"pim policies: {scope.Describe()} still differs from the snapshot after the restore ({string.Join(", ", remaining)}); snapshot kept at {FileOf(key)}");
                return false;
            }

            Forget(key, log, differing.Count == 0
                ? $"pim policies: {scope.Describe()} already equals the snapshot; nothing to restore"
                : $"pim policies: verified {scope.Describe()} equals the snapshot (restored {string.Join(", ", differing)})");
            return true;
        }
        catch (GraphException ex)
        {
            log.Add($"pim policies: restoring {scope.Describe()} failed: {ProbeReport.Describe(ex)}; snapshot kept at {FileOf(key)}");
            return false;
        }
    }

    /// <summary>Restores every pending snapshot; returns one leftover line per policy that could not be restored.</summary>
    public async Task<IReadOnlyList<string>> RestoreAllAsync(IList<string> log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(log);
        var leftovers = new List<string>();
        foreach (var snapshot in _snapshots.Values.OrderBy(s => KeyOf(s.Scope), StringComparer.Ordinal).ToList())
        {
            if (!await RestoreAsync(snapshot.Scope, log, cancellationToken).ConfigureAwait(false))
            {
                leftovers.Add($"{snapshot.Scope.Describe()} could not be restored to its snapshot; PATCH each rule back from {FileOf(KeyOf(snapshot.Scope))} (PATCH /policies/roleManagementPolicies/{{policyId}}/rules/{{ruleId}}, keep @odata.type)");
            }
        }

        return leftovers;
    }

    /// <summary>The ids of the snapshot rules whose live rule differs in the provider's comparable shape (a rule the live policy lacks counts as differing).</summary>
    public static List<string> Differences(IReadOnlyDictionary<string, JsonObject> snapshot, IReadOnlyDictionary<string, JsonObject> live)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(live);
        return snapshot
            .Where(p => !live.TryGetValue(p.Key, out var current) || !string.Equals(Comparable(p.Value), Comparable(current), StringComparison.Ordinal))
            .Select(p => p.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The canonical text of a Graph rule in the provider's comparable ARM shape (casing normalised, approvers sorted, arrays as sets).</summary>
    public static string Comparable(JsonObject graphRule)
    {
        return AuthMethodRestoreGuard.Canonical(RoleManagementPolicyShape.ToArm(graphRule));
    }

    /// <summary>
    /// The PATCH body that writes a snapshotted Graph rule back as it was read: OData annotations other than <c>@odata.type</c> removed, the read-only
    /// approver <c>description</c> removed, null members left out (an empty array is kept: it is a value).
    /// </summary>
    public static JsonObject RestoreBody(JsonObject graphRule)
    {
        ArgumentNullException.ThrowIfNull(graphRule);
        var body = (JsonObject)AuthMethodRestoreGuard.StripAnnotations(graphRule)!;
        if (body["setting"]?["approvalStages"] is JsonArray stages)
        {
            foreach (var stage in stages.OfType<JsonObject>())
            {
                foreach (string name in ApproverArrays)
                {
                    if (stage[name] is JsonArray approvers)
                    {
                        foreach (var approver in approvers.OfType<JsonObject>())
                        {
                            approver.Remove("description");
                        }
                    }
                }
            }
        }

        WithoutNulls(body);
        return body;
    }

    /// <summary>The entity path of one rule of a policy.</summary>
    public static string RulePath(string policyId, string ruleId)
    {
        return RoleManagementPolicyLookup.PoliciesPath + "/" + GraphPath.Segment(policyId) + "/rules/" + GraphPath.Segment(ruleId);
    }

    private async Task PatchBackAsync(string policyId, JsonObject snapshotRule, IList<string> log, PolicyScope scope, CancellationToken cancellationToken)
    {
        string ruleId = snapshotRule.RequireId();
        var request = new GraphRequest(RulePath(policyId, ruleId)).WithHint(RetryHint.ExpectReplicationLag);
        try
        {
            await _graph.PatchAsync(request, RestoreBody(snapshotRule), cancellationToken).ConfigureAwait(false);
            log.Add($"pim policies: restored rule {ruleId} of {scope.Describe()} from the snapshot");
        }
        catch (GraphException ex) when (ex.Status == System.Net.HttpStatusCode.BadRequest)
        {
            // The rule as Graph read it was refused: send what the provider sends for the same values (the shape the scenario PATCHes succeeded with).
            log.Add($"pim policies: PATCH of the snapshot rule {ruleId} as read was refused ({ProbeReport.Describe(ex)}); retrying with the provider's body");
            await _graph.PatchAsync(request, RoleManagementPolicyShape.ToGraph(RoleManagementPolicyShape.ToArm(snapshotRule)), cancellationToken).ConfigureAwait(false);
            log.Add($"pim policies: restored rule {ruleId} of {scope.Describe()} from the snapshot (provider body)");
        }
    }

    private async Task EnsureAllowedAsync(PolicyScope scope, CancellationToken cancellationToken)
    {
        if (scope.IsGroup)
        {
            var group = await _graph.GetObjectAsync(new GraphRequest("/groups/" + GraphPath.Segment(scope.ScopeId)).WithSelect("id", "displayName"), cancellationToken).ConfigureAwait(false);
            string? name = group.Text("displayName");
            if (name is null || !name.StartsWith(_prefix + "-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Refusing to touch {scope.Describe()}: only the policies of groups named '{_prefix}-…' (this run's prefix) may be changed, and this group is '{name ?? "(not found)"}'.");
            }

            return;
        }

        if (NeverTouched.Contains(scope.RoleDefinitionId, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to touch {scope.Describe()}: the Global Administrator and Privileged Role Administrator policies are never changed by the tests.");
        }

        if (!string.Equals(scope.ScopeType, RoleManagementPolicyRules.DirectoryRoleScope, StringComparison.Ordinal)
            || !string.Equals(scope.ScopeId, RoleManagementPolicyRules.RootScopeId, StringComparison.Ordinal)
            || !string.Equals(scope.RoleDefinitionId, MessageCenterReaderTemplateId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to touch {scope.Describe()}: the only directory role policy the tests may change is Message Center Reader's ({MessageCenterReaderTemplateId}, scope '/').");
        }
    }

    private void Forget(string key, IList<string> log, string line)
    {
        _snapshots.TryRemove(key, out _);
        TryDelete(key);
        log.Add(line);
    }

    private static string KeyOf(PolicyScope scope)
    {
        return $"{scope.ScopeType}-{(scope.ScopeId == RoleManagementPolicyRules.RootScopeId ? "root" : scope.ScopeId)}-{scope.RoleDefinitionId}".ToLowerInvariant();
    }

    private string FileOf(string key)
    {
        return Path.Combine(_directory, key + ".json");
    }

    private void Persist(string key, Snapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var document = new JsonObject
            {
                ["scopeType"] = snapshot.Scope.ScopeType,
                ["scopeId"] = snapshot.Scope.ScopeId,
                ["roleDefinitionId"] = snapshot.Scope.RoleDefinitionId,
                ["policyId"] = snapshot.PolicyId,
                ["rules"] = new JsonArray([.. snapshot.Rules.Values.Select(r => (JsonNode?)r.DeepClone())]),
            };
            File.WriteAllText(FileOf(key), document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }
        catch (IOException)
        {
            // The in-memory snapshot still drives the restore; the file only helps after a crash.
        }
    }

    private void TryDelete(string key)
    {
        try
        {
            File.Delete(FileOf(key));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void WithoutNulls(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (string key in obj.Where(p => p.Value is null).Select(p => p.Key).ToList())
                {
                    obj.Remove(key);
                }

                foreach (var child in obj.Select(p => p.Value).ToList())
                {
                    WithoutNulls(child);
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    WithoutNulls(child);
                }

                break;
        }
    }

    private sealed record Snapshot(PolicyScope Scope, string? PolicyId, IReadOnlyDictionary<string, JsonObject> Rules);
}
