using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EArm.Core;
using EArm.Core.Json;
using EArm.Core.Validation;
using EArm.Engine;
using EArm.Engine.Planning;
using EArm.Engine.Providers;

namespace EArm.Providers;

/// <summary>
/// The template-level conflicts of the time-bound PIM active assignment types (<see cref="IDeclarationConflictSource"/>), found once the instances are
/// expanded and their same-template references resolved, before any Graph call:
/// <list type="bullet">
/// <item><c>roleAssignmentScheduleRequests</c> vs a <c>roleAssignments</c> resource or a principal's <c>directoryRoles</c> entry for the same principal,
/// role and scope: Graph holds one assignment for both, so one would make it time-bound and the other permanent (and re-create it when it expires).</item>
/// <item><c>privilegedAccess/groupAssignmentScheduleRequests</c> vs the target group's <c>members</c> (accessId member) or <c>owners</c> (owner): the
/// declared list is authoritative, and a PIM active member is a real member, so the edge pass would remove it; vs the principal's <c>memberOf</c> /
/// <c>ownerOf</c> naming the group: ensure-only, so it would add the membership back permanently when the schedule ends.</item>
/// </list>
/// Two values are the same when they reference the same declared resource (<c>reference()</c> / <c>resourceId()</c>, resolved per copy iteration), or
/// are the same literal, or the same expression text (outside copy loops); a role is also matched between its name (<c>role</c>,
/// <c>roleTemplate('…')</c>) spellings and a scope defaults to <c>/</c>. Values spelled differently (a literal object id against an expression) are not
/// matched here; the live checks of the providers (a permanent assignment under the tuple) cover what is already deployed.
/// </summary>
internal static class DeclarationConflicts
{
    private const string GroupsType = "Microsoft.Entra/groups";

