using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core.Json;
using EArm.Core.Validation;
using EArm.Engine.Providers;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>
/// The two shapes of a PIM policy rule and the comparable view between them. Templates use ARM's shape (<c>ruleType</c>, approvers as
/// <c>{ id, userType: User | Group }</c>); Graph uses <c>@odata.type</c> (<c>#microsoft.graph.unifiedRoleManagementPolicyApprovalRule</c>...) and approvers as
/// subject sets (<c>#microsoft.graph.singleUser { userId }</c>, <c>#microsoft.graph.groupMembers { groupId }</c>). Every other field has the same name and
/// meaning in both (Learn: unifiedrolemanagementpolicyrule-update, approvalsettings, unifiedapprovalstage). An approver's <c>description</c> is read-only in
/// Graph: it is never compared or sent.
/// </summary>
public static class RoleManagementPolicyShape
{
    public const string ODataTypeProperty = "@odata.type";

    public const string SingleUser = "#microsoft.graph.singleUser";

    public const string GroupMembers = "#microsoft.graph.groupMembers";

    private static readonly string[] ApproverArrays = ["primaryApprovers", "escalationApprovers"];

    /// <summary>A live Graph rule in ARM's shape (normalised casing, approvers mapped back, Graph's <c>@odata.type</c> annotations removed).</summary>
    public static JsonObject ToArm(JsonObject graphRule)
    {
        ArgumentNullException.ThrowIfNull(graphRule);
        var rule = (JsonObject)JsonValues.Clone(graphRule)!;
        string? odataType = JsonObjects.GetString(rule, ODataTypeProperty);
        rule.Remove(ODataTypeProperty);
        var type = RoleManagementPolicyRules.FindODataType(odataType) ?? RoleManagementPolicyRules.RuleTypeOfId(JsonObjects.GetId(rule));
        if (type is not null)
        {
            rule["ruleType"] = type.RuleType;
        }

        if (rule["target"] is JsonObject target)
        {
            target.Remove(ODataTypeProperty);
        }

        if (rule["setting"] is JsonObject setting)
        {
            setting.Remove(ODataTypeProperty);
            foreach (var stage in Stages(setting))
            {
                stage.Remove(ODataTypeProperty);
                foreach (var name in ApproverArrays)
                {
                    if (stage[name] is JsonArray approvers)
                    {
                        stage[name] = new JsonArray(approvers.Select(a => (JsonNode?)(a is JsonObject subject ? ApproverToArm(subject) : JsonValues.Clone(a))).ToArray());
                    }
                }
            }
        }

        RoleManagementPolicyRules.NormalizeRules(new JsonArray(rule), static _ => false);
        return Comparable(rule);
    }

    /// <summary>
    /// The PATCH body of a rule in ARM's shape: <c>@odata.type</c> first (Graph refuses a rule PATCH without it), <c>ruleType</c> dropped, approvers as Graph
    /// subject sets, null members left out (an explicit <c>[]</c> is kept: it clears a list).
    /// </summary>
    public static JsonObject ToGraph(JsonObject armRule)
    {
        ArgumentNullException.ThrowIfNull(armRule);
        string id = JsonObjects.GetId(armRule) ?? throw new ArgumentException("The rule has no id.", nameof(armRule));
        var type = RoleManagementPolicyRules.RuleTypeOfId(id) ?? throw new ArgumentException($"'{id}' is not a PIM policy rule id.", nameof(armRule));
        var body = new JsonObject { [ODataTypeProperty] = type.ODataType };
        foreach (var property in armRule)
        {
            if (property.Key.Equals("ruleType", StringComparison.OrdinalIgnoreCase) || property.Key.Equals(ODataTypeProperty, StringComparison.Ordinal) || IsNull(property.Value))
            {
                continue;
            }

            body[property.Key] = WithoutNulls(JsonValues.Clone(property.Value));
        }

        if (body["setting"] is JsonObject setting)
        {
            foreach (var stage in Stages(setting))
            {
                foreach (var name in ApproverArrays)
                {
                    if (stage[name] is JsonArray approvers)
                    {
                        stage[name] = new JsonArray(approvers.Select(a => (JsonNode?)(a is JsonObject approver ? ApproverToGraph(approver) : JsonValues.Clone(a))).ToArray());
                    }
                }
            }
        }

        return body;
    }

