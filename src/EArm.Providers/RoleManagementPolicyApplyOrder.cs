using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Validation;
using EArm.Engine.Providers;

namespace EArm.Providers;

/// <summary>
/// The PIM schedule requests a <c>roleManagementPolicies</c> declaration governs, found on the declarations before any Graph call (for
/// <see cref="IApplyOrderSource"/>): a directory role policy governs the <c>roleEligibilityScheduleRequests</c> and <c>roleAssignmentScheduleRequests</c>
/// of the same role (any scope: directory role policies are tenant-wide); a group policy governs the <c>privilegedAccess/groupEligibilityScheduleRequests</c>
/// and <c>privilegedAccess/groupAssignmentScheduleRequests</c> of the same group and access (member or owner; a computed access matches both). Values are
/// matched the way <see cref="DeclarationConflicts"/> matches them: the same declared resource referenced, the same literal, a role by its name or template
/// id, a group by its uniqueName (the groups resource name) or object id.
/// </summary>
internal static class RoleManagementPolicyApplyOrder
{
    private static readonly string[] RoleRequestTypes = [RoleEligibilityScheduleRules.ResourceType, RoleAssignmentScheduleRules.ResourceType];

    private static readonly string[] GroupRequestTypes = [GroupEligibilityScheduleRules.ResourceType, GroupAssignmentScheduleRules.ResourceType];

    private const string GroupsType = "Microsoft.Entra/groups";

    /// <summary>The declared requests <paramref name="policy"/> governs (applied after it).</summary>
    public static IReadOnlyList<ResourceInstance> GovernedRequests(ResourceInstance policy, IReadOnlyList<ResourceInstance> declared)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(declared);
        if (policy.Declaration.Properties is not JsonObject properties)
        {
            return [];
        }

        var path = DeclarationConflicts.PropertiesPath(policy);
        var requests = declared.Where(d => !ReferenceEquals(d, policy) && !d.IsIgnored && d.Declaration.Properties is JsonObject).ToList();
        switch (RoleManagementPolicyRules.ScopeKind(properties, IsExpression))
        {
            case RoleManagementPolicyRules.DirectoryRoleScope:
            {
                string? role = RoleOf(policy, properties);
                return role is null
                    ? []
                    : requests.Where(r => RoleRequestTypes.Any(t => ResourceIdentifier.TypeEquals(r.Type, t))
                        && RoleOf(r, (JsonObject)r.Declaration.Properties!) == role).ToList();
            }

            case RoleManagementPolicyRules.GroupScope:
            {
                var (member, owner) = PolicyAccess(properties);
                var groups = PolicyGroups(policy, properties, path, declared);
                string? literal = GroupLiteral(properties, "group") ?? GroupLiteral(properties, "scopeId");
                return requests.Where(r =>
                {
                    if (!GroupRequestTypes.Any(t => ResourceIdentifier.TypeEquals(r.Type, t)))
                    {
                        return false;
                    }

                    var requestProperties = (JsonObject)r.Declaration.Properties!;
                    var (requestMember, requestOwner) = DeclarationConflicts.AccessKinds(requestProperties);
                    if (!((member && requestMember) || (owner && requestOwner)))
                    {
                        return false;
                    }

                    var requestGroups = DeclarationConflicts.TargetGroups(r, requestProperties, DeclarationConflicts.PropertiesPath(r), declared);
                    string? requestLiteral = GroupLiteral(requestProperties, "group") ?? GroupLiteral(requestProperties, "groupId");
                    return requestGroups.Any(groups.Contains) || (literal is not null && literal == requestLiteral);
                }).ToList();
            }

            default:
                return [];
        }
    }

    /// <summary>A role spelling where a built-in role name and its template id are the same role.</summary>
    private static string? RoleOf(ResourceInstance instance, JsonObject properties)
    {
        string? spelling = DeclarationConflicts.RoleSpelling(instance, properties, DeclarationConflicts.PropertiesPath(instance));
        return spelling is not null && spelling.StartsWith("name:", StringComparison.Ordinal) && RoleAssignmentsRules.ResolveRoleOffline(spelling["name:".Length..]) is { } templateId
            ? "id:" + RoleAssignmentsRules.NormalizeId(templateId)
            : spelling;
    }

    /// <summary>The access kinds a group policy names: the literal accessId (or roleDefinitionId), or both when it is computed or missing.</summary>
    private static (bool Member, bool Owner) PolicyAccess(JsonObject properties)
    {
        string? access = DeclarationConflicts.Literal(properties, "accessId") ?? DeclarationConflicts.Literal(properties, "roleDefinitionId");
        return GroupEligibilityScheduleRules.NormalizeAccessId(access) is { } normalized
            ? (normalized == GroupEligibilityScheduleRules.Member, normalized == GroupEligibilityScheduleRules.Owner)
            : (true, true);
    }

    /// <summary>The declared groups a group policy names: referenced by <c>group</c> or <c>scopeId</c>, or named (uniqueName) by a literal <c>group</c>.</summary>
    private static HashSet<ResourceInstance> PolicyGroups(ResourceInstance policy, JsonObject properties, Core.Json.JsonPath path, IReadOnlyList<ResourceInstance> declared)
    {
        string? groupKey = JsonObjects.FindKey(properties, "group");
        string? scopeKey = JsonObjects.FindKey(properties, "scopeId");
        string? named = DeclarationConflicts.Literal(properties, "group");
        var groups = new HashSet<ResourceInstance>();
        foreach (var candidate in declared)
        {
            if (candidate.IsIgnored || !ResourceIdentifier.TypeEquals(candidate.Type, GroupsType))
            {
                continue;
            }

            bool referenced = (groupKey is not null && DeclarationConflicts.IsReferencedBy(policy, path.Append(groupKey), candidate))
                || (scopeKey is not null && DeclarationConflicts.IsReferencedBy(policy, path.Append(scopeKey), candidate));
            if (referenced || (named is not null && string.Equals(named, candidate.Id.NameLeaf, StringComparison.OrdinalIgnoreCase)))
            {
                groups.Add(candidate);
            }
        }

        return groups;
    }

    /// <summary>A literal group spelling (an object id normalised, a uniqueName lower case), or <c>null</c>.</summary>
    private static string? GroupLiteral(JsonObject properties, string name)
    {
        return DeclarationConflicts.Literal(properties, name) is { } text
            ? RoleAssignmentsRules.IsGuid(text) ? RoleAssignmentsRules.NormalizeId(text) : text.ToLowerInvariant()
            : null;
    }

    private static bool IsExpression(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue(out string? text) && ExpressionSyntax.IsExpression(text);
    }
}
