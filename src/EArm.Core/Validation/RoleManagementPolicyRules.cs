using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EArm.Core.Json;
using static EArm.Core.Validation.PimScheduleRules;

namespace EArm.Core.Validation;

/// <summary>One rule type of a PIM role management policy: its ARM <c>ruleType</c>, the rule id prefix, the Graph <c>@odata.type</c> and the properties it carries.</summary>
/// <param name="RuleType">The ARM <c>ruleType</c> (<c>RoleManagementPolicyExpirationRule</c>...).</param>
/// <param name="IdPrefix">The prefix of the rule ids of this type (<c>Expiration</c> for <c>Expiration_EndUser_Assignment</c>).</param>
/// <param name="Properties">The type's own properties (besides <c>id</c>, <c>ruleType</c> and <c>target</c>).</param>
public sealed record PolicyRuleType(string RuleType, string IdPrefix, IReadOnlyList<string> Properties)
{
    /// <summary>The Graph <c>@odata.type</c>: <c>#microsoft.graph.unified</c> + the ARM rule type.</summary>
    public string ODataType => "#microsoft.graph.unified" + RuleType;
}

/// <summary>
/// The pure rules of <c>Microsoft.Entra/roleManagementPolicies</c> (the PIM policy of a directory role or of a PIM for Groups membership / ownership),
/// shared by the offline validator (literal values) and the provider (evaluated values). The declaration is shaped like ARM's
/// <c>Microsoft.Authorization/roleManagementPolicies</c>: <c>properties.rules[]</c> with <c>id</c>, <c>ruleType</c>, <c>target</c> and the per-type fields; the
/// policy is named by its assignment (<c>scopeType</c>, <c>scopeId</c>, <c>roleDefinitionId</c>) or by the EARM sugar <c>role</c> (a directory role) or
/// <c>group</c> + <c>accessId</c> (a group). Rules checked: the property set and the scope combinations, the rule ids (unique, of the known
/// <c>Type_Caller_Level</c> shapes, agreeing with <c>ruleType</c>, <c>target.caller</c> / <c>target.level</c> and a notification's <c>recipientType</c>), the
/// properties each rule type carries, the target and notification enums, ISO 8601 <c>maximumDuration</c>, <c>enabledRules</c> ⊂ {MultiFactorAuthentication,
/// Justification, Ticketing}, an authentication context <c>claimValue</c> c1..c99, and an approval rule that requires approval naming at least one approver
/// in every stage (approvers are <c>{ id, userType: User | Group }</c>). Enums are case-insensitive and normalised to the ARM / Graph casing.
/// </summary>
public static class RoleManagementPolicyRules
{
    public const string ResourceType = "Microsoft.Entra/roleManagementPolicies";

    /// <summary>The <c>scopeType</c> of a directory role policy (Graph also accepts <c>Directory</c> in the lookup; EARM normalises to this).</summary>
    public const string DirectoryRoleScope = "DirectoryRole";

    /// <summary>The <c>scopeType</c> of a PIM for Groups policy.</summary>
    public const string GroupScope = "Group";

    /// <summary>The only <c>scopeId</c> of a directory role policy.</summary>
    public const string RootScopeId = "/";

    public static IReadOnlyList<PolicyRuleType> RuleTypes { get; } =
    [
        new("RoleManagementPolicyExpirationRule", "Expiration", ["isExpirationRequired", "maximumDuration"]),
        new("RoleManagementPolicyEnablementRule", "Enablement", ["enabledRules"]),
        new("RoleManagementPolicyApprovalRule", "Approval", ["setting"]),
        new("RoleManagementPolicyAuthenticationContextRule", "AuthenticationContext", ["isEnabled", "claimValue"]),
        new("RoleManagementPolicyNotificationRule", "Notification", ["notificationType", "recipientType", "notificationLevel", "notificationRecipients", "isDefaultRecipientsEnabled"]),
    ];

    /// <summary>
    /// The EARM property of a group policy that opts in to onboarding the group in PIM for Groups: <c>{ "temporaryEligiblePrincipalId": "…" }</c>, the
    /// principal a short-lived member eligibility is created for (and removed again) when Graph lists no policy for the group. Never sent to Graph.
    /// </summary>
    public const string OnboardingProperty = "onboarding";

    /// <summary>The member of <see cref="OnboardingProperty"/>: the object id of the user or service principal made eligible for the onboarding.</summary>
    public const string TemporaryEligiblePrincipalIdProperty = "temporaryEligiblePrincipalId";