    /// <summary>
    /// The comparable copy of a rule in ARM's shape: approvers without their read-only <c>description</c> (and an <c>isBackup</c> false) and sorted (by userType, then id), an empty
    /// <c>claimValue</c> as absent. Applied to the desired and the live rule alike, so the comparison is order- and casing-insensitive where Graph is.
    /// </summary>
    public static JsonObject Comparable(JsonObject armRule)
    {
        ArgumentNullException.ThrowIfNull(armRule);
        var rule = (JsonObject)JsonValues.Clone(armRule)!;
        if (JsonObjects.FindKey(rule, "claimValue") is { } claimKey && JsonValues.TryGetString(rule[claimKey], out var claim) && claim.Length == 0)
        {
            rule[claimKey] = null;
        }

        if (JsonObjects.TryGetIgnoreCase(rule, "setting", out var settingNode) && settingNode is JsonObject setting)
        {
            foreach (var stage in Stages(setting))
            {
                foreach (var name in ApproverArrays)
                {
                    if (JsonObjects.FindKey(stage, name) is { } key && stage[key] is JsonArray approvers)
                    {
                        var sorted = approvers
                            .Select(a => a is JsonObject approver ? WithoutDescription(approver) : JsonValues.Clone(a))
                            .OrderBy(a => a is JsonObject o ? (JsonObjects.GetString(o, "userType") ?? string.Empty).ToLowerInvariant() : string.Empty, StringComparer.Ordinal)
                            .ThenBy(a => a is JsonObject o ? (JsonObjects.GetId(o) ?? string.Empty).ToLowerInvariant() : CanonicalJson.Serialize(a), StringComparer.Ordinal)
                            .ToArray();
                        stage[key] = new JsonArray(sorted);
                    }
                }
            }
        }

        return rule;
    }

