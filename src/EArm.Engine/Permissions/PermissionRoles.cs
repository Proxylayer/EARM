using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Engine.Providers;

namespace EArm.Engine.Permissions;

/// <summary>
/// The Entra roles a resource needs of the signed-in user (<see cref="PermissionMode.Delegated"/>): the overlay's roles, roles needed together and roles of
/// conditional properties; and what the template says about the group a catalog resource adds, which narrows both the roles and the app permissions.
/// </summary>
internal static class PermissionRoles
{
    private const string PrivilegedRoleAdministrator = "Privileged Role Administrator";

    /// <summary>What the template says about the group a catalog resource adds.</summary>
    internal enum GroupKind
    {
        NotApplicable,
        Unknown,
        Regular,
        RoleAssignable,
    }

    internal static void AddRoles(ResourceInstance instance, string resource, PermissionAccumulator roles, GroupKind catalogGroup)
    {
        var overlay = instance.Definition.Permissions;
        if (overlay.Roles.Count > 0)
        {
            roles.AddGroupSet(overlay.Roles, resource, "provider");
        }

        if (RolesTogether(overlay.Raw?["rolesAllOf"]) is { Count: > 0 } parts)
        {
            roles.AddTogether(NarrowForGroup(parts, catalogGroup), resource, catalogGroup switch
            {
                GroupKind.RoleAssignable => "provider: the group is role-assignable",
                GroupKind.Regular => "provider: the group is not role-assignable",
                _ => "provider (Privileged Role Administrator only for a role-assignable group)",
            });
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

            var names = roleArray.Select(n => n?.GetValue<string>()).OfType<string>().ToArray();
            if (names.Length > 0)
            {
                roles.AddGroupSet(names, resource, "condition: " + entry.Key);
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
    /// The parts of a catalog resource's together-requirement for the kind of group it adds: only Privileged Role Administrator may change a
    /// role-assignable group (Learn: groups-concept), and it is not a role that changes any other group (Learn: entitlement-management-delegate lists
    /// Groups Administrator, Intune Administrator and, as a catalog owner, User Administrator). An unknown kind keeps every alternative.
    /// </summary>
    private static IEnumerable<IReadOnlyList<string>> NarrowForGroup(IReadOnlyList<IReadOnlyList<string>> parts, GroupKind kind)
    {
        foreach (var part in parts)
        {
            bool hasPra = part.Contains(PrivilegedRoleAdministrator, StringComparer.OrdinalIgnoreCase);
            yield return (kind, hasPra, part.Count > 1) switch
            {
                (GroupKind.RoleAssignable, true, _) => [PrivilegedRoleAdministrator],
                (GroupKind.Regular, true, true) => part.Where(r => !r.Equals(PrivilegedRoleAdministrator, StringComparison.OrdinalIgnoreCase)).ToArray(),
                _ => part,
            };
        }
    }

    internal static bool IsCatalogResource(string type)
    {
        return type.EndsWith("/entitlementManagement/catalogResources", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the group a catalog resource adds (its <c>originId</c>) is role-assignable, as far as the template says: a group of the template that
    /// <c>originId</c> references with a literal <c>isAssignableToRole</c> (absent counts as false); <see cref="GroupKind.Unknown"/> for an existing
    /// group, a computed flag, or an <c>originId</c> that references no template group.
    /// </summary>
    internal static GroupKind CatalogResourceGroupKind(ResourceInstance instance, IReadOnlyList<ResourceInstance> instances)
    {
        var originPath = instance.Declaration.Path.Append("properties");
        if (instance.Declaration.Properties is JsonObject declared && JsonObjects.FindKey(declared, "originId") is { } key)
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
