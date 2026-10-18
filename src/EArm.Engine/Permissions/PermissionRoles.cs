using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Engine.Providers;

namespace EArm.Engine.Permissions;

/// <summary>
/// The Entra roles a resource needs of the signed-in user (<see cref="PermissionMode.Delegated"/>): the overlay's roles, roles needed together and roles of
/// conditional properties; and what the template says about the group a catalog resource adds or a PIM for Groups request targets, which narrows the
/// roles (and, for a catalog resource, the app permissions).
/// </summary>
internal static class PermissionRoles
{
    private const string PrivilegedRoleAdministrator = "Privileged Role Administrator";

    /// <summary>What the template says about the group a catalog resource adds or a PIM for Groups request targets.</summary>
    internal enum GroupKind
    {
        NotApplicable,
        Unknown,
        Regular,
        RoleAssignable,
    }

    internal static void AddRoles(ResourceInstance instance, string resource, PermissionAccumulator roles, GroupKind targetGroup)
    {
        var overlay = instance.Definition.Permissions;

        // A group PIM policy: the group kind narrows only the roles of its onboarding (a PIM for Groups eligibility); the policy itself needs Privileged Role Administrator.
        bool policy = IsRoleManagementPolicy(instance.Type);
        if (overlay.Roles.Count > 0)
        {
            // A PIM for Groups request: only Privileged Role Administrator may manage a role-assignable group, and the other roles any other group.
            if (targetGroup == GroupKind.NotApplicable || policy)
            {
                roles.AddGroupSet(overlay.Roles, resource, "provider");
            }
            else
            {
                roles.AddGroupSet(NarrowForGroup(overlay.Roles, targetGroup), resource, GroupReason(targetGroup));
            }
        }

        if (RolesTogetherForValue(instance, overlay.Raw?["rolesAllOfByValue"]) is { } byValue)
        {
            roles.AddTogether(byValue.Parts, resource, byValue.Reason);
        }
        else if (RolesTogether(overlay.Raw?["rolesAllOf"]) is { Count: > 0 } parts)
        {
            roles.AddTogether(parts.Select(p => NarrowForGroup(p, targetGroup)), resource, GroupReason(targetGroup));
        }

        if (overlay.Raw?["conditional"] is not JsonObject conditional || instance.Declaration.Properties is not JsonObject declared)
        {
            return;
        }

        foreach (var entry in conditional)
        {
            string? key = JsonObjects.FindKey(declared, entry.Key);
            if (key is null || entry.Value is not JsonObject spec || spec["roles"] is not JsonArray roleArray)
            {
                continue;
            }

            // isAssignableToRole: false (a literal) does not make the group role-assignable.
            if (declared[key] is JsonValue flag && flag.TryGetValue<bool>(out bool value) && !value)
            {
                continue;
            }

            IReadOnlyList<string> names = roleArray.Select(n => n?.GetValue<string>()).OfType<string>().ToArray();
            string reason = "condition: " + entry.Key;
            if (policy && string.Equals(entry.Key, "onboarding", StringComparison.OrdinalIgnoreCase) && targetGroup is GroupKind.RoleAssignable or GroupKind.Regular)
            {
                names = NarrowForGroup(names, targetGroup);
                reason += targetGroup == GroupKind.RoleAssignable ? "; the group is role-assignable" : "; the group is not role-assignable";
            }

            if (names.Count > 0)
            {
                roles.AddGroupSet(names, resource, reason);
            }
        }
    }

    /// <summary>The overlay's <c>rolesAllOf</c>: an array of parts, each an array of alternative role names, needed together.</summary>
    private static List<IReadOnlyList<string>>? RolesTogether(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return null;
        }

        var parts = new List<IReadOnlyList<string>>();
        foreach (var item in array)
        {
            if (item is JsonArray part && part.Select(n => n is JsonValue v && v.TryGetValue(out string? name) ? name : null).OfType<string>().ToArray() is { Length: > 0 } names)
            {
                parts.Add(names);
            }
        }