    /// <summary>The property names a policy declaration accepts (ARM names and EARM sugar).</summary>
    public static IReadOnlyList<string> AllowedProperties { get; } = ["scopeType", "scopeId", "roleDefinitionId", "role", "group", "accessId", "rules", OnboardingProperty];

    public static IReadOnlyList<string> Callers { get; } = ["Admin", "EndUser"];

    public static IReadOnlyList<string> Levels { get; } = ["Assignment", "Eligibility"];

    public static IReadOnlyList<string> Operations { get; } = ["All", "Activate", "Deactivate", "Assign", "Update", "Remove", "Extend", "Renew"];

    public static IReadOnlyList<string> EnabledRules { get; } = ["MultiFactorAuthentication", "Justification", "Ticketing"];

    public static IReadOnlyList<string> RecipientTypes { get; } = ["Requestor", "Approver", "Admin"];

    public static IReadOnlyList<string> NotificationLevels { get; } = ["None", "Critical", "All"];

    public static IReadOnlyList<string> NotificationTypes { get; } = ["Email"];

    public static IReadOnlyList<string> ApprovalModes { get; } = ["SingleStage", "Serial", "Parallel", "NoApproval"];

    public static IReadOnlyList<string> UserTypes { get; } = ["User", "Group"];

    public static IReadOnlyList<string> TargetProperties { get; } = ["caller", "operations", "level", "targetObjects", "inheritableSettings", "enforcedSettings"];

    public static IReadOnlyList<string> SettingProperties { get; } = ["isApprovalRequired", "isApprovalRequiredForExtension", "isRequestorJustificationRequired", "approvalMode", "approvalStages"];

    public static IReadOnlyList<string> StageProperties { get; } = ["approvalStageTimeOutInDays", "isApproverJustificationRequired", "escalationTimeInMinutes", "primaryApprovers", "isEscalationEnabled", "escalationApprovers"];

    public static IReadOnlyList<string> ApproverProperties { get; } = ["id", "userType", "description", "isBackup"];

