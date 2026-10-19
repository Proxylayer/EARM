using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Engine.Providers;

namespace EArm.Providers;

/// <summary>
/// The entitlement management orders the template's references do not show, found on the declarations before any Graph call (for
/// <see cref="IApplyOrderSource"/>): a catalog resource applies before the role scopes that grant a role of the same group (the group must be in the
/// catalog first), and a role scope applies before the assignment policies of the same package. Values match the way <see cref="DeclarationConflicts"/>
/// matches them: the same declared resource referenced, or the same literal.
/// </summary>
internal static class AccessPackageApplyOrder
{
    private const string GroupsType = "Microsoft.Entra/groups";

    private const string ServicePrincipalsType = "Microsoft.Entra/servicePrincipals";

    /// <summary>
    /// The declared role scopes that grant a role of the group, service principal or site <paramref name="catalogResource"/> adds to a catalog: the same
    /// declared group or service principal referenced, or the same literal (a site URL regardless of case and trailing slash).
    /// </summary>
    public static IReadOnlyList<ResourceInstance> RoleScopesOfResource(ResourceInstance catalogResource, IReadOnlyList<ResourceInstance> declared)
    {
        ArgumentNullException.ThrowIfNull(catalogResource);
        ArgumentNullException.ThrowIfNull(declared);
        if (catalogResource.Declaration.Properties is not JsonObject properties)
        {
            return [];
        }

        var targets = Referenced(catalogResource, properties, "originId", declared, GroupsType);
        targets.UnionWith(Referenced(catalogResource, properties, "originId", declared, ServicePrincipalsType));
        string? literal = Normalized(DeclarationConflicts.Literal(properties, "originId"));
        return declared.Where(r =>
        {
            if (ReferenceEquals(r, catalogResource) || r.IsIgnored || !ResourceIdentifier.TypeEquals(r.Type, ResourceRoleScopesProvider.ResourceType)
                || r.Declaration.Properties is not JsonObject scope)
            {
                return false;
            }

            return Referenced(r, scope, "resourceOriginId", declared, GroupsType).Any(targets.Contains)
                || Referenced(r, scope, "resourceOriginId", declared, ServicePrincipalsType).Any(targets.Contains)
                || (literal is not null && literal == Normalized(DeclarationConflicts.Literal(scope, "resourceOriginId")));
        }).ToList();
    }

    /// <summary>The declared assignment policies of the package <paramref name="roleScope"/> grants a role through.</summary>
    public static IReadOnlyList<ResourceInstance> PoliciesOfPackage(ResourceInstance roleScope, IReadOnlyList<ResourceInstance> declared)
    {
        ArgumentNullException.ThrowIfNull(roleScope);
        ArgumentNullException.ThrowIfNull(declared);
        if (roleScope.Declaration.Properties is not JsonObject properties)
        {
            return [];
        }

        var packages = Referenced(roleScope, properties, "accessPackageId", declared, AccessPackagesProvider.ResourceType);
        if (packages.Count == 0)
        {
            return [];
        }

        return declared.Where(p => !p.IsIgnored
            && ResourceIdentifier.TypeEquals(p.Type, AssignmentPoliciesProvider.ResourceType)
            && p.PlannedId.Parent is { } parent
            && packages.Any(package => package.PlannedId.Equals(parent) || package.Id.Equals(parent))).ToList();
    }

    /// <summary>
    /// True when the template declares something that onboards the group <paramref name="roleScope"/> grants a role of in PIM for Groups when it runs
    /// (<see cref="OnboardingSources"/>): a schedule request (eligibility or time-bound assignment; Learn: privilegedidentitymanagement-for-groups-api-overview)
    /// or a group PIM policy that opts in to onboarding (<c>properties.onboarding</c>).
    /// </summary>
    public static bool GroupOnboardedThisRun(ResourceInstance roleScope, IReadOnlyList<ResourceInstance> declared)
    {
        return OnboardingSources(roleScope, declared).Count > 0;
    }

    /// <summary>
    /// The declarations that onboard the group <paramref name="roleScope"/> grants a role of: the PIM for Groups schedule requests for it
    /// (<see cref="OnboardingRequests"/>) and the group PIM policies for it that opt in to onboarding (<see cref="OnboardingPolicies"/>).
    /// </summary>
    public static IReadOnlyList<ResourceInstance> OnboardingSources(ResourceInstance roleScope, IReadOnlyList<ResourceInstance> declared)
    {
        return [.. OnboardingRequests(roleScope, declared), .. OnboardingPolicies(roleScope, declared)];
    }