    /// <summary>
    /// Fills, in place, every sub-property of a declared rule the declaration leaves out from the live rule (Graph PATCH replaces a rule's complex members
    /// whole): nested objects recursively, approval stages by position, approvers by id. A declared value always wins. A <c>maximumDuration</c> equal to the
    /// live one in another spelling (<c>PT480M</c> for <c>PT8H</c>) takes the live spelling.
    /// </summary>
    public static void FillFromLive(JsonObject desired, JsonObject live)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(live);
        FillObject(desired, live);
        if (JsonObjects.FindKey(desired, "maximumDuration") is { } key
            && JsonValues.TryGetString(desired[key], out var declared) && JsonObjects.GetString(live, "maximumDuration") is { } current
            && PimScheduleRules.TryParseDuration(declared, out var declaredSpan) && PimScheduleRules.TryParseDuration(current, out var liveSpan) && declaredSpan == liveSpan)
        {
            desired[key] = current;
        }
    }

    private static void FillObject(JsonObject desired, JsonObject live)
    {
        foreach (var property in live)
        {
            if (JsonObjects.FindKey(desired, property.Key) is not { } key)
            {
                if (!JsonObjects.IsAbsent(property.Value))
                {
                    desired[property.Key] = JsonValues.Clone(property.Value);
                }

                continue;
            }

            switch (desired[key])
            {
                case JsonObject desiredChild when property.Value is JsonObject liveChild:
                    FillObject(desiredChild, liveChild);
                    break;
                case JsonArray desiredArray when property.Value is JsonArray liveArray && key.Equals("approvalStages", StringComparison.OrdinalIgnoreCase):
                    for (int i = 0; i < desiredArray.Count && i < liveArray.Count; i++)
                    {
                        if (desiredArray[i] is JsonObject desiredStage && liveArray[i] is JsonObject liveStage)
                        {
                            FillObject(desiredStage, liveStage);
                        }
                    }

                    break;
                case JsonArray desiredApprovers when property.Value is JsonArray liveApprovers && ApproverArrays.Contains(key, StringComparer.OrdinalIgnoreCase):
                    foreach (var approver in desiredApprovers.OfType<JsonObject>())
                    {
                        if (liveApprovers.OfType<JsonObject>().FirstOrDefault(l => JsonObjects.KeyEquals(JsonObjects.GetId(l), JsonObjects.GetId(approver))) is { } match)
                        {
                            FillObject(approver, match);
                        }
                    }

                    break;
            }
        }
    }

    private static IEnumerable<JsonObject> Stages(JsonObject setting)
    {
        return JsonObjects.TryGetIgnoreCase(setting, "approvalStages", out var stages) && stages is JsonArray array ? array.OfType<JsonObject>() : [];
    }

    private static JsonObject ApproverToArm(JsonObject subject)
    {
        string? odataType = JsonObjects.GetString(subject, ODataTypeProperty);
        var approver = new JsonObject();
        if (string.Equals(odataType?.TrimStart('#'), SingleUser.TrimStart('#'), StringComparison.OrdinalIgnoreCase))
        {
            approver["id"] = Id(JsonObjects.GetString(subject, "userId"));
            approver["userType"] = "User";
        }
        else if (string.Equals(odataType?.TrimStart('#'), GroupMembers.TrimStart('#'), StringComparison.OrdinalIgnoreCase))
        {
            approver["id"] = Id(JsonObjects.GetString(subject, "groupId"));
            approver["userType"] = "Group";
        }
        else
        {
            // A subject set ARM has no userType for (Graph lists more kinds than PIM accepts): kept as Graph sent it, so a declaration can never match it by accident.
            return (JsonObject)JsonValues.Clone(subject)!;
        }

        if (JsonObjects.TryGetIgnoreCase(subject, "isBackup", out var backup) && !JsonObjects.IsAbsent(backup))
        {
            approver["isBackup"] = JsonValues.Clone(backup);
        }

        return approver;
    }

    private static JsonObject ApproverToGraph(JsonObject approver)
    {
        string? userType = JsonObjects.GetString(approver, "userType");
        string? id = JsonObjects.GetId(approver);
        JsonObject subject;
        if (string.Equals(userType, "User", StringComparison.OrdinalIgnoreCase))
        {
            subject = new JsonObject { [ODataTypeProperty] = SingleUser, ["userId"] = Id(id) };
        }
        else if (string.Equals(userType, "Group", StringComparison.OrdinalIgnoreCase))
        {
            subject = new JsonObject { [ODataTypeProperty] = GroupMembers, ["groupId"] = Id(id) };
        }
        else
        {
            // A live subject set of another kind (filled in from the live rule): sent back as Graph returned it.
            return (JsonObject)WithoutNulls(JsonValues.Clone(approver))!;
        }

        if (JsonObjects.TryGetIgnoreCase(approver, "isBackup", out var backup) && !JsonObjects.IsAbsent(backup))
        {
            subject["isBackup"] = JsonValues.Clone(backup);
        }

        return subject;
    }

    private static JsonNode WithoutDescription(JsonObject approver)
    {
        var copy = (JsonObject)JsonValues.Clone(approver)!;
        if (JsonObjects.FindKey(copy, "description") is { } key)
        {
            copy.Remove(key);
        }

        // isBackup false is the default, and Graph v1.0 does not echo it on every subject set: false and absent are the same.
        if (JsonObjects.FindKey(copy, "isBackup") is { } backupKey && JsonValues.TryGetBool(copy[backupKey], out bool backup) && !backup)
        {
            copy.Remove(backupKey);
        }

        return copy;
    }

    private static string? Id(string? id)
    {
        return id is not null && Guid.TryParse(id, out var guid) ? guid.ToString("D") : id;
    }

    private static bool IsNull(JsonNode? node)
    {
        return node is null || (node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.Null);
    }

    /// <summary>A copy without null-valued object members (recursively); arrays keep their members.</summary>
    private static JsonNode? WithoutNulls(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Where(p => IsNull(p.Value)).Select(p => p.Key).ToList())
                {
                    obj.Remove(key);
                }

                foreach (var child in obj.Select(p => p.Value).ToList())
                {
                    WithoutNulls(child);
                }

                return obj;
            case JsonArray array:
                foreach (var child in array)
                {
                    WithoutNulls(child);
                }

                return array;
            default:
                return node;
        }
    }
}