    internal static readonly Regex RuleId = new(@"^(?<type>Expiration|Enablement|Approval|AuthenticationContext)_(?<caller>Admin|EndUser)_(?<level>Assignment|Eligibility)$|^Notification_(?<recipient>Admin|Requestor|Approver)_(?<caller>Admin|EndUser)_(?<level>Assignment|Eligibility)$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));

    internal static readonly Regex ClaimValue = new("^c([1-9]|[1-9][0-9])$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The rule type of an ARM <c>ruleType</c> (any casing), or <c>null</c>.</summary>
    public static PolicyRuleType? FindRuleType(string? ruleType)
    {
        return RuleTypes.FirstOrDefault(t => t.RuleType.Equals(ruleType?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The rule type of a Graph <c>@odata.type</c> (with or without the leading <c>#</c>), or <c>null</c>.</summary>
    public static PolicyRuleType? FindODataType(string? odataType)
    {
        string? text = odataType?.Trim().TrimStart('#');
        return RuleTypes.FirstOrDefault(t => t.ODataType.TrimStart('#').Equals(text, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The rule type a rule id names (<c>Expiration_EndUser_Assignment</c> → the expiration rule), or <c>null</c> for an id of an unknown shape.</summary>
    public static PolicyRuleType? RuleTypeOfId(string? id)
    {
        if (id is null || RuleId.Match(id.Trim()) is not { Success: true } match)
        {
            return null;
        }

        string prefix = match.Groups["type"].Success ? match.Groups["type"].Value : "Notification";
        return RuleTypes.First(t => t.IdPrefix == prefix);
    }

    /// <summary>The canonical spelling of <paramref name="value"/> among <paramref name="allowed"/> (case-insensitive), or <c>null</c>.</summary>
    public static string? Canonical(IReadOnlyList<string> allowed, string? value)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        return allowed.FirstOrDefault(a => a.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The canonical <c>scopeType</c>: <c>DirectoryRole</c> (also for Graph's <c>Directory</c>) or <c>Group</c>, or <c>null</c>.</summary>
    public static string? NormalizeScopeType(string? scopeType)
    {
        string? text = scopeType?.Trim();
        if (string.Equals(text, DirectoryRoleScope, StringComparison.OrdinalIgnoreCase) || string.Equals(text, "Directory", StringComparison.OrdinalIgnoreCase))
        {
            return DirectoryRoleScope;
        }

        return string.Equals(text, GroupScope, StringComparison.OrdinalIgnoreCase) ? GroupScope : null;
    }

    /// <summary>
    /// The scope kind a declaration means (<see cref="DirectoryRoleScope"/> or <see cref="GroupScope"/>): <c>role</c> → directory role; <c>group</c> or
    /// <c>accessId</c> → group; else the literal <c>scopeType</c>. <c>null</c> when it is computed or missing.
    /// </summary>
    public static string? ScopeKind(JsonObject properties, Func<JsonNode?, bool> isOpaque)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(isOpaque);
        if (Declared(properties, "role"))
        {
            return DirectoryRoleScope;
        }

        if (Declared(properties, "group") || Declared(properties, "accessId"))
        {
            return GroupScope;
        }

        return Judged(properties, "scopeType", isOpaque) is (_, var value) && TryString(value, out string? text) ? NormalizeScopeType(text) : null;
    }

    /// <summary>The problems of a declaration. Nothing is changed.</summary>
    /// <param name="properties">The declared (offline) or evaluated (plan time) properties.</param>
    /// <param name="isOpaque">True for a value that cannot be judged yet (a template expression, a deploy-time value, a secret).</param>
    public static IReadOnlyList<PimRequestFinding> Check(JsonObject properties, Func<JsonNode?, bool> isOpaque)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(isOpaque);
        var findings = new List<PimRequestFinding>();
        foreach (var property in properties)
        {
            if (!AllowedProperties.Contains(property.Key, StringComparer.OrdinalIgnoreCase))
            {
                findings.Add(new(JsonPath.Root.Append(property.Key), $"properties.{property.Key} is not a property of a role management policy; expected role, or group and accessId, or scopeType, scopeId and roleDefinitionId, rules, and for a group policy onboarding (ARM's read-only displayName, description, scope and effectiveRules are not declared)"));
            }
        }

        CheckScope(properties, isOpaque, findings);
        CheckOnboarding(properties, isOpaque, findings);
        RoleManagementPolicyRuleChecks.CheckRules(properties, isOpaque, findings);
        return findings;
    }

    // ---- onboarding ----

    /// <summary>
    /// <c>properties.onboarding</c> (absent or null: no opt-in): an object with exactly <c>temporaryEligiblePrincipalId</c>, a GUID (or a template expression,
    /// typically a <c>reference(…).id</c>), and only on a group policy (a directory role always has a policy).
    /// </summary>
    private static void CheckOnboarding(JsonObject properties, Func<JsonNode?, bool> isOpaque, List<PimRequestFinding> findings)
    {
        if (Property(properties, OnboardingProperty) is not (string key, var node) || IsAbsent(node))
        {
            return;
        }

        var path = JsonPath.Root.Append(key);
        if (ScopeKind(properties, isOpaque) == DirectoryRoleScope)
        {
            findings.Add(new(path, $"properties.{key} applies only to a group policy (PIM for Groups onboarding); a directory role always has a policy, so remove it"));
            return;
        }

        if (isOpaque(node))
        {
            return;
        }

        if (node is not JsonObject onboarding)
        {
            findings.Add(new(path, $"properties.{key} must be an object: {{ \"{TemporaryEligiblePrincipalIdProperty}\": \"<object id of a user or service principal>\" }}", SchemaCovered: true));
            return;
        }

        foreach (var member in onboarding)
        {
            if (!member.Key.Equals(TemporaryEligiblePrincipalIdProperty, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new(path.Append(member.Key), $"properties.{key}.{member.Key} is not a property of onboarding; expected only {TemporaryEligiblePrincipalIdProperty}"));
            }
        }

        if (Property(onboarding, TemporaryEligiblePrincipalIdProperty) is not (string principalKey, var principal) || IsAbsent(principal))
        {
            findings.Add(new(path.Append(TemporaryEligiblePrincipalIdProperty), $"properties.{key}.{TemporaryEligiblePrincipalIdProperty} is required: the object id of the user or service principal made eligible (member, for an hour) to onboard the group, then removed again"));
            return;
        }

        if (!isOpaque(principal) && !(TryString(principal, out string? text) && Guid.TryParse(text, out _)))
        {
            string shown = TryString(principal, out string? raw) ? raw : principal?.ToJsonString() ?? "null";
            findings.Add(new(path.Append(principalKey), $"properties.{key}.{principalKey} '{shown}' is not a GUID; name the principal by its object id, or reference it (for example [reference(resourceId('Microsoft.Entra/users', 'pim-onboarding')).id])", SchemaCovered: true));
        }
    }

    // ---- scope ----

    private static void CheckScope(JsonObject properties, Func<JsonNode?, bool> isOpaque, List<PimRequestFinding> findings)
    {
        var root = JsonPath.Root;
        bool role = Declared(properties, "role");
        bool group = Declared(properties, "group");
        bool access = Declared(properties, "accessId");
        string? kind = ScopeKind(properties, isOpaque);

        if (role && (group || access))
        {
            findings.Add(new(root.Append(KeyOf(properties, "role")), "declare properties.role (a directory role policy) or properties.group and accessId (a group policy), not both"));
            return;
        }

        if (Judged(properties, "scopeType", isOpaque) is (string scopeTypeKey, var scopeType))
        {
            string? declaredKind = TryString(scopeType, out string? text) ? NormalizeScopeType(text) : null;
            if (declaredKind is null)
            {
                findings.Add(new(root.Append(scopeTypeKey), $"properties.{scopeTypeKey} must be DirectoryRole (a directory role policy) or Group (a PIM for Groups policy)"));
            }
            else if ((role && declaredKind != DirectoryRoleScope) || ((group || access) && declaredKind != GroupScope))
            {
                findings.Add(new(root.Append(scopeTypeKey), $"properties.{scopeTypeKey} {declaredKind} contradicts the {(role ? "role" : "group")} sugar; omit scopeType"));
            }
        }

        if (role)
        {
            CheckNonEmptyString(properties, KeyOf(properties, "role"), isOpaque, findings);
            Exclusive(properties, "role", "roleDefinitionId", findings);
            if (Judged(properties, "scopeId", isOpaque) is (string scopeIdKey, var scopeId) && !(TryString(scopeId, out string? id) && id.Trim() == RootScopeId))
            {
                findings.Add(new(root.Append(scopeIdKey), $"properties.{scopeIdKey} of a directory role policy must be '/' (omit it)"));
            }
        }
        else if (group || access)
        {
            if (!group && !Declared(properties, "scopeId"))
            {
                findings.Add(new(root.Append("group"), "a group policy needs the group: properties.group (the group's uniqueName or object id) or properties.scopeId (its object id)"));
            }

            Exclusive(properties, "group", "scopeId", findings);
            Exclusive(properties, "accessId", "roleDefinitionId", findings);
            if (group)
            {
                CheckNonEmptyString(properties, KeyOf(properties, "group"), isOpaque, findings);
            }

            if (!access && !Declared(properties, "roleDefinitionId"))
            {
                findings.Add(new(root.Append("accessId"), "a group policy needs properties.accessId: member (the policy of the group's membership) or owner (of its ownership)"));
            }
        }
        else
        {
            foreach (var name in new[] { "scopeType", "scopeId", "roleDefinitionId" })
            {
                if (!Declared(properties, name))
                {
                    findings.Add(new(root.Append(name), $"properties.{name} is required (or name the policy with properties.role, or properties.group and accessId)"));
                }
            }
        }

        // The values, once the kind is known.
        if (kind == DirectoryRoleScope)
        {
            if (!role && Judged(properties, "scopeId", isOpaque) is (string scopeIdKey, var scopeId) && !(TryString(scopeId, out string? id) && id.Trim() == RootScopeId))
            {
                findings.Add(new(root.Append(scopeIdKey), $"properties.{scopeIdKey} of a directory role policy must be '/' (directory role policies are tenant-wide)"));
            }

            if (Judged(properties, "roleDefinitionId", isOpaque) is (string roleKey, _))
            {
                CheckGuid(properties, roleKey, isOpaque, findings);
            }
        }
        else if (kind == GroupScope)
        {
            if (Judged(properties, "scopeId", isOpaque) is (string scopeIdKey, _))
            {
                CheckGuid(properties, scopeIdKey, isOpaque, findings);
            }

            foreach (var name in new[] { "accessId", "roleDefinitionId" })
            {
                if (Judged(properties, name, isOpaque) is (string key, var value) && !(TryString(value, out string? text) && GroupEligibilityScheduleRules.NormalizeAccessId(text) is not null))
                {
                    findings.Add(new(root.Append(key), $"properties.{key} of a group policy must be member or owner"));
                }
            }
        }
    }

    // ---- normalisation ----

    /// <summary>
    /// Normalises evaluated rules in place before they are diffed and sent: <c>ruleType</c> set from the rule id, the target, notification, approval-mode and
    /// approver enums and <c>enabledRules</c> in their canonical casing, approver ids lower case. Opaque values are left alone; run <see cref="Check"/> first.
    /// </summary>
    public static void NormalizeRules(JsonArray rules, Func<JsonNode?, bool> isOpaque)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(isOpaque);
        foreach (var node in rules)
        {
            if (node is not JsonObject rule || Judged(rule, "id", isOpaque) is not (string idKey, var idNode) || !TryString(idNode, out string? id) || RuleTypeOfId(id) is not { } type)
            {
                continue;
            }

            rule[idKey] = id.Trim();
            Rename(rule, "ruleType", type.RuleType);
            if (Property(rule, "target") is (_, JsonObject target))
            {
                CanonicalValue(target, "caller", Callers, isOpaque);
                CanonicalValue(target, "level", Levels, isOpaque);
                CanonicalArray(target, "operations", Operations, isOpaque);
            }

            CanonicalArray(rule, "enabledRules", EnabledRules, isOpaque);
            CanonicalValue(rule, "notificationType", NotificationTypes, isOpaque);
            CanonicalValue(rule, "recipientType", RecipientTypes, isOpaque);
            CanonicalValue(rule, "notificationLevel", NotificationLevels, isOpaque);
            if (Property(rule, "setting") is (_, JsonObject setting))
            {
                CanonicalValue(setting, "approvalMode", ApprovalModes, isOpaque);
                if (Property(setting, "approvalStages") is (_, JsonArray stages))
                {
                    foreach (var stage in stages.OfType<JsonObject>())
                    {
                        foreach (var name in new[] { "primaryApprovers", "escalationApprovers" })
                        {
                            if (Property(stage, name) is (_, JsonArray approvers))
                            {
                                foreach (var approver in approvers.OfType<JsonObject>())
                                {
                                    CanonicalValue(approver, "userType", UserTypes, isOpaque);
                                    if (Property(approver, "id") is (string approverKey, var approverId) && !isOpaque(approverId) && TryString(approverId, out string? text) && Guid.TryParse(text, out var guid))
                                    {
                                        approver[approverKey] = guid.ToString("D");
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    // ---- helpers ----

    internal static bool IsCommonRuleProperty(string name)
    {
        return name.Equals("id", StringComparison.OrdinalIgnoreCase) || name.Equals("ruleType", StringComparison.OrdinalIgnoreCase) || name.Equals("target", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool Declared(JsonObject obj, string name)
    {
        return Property(obj, name) is (_, var value) && !IsAbsent(value);
    }

    internal static string KeyOf(JsonObject obj, string name)
    {
        return Property(obj, name)?.Key ?? name;
    }

    private static void Exclusive(JsonObject properties, string sugar, string canonical, List<PimRequestFinding> findings)
    {
        if (Declared(properties, sugar) && Declared(properties, canonical))
        {
            findings.Add(new(JsonPath.Root.Append(KeyOf(properties, sugar)), $"declare one of properties.{sugar} and properties.{canonical}, not both"));
        }
    }



    private static void CanonicalValue(JsonObject owner, string name, IReadOnlyList<string> allowed, Func<JsonNode?, bool> isOpaque)
    {
        if (Property(owner, name) is (string key, var value) && !isOpaque(value) && TryString(value, out string? text) && Canonical(allowed, text) is { } canonical)
        {
            owner[key] = canonical;
        }
    }

    private static void CanonicalArray(JsonObject owner, string name, IReadOnlyList<string> allowed, Func<JsonNode?, bool> isOpaque)
    {
        if (Property(owner, name) is (string key, JsonArray array) && !isOpaque(array))
        {
            for (int i = 0; i < array.Count; i++)
            {
                if (!isOpaque(array[i]) && TryString(array[i], out string? text) && Canonical(allowed, text) is { } canonical)
                {
                    array[i] = canonical;
                }
            }
        }
    }

    /// <summary>Sets <paramref name="name"/> (whatever its casing) to <paramref name="value"/>.</summary>
    private static void Rename(JsonObject owner, string name, string value)
    {
        string key = KeyOf(owner, name);
        owner[key] = value;
    }


}
