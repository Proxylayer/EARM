using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Secrets;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using EArm.Graph;

namespace EArm.Providers;

/// <summary>The writes of <see cref="AzureRoleManagementPoliciesProvider"/>: no create, and the one PATCH of the changed declared rules.</summary>
public sealed partial class AzureRoleManagementPoliciesProvider
{
    // ---- create ----

    /// <summary>Never creates: Plan reports a missing policy (<see cref="EngineDiagnosticCodes.PimPolicyNotAvailable"/>) and plans no Create row.</summary>
    public override Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        throw new ProviderException(EngineDiagnosticCodes.PimPolicyNotAvailable, $"{desired.Describe()}: the Azure PIM policy does not exist; role management policies are never created.", desired.Id);
    }

    // ---- update ----

    /// <summary>
    /// One <c>PATCH</c> of the declared rules that differ from the live rules of <paramref name="existing"/> (each filled from its live rule, so it is
    /// complete); nothing is sent when none differs. ARM's answer (the whole policy) is the re-read.
    /// </summary>
    public override async Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = desired.Properties ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before updating.");
        var key = ResolveKey(desired);
        var scope = ScopeOf(key, desired.Id);
        var declared = DeclaredRules(properties) ?? throw Invalid(desired, "the rules are not known");
        var liveRules = LiveRules(existing.Properties);
        var changed = new List<JsonObject>();
        foreach (var rule in declared)
        {
            string ruleId = JsonObjects.GetId(rule) ?? throw Invalid(desired, "a rule has no id");
            if (!liveRules.TryGetValue(ruleId, out var liveRule))
            {
                throw Invalid(desired, $"the policy {existing.GraphId} has no rule {ruleId}");
            }

            RoleManagementPolicyShape.FillFromLive(rule, liveRule);
            if (Differs(RoleManagementPolicyShape.Comparable(rule), liveRule))
            {
                changed.Add(WithoutNulls((JsonObject)SecretJson.Reveal(rule)!));
            }
        }

        string policyPath = AzureRoleManagementPolicyLookup.PolicyPath(scope, existing.GraphId)
            ?? throw new ProviderException(EngineDiagnosticCodes.ReadFailed, $"{desired.Describe()}: the policy '{existing.GraphId}' is not in {AzureRoleManagementPolicyLookup.PoliciesPath(scope.Scope)}; EARM never changes a policy of another scope.", desired.Id);
        if (changed.Count == 0)
        {
            // Every declared rule already has its value (planned before another change landed): a PATCH would only bump lastModifiedDateTime.
            return new AppliedResource(desired.Id, existing.GraphId, (JsonObject)JsonValues.Clone(existing.Raw)!, key);
        }

        var answer = await AzureRoleManagementPolicyLookup.PatchRulesAsync(ResourceManager, policyPath, changed, cancellationToken).ConfigureAwait(false);
        var live = answer is not null && JsonObjects.TryGetIgnoreCase(answer, "properties", out var answered) && answered is JsonObject
            ? answer
            : (await AzureRoleManagementPolicyLookup.FindAsync(ResourceManager, scope, cancellationToken).ConfigureAwait(false))?.Policy;
        return new AppliedResource(desired.Id, existing.GraphId, live ?? (JsonObject)JsonValues.Clone(existing.Raw)!, key);
    }
}
