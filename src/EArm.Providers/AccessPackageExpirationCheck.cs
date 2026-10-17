using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Xml;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Validation;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Expressions;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>
/// The offline check of an access package assignment policy's <c>expiration</c> against the PIM for Groups eligibility maximum of the groups the package
/// grants eligibly (<see cref="IDeclarationConflictSource"/>, a <see cref="DiagnosticSeverity.Warning"/>): for a policy of a template package, every
/// template <c>resourceRoleScopes</c> of the package that grants <c>Eligible Member</c> / <c>Eligible Owner</c> of a group, and every template
/// <c>roleManagementPolicies</c> of the same group and access whose <c>Expiration_Admin_Eligibility</c> rule requires an expiration, a policy whose
/// assignments outlive the rule's <c>maximumDuration</c> (or never expire) is reported.
/// <para>
/// A warning, not an error: the deployment itself succeeds, and what goes wrong happens later, to the people assigned: PIM ends the eligibility at its
/// maximum while entitlement management still shows the assignment (docs/access-packages.md). The live policy of a group the template does not
/// declare a policy for is not read; values only known at deployment (a <c>reference()</c>) are not compared. The declarations' own expressions
/// (parameters, variables, copy indexes) are evaluated in each instance's context, which is all a conflict source can see before planning.
/// </para>
/// </summary>
internal static class AccessPackageExpirationCheck
{
    private const string EligibilityRule = "Expiration_Admin_Eligibility";

    private const string GroupsType = "Microsoft.Entra/groups";

    public static IEnumerable<DeclarationConflict> Describe(ResourceInstance policy, IReadOnlyList<ResourceInstance> declared)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(declared);
        if (policy.Declaration.Properties is not JsonObject properties
            || JsonObjects.FindKey(properties, "expiration") is not { } expirationKey
            || Expiration(Evaluate(properties[expirationKey], policy)) is not { } expiration
            || policy.PlannedId.Parent is not { } packageId
            || declared.FirstOrDefault(d => !d.IsIgnored && ResourceIdentifier.TypeEquals(d.Type, AccessPackagesProvider.ResourceType) && string.Equals(d.PlannedId.Id, packageId.Id, StringComparison.OrdinalIgnoreCase)) is not { } package)
        {
            yield break;
        }

