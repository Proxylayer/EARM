using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;

namespace EArm.Integration.Tests;

/// <summary>The policy assignment of the target role at the target scope and the policy it names, as Azure Resource Manager read them.</summary>
/// <param name="Assignment">The <c>roleManagementPolicyAssignments</c> item.</param>
/// <param name="PolicyPath">The policy's resource path, rebuilt from the configured scope and the policy name (never taken from a response as is).</param>
/// <param name="Policy">The <c>roleManagementPolicies</c> resource.</param>
internal sealed record AzurePolicyState(JsonObject Assignment, string PolicyPath, JsonObject Policy);

/// <summary>
/// The restore guarantee of the Azure PIM policy probes (<c>EARM_IT_ALLOW_AZURE_PIM_POLICIES=1</c>), modelled on <see cref="PimPolicyRestoreGuard"/>. An
/// Azure role management policy exists for every role at every scope and cannot be deleted, so a probe snapshots the live rules BEFORE its first write
/// (<see cref="SnapshotAsync"/>; <see cref="PatchRulesAsync"/> refuses to write without one) and restores them in a <c>finally</c>
/// (<see cref="RestoreAsync"/>); the fixture restores whatever is still pending at teardown (<see cref="RestoreAllAsync"/>). The first snapshot wins
/// until it is restored. It is also written owner-only to <c>%TEMP%\earm-it\&lt;prefix&gt;\azure-pim-policies\&lt;role&gt;.json</c> (deleted once restored),
/// for a manual restore after a crash.
/// <para>
/// Only the configured target is ever touched (<see cref="AzurePimPolicyTarget"/>: one role, Reader by default, at one resource group; the roles that
/// grant access management are refused when the settings are read). A policy assignment or policy whose scope or role is not exactly the target's, for
/// example a policy inherited from the subscription, is refused before a write is sent.
/// </para>
/// <para>
/// A restore PATCHes <c>properties.rules</c> with the whole snapshot array (the rules exactly as ARM returned them) when any rule differs; if ARM refuses
/// that with 400, each differing rule is PATCHed alone with its null members left out. It then re-reads until every snapshotted rule equals the snapshot
/// (arrays compared as sets; up to 60 s). A rule that still differs is reported and keeps the snapshot pending.
/// </para>
/// </summary>
internal sealed class AzurePimPolicyRestoreGuard
{
    private readonly ArmClient _arm;
    private readonly AzurePimPolicyTarget _target;
    private readonly string _directory;
    private readonly object _gate = new();
    private Snapshot? _snapshot;

    public AzurePimPolicyRestoreGuard(ArmClient arm, AzurePimPolicyTarget target, string runDirectory)
    {
        ArgumentNullException.ThrowIfNull(arm);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(runDirectory);
        _arm = arm;
        _target = target;
        _directory = Path.Combine(runDirectory, "azure-pim-policies");
    }

    /// <summary>The one policy this guard may touch.</summary>
    public AzurePimPolicyTarget Target => _target;

    /// <summary>The policies whose snapshot has not been restored yet (the target's description, or nothing).</summary>
    public IReadOnlyList<string> Pending
    {
        get
        {
            lock (_gate)
            {
                return _snapshot is null ? [] : [_target.Describe()];
            }
        }
    }

    /// <summary>The path of the snapshot file (it exists while a snapshot is pending, unless it could not be written).</summary>
    public string SnapshotFile => Path.Combine(_directory, _target.RoleDefinitionId + ".json");

