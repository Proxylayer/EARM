using System;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core.Validation;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>Planning for <see cref="RoleManagementPoliciesProvider"/>: the rule diff, the policy not available yet (group onboarding) and the unknown and unsupported changes.</summary>
public sealed partial class RoleManagementPoliciesProvider
{
    // ---- plan ----

    public override ResourceChange Plan(ResourceInstance desired, ExistingResource? existing, PlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(options);
        if (desired.IsIgnored || desired.Properties is not { } properties)
        {
            return base.Plan(desired, existing, options);
        }

        if (!desired.IsSymbolic && !RoleAssignmentsRules.IsGuid(desired.Id.NameLeaf))
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: the name '{desired.Id.NameLeaf}' is not a GUID; name role management policies with a deterministic GUID, for example [guid('pim-policy', 'Global Administrator')].", InvalidCode);
        }

        if (RoleManagementPolicyRules.Check(properties, IsOpaque) is { Count: > 0 } findings)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: {string.Join("; ", findings.Select(f => f.Message))}.", InvalidCode);
        }

        // The onboarding opt-in only steers what happens when the group has no policy yet; it is never compared or sent.
        var onboarding = TakeOnboarding(desired, properties);
        Normalize(properties);
        if (existing is null)
        {
            return PlanMissing(desired, properties, onboarding);
        }

        if (DeclaredRules(properties) is not { } declared)
        {
            return Unknown(desired, existing, "the rules are only known at deployment time");
        }

        var liveRules = LiveRules(existing.Properties);
        var missing = declared.Select(JsonObjects.GetId).Where(id => id is not null && !liveRules.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            return Unsupported(
                desired,
                existing,
                $"{desired.Describe()}: the policy {existing.GraphId} has no rule {string.Join(", ", missing)}; its rules are {string.Join(", ", liveRules.Keys.Order(StringComparer.Ordinal))}.",
                InvalidCode);
        }

        foreach (var rule in declared)
        {
            RoleManagementPolicyShape.FillFromLive(rule, liveRules[JsonObjects.GetId(rule)!]);
        }

        ReplaceRules(properties, declared.Select(RoleManagementPolicyShape.Comparable));
        var aligned = new ExistingResource(existing.GraphId, AlignedLive(existing.Properties, declared), existing.Raw);
        var change = base.Plan(desired, aligned, options);
        return change is { Type: ChangeType.Modify, HasError: false }
            ? change with { Reasons = [.. change.Reasons, "each changed rule is updated in place (PATCH of the rule); undeclared rules are left as they are"] }
            : change;
    }

    /// <summary>
    /// No live policy: an unknown scope is resolved at deployment; a directory role is an error; a group is deferred when a schedule request of this
    /// deployment onboards it, onboarded by the create when the declaration opts in (<c>properties.onboarding</c>), and an error otherwise.
    /// </summary>
    private ResourceChange PlanMissing(ResourceInstance desired, JsonObject properties, OnboardingRequest? onboarding)
    {
        if (desired.IsSymbolic || UnknownJson.ContainsUnknown(Get(properties, "scopeId")) || UnknownJson.ContainsUnknown(Get(properties, "roleDefinitionId"))
            || Get(properties, "role") is not null || Get(properties, "group") is not null)
        {
            return Unknown(desired, null, "the policy is looked up when the deployment runs (its scope is only known then)");
        }

        var scope = ScopeOf(properties);
        if (scope is null)
        {
            return Unknown(desired, null, "the policy is looked up when the deployment runs (its scope is only known then)");
        }

        if (!scope.IsGroup)
        {
            return Unsupported(desired, null, $"{desired.Describe()}: Graph lists no PIM policy for directory role {scope.RoleDefinitionId} (GET {RoleManagementPolicyLookup.AssignmentsPath}?$filter={RoleManagementPolicyLookup.Filter(scope)}); check that roleDefinitionId is the role's template id. Role management policies are never created.", EngineDiagnosticCodes.PimPolicyNotAvailable);
        }

        string notOnboarded = $"Graph lists no PIM policy for the {scope.RoleDefinitionId} access of group {scope.ScopeId}: PIM for Groups has not onboarded the group";
        if (_onboardedThisRun.TryGetValue(desired, out _))
        {
            string message = ProviderWarnings.Format(
                EngineDiagnosticCodes.PimPolicyNotAvailable,
                $"{notOnboarded} yet. A schedule request of this deployment onboards it; the policy is applied by the next run (deploy again once this one succeeded). Until then the group's default policy applies to those requests.");
            return new ResourceChange
            {
                Id = desired.Id,
                ResourceType = TypeName,
                Type = ChangeType.Ignore,
                Mode = ChangeMode.None,
                After = JsonDiff.RenderObject(desired.Properties),
                Reasons = [message],
            };
        }

        if (onboarding is not null)
        {
            string principal = onboarding.PrincipalId ?? "the principal of properties.onboarding (known at deployment)";
            return new ResourceChange
            {
                Id = desired.Id,
                ResourceType = TypeName,
                Type = ChangeType.Create,
                Mode = ChangeMode.Create,
                After = JsonDiff.RenderObject(desired.Properties),
                IsUnknown = desired.HasUnknownProperties || onboarding.PrincipalId is null,
                Reasons =
                [
                    $"group not onboarded: a temporary eligibility for {principal} will onboard it, then be removed",
                    $"{notOnboarded} yet. The deployment requests a {GroupPolicyOnboarding.TemporaryDuration} {GroupPolicyOnboarding.TemporaryAccess} eligibility for {principal} (justification '{GroupPolicyOnboarding.Justification(scope.ScopeId)}'), waits for Graph to list the group's policy, removes the eligibility, and then applies the declared rules to that policy; nothing else is created",
                ],
            };
        }

        return Unsupported(desired, null, $"{desired.Describe()}: {notOnboarded}, and no schedule request of this deployment onboards it. PIM onboards a group on its first eligibility or assignment request (or policy update; Learn: privilegedidentitymanagement-for-groups-api-overview); declare a privilegedAccess/groupEligibilityScheduleRequests (or groupAssignmentScheduleRequests) for the group in the same template, let this policy onboard the group (properties.onboarding.temporaryEligiblePrincipalId), or onboard it in the portal, then deploy again.", EngineDiagnosticCodes.PimPolicyNotAvailable);
    }

    /// <summary>
    /// Removes <c>properties.onboarding</c> from the properties and remembers it for the create; on a second plan of the same instance (the property is
    /// already gone) the remembered opt-in. The principal is <c>null</c> while it is only known at deployment.
    /// </summary>
    private OnboardingRequest? TakeOnboarding(ResourceInstance desired, JsonObject properties)
    {
        if (JsonObjects.FindKey(properties, RoleManagementPolicyRules.OnboardingProperty) is not { } key)
        {
            return _onboardingRequests.TryGetValue(desired, out var remembered) ? remembered : null;
        }

        var node = properties[key];
        properties.Remove(key);
        if (JsonObjects.IsAbsent(node))
        {
            _onboardingRequests.Remove(desired);
            return null;
        }

        string? principal = node is JsonObject onboarding && Text(onboarding, RoleManagementPolicyRules.TemporaryEligiblePrincipalIdProperty) is { } id && RoleAssignmentsRules.IsGuid(id)
            ? RoleAssignmentsRules.NormalizeId(id)
            : null;
        var request = new OnboardingRequest(principal);
        _onboardingRequests.AddOrUpdate(desired, request);
        return request;
    }

    private ResourceChange Unknown(ResourceInstance desired, ExistingResource? existing, string reason)
    {
        return new ResourceChange
        {
            Id = desired.Id,
            ResourceType = TypeName,
            Type = ChangeType.Modify,
            Mode = ChangeMode.Patch,
            GraphId = existing?.GraphId,
            After = JsonDiff.RenderObject(desired.Properties),
            IsUnknown = true,
            IsSymbolicId = desired.IsSymbolic,
            Reasons = [reason],
        };
    }

    private ResourceChange Unsupported(ResourceInstance desired, ExistingResource? existing, string message, string code)
    {
        return new ResourceChange
        {
            Id = desired.Id,
            ResourceType = TypeName,
            Type = ChangeType.Modify,
            Mode = ChangeMode.Patch,
            GraphId = existing?.GraphId,
            Before = existing is null ? null : RenderBefore(existing.Properties, desired.Properties),
            After = JsonDiff.RenderObject(desired.Properties),
            IsUnknown = desired.HasUnknownProperties || desired.IsSymbolic,
            IsSymbolicId = desired.IsSymbolic,
            Reasons = [message],
            UnsupportedReason = message,
            UnsupportedCode = code,
        };
    }
}
