using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EArm.Core.Json;
using static EArm.Core.Validation.PimScheduleRules;
using static EArm.Core.Validation.RoleManagementPolicyRules;

namespace EArm.Core.Validation;

/// <summary>
/// The rule-level checks of <see cref="RoleManagementPolicyRules.Check"/>: every declared rule of <c>properties.rules[]</c> (its id and type, the properties
/// its type carries, the target, and the expiration, enablement, approval, authentication context and notification fields).
/// </summary>
internal static class RoleManagementPolicyRuleChecks
{
    // ---- rules ----

    public static void CheckRules(JsonObject properties, Func<JsonNode?, bool> isOpaque, List<PimRequestFinding> findings)
    {
        var rules = Property(properties, "rules");
        if (rules is null || IsAbsent(rules.Value.Value))
        {
            findings.Add(new(JsonPath.Root.Append("rules"), "properties.rules is required: the policy rules to manage (only the declared rule ids are changed)"));
            return;
        }

        if (isOpaque(rules.Value.Value))
        {
            return;
        }

        var rulesPath = JsonPath.Root.Append(rules.Value.Key);
        if (rules.Value.Value is not JsonArray array)
        {
            findings.Add(new(rulesPath, $"properties.{rules.Value.Key} must be an array of rules", SchemaCovered: true));
            return;
        }

        if (array.Count == 0)
        {
            findings.Add(new(rulesPath, $"properties.{rules.Value.Key} must declare at least one rule"));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < array.Count; i++)
        {
            var path = rulesPath.Append(i);
            string where = $"properties.{rules.Value.Key}[{i}]";
            if (isOpaque(array[i]))
            {
                continue;
            }

            if (array[i] is not JsonObject rule)
            {
                findings.Add(new(path, $"{where} must be an object", SchemaCovered: true));
                continue;
            }

            if (CheckRule(rule, path, where, isOpaque, findings) is { } id && !seen.Add(id))
            {
                findings.Add(new(path, $"{where}: the rule id '{id}' is declared twice"));
            }
        }
    }

    /// <summary>Checks one rule; returns its literal id (for the duplicate check), or <c>null</c>.</summary>
    private static string? CheckRule(JsonObject rule, JsonPath path, string where, Func<JsonNode?, bool> isOpaque, List<PimRequestFinding> findings)
    {
        var idProperty = Property(rule, "id");
        if (idProperty is null || IsAbsent(idProperty.Value.Value))
        {
            findings.Add(new(path.Append("id"), $"{where}.id is required: the rule id, for example Expiration_EndUser_Assignment", SchemaCovered: true));
            return null;
        }

        if (isOpaque(idProperty.Value.Value))
        {
            return null;
        }

        if (!TryString(idProperty.Value.Value, out string? id) || RuleId.Match(id.Trim()) is not { Success: true } match)
        {
            findings.Add(new(path.Append(idProperty.Value.Key), $"{where}.id '{Render(idProperty.Value.Value)}' is not a PIM policy rule id; rule ids are Expiration_, Enablement_, Approval_ or AuthenticationContext_ followed by Admin_ or EndUser_ and Assignment or Eligibility, or Notification_ followed by Admin_, Requestor_ or Approver_ and the same caller and level (for example Approval_EndUser_Assignment)"));
            return null;
        }

        id = id.Trim();
        var type = RuleTypeOfId(id)!;
        if (Judged(rule, "ruleType", isOpaque) is (string ruleTypeKey, var ruleTypeNode))
        {
            var declared = TryString(ruleTypeNode, out string? ruleType) ? FindRuleType(ruleType) : null;
            if (declared is null)
            {
                findings.Add(new(path.Append(ruleTypeKey), $"{where}.{ruleTypeKey} must be one of {string.Join(", ", RuleTypes.Select(t => t.RuleType))}"));
            }
            else if (declared != type)
            {
                findings.Add(new(path.Append(ruleTypeKey), $"{where}.{ruleTypeKey} {declared.RuleType} does not match the rule id {id} (a {type.RuleType}); rule ids start with their type ({type.IdPrefix}_...)"));
            }
        }

        foreach (var property in rule)
        {
            if (!IsCommonRuleProperty(property.Key) && !type.Properties.Contains(property.Key, StringComparer.OrdinalIgnoreCase))
            {
                findings.Add(new(path.Append(property.Key), $"{where}.{property.Key} is not a property of a {type.RuleType} (expected id, ruleType, target, {string.Join(", ", type.Properties)})"));
            }
        }

        CheckTarget(rule, match, path, where, isOpaque, findings);
        switch (type.IdPrefix)
        {
            case "Expiration":
                if (Judged(rule, "maximumDuration", isOpaque) is (string durationKey, var duration) && !(TryString(duration, out string? text) && TryParseDuration(text, out _)))
                {
                    findings.Add(new(path.Append(durationKey), $"{where}.{durationKey} '{Render(duration)}' is not a positive ISO 8601 duration (for example PT8H or P365D)"));
                }

                break;
            case "Enablement":
                CheckEnumArray(rule, "enabledRules", EnabledRules, path, where, isOpaque, findings);
                break;
            case "Approval":
                CheckApproval(rule, path, where, isOpaque, findings);
                break;
            case "AuthenticationContext":
                CheckAuthenticationContext(rule, path, where, isOpaque, findings);
                break;
            default:
                CheckEnum(rule, "notificationType", NotificationTypes, path, where, isOpaque, findings);
                CheckEnum(rule, "notificationLevel", NotificationLevels, path, where, isOpaque, findings);
                if (CheckEnum(rule, "recipientType", RecipientTypes, path, where, isOpaque, findings) is (string recipientKey, string recipient)
                    && !recipient.Equals(match.Groups["recipient"].Value, StringComparison.Ordinal))
                {
                    findings.Add(new(path.Append(recipientKey), $"{where}.{recipientKey} {recipient} does not match the rule id {id} (recipient {match.Groups["recipient"].Value})"));
                }

                break;
        }

        return id;
    }