    /// <summary>
    /// The declared <c>roleManagementPolicies</c> of the group <paramref name="roleScope"/> grants a role of that opt in to onboarding
    /// (<c>properties.onboarding.temporaryEligiblePrincipalId</c>): creating such a policy onboards the group in PIM for Groups
    /// (<see cref="RoleManagementPolicyApplyOrder.OnboardsGroup"/>).
    /// </summary>
    public static IReadOnlyList<ResourceInstance> OnboardingPolicies(ResourceInstance roleScope, IReadOnlyList<ResourceInstance> declared)
    {
        ArgumentNullException.ThrowIfNull(roleScope);
        ArgumentNullException.ThrowIfNull(declared);
        if (roleScope.Declaration.Properties is not JsonObject properties)
        {
            return [];
        }

        var groups = Referenced(roleScope, properties, "resourceOriginId", declared, GroupsType);
        string? literal = DeclarationConflicts.Literal(properties, "resourceOriginId")?.Trim();
        return declared.Where(r => !ReferenceEquals(r, roleScope)
            && ResourceIdentifier.TypeEquals(r.Type, RoleManagementPoliciesProvider.ResourceType)
            && RoleManagementPolicyApplyOrder.OnboardsGroup(r, groups, literal, declared)).ToList();
    }

    /// <summary>The declared PIM for Groups schedule requests (eligibility or time-bound assignment) for the group <paramref name="roleScope"/> grants a role of.</summary>
    public static IReadOnlyList<ResourceInstance> OnboardingRequests(ResourceInstance roleScope, IReadOnlyList<ResourceInstance> declared)
    {
        ArgumentNullException.ThrowIfNull(roleScope);
        ArgumentNullException.ThrowIfNull(declared);
        if (roleScope.Declaration.Properties is not JsonObject properties)
        {
            return [];
        }

        var groups = Referenced(roleScope, properties, "resourceOriginId", declared, GroupsType);
        string? literal = Normalized(DeclarationConflicts.Literal(properties, "resourceOriginId"));
        return declared.Where(r =>
        {
            if (r.IsIgnored || r.Declaration.Properties is not JsonObject request
                || !(ResourceIdentifier.TypeEquals(r.Type, Core.Validation.GroupEligibilityScheduleRules.ResourceType)
                    || ResourceIdentifier.TypeEquals(r.Type, Core.Validation.GroupAssignmentScheduleRules.ResourceType)))
            {
                return false;
            }

            return DeclarationConflicts.TargetGroups(r, request, DeclarationConflicts.PropertiesPath(r), declared).Any(groups.Contains)
                || (literal is not null && literal == Normalized(DeclarationConflicts.Literal(request, "groupId")));
        }).ToList();
    }

    /// <summary>
    /// The declared role scopes that grant an eligible role (<c>Eligible Member</c> / <c>Eligible Owner</c>, or a computed role) of the group
    /// <paramref name="source"/> onboards (a PIM for Groups schedule request, or a group PIM policy that opts in to onboarding; <see cref="OnboardingSources"/>):
    /// they are applied after it, so the catalog offers the eligible role by then.
    /// </summary>
    public static IReadOnlyList<ResourceInstance> EligibleRoleScopesOnboardedBy(ResourceInstance source, IReadOnlyList<ResourceInstance> declared)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(declared);
        return declared.Where(r => !r.IsIgnored
            && ResourceIdentifier.TypeEquals(r.Type, ResourceRoleScopesProvider.ResourceType)
            && r.Declaration.Properties is JsonObject scope
            && (JsonObjects.FindKey(scope, "role") is not { } roleKey || DeclarationConflicts.Literal(scope, roleKey) is not { } role || ResourceRoleScopesProvider.IsEligibleRole(role))
            && OnboardingSources(r, declared).Contains(source)).ToList();
    }
    /// <summary>The declared resources of <paramref name="type"/> the value <paramref name="name"/> of <paramref name="instance"/> references.</summary>
    private static HashSet<ResourceInstance> Referenced(ResourceInstance instance, JsonObject properties, string name, IReadOnlyList<ResourceInstance> declared, string type)
    {
        var result = new HashSet<ResourceInstance>();
        if (JsonObjects.FindKey(properties, name) is not { } key)
        {
            return result;
        }

        var path = DeclarationConflicts.PropertiesPath(instance).Append(key);
        foreach (var candidate in declared)
        {
            if (!candidate.IsIgnored && ResourceIdentifier.TypeEquals(candidate.Type, type) && DeclarationConflicts.IsReferencedBy(instance, path, candidate))
            {
                result.Add(candidate);
            }
        }

        return result;
    }

    private static string? Normalized(string? text)
    {
        return text is null ? null : AccessPackageResourceOrigin.NormalizeId(text).ToLowerInvariant();
    }
}
