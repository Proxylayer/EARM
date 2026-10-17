using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Secrets;
using EArm.Core.Validation;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using EArm.Graph;

namespace EArm.Providers;

/// <summary>The writes of <see cref="RoleManagementPoliciesProvider"/>: create (onboarding a group when needed) and update, both PATCHing the declared rules.</summary>
public sealed partial class RoleManagementPoliciesProvider
{
    // ---- create ----

    /// <summary>
    /// Never creates a policy (a policy exists for every role and onboarded group; Plan reports the missing ones). The one Create row it plans is a group
    /// policy that opts in to onboarding: the group is onboarded with a temporary eligibility (<see cref="GroupPolicyOnboarding"/>), and the declared rules
    /// are applied to the policy Graph lists afterwards. An eligibility that could not be removed fails the resource
    /// (<see cref="EngineDiagnosticCodes.PimOnboardingEligibilityNotRemoved"/>) after the rules were applied, naming the eligibility.
    /// </summary>
    public override async Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = desired.Properties ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before creating.");
        var key = ResolveKey(desired);
        var scope = ScopeOf(key, desired.Id);
        if (!scope.IsGroup || !_onboardingRequests.TryGetValue(desired, out var onboarding))
        {
            throw new ProviderException(EngineDiagnosticCodes.PimPolicyNotAvailable, $"{desired.Describe()}: the PIM policy does not exist; role management policies are never created.", desired.Id);
        }

        string principalId = onboarding.PrincipalId
            ?? throw Invalid(desired, $"properties.{RoleManagementPolicyRules.OnboardingProperty}.{RoleManagementPolicyRules.TemporaryEligiblePrincipalIdProperty} is not a known object id at deployment time");

        // Another declaration of this run (the group's other access) may have onboarded the group since this one was planned: then nothing is requested.
        var assignment = await RoleManagementPolicyLookup.FindAsync(graph, scope, desired.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false);
        string? leftover = null;
        if (assignment is null)
        {
            var onboarded = await _onboarding.OnboardAsync(desired, scope, principalId, graph, cancellationToken).ConfigureAwait(false);
            assignment = onboarded.Assignment;
            leftover = onboarded.Leftover;
        }

        AppliedResource applied;
        try
        {
            applied = await ApplyRulesAsync(desired, properties, key, scope, ToExisting(assignment, scope), graph, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (leftover is not null && ex is ProviderException or GraphException or GraphTransportException)
        {
            throw new ProviderException(EngineDiagnosticCodes.PimOnboardingEligibilityNotRemoved, $"{desired.Describe()}: the group was onboarded, but applying the rules failed ({ex.Message}); and {leftover}.", desired.Id, ex);
        }

        if (leftover is not null)
        {
            // The policy is applied (recorded as the partial result); the resource fails, and the leftover eligibility is also reported under its own code.
            var partial = applied with { Warnings = [.. applied.Warnings, new ProviderWarning(EngineDiagnosticCodes.PimOnboardingEligibilityNotRemoved, leftover)] };
            throw new ProviderException(EngineDiagnosticCodes.PimOnboardingEligibilityNotRemoved, $"the group was onboarded and the rules were applied, but {leftover}", desired.Id) { Partial = partial };
        }

        return applied;
    }

    // ---- update ----

    public override Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = desired.Properties ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before updating.");
        var key = ResolveKey(desired);
        return ApplyRulesAsync(desired, properties, key, ScopeOf(key, desired.Id), existing, graph, cancellationToken);
    }

    /// <summary>One PATCH per declared rule that differs from the live rule of <paramref name="existing"/>; then the policy is read back.</summary>
    private async Task<AppliedResource> ApplyRulesAsync(ResourceInstance desired, JsonObject properties, ResourceKey key, PolicyScope scope, ExistingResource existing, IGraphClient graph, CancellationToken cancellationToken)
    {
        var declared = DeclaredRules(properties) ?? throw Invalid(desired, "the rules are not known");
        var liveRules = LiveRules(existing.Properties);
        string policyId = existing.GraphId;

        foreach (var rule in declared)
        {
            string ruleId = JsonObjects.GetId(rule) ?? throw Invalid(desired, "a rule has no id");
            if (!liveRules.TryGetValue(ruleId, out var liveRule))
            {
                throw Invalid(desired, $"the policy {policyId} has no rule {ruleId}");
            }

            RoleManagementPolicyShape.FillFromLive(rule, liveRule);
            var comparable = RoleManagementPolicyShape.Comparable(rule);
            if (!Differs(comparable, liveRule))
            {
                continue;
            }

            var body = (JsonObject)SecretJson.Reveal(comparable)!;
            try
            {
                await RoleManagementPolicyLookup.PatchRuleAsync(graph, policyId, body, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false);
            }
            catch (GraphException ex) when (ex.IsNotFound && scope.IsGroup)
            {
                // The group's first policy update onboarded it in PIM for Groups, which re-ids its policy (Learn): find the new id and send the rule again.
                var reread = await RoleManagementPolicyLookup.FindAsync(graph, scope, desired.Api, RetryHint.ExpectReplicationLag, cancellationToken).ConfigureAwait(false);
                string? newId = reread is null ? null : RoleManagementPolicyLookup.PolicyId(reread);
                if (newId is null || string.Equals(newId, policyId, StringComparison.OrdinalIgnoreCase))
                {
                    throw;
                }

                policyId = newId;
                await RoleManagementPolicyLookup.PatchRuleAsync(graph, policyId, body, desired.Api, RetryHint.ExpectReplicationLag, cancellationToken).ConfigureAwait(false);
            }
        }

        var live = await RoleManagementPolicyLookup.FindAsync(graph, scope, desired.Api, scope.IsGroup ? RetryHint.ExpectReplicationLag : desired.RetryHint, cancellationToken).ConfigureAwait(false);
        string graphId = (live is null ? null : RoleManagementPolicyLookup.PolicyId(live)) ?? policyId;
        return new AppliedResource(desired.Id, graphId, live ?? (JsonObject)JsonValues.Clone(existing.Raw)!, key);
    }
}