    private static void CheckTarget(JsonObject rule, Match id, JsonPath path, string where, Func<JsonNode?, bool> isOpaque, List<PimRequestFinding> findings)
    {
        if (Judged(rule, "target", isOpaque) is not (string targetKey, var targetNode))
        {
            return;
        }

        var targetPath = path.Append(targetKey);
        string targetWhere = where + "." + targetKey;
        if (targetNode is not JsonObject target)
        {
            findings.Add(new(targetPath, $"{targetWhere} must be an object ({{ caller, operations, level }})", SchemaCovered: true));
            return;
        }

        foreach (var property in target)
        {
            if (!TargetProperties.Contains(property.Key, StringComparer.OrdinalIgnoreCase) && !property.Key.Equals("@odata.type", StringComparison.Ordinal))
            {
                findings.Add(new(targetPath.Append(property.Key), $"{targetWhere}.{property.Key} is not a property of a rule target (expected {string.Join(", ", TargetProperties)})"));
            }
        }

        foreach (var (name, allowed, expected) in new[] { ("caller", Callers, id.Groups["caller"].Value), ("level", Levels, id.Groups["level"].Value) })
        {
            if (CheckEnum(target, name, allowed, targetPath, targetWhere, isOpaque, findings) is (string key, string value) && !value.Equals(expected, StringComparison.Ordinal))
            {
                findings.Add(new(targetPath.Append(key), $"{targetWhere}.{key} {value} does not match the rule id {JsonValueText(rule, "id")} ({name} {expected}); the id names the caller and level the rule applies to"));
            }
        }

        CheckEnumArray(target, "operations", Operations, targetPath, targetWhere, isOpaque, findings);
    }