    /// <summary>
    /// The target's policy assignment (filtered by the subscription-qualified role definition id, the matching item at exactly the target scope) and its
    /// policy, or null when ARM lists no assignment for the role at the scope.
    /// </summary>
    /// <exception cref="InvalidOperationException">The assignment names a policy outside the target scope (an inherited policy is never written).</exception>
    public async Task<AzurePolicyState?> ReadAsync(CancellationToken cancellationToken)
    {
        JsonObject? assignment = null;
        await foreach (var item in _arm.ListAsync(_target.PolicyAssignmentsPath, AzurePimPolicyTarget.PimApiVersion, RoleFilter(_target.SubscriptionRoleDefinitionId), cancellationToken).ConfigureAwait(false))
        {
            if (IsTargetAssignment(item, _target))
            {
                assignment = item;
                break;
            }
        }

        if (assignment is null)
        {
            return null;
        }

        string policyPath = PolicyPathOf(assignment, _target);
        var policy = await _arm.GetAsync(policyPath, AzurePimPolicyTarget.PimApiVersion, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"GET {policyPath} answered with an empty body.");
        EnsurePolicyIsTarget(policy, policyPath);
        return new AzurePolicyState(assignment, policyPath, policy);
    }

    /// <summary>
    /// Reads the live policy and keeps its rules as the snapshot (unless one is already pending). Returns the pending snapshot's rules by id (copies).
    /// </summary>
    /// <exception cref="InvalidOperationException">ARM lists no policy for the target, or the policy carries no rules (nothing to restore, so no probe may write).</exception>
    public async Task<IReadOnlyDictionary<string, JsonObject>> SnapshotAsync(CancellationToken cancellationToken)
    {
        Snapshot? pending;
        lock (_gate)
        {
            pending = _snapshot;
        }

        if (pending is null)
        {
            var live = await ReadAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Azure Resource Manager lists no policy assignment for {_target.Describe()}; there is nothing to snapshot, so no probe may write.");
            var rules = RulesOf(live.Policy);
            if (rules.Count == 0)
            {
                throw new InvalidOperationException($"{_target.Describe()} was read without rules; nothing can be snapshotted, so no probe may write.");
            }

            var candidate = new Snapshot(live.PolicyPath, live.Policy["properties"]?["rules"]?.DeepClone() as JsonArray ?? [], rules);
            lock (_gate)
            {
                _snapshot ??= candidate;
                pending = _snapshot;
            }

            Persist(pending);
        }

        return Copy(pending.Rules);
    }

    /// <summary>
    /// PATCHes <c>properties.rules</c> of the target policy with <paramref name="rules"/> and returns ARM's answer. Refused unless a snapshot is pending,
    /// so nothing is written that the guard could not put back.
    /// </summary>
    public Task<JsonObject?> PatchRulesAsync(JsonArray rules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rules);
        Snapshot snapshot;
        lock (_gate)
        {
            snapshot = _snapshot ?? throw new InvalidOperationException($"Refusing to write {_target.Describe()}: take the snapshot first (SnapshotAsync).");
        }

