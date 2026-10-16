using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core.Json;
using static EArm.Core.Validation.PimScheduleRules;

namespace EArm.Core.Validation;

/// <summary>
/// The pure rules of <c>Microsoft.Entra/privilegedAccess/groupEligibilityScheduleRequests</c> (PIM for Groups eligibility: a principal made eligible
/// for the membership or ownership of a group), shared by the offline validator (literal values) and the provider (evaluated values): the property set
/// (Graph names plus the EARM sugar <c>group</c>), the group (<c>groupId</c> or <c>group</c>, exactly one), <c>principalId</c>, <c>accessId</c>
/// (<c>member</c> or <c>owner</c>, any casing). The request and schedule rules every PIM type shares are <see cref="PimScheduleRules"/>.
/// </summary>
public static class GroupEligibilityScheduleRules
{
    public const string ResourceType = "Microsoft.Entra/privilegedAccess/groupEligibilityScheduleRequests";

    public const string Member = "member";

    public const string Owner = "owner";

    /// <summary>The property names a request accepts (Graph names and EARM sugar).</summary>
    public static IReadOnlyList<string> AllowedProperties { get; } =
        ["groupId", "group", "principalId", "accessId", "requestType", "scheduleInfo", "justification", "ticketInfo"];

    private static readonly string[] AccessIds = [Member, Owner];

    /// <summary>The Graph accessId (<c>member</c> / <c>owner</c>) for any casing, or <c>null</c>.</summary>
    public static string? NormalizeAccessId(string? accessId)
    {
        return AccessIds.FirstOrDefault(a => a.Equals(accessId?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The problems of a declaration. Nothing is changed.</summary>
    /// <param name="properties">The declared (offline) or evaluated (plan time) properties.</param>
    /// <param name="isOpaque">True for a value that cannot be judged yet (a template expression, a deploy-time value, a secret).</param>
    public static IReadOnlyList<PimRequestFinding> Check(JsonObject properties, Func<JsonNode?, bool> isOpaque)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(isOpaque);
        var findings = new List<PimRequestFinding>();
        var root = JsonPath.Root;

        foreach (var property in properties)
        {
            if (!AllowedProperties.Contains(property.Key, StringComparer.OrdinalIgnoreCase))
            {
                findings.Add(new(root.Append(property.Key), $"properties.{property.Key} is not a property of a group eligibility schedule request; expected groupId or group, principalId, accessId, scheduleInfo, justification, ticketInfo and requestType"));
            }
        }

        CheckGroup(properties, isOpaque, findings);
        CheckRequiredGuid(properties, "principalId", "properties.principalId is required: the object id of the user or group to make eligible", isOpaque, findings);

        var access = Property(properties, "accessId");
        if (access is null || IsAbsent(access.Value.Value))
        {
            findings.Add(new(root.Append(access?.Key ?? "accessId"), "properties.accessId is required: member (eligible membership) or owner (eligible ownership)"));
        }
        else if (!isOpaque(access.Value.Value) && !(TryString(access.Value.Value, out string? text) && NormalizeAccessId(text) is not null))
        {
            findings.Add(new(root.Append(access.Value.Key), $"properties.{access.Value.Key} must be member or owner"));
        }

        CheckRequest(properties, isOpaque, findings);
        return findings;
    }

    /// <summary>Normalises evaluated properties in place: the shared PIM normalisation plus <c>accessId</c> in Graph's lowercase.</summary>
    public static void Normalize(JsonObject properties, Func<JsonNode?, bool> isOpaque)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(isOpaque);
        PimScheduleRules.Normalize(properties, isOpaque);
        if (Property(properties, "accessId") is (string key, var value) && !isOpaque(value) && TryString(value, out string? text) && NormalizeAccessId(text) is { } normalized)
        {
            properties[key] = normalized;
        }
    }

    private static void CheckGroup(JsonObject properties, Func<JsonNode?, bool> isOpaque, List<PimRequestFinding> findings)
    {
        var groupId = Property(properties, "groupId");
        var group = Property(properties, "group");
        bool hasId = groupId is not null && !IsAbsent(groupId.Value.Value);
        bool hasGroup = group is not null && !IsAbsent(group.Value.Value);
        if (hasId && hasGroup)
        {
            findings.Add(new(JsonPath.Root.Append(group!.Value.Key), "declare one of properties.group and properties.groupId, not both"));
        }
        else if (!hasId && !hasGroup)
        {
            findings.Add(new(JsonPath.Root.Append("groupId"), "the group is required: properties.groupId (the group's object id, for example [reference(resourceId('Microsoft.Entra/groups', 'ops-admins')).id]) or properties.group (the group's uniqueName or object id)"));
        }

        if (hasId)
        {
            CheckGuid(properties, groupId!.Value.Key, isOpaque, findings);
        }

        if (hasGroup)
        {
            CheckNonEmptyString(properties, group!.Value.Key, isOpaque, findings);
        }
    }
}