    private static void CheckApproval(JsonObject rule, JsonPath path, string where, Func<JsonNode?, bool> isOpaque, List<PimRequestFinding> findings)
    {
        if (Judged(rule, "setting", isOpaque) is not (string settingKey, var settingNode))
        {
            return;
        }

        var settingPath = path.Append(settingKey);
        string settingWhere = where + "." + settingKey;
        if (settingNode is not JsonObject setting)
        {
            findings.Add(new(settingPath, $"{settingWhere} must be an object", SchemaCovered: true));
            return;
        }

        foreach (var property in setting)
        {
            if (!SettingProperties.Contains(property.Key, StringComparer.OrdinalIgnoreCase))
            {
                findings.Add(new(settingPath.Append(property.Key), $"{settingWhere}.{property.Key} is not a property of the approval setting (expected {string.Join(", ", SettingProperties)})"));
            }
        }

        CheckEnum(setting, "approvalMode", ApprovalModes, settingPath, settingWhere, isOpaque, findings);
        bool required = Judged(setting, "isApprovalRequired", isOpaque) is (_, JsonValue flag) && flag.TryGetValue(out bool value) && value;
        var stages = Property(setting, "approvalStages");
        if (stages is null || IsAbsent(stages.Value.Value))
        {
            if (required)
            {
                findings.Add(new(settingPath.Append("approvalStages"), $"{settingWhere}.isApprovalRequired is true but no approvalStages are declared; an approval rule that requires approval needs at least one approver, or nobody could approve an activation"));
            }

            return;
        }

        if (isOpaque(stages.Value.Value))
        {
            return;
        }

        var stagesPath = settingPath.Append(stages.Value.Key);
        if (stages.Value.Value is not JsonArray stageArray)
        {
            findings.Add(new(stagesPath, $"{settingWhere}.{stages.Value.Key} must be an array", SchemaCovered: true));
            return;
        }

        if (stageArray.Count > 2)
        {
            findings.Add(new(stagesPath, $"{settingWhere}.{stages.Value.Key} declares {stageArray.Count} stages; PIM approval has one or two stages"));
        }

        if (required && stageArray.Count == 0)
        {
            findings.Add(new(stagesPath, $"{settingWhere}.isApprovalRequired is true but approvalStages is empty; an approval rule that requires approval needs at least one approver"));
        }

        for (int s = 0; s < stageArray.Count; s++)
        {
            var stagePath = stagesPath.Append(s);
            string stageWhere = $"{settingWhere}.{stages.Value.Key}[{s}]";
            if (isOpaque(stageArray[s]))
            {
                continue;
            }

            if (stageArray[s] is not JsonObject stage)
            {
                findings.Add(new(stagePath, $"{stageWhere} must be an object", SchemaCovered: true));
                continue;
            }

            foreach (var property in stage)
            {
                if (!StageProperties.Contains(property.Key, StringComparer.OrdinalIgnoreCase))
                {
                    findings.Add(new(stagePath.Append(property.Key), $"{stageWhere}.{property.Key} is not a property of an approval stage (expected {string.Join(", ", StageProperties)})"));
                }
            }

            int primary = CheckApprovers(stage, "primaryApprovers", stagePath, stageWhere, isOpaque, findings, out bool primaryOpaque);
            if (required && primary == 0 && !primaryOpaque)
            {
                findings.Add(new(stagePath.Append("primaryApprovers"), $"{stageWhere} has no primaryApprovers; an approval rule that requires approval needs at least one approver (a user or a group) in every stage"));
            }

            int escalation = CheckApprovers(stage, "escalationApprovers", stagePath, stageWhere, isOpaque, findings, out bool escalationOpaque);
            if (Judged(stage, "isEscalationEnabled", isOpaque) is (_, JsonValue escalate) && escalate.TryGetValue(out bool escalates) && escalates && escalation == 0 && !escalationOpaque)
            {
                findings.Add(new(stagePath.Append("escalationApprovers"), $"{stageWhere}.isEscalationEnabled is true but no escalationApprovers are declared"));
            }
        }
    }

    /// <summary>Checks an approver array; returns the number of approvers declared (opaque ones included).</summary>
    private static int CheckApprovers(JsonObject stage, string name, JsonPath stagePath, string stageWhere, Func<JsonNode?, bool> isOpaque, List<PimRequestFinding> findings, out bool opaque)
    {
        opaque = false;
        var approvers = Property(stage, name);
        if (approvers is null || IsAbsent(approvers.Value.Value))
        {
            return 0;
        }

        if (isOpaque(approvers.Value.Value))
        {
            opaque = true;
            return 0;
        }

        var arrayPath = stagePath.Append(approvers.Value.Key);
        string arrayWhere = stageWhere + "." + approvers.Value.Key;
        if (approvers.Value.Value is not JsonArray array)
        {
            findings.Add(new(arrayPath, $"{arrayWhere} must be an array of approvers ({{ id, userType }})", SchemaCovered: true));
            return 0;
        }

        for (int a = 0; a < array.Count; a++)
        {
            var approverPath = arrayPath.Append(a);
            string approverWhere = $"{arrayWhere}[{a}]";
            if (isOpaque(array[a]))
            {
                continue;
            }

            if (array[a] is not JsonObject approver)
            {
                findings.Add(new(approverPath, $"{approverWhere} must be an object ({{ id, userType }})", SchemaCovered: true));
                continue;
            }

            foreach (var property in approver)
            {
                if (!ApproverProperties.Contains(property.Key, StringComparer.OrdinalIgnoreCase))
                {
                    findings.Add(new(approverPath.Append(property.Key), $"{approverWhere}.{property.Key} is not a property of an approver (expected id, userType, description, isBackup)"));
                }
            }

            var id = Property(approver, "id");
            if (id is null || IsAbsent(id.Value.Value))
            {
                findings.Add(new(approverPath.Append("id"), $"{approverWhere}.id is required: the object id of the approving user or group", SchemaCovered: true));
            }
            else if (!isOpaque(id.Value.Value) && !(TryString(id.Value.Value, out string? text) && Guid.TryParse(text, out _)))
            {
                findings.Add(new(approverPath.Append(id.Value.Key), $"{approverWhere}.{id.Value.Key} '{Render(id.Value.Value)}' is not a GUID", SchemaCovered: true));
            }

            var userType = Property(approver, "userType");
            if (userType is null || IsAbsent(userType.Value.Value))
            {
                findings.Add(new(approverPath.Append("userType"), $"{approverWhere}.userType is required: User (a single user approves) or Group (any member of the group approves)", SchemaCovered: true));
            }
            else
            {
                CheckEnum(approver, "userType", UserTypes, approverPath, approverWhere, isOpaque, findings);
            }

            // Graph accepts isBackup on a PATCH but never returns it on a policy read: true could neither be verified nor checked for drift (every plan
            // would show a Modify), so only false (the default, the same as absent) is accepted.
            if (Judged(approver, "isBackup", isOpaque) is ({ } backupKey, JsonValue backup) && backup.TryGetValue(out bool isBackup) && isBackup)
            {
                findings.Add(new(approverPath.Append(backupKey), $"{approverWhere}.{backupKey} true is not supported: Graph does not return isBackup when the policy is read, so EARM could neither verify it nor detect drift (every plan would show a change). Leave isBackup out (false is the default)"));
            }
        }

        return array.Count;
    }