        return _arm.PatchAsync(snapshot.PolicyPath, AzurePimPolicyTarget.PimApiVersion, RulesBody(rules), cancellationToken);
    }

    /// <summary>
    /// Restores the pending snapshot (a no-op when none is pending). Returns true when every snapshotted rule equals the snapshot afterwards; the lines of
    /// <paramref name="log"/> say what was sent and what still differs.
    /// </summary>
    public async Task<bool> RestoreAsync(IList<string> log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(log);
        Snapshot? snapshot;
        lock (_gate)
        {
            snapshot = _snapshot;
        }

        if (snapshot is null)
        {
            return true;
        }

        try
        {
            var live = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var differing = live is null ? [.. snapshot.Rules.Keys.Order(StringComparer.Ordinal)] : Differences(snapshot.Rules, RulesOf(live.Policy));
            if (differing.Count > 0)
            {
                await PatchBackAsync(snapshot, differing, log, cancellationToken).ConfigureAwait(false);
            }

            // ARM answers a policy PATCH synchronously, but re-read until the snapshot is visible or the time is up, as the Entra guard does.
            var deadline = DateTime.UtcNow.AddSeconds(60);
            List<string> remaining;
            while (true)
            {
                var after = await ReadAsync(cancellationToken).ConfigureAwait(false);
                remaining = after is null ? ["(policy not listed)"] : Differences(snapshot.Rules, RulesOf(after.Policy));
                if (remaining.Count == 0 || DateTime.UtcNow >= deadline)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }

            if (remaining.Count > 0)
            {
                log.Add($"azure pim policies: {_target.Describe()} still differs from the snapshot after the restore ({string.Join(", ", remaining)}); snapshot kept at {SnapshotFile}");
                return false;
            }

            Forget(log, differing.Count == 0
                ? $"azure pim policies: {_target.Describe()} already equals the snapshot; nothing to restore"
                : $"azure pim policies: verified {_target.Describe()} equals the snapshot (restored {string.Join(", ", differing)})");
            return true;
        }
        catch (Exception ex) when (ex is GraphException or GraphTransportException or InvalidOperationException)
        {
            log.Add($"azure pim policies: restoring {_target.Describe()} failed: {(ex is GraphException graph ? ProbeReport.Describe(graph) : ex.Message)}; snapshot kept at {SnapshotFile}");
            return false;
        }
    }

    /// <summary>Restores the pending snapshot; returns one leftover line when it could not be restored.</summary>
    public async Task<IReadOnlyList<string>> RestoreAllAsync(IList<string> log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (Pending.Count == 0 || await RestoreAsync(log, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        return [$"{_target.Describe()} could not be restored to its snapshot; PATCH https://management.azure.com{_snapshot?.PolicyPath}?api-version={AzurePimPolicyTarget.PimApiVersion} with {{\"properties\":{{\"rules\":<the rules array of {SnapshotFile}>}}}}"];
    }

    /// <summary>The rules of a policy resource by rule id (<c>properties.rules</c>).</summary>
    public static Dictionary<string, JsonObject> RulesOf(JsonObject? policy)
    {
        var rules = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        if (policy?["properties"]?["rules"] is JsonArray array)
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

    /// <summary>The ids of the snapshot rules whose live rule differs (members compared in canonical form, arrays as sets; a rule the live policy lacks counts as differing).</summary>
    public static List<string> Differences(IReadOnlyDictionary<string, JsonObject> snapshot, IReadOnlyDictionary<string, JsonObject> live)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(live);
        return snapshot
            .Where(p => !live.TryGetValue(p.Key, out var current) || !string.Equals(AuthMethodRestoreGuard.Canonical(p.Value), AuthMethodRestoreGuard.Canonical(current), StringComparison.Ordinal))
            .Select(p => p.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The PATCH body <c>{"properties":{"rules":[...]}}</c>.</summary>
    public static JsonObject RulesBody(JsonArray rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return new JsonObject { ["properties"] = new JsonObject { ["rules"] = rules.DeepClone() } };
    }

    /// <summary>The <c>$filter</c> of a policy assignment or policy list by role definition id.</summary>
    public static string RoleFilter(string roleDefinitionId)
    {
        return $"roleDefinitionId eq '{roleDefinitionId.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    /// <summary>True when a policy assignment is the target role's at exactly the target scope.</summary>
    public static bool IsTargetAssignment(JsonObject assignment, AzurePimPolicyTarget target)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(target);
        var properties = assignment["properties"];
        return string.Equals(properties.Text("scope")?.TrimEnd('/'), target.Scope, StringComparison.OrdinalIgnoreCase)
            && IsTargetRole(properties.Text("roleDefinitionId"), target);
    }

    /// <summary>True when a role definition id (bare GUID or qualified) names the target role.</summary>
    public static bool IsTargetRole(string? roleDefinitionId, AzurePimPolicyTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return roleDefinitionId is not null
            && (roleDefinitionId.EndsWith("/roleDefinitions/" + target.RoleDefinitionId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(roleDefinitionId, target.RoleDefinitionId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The resource path of the policy an assignment names, rebuilt as <c>{target scope}/providers/Microsoft.Authorization/roleManagementPolicies/{name}</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The assignment names no policy, or a policy outside the target scope.</exception>
    public static string PolicyPathOf(JsonObject assignment, AzurePimPolicyTarget target)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(target);
        string policyId = assignment["properties"].Text("policyId")
            ?? throw new InvalidOperationException($"The policy assignment of {target.Describe()} names no policyId.");
        string prefix = target.PoliciesPath + "/";
        string name = policyId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? policyId[prefix.Length..] : string.Empty;
        string path = prefix + name;
        if (name.Length == 0 || name.Contains('/', StringComparison.Ordinal) || !ArmClient.IsValidResourcePath(path))
        {
            throw new InvalidOperationException($"Refusing to use the policy '{policyId}' of {target.Describe()}: it is not a policy at exactly {target.Scope} (an inherited policy is never written by the probes).");
        }

        return path;
    }

    private void EnsurePolicyIsTarget(JsonObject policy, string policyPath)
    {
        string? scope = policy["properties"].Text("scope")?.TrimEnd('/');
        if (scope is not null && !string.Equals(scope, _target.Scope, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to use the policy {policyPath}: its properties.scope is '{scope}', not {_target.Scope}.");
        }
    }

    private async Task PatchBackAsync(Snapshot snapshot, IReadOnlyList<string> differing, IList<string> log, CancellationToken cancellationToken)
    {
        try
        {
            await _arm.PatchAsync(snapshot.PolicyPath, AzurePimPolicyTarget.PimApiVersion, RulesBody(snapshot.RulesArray), cancellationToken).ConfigureAwait(false);
            log.Add($"azure pim policies: PATCHed the {snapshot.RulesArray.Count} snapshot rules of {_target.Describe()} back (differing: {string.Join(", ", differing)})");
        }
        catch (GraphException ex) when (ex.Status == System.Net.HttpStatusCode.BadRequest)
        {
            // The whole array as read was refused: write back only the rules that differ, one by one, without their null members.
            log.Add($"azure pim policies: PATCH of the whole snapshot rules array was refused ({ProbeReport.Describe(ex)}); retrying rule by rule");
            foreach (string ruleId in differing)
            {
                var rule = (JsonObject)snapshot.Rules[ruleId].DeepClone();
                WithoutNulls(rule);
                await _arm.PatchAsync(snapshot.PolicyPath, AzurePimPolicyTarget.PimApiVersion, RulesBody([rule]), cancellationToken).ConfigureAwait(false);
                log.Add($"azure pim policies: restored rule {ruleId} of {_target.Describe()} from the snapshot");
            }
        }
    }

    private void Forget(IList<string> log, string line)
    {
        lock (_gate)
        {
            _snapshot = null;
        }

        try
        {
            File.Delete(SnapshotFile);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        log.Add(line);
    }

    private void Persist(Snapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var document = new JsonObject
            {
                ["scope"] = _target.Scope,
                ["roleDefinitionId"] = _target.RoleDefinitionId,
                ["policyPath"] = snapshot.PolicyPath,
                ["apiVersion"] = AzurePimPolicyTarget.PimApiVersion,
                ["rules"] = snapshot.RulesArray.DeepClone(),
            };
            OwnerOnlyFile.WriteAllText(SnapshotFile, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The in-memory snapshot still drives the restore; the file only helps after a crash.
        }
    }

    private static Dictionary<string, JsonObject> Copy(IReadOnlyDictionary<string, JsonObject> rules)
    {
        return rules.ToDictionary(p => p.Key, p => (JsonObject)p.Value.DeepClone(), StringComparer.OrdinalIgnoreCase);
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

    /// <summary>A pending snapshot: the policy's path, its rules array exactly as read (what a restore PATCHes back) and the same rules by id (copies).</summary>
    private sealed class Snapshot
    {
        public Snapshot(string policyPath, JsonArray rulesArray, IReadOnlyDictionary<string, JsonObject> rules)
        {
            PolicyPath = policyPath;
            RulesArray = rulesArray;
            Rules = Copy(rules);
        }

        public string PolicyPath { get; }

        public JsonArray RulesArray { get; }

        public IReadOnlyDictionary<string, JsonObject> Rules { get; }
    }
}