    private static readonly Regex RoleTemplateCall = new(@"^\[\s*roleTemplate\(\s*'((?:[^']|'')*)'\s*\)\s*\]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // ---- roleAssignmentScheduleRequests ----

    public static IEnumerable<DeclarationConflict> RoleAssignment(ResourceInstance instance, IReadOnlyList<ResourceInstance> declared)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(declared);
        if (instance.Declaration.Properties is not JsonObject properties)
        {
            yield break;
        }

        var path = PropertiesPath(instance);
        var principal = Spelling(instance, properties, path, "principalId");
        var role = RoleSpelling(instance, properties, path);
        var scope = ScopeSpelling(instance, properties, path);
        if (principal is null || role is null || scope is null)
        {
            yield break;
        }

        foreach (var other in declared)
        {
            if (ReferenceEquals(other, instance) || other.IsIgnored || other.Declaration.Properties is not JsonObject otherProperties)
            {
                continue;
            }

            var otherPath = PropertiesPath(other);
            if (ResourceIdentifier.TypeEquals(other.Type, RoleAssignmentsProvider.ResourceType)
                && principal == Spelling(other, otherProperties, otherPath, "principalId")
                && role == RoleSpelling(other, otherProperties, otherPath)
                && scope == ScopeSpelling(other, otherProperties, otherPath))
            {
                yield return new DeclarationConflict(
                    EngineDiagnosticCodes.PimAssignmentConflict,
                    $"{instance.Describe()} and {other.Describe()} declare the same directory role assignment (principal, role and scope). Graph holds one assignment for both: {RoleAssignmentScheduleRequestsProvider.ResourceType} makes it time-bound, {RoleAssignmentsProvider.ResourceType} permanent (and creates it again when it expires). Declare one of them: roleAssignments for a permanent assignment, roleAssignmentScheduleRequests for a time-bound one.",
                    path);
                continue;
            }

            if (!IsReferencedBy(instance, path.Append("principalId"), other)
                || !JsonObjects.TryGetIgnoreCase(otherProperties, "directoryRoles", out var rolesNode) || rolesNode is not JsonArray entries)
            {
                continue;
            }

            var rolesPath = otherPath.Append(JsonObjects.FindKey(otherProperties, "directoryRoles") ?? "directoryRoles");
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i] is JsonObject entry
                    && role == RoleSpelling(other, entry, rolesPath.Append(i))
                    && scope == ScopeSpelling(other, entry, rolesPath.Append(i)))
                {
                    yield return new DeclarationConflict(
                        EngineDiagnosticCodes.PimAssignmentConflict,
                        $"{instance.Describe()} makes a time-bound active assignment of the role that {other.Describe()} declares in directoryRoles[{i}] (same role and scope). Graph holds one assignment for both, and the edge pass would create it again, permanently, when the schedule ends. Declare one of them: directoryRoles (or roleAssignments) for a permanent assignment, roleAssignmentScheduleRequests for a time-bound one.",
                        path);
                }
            }
        }
    }

    // ---- privilegedAccess/groupAssignmentScheduleRequests ----

    public static IEnumerable<DeclarationConflict> GroupAssignment(ResourceInstance instance, IReadOnlyList<ResourceInstance> declared, DeploymentMode mode)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(declared);
        if (instance.Declaration.Properties is not JsonObject properties)
        {
            yield break;
        }

        var path = PropertiesPath(instance);
        var (member, owner) = AccessKinds(properties);
        var groups = TargetGroups(instance, properties, path, declared).ToList();
        foreach (var group in groups)
        {
            if (group.Declaration.Properties is not JsonObject groupProperties)
            {
                continue;
            }

            foreach (var (applies, list, access) in new[] { (member, "members", GroupEligibilityScheduleRules.Member), (owner, "owners", GroupEligibilityScheduleRules.Owner) })
            {
                bool declaresList = JsonObjects.TryGetIgnoreCase(groupProperties, list, out var value) ? IsDeclared(value, mode) : PropertyCopyBlock.Declares(groupProperties, list);
                if (applies && declaresList)
                {
                    yield return new DeclarationConflict(
                        EngineDiagnosticCodes.PimAssignmentConflict,
                        $"{instance.Describe()} makes a time-bound active {access} of {group.Describe()}, which declares an authoritative {list} list. A PIM active {access} is a real {access} of the group, so the edge pass would remove it as undeclared (and listing the principal in {list} would make it permanent). Remove the {list} list from the group (manage its permanent {list} another way), or drop the time-bound assignment; use privilegedAccess/groupEligibilityScheduleRequests for just-in-time access, which never touches {list}.",
                        path);
                }
            }
        }

        // The principal side: memberOf / ownerOf are ensure-only and would add the membership back, permanently, when the schedule ends.
        foreach (var principal in declared.Where(d => !ReferenceEquals(d, instance) && !d.IsIgnored && IsReferencedBy(instance, path.Append("principalId"), d)))
        {
            if (principal.Declaration.Properties is not JsonObject principalProperties)
            {
                continue;
            }

            foreach (var (applies, property, access) in new[] { (member, "memberOf", GroupEligibilityScheduleRules.Member), (owner, "ownerOf", GroupEligibilityScheduleRules.Owner) })
            {
                if (!applies || PropertyCopyBlock.DeclaredKey(principalProperties, property) is not { } key)
                {
                    continue;
                }

                var listPath = PropertiesPath(principal).Append(key);
                bool names = groups.Any(g => IsReferencedBy(principal, listPath, g))
                    || (Literal(properties, "groupId") is { } groupId && principalProperties[key] is JsonArray entries
                        && entries.Any(e => e is JsonValue v && v.TryGetValue(out string? text) && JsonObjects.KeyEquals(text, groupId)));
                if (names)
                {
                    yield return new DeclarationConflict(
                        EngineDiagnosticCodes.PimAssignmentConflict,
                        $"{instance.Describe()} makes a time-bound active {access} of a group that {principal.Describe()} also lists in {key}. {key} is ensure-only: the edge pass would add the {access}ship back, permanently, when the schedule ends. Declare one of them: {key} for a permanent {access}ship, groupAssignmentScheduleRequests for a time-bound one.",
                        path);
                }
            }
        }
    }

    /// <summary>The access kinds a declaration can mean: the literal accessId, or both when it is computed or missing.</summary>
    internal static (bool Member, bool Owner) AccessKinds(JsonObject properties)
    {
        return Literal(properties, "accessId") is { } access && GroupEligibilityScheduleRules.NormalizeAccessId(access) is { } normalized
            ? (normalized == GroupEligibilityScheduleRules.Member, normalized == GroupEligibilityScheduleRules.Owner)
            : (true, true);
    }

    /// <summary>The declared groups the request targets: referenced by <c>groupId</c>, or named (uniqueName, the groups' resource name) by <c>group</c>.</summary>
    internal static IEnumerable<ResourceInstance> TargetGroups(ResourceInstance instance, JsonObject properties, JsonPath path, IReadOnlyList<ResourceInstance> declared)
    {
        string? groupKey = JsonObjects.FindKey(properties, "group");
        string? groupText = groupKey is null ? null : RawText(properties[groupKey]);
        foreach (var candidate in declared)
        {
            if (ReferenceEquals(candidate, instance) || candidate.IsIgnored || !ResourceIdentifier.TypeEquals(candidate.Type, GroupsType))
            {
                continue;
            }

            bool referenced = IsReferencedBy(instance, path.Append(JsonObjects.FindKey(properties, "groupId") ?? "groupId"), candidate)
                || (groupKey is not null && IsReferencedBy(instance, path.Append(groupKey), candidate));
            bool named = groupText is not null
                && (ExpressionSyntax.IsExpression(groupText)
                    ? string.Equals(Compact(groupText), candidate.Declaration.Name is { } name ? Compact(name) : null, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(groupText.Trim(), candidate.Id.NameLeaf, StringComparison.OrdinalIgnoreCase));
            if (referenced || named)
            {
                yield return candidate;
            }
        }
    }

    // ---- spellings ----

    /// <summary>
    /// A comparable spelling of a property value: the declared resources it references, else its literal, else its expression text (none inside a copy
    /// loop, where the same text means different values per iteration); <c>null</c> when absent or not comparable.
    /// </summary>
    internal static string? Spelling(ResourceInstance instance, JsonObject owner, JsonPath ownerPath, string name)
    {
        if (JsonObjects.FindKey(owner, name) is not { } key || JsonObjects.IsAbsent(owner[key]))
        {
            return null;
        }

        var targets = References(instance, ownerPath.Append(key));
        if (targets.Count > 0)
        {
            return "ref:" + string.Join("|", targets);
        }

        if (RawText(owner[key]) is not { } text)
        {
            return null;
        }

        if (!ExpressionSyntax.IsExpression(text))
        {
            return "lit:" + (RoleAssignmentsRules.IsGuid(text) ? RoleAssignmentsRules.NormalizeId(text) : text.Trim().ToLowerInvariant());
        }

        return instance.Copy is null ? "expr:" + Compact(text).ToLowerInvariant() : null;
    }

    /// <summary>The role: a template id (literal GUID), a role name (<c>role</c> literal or <c>roleTemplate('…')</c>), or another spelling.</summary>
    internal static string? RoleSpelling(ResourceInstance instance, JsonObject owner, JsonPath ownerPath)
    {
        foreach (string name in new[] { "roleDefinitionId", "role" })
        {
            if (JsonObjects.FindKey(owner, name) is not { } key || JsonObjects.IsAbsent(owner[key]))
            {
                continue;
            }

            if (RawText(owner[key]) is { } text)
            {
                if (!ExpressionSyntax.IsExpression(text))
                {
                    return RoleAssignmentsRules.IsGuid(text) ? "id:" + RoleAssignmentsRules.NormalizeId(text) : "name:" + text.Trim().ToLowerInvariant();
                }

                if (RoleTemplateCall.Match(text.Trim()) is { Success: true } call)
                {
                    return "name:" + call.Groups[1].Value.Replace("''", "'", StringComparison.Ordinal).Trim().ToLowerInvariant();
                }
            }

            return Spelling(instance, owner, ownerPath, name);
        }

        return null;
    }

    /// <summary>The scope: <c>/</c> when none is declared, a canonical directory scope, an administrative unit (its id as a scope, or its spelling), or an app scope.</summary>
    private static string? ScopeSpelling(ResourceInstance instance, JsonObject owner, JsonPath ownerPath)
    {
        if (JsonObjects.FindKey(owner, "administrativeUnit") is { } unitKey && !JsonObjects.IsAbsent(owner[unitKey]))
        {
            string? text = RawText(owner[unitKey]);
            return text is not null && !ExpressionSyntax.IsExpression(text) && RoleAssignmentsRules.IsGuid(text)
                ? "scope:" + RoleAssignmentsRules.NormalizeScope("/administrativeUnits/" + RoleAssignmentsRules.NormalizeId(text)).ToLowerInvariant()
                : Spelling(instance, owner, ownerPath, unitKey) is { } unit ? "unit:" + unit : null;
        }

        if (JsonObjects.FindKey(owner, "directoryScopeId") is { } scopeKey && !JsonObjects.IsAbsent(owner[scopeKey]))
        {
            string? text = RawText(owner[scopeKey]);
            return text is not null && !ExpressionSyntax.IsExpression(text)
                ? "scope:" + RoleAssignmentsRules.NormalizeScope(text).ToLowerInvariant()
                : Spelling(instance, owner, ownerPath, scopeKey) is { } scope ? "scope:" + scope : null;
        }

        if (JsonObjects.FindKey(owner, "appScopeId") is { } appKey && !JsonObjects.IsAbsent(owner[appKey]))
        {
            return Spelling(instance, owner, ownerPath, appKey) is { } app ? "app:" + app : null;
        }

        return "scope:" + RoleAssignmentsRules.RootScope;
    }

    // ---- references ----

    /// <summary>The ids (lowercase, sorted) of the same-template resources the value at <paramref name="path"/> (or under it) references.</summary>
    private static List<string> References(ResourceInstance instance, JsonPath path)
    {
        return instance.ObjectReferences.Concat(instance.EdgeReferences)
            .Where(o => o.IsSameTemplate && o.Target is not null && o.Path.StartsWith(path))
            .Select(o => o.Target!.Id.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>True when the value at <paramref name="path"/> of <paramref name="instance"/> references <paramref name="target"/>.</summary>
    internal static bool IsReferencedBy(ResourceInstance instance, JsonPath path, ResourceInstance target)
    {
        return References(instance, path).Contains(target.PlannedId.Id.ToLowerInvariant(), StringComparer.Ordinal);
    }

    // ---- JSON ----

    /// <summary>
    /// True for a declared value. A literal empty array counts only in Complete mode, where an empty members list is authoritative too and removes
    /// everything (an empty owners list on a group is refused there by the groups provider); in Incremental mode it declares nothing. An expression
    /// (not evaluated yet) always counts.
    /// </summary>
    private static bool IsDeclared(JsonNode? node, DeploymentMode mode)
    {
        return node is not null
            && !(node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.Null)
            && !(node is JsonArray { Count: 0 } && mode == DeploymentMode.Incremental);
    }

    internal static JsonPath PropertiesPath(ResourceInstance instance)
    {
        return instance.Declaration.Path.Append("properties");
    }

    private static string? RawText(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) ? text : null;
    }

    /// <summary>A literal (not an expression) string property, trimmed, or <c>null</c>.</summary>
    internal static string? Literal(JsonObject properties, string name)
    {
        return JsonObjects.FindKey(properties, name) is { } key && RawText(properties[key]) is { } text && !ExpressionSyntax.IsExpression(text) ? text.Trim() : null;
    }

    /// <summary>Expression text without whitespace, for comparing two spellings of the same expression.</summary>
    private static string Compact(string text)
    {
        return string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
    }
}