        return parts;
    }

    /// <summary>
    /// The overlay's <c>rolesAllOfByValue</c> (<c>{ property: { value: rolesAllOf parts } }</c>) for the value the instance declares (evaluated when the
    /// offline expansion could, for example from a parameter), in place of <c>rolesAllOf</c>: a catalog resource's roles depend on its <c>originSystem</c>.
    /// <c>null</c> when the property is absent, still computed, or another value.
    /// </summary>
    private static (List<IReadOnlyList<string>> Parts, string Reason)? RolesTogetherForValue(ResourceInstance instance, JsonNode? node)
    {
        if (node is not JsonObject byProperty || (instance.Properties ?? instance.Declaration.Properties) is not JsonObject declared)
        {
            return null;
        }

        foreach (var (property, values) in byProperty)
        {
            if (JsonObjects.FindKey(declared, property) is not { } key || declared[key] is not JsonValue literal || !literal.TryGetValue(out string? value)
                || values is not JsonObject table)
            {
                continue;
            }

            foreach (var (candidate, parts) in table)
            {
                if (candidate.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase) && RolesTogether(parts) is { Count: > 0 } found)
                {
                    return (found, $"provider: {property} {candidate}");
                }
            }
        }

        return null;
    }

    private static string GroupReason(GroupKind kind)
    {
        return kind switch
        {
            GroupKind.RoleAssignable => "provider: the group is role-assignable",
            GroupKind.Regular => "provider: the group is not role-assignable",
            _ => "provider (Privileged Role Administrator only for a role-assignable group)",
        };
    }

    /// <summary>
    /// The alternatives of one role part for the kind of group the resource adds or targets: only Privileged Role Administrator may change a
    /// role-assignable group (Learn: groups-concept, privilegedaccessgroup-post-eligibilityschedulerequests), and it is not a role that changes any
    /// other group (Learn: entitlement-management-delegate lists Groups Administrator, Intune Administrator and, as a catalog owner, User Administrator;
    /// PIM for Groups lists Directory Writers, Groups Administrator, Identity Governance Administrator and User Administrator). An unknown kind keeps
    /// every alternative.
    /// </summary>
    private static IReadOnlyList<string> NarrowForGroup(IReadOnlyList<string> part, GroupKind kind)
    {
        bool hasPra = part.Contains(PrivilegedRoleAdministrator, StringComparer.OrdinalIgnoreCase);
        return (kind, hasPra, part.Count > 1) switch
        {
            (GroupKind.RoleAssignable, true, _) => [PrivilegedRoleAdministrator],
            (GroupKind.Regular, true, true) => part.Where(r => !r.Equals(PrivilegedRoleAdministrator, StringComparison.OrdinalIgnoreCase)).ToArray(),
            _ => part,
        };
    }

    internal static bool IsCatalogResource(string type)
    {
        return type.EndsWith("/entitlementManagement/catalogResources", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A catalog resource that declares a literal <c>originSystem</c> other than <c>AadGroup</c> (an application or a site) adds no group.</summary>
    private static bool DeclaresOtherOrigin(ResourceInstance instance)
    {
        return (instance.Properties ?? instance.Declaration.Properties) is JsonObject declared && JsonObjects.FindKey(declared, "originSystem") is { } key
            && declared[key] is JsonValue value && value.TryGetValue(out string? origin)
            && !origin.TrimStart().StartsWith('[') && !origin.Trim().Equals("AadGroup", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGroupPimRequest(string type)
    {
        return type.EndsWith("/privilegedAccess/groupEligibilityScheduleRequests", StringComparison.OrdinalIgnoreCase)
            || type.EndsWith("/privilegedAccess/groupAssignmentScheduleRequests", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRoleManagementPolicy(string type)
    {
        return type.EndsWith("/roleManagementPolicies", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The properties that name the group, in order of preference; empty for a type that targets no group.</summary>
    private static string[] GroupProperties(string type)
    {
        return IsCatalogResource(type) ? ["originId"]
            : IsGroupPimRequest(type) ? ["groupId"]
            : IsRoleManagementPolicy(type) ? ["group", "scopeId"]
            : [];
    }

    /// <summary>
    /// Whether the group a catalog resource adds (its <c>originId</c>), a PIM for Groups request targets (its <c>groupId</c>) or a group PIM policy governs
    /// (its <c>group</c> or <c>scopeId</c>) is role-assignable, as far as the template says: a group of the template that the property references with a
    /// literal <c>isAssignableToRole</c> (absent counts as false); <see cref="GroupKind.Unknown"/> for an existing group (including a <c>group</c>
    /// uniqueName), a computed flag, or a property that references no template group (a directory role policy); <see cref="GroupKind.NotApplicable"/> for
    /// a catalog resource of an application or site, and for every other type.
    /// </summary>
    internal static GroupKind TargetGroupKind(ResourceInstance instance, IReadOnlyList<ResourceInstance> instances)
    {
        var properties = GroupProperties(instance.Type);
        if (properties.Length == 0 || (IsCatalogResource(instance.Type) && DeclaresOtherOrigin(instance)))
        {
            return GroupKind.NotApplicable;
        }

        var originPath = instance.Declaration.Path.Append("properties");
        if (instance.Declaration.Properties is JsonObject declared && properties.Select(p => JsonObjects.FindKey(declared, p)).FirstOrDefault(k => k is not null) is { } key)
        {
            originPath = originPath.Append(key);
        }
        else
        {
            return GroupKind.Unknown;
        }

        var targets = instance.ObjectReferences
            .Where(o => o.Target is not null && o.Path.StartsWith(originPath))
            .Select(o => o.Target!.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groups = instances
            .Where(i => i.Type.EndsWith("/groups", StringComparison.OrdinalIgnoreCase) && targets.Contains(i.PlannedId.Id))
            .ToList();
        if (groups.Count != 1)
        {
            return GroupKind.Unknown;
        }

        if (groups[0].Declaration.Properties is not JsonObject group || JsonObjects.FindKey(group, "isAssignableToRole") is not { } flagKey)
        {
            return GroupKind.Regular;
        }

        return group[flagKey] is JsonValue value && value.TryGetValue<bool>(out bool assignable)
            ? assignable ? GroupKind.RoleAssignable : GroupKind.Regular
            : GroupKind.Unknown;
    }
}