    private static void CheckAuthenticationContext(JsonObject rule, JsonPath path, string where, Func<JsonNode?, bool> isOpaque, List<PimRequestFinding> findings)
    {
        bool enabled = Judged(rule, "isEnabled", isOpaque) is (_, JsonValue flag) && flag.TryGetValue(out bool value) && value;
        var claim = Property(rule, "claimValue");
        bool claimDeclared = claim is not null && !IsAbsent(claim.Value.Value) && !(TryString(claim.Value.Value, out string? empty) && empty.Length == 0);
        if (enabled && !claimDeclared)
        {
            findings.Add(new(path.Append("claimValue"), $"{where}.isEnabled is true but no claimValue is declared; name the authentication context (c1 to c99) the activation must satisfy"));
            return;
        }

        if (claimDeclared && !isOpaque(claim!.Value.Value) && !(TryString(claim.Value.Value, out string? text) && ClaimValue.IsMatch(text.Trim())))
        {
            findings.Add(new(path.Append(claim.Value.Key), $"{where}.{claim.Value.Key} '{Render(claim.Value.Value)}' is not an authentication context id; use c1 to c99 (the id of a Microsoft.Entra/conditionalAccess/authenticationContextClassReferences resource)"));
        }
    }

    // ---- helpers ----

    /// <summary>Checks a string enum; returns the key and canonical value when it is known and valid.</summary>
    internal static (string Key, string Value)? CheckEnum(JsonObject owner, string name, IReadOnlyList<string> allowed, JsonPath ownerPath, string ownerWhere, Func<JsonNode?, bool> isOpaque, List<PimRequestFinding> findings)
    {
        if (Judged(owner, name, isOpaque) is not (string key, var value))
        {
            return null;
        }

        if (TryString(value, out string? text) && Canonical(allowed, text) is { } canonical)
        {
            return (key, canonical);
        }

        findings.Add(new(ownerPath.Append(key), $"{ownerWhere}.{key} '{Render(value)}' must be one of {string.Join(", ", allowed)}"));
        return null;
    }

    internal static void CheckEnumArray(JsonObject owner, string name, IReadOnlyList<string> allowed, JsonPath ownerPath, string ownerWhere, Func<JsonNode?, bool> isOpaque, List<PimRequestFinding> findings)
    {
        if (Judged(owner, name, isOpaque) is not (string key, var value))
        {
            return;
        }

        if (value is not JsonArray array)
        {
            findings.Add(new(ownerPath.Append(key), $"{ownerWhere}.{key} must be an array", SchemaCovered: true));
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < array.Count; i++)
        {
            if (isOpaque(array[i]))
            {
                continue;
            }

            if (!TryString(array[i], out string? text) || Canonical(allowed, text) is not { } canonical)
            {
                findings.Add(new(ownerPath.Append(key).Append(i), $"{ownerWhere}.{key}[{i}] '{Render(array[i])}' must be one of {string.Join(", ", allowed)}"));
            }
            else if (!seen.Add(canonical))
            {
                findings.Add(new(ownerPath.Append(key).Append(i), $"{ownerWhere}.{key} lists {canonical} twice"));
            }
        }
    }

    internal static string JsonValueText(JsonObject owner, string name)
    {
        return Property(owner, name) is (_, var value) && TryString(value, out string? text) ? text.Trim() : "?";
    }

    internal static string Render(JsonNode? node)
    {
        return TryString(node, out string? text) ? text : node?.ToJsonString() ?? "null";
    }
}