        var expirationPath = DeclarationConflicts.PropertiesPath(policy).Append(expirationKey);
        foreach (var scope in declared.Where(d => !d.IsIgnored && ResourceIdentifier.TypeEquals(d.Type, ResourceRoleScopesProvider.ResourceType)))
        {
            if (scope.Declaration.Properties is not JsonObject scopeProperties
                || !DeclarationConflicts.IsReferencedBy(scope, DeclarationConflicts.PropertiesPath(scope).Append(JsonObjects.FindKey(scopeProperties, "accessPackageId") ?? "accessPackageId"), package)
                || EligibleAccess(scope, scopeProperties) is not { } eligible)
            {
                continue;
            }

            foreach (var groupPolicy in declared.Where(d => !d.IsIgnored && ResourceIdentifier.TypeEquals(d.Type, RoleManagementPoliciesProvider.ResourceType)))
            {
                if (groupPolicy.Declaration.Properties is not JsonObject groupPolicyProperties
                    || !string.Equals(PolicyAccess(groupPolicy, groupPolicyProperties), eligible.Access, StringComparison.Ordinal)
                    || !SameGroup(scope, scopeProperties, groupPolicy, groupPolicyProperties, declared)
                    || EligibilityMaximum(groupPolicy, groupPolicyProperties) is not { } maximum
                    || !Exceeds(expiration, maximum))
                {
                    continue;
                }

                string lasts = expiration.Duration is { } duration
                    ? $"lets an assignment last {expiration.Text} ({Days(duration)})"
                    : "lets an assignment last forever (noExpiration)";
                yield return new DeclarationConflict(
                    EngineDiagnosticCodes.AccessPackageExpirationExceedsEligibility,
                    $"{policy.Describe()} {lasts}, longer than the eligibility maximum {maximum.Text} ({Days(maximum.Duration)}) that {groupPolicy.Describe()} sets for the group's {eligible.Access}s ({EligibilityRule}), which {scope.Describe()} grants as '{eligible.Role}'. PIM ends the eligibility at its maximum while entitlement management still shows the person as assigned. Shorten the policy's expiration (expiration.duration) to {maximum.Text} or less, or raise the group policy's maximumDuration.",
                    expirationPath,
                    DiagnosticSeverity.Warning);
            }
        }
    }

    // ---- the policy's expiration ----

    /// <summary>The policy's assignment lifetime: a duration (<c>afterDuration</c>), or <c>null</c> duration for <c>noExpiration</c>; <c>null</c> when unknown or a date.</summary>
    private static PolicyExpiration? Expiration(JsonNode? node)
    {
        if (node is not JsonObject expiration)
        {
            return null;
        }

        string? type = JsonValues.TryGetString(Get(expiration, "type"), out string typeText) ? typeText.Trim() : null;
        if (string.Equals(type, "noExpiration", StringComparison.OrdinalIgnoreCase))
        {
            return new PolicyExpiration(null, "noExpiration");
        }

        bool afterDuration = type is null || string.Equals(type, "afterDuration", StringComparison.OrdinalIgnoreCase);
        return afterDuration && JsonValues.TryGetString(Get(expiration, "duration"), out string text) && PimScheduleRules.TryParseDuration(text, out var duration)
            ? new PolicyExpiration(duration, text.Trim())
            : null;
    }

    private static bool Exceeds(PolicyExpiration expiration, EligibilityMaximumValue maximum)
    {
        return expiration.Duration is not { } duration || duration > maximum.Duration;
    }

    // ---- the role scope ----

    /// <summary>The PIM for Groups access (<c>member</c> / <c>owner</c>) an eligible role scope grants; <c>null</c> for an active role or an unknown one.</summary>
    private static (string Access, string Role)? EligibleAccess(ResourceInstance scope, JsonObject properties)
    {
        if (JsonObjects.FindKey(properties, "role") is not { } key || !JsonValues.TryGetString(Evaluate(properties[key], scope), out string role))
        {
            return null;
        }

        string trimmed = role.Trim();
        return trimmed.ToLowerInvariant() switch
        {
            "eligible member" => (GroupEligibilityScheduleRules.Member, trimmed),
            "eligible owner" => (GroupEligibilityScheduleRules.Owner, trimmed),
            _ => null,
        };
    }

    // ---- the group policy ----

    /// <summary>The access a group policy governs (<c>accessId</c>, or its <c>roleDefinitionId</c> spelling); <c>null</c> for a directory role policy or an unknown access.</summary>
    private static string? PolicyAccess(ResourceInstance groupPolicy, JsonObject properties)
    {
        foreach (string name in new[] { "accessId", "roleDefinitionId" })
        {
            if (JsonObjects.FindKey(properties, name) is { } key && JsonValues.TryGetString(Evaluate(properties[key], groupPolicy), out string access))
            {
                return GroupEligibilityScheduleRules.NormalizeAccessId(access);
            }
        }

        return null;
    }

    /// <summary>
    /// True when the role scope's <c>resourceOriginId</c> and the group policy's <c>group</c> / <c>scopeId</c> name the same group: the same template group
    /// (referenced, or named by its uniqueName), or the same literal object id.
    /// </summary>
    private static bool SameGroup(ResourceInstance scope, JsonObject scopeProperties, ResourceInstance groupPolicy, JsonObject policyProperties, IReadOnlyList<ResourceInstance> declared)
    {
        var scopePath = DeclarationConflicts.PropertiesPath(scope);
        string originKey = JsonObjects.FindKey(scopeProperties, "resourceOriginId") ?? "resourceOriginId";
        var granted = declared
            .Where(d => !d.IsIgnored && ResourceIdentifier.TypeEquals(d.Type, GroupsType) && DeclarationConflicts.IsReferencedBy(scope, scopePath.Append(originKey), d))
            .ToList();

        var policyPath = DeclarationConflicts.PropertiesPath(groupPolicy);
        var governed = DeclarationConflicts.TargetGroups(groupPolicy, policyProperties, policyPath, declared).ToList();
        if (JsonObjects.FindKey(policyProperties, "scopeId") is { } scopeIdKey)
        {
            governed.AddRange(declared.Where(d => !d.IsIgnored && ResourceIdentifier.TypeEquals(d.Type, GroupsType) && DeclarationConflicts.IsReferencedBy(groupPolicy, policyPath.Append(scopeIdKey), d)));
        }

        if (granted.Intersect(governed).Any())
        {
            return true;
        }

        string? origin = DeclarationConflicts.Literal(scopeProperties, "resourceOriginId");
        string? target = DeclarationConflicts.Literal(policyProperties, "group") ?? DeclarationConflicts.Literal(policyProperties, "scopeId");
        return origin is not null && target is not null && RoleAssignmentsRules.IsGuid(origin) && RoleAssignmentsRules.IsGuid(target)
            && string.Equals(RoleAssignmentsRules.NormalizeId(origin), RoleAssignmentsRules.NormalizeId(target), StringComparison.Ordinal);
    }

    /// <summary>The declared <c>Expiration_Admin_Eligibility</c> rule's maximum when it requires an expiration; <c>null</c> when absent, optional or unknown.</summary>
    private static EligibilityMaximumValue? EligibilityMaximum(ResourceInstance groupPolicy, JsonObject properties)
    {
        if (JsonObjects.FindKey(properties, "rules") is not { } key || Evaluate(properties[key], groupPolicy) is not JsonArray rules)
        {
            return null;
        }

        foreach (var node in rules)
        {
            if (node is not JsonObject rule || !JsonValues.TryGetString(Get(rule, "id"), out string id) || !string.Equals(id.Trim(), EligibilityRule, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!JsonValues.TryGetBool(Get(rule, "isExpirationRequired"), out bool required) || !required)
            {
                return null;
            }

            return JsonValues.TryGetString(Get(rule, "maximumDuration"), out string text) && PimScheduleRules.TryParseDuration(text, out var maximum)
                ? new EligibilityMaximumValue(maximum, text.Trim())
                : null;
        }

        return null;
    }

    // ---- helpers ----

    /// <summary>The value of <paramref name="node"/> in the instance's context; <c>null</c> when it cannot be evaluated before planning.</summary>
    private static JsonNode? Evaluate(JsonNode? node, ResourceInstance instance)
    {
        try
        {
            var value = ExpressionEvaluator.Default.EvaluateTree(node, instance.Context);
            return UnknownJson.IsUnknown(value) ? null : value;
        }
        catch (ExpressionException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static JsonNode? Get(JsonObject owner, string name)
    {
        return JsonObjects.FindKey(owner, name) is { } key && !UnknownJson.IsUnknown(owner[key]) ? owner[key] : null;
    }

    private static string Days(TimeSpan duration)
    {
        return duration.TotalDays >= 1
            ? duration.TotalDays.ToString("0.##", CultureInfo.InvariantCulture) + " days"
            : XmlConvert.ToString(duration);
    }

    private sealed record PolicyExpiration(TimeSpan? Duration, string Text);

    private sealed record EligibilityMaximumValue(TimeSpan Duration, string Text);
}
