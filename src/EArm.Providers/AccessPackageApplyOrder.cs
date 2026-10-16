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

    /// <summary>The declared role scopes that grant a role of the group <paramref name="catalogResource"/> adds to a catalog.</summary>
    public static IReadOnlyList<ResourceInstance> RoleScopesOfGroup(ResourceInstance catalogResource, IReadOnlyList<ResourceInstance> declared)
    {
        ArgumentNullException.ThrowIfNull(catalogResource);
        ArgumentNullException.ThrowIfNull(declared);
        if (catalogResource.Declaration.Properties is not JsonObject properties)
        {
            return [];
        }

        var groups = Referenced(catalogResource, properties, "originId", declared, GroupsType);
        string? literal = Normalized(DeclarationConflicts.Literal(properties, "originId"));
        return declared.Where(r =>
        {
            if (ReferenceEquals(r, catalogResource) || r.IsIgnored || !ResourceIdentifier.TypeEquals(r.Type, ResourceRoleScopesProvider.ResourceType)
                || r.Declaration.Properties is not JsonObject scope)
            {
                return false;
            }

            return Referenced(r, scope, "resourceOriginId", declared, GroupsType).Any(groups.Contains)
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
    /// True when the template declares a PIM for Groups schedule request (eligibility or time-bound assignment) for the group <paramref name="roleScope"/>
    /// grants a role of: that request onboards the group in PIM for Groups when it runs (Learn: privilegedidentitymanagement-for-groups-api-overview).
    /// </summary>
    public static bool GroupOnboardedThisRun(ResourceInstance roleScope, IReadOnlyList<ResourceInstance> declared)
    {
        ArgumentNullException.ThrowIfNull(roleScope);
        ArgumentNullException.ThrowIfNull(declared);
        if (roleScope.Declaration.Properties is not JsonObject properties)
        {
            return false;
        }

        var groups = Referenced(roleScope, properties, "resourceOriginId", declared, GroupsType);
        string? literal = Normalized(DeclarationConflicts.Literal(properties, "resourceOriginId"));
        return declared.Any(r =>
        {
            if (r.IsIgnored || r.Declaration.Properties is not JsonObject request
                || !(ResourceIdentifier.TypeEquals(r.Type, Core.Validation.GroupEligibilityScheduleRules.ResourceType)
                    || ResourceIdentifier.TypeEquals(r.Type, Core.Validation.GroupAssignmentScheduleRules.ResourceType)))
            {
                return false;
            }

            return DeclarationConflicts.TargetGroups(r, request, DeclarationConflicts.PropertiesPath(r), declared).Any(groups.Contains)
                || (literal is not null && literal == Normalized(DeclarationConflicts.Literal(request, "groupId")));
        });
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
        return text?.Trim().ToLowerInvariant();
    }
}
