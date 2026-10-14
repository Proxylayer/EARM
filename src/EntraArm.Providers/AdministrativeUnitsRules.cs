using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EntraArm.Engine.Providers;
using EntraArm.Expressions.Values;

namespace EntraArm.Providers;

/// <summary>
/// The pure rules of administrative units (plan sections 1.3 and 3.2): the managed property set, the membership
/// kinds (<c>Assigned</c> / <c>Dynamic</c>), the <c>membershipRuleProcessingState: On</c> injection for dynamic units
/// and the canonical form of the live object. No Graph here.
/// </summary>
public static class AdministrativeUnitsRules
{
    public const string MembershipAssigned = "Assigned";

    public const string MembershipDynamic = "Dynamic";

    public const string ProcessingOn = "On";

    public const string ProcessingPaused = "Paused";

    /// <summary>The properties every read selects (users-style tiny defaults are not a problem here, but the select keeps reads stable).</summary>
    public static readonly string[] ManagedProperties =
    [
        "id", "displayName", "description", "visibility", "membershipType", "membershipRule", "membershipRuleProcessingState", "isMemberManagementRestricted", "deletedDateTime",
    ];

    /// <summary>
    /// Normalises the evaluated properties in place and returns the problems found (empty when the object is valid):
    /// <c>membershipType</c> and <c>membershipRuleProcessingState</c> are put into their canonical casing, a dynamic
    /// unit requires <c>membershipRule</c> and gets <c>membershipRuleProcessingState: On</c> when none is declared,
    /// and <c>isMemberManagementRestricted</c> must be a boolean. Deploy-time-unknown leaves are left alone.
    /// </summary>
    public static IReadOnlyList<string> NormalizeDesired(JsonObject properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var problems = new List<string>();

        string? membershipType = null;
        if (JsonObjects.TryGetIgnoreCase(properties, "membershipType", out var typeNode) && !JsonObjects.IsAbsent(typeNode) && !UnknownJson.IsUnknown(typeNode))
        {
            if (!JsonValues.TryGetString(typeNode, out var typeText) || Canonical(typeText, MembershipAssigned, MembershipDynamic) is not { } canonicalType)
            {
                problems.Add($"properties.membershipType must be '{MembershipAssigned}' or '{MembershipDynamic}', got {JsonValues.Describe(typeNode)}");
            }
            else
            {
                membershipType = canonicalType;
                properties[JsonObjects.FindKey(properties, "membershipType")!] = canonicalType;
            }
        }

        bool hasRule = JsonObjects.TryGetIgnoreCase(properties, "membershipRule", out var ruleNode) && !JsonObjects.IsAbsent(ruleNode);
        if (hasRule && !UnknownJson.IsUnknown(ruleNode) && (!JsonValues.TryGetString(ruleNode, out var ruleText) || string.IsNullOrWhiteSpace(ruleText)))
        {
            problems.Add($"properties.membershipRule must be a non-empty string, got {JsonValues.Describe(ruleNode)}");
        }

        if (JsonObjects.TryGetIgnoreCase(properties, "membershipRuleProcessingState", out var stateNode) && !JsonObjects.IsAbsent(stateNode) && !UnknownJson.IsUnknown(stateNode))
        {
            if (!JsonValues.TryGetString(stateNode, out var stateText) || Canonical(stateText, ProcessingOn, ProcessingPaused) is not { } canonicalState)
            {
                problems.Add($"properties.membershipRuleProcessingState must be '{ProcessingOn}' or '{ProcessingPaused}', got {JsonValues.Describe(stateNode)}");
            }
            else
            {
                properties[JsonObjects.FindKey(properties, "membershipRuleProcessingState")!] = canonicalState;
            }
        }
        else if (string.Equals(membershipType, MembershipDynamic, StringComparison.Ordinal) && hasRule)
        {
            // Plan section 3.2: a dynamic unit is created with its rule processing switched on.
            properties[JsonObjects.FindKey(properties, "membershipRuleProcessingState") ?? "membershipRuleProcessingState"] = ProcessingOn;
        }

        if (string.Equals(membershipType, MembershipDynamic, StringComparison.Ordinal) && !hasRule)
        {
            problems.Add("a dynamic administrative unit (membershipType: Dynamic) requires properties.membershipRule");
        }

        if (JsonObjects.TryGetIgnoreCase(properties, "isMemberManagementRestricted", out var restrictedNode) && !JsonObjects.IsAbsent(restrictedNode) && !UnknownJson.IsUnknown(restrictedNode)
            && !JsonValues.TryGetBool(restrictedNode, out _))
        {
            problems.Add($"properties.isMemberManagementRestricted must be a boolean, got {JsonValues.Describe(restrictedNode)}");
        }

        return problems;
    }

    /// <summary>
    /// The live diff view: Graph reports a unit without a membership type as <c>null</c>, which is an assigned unit,
    /// so a declared <c>membershipType: Assigned</c> is not drift.
    /// </summary>
    public static JsonObject NormalizeLive(JsonObject stripped)
    {
        ArgumentNullException.ThrowIfNull(stripped);
        var copy = (JsonObject)JsonValues.Clone(stripped)!;
        string key = JsonObjects.FindKey(copy, "membershipType") ?? "membershipType";
        if (!JsonObjects.TryGetIgnoreCase(copy, "membershipType", out var node) || JsonObjects.IsAbsent(node))
        {
            copy[key] = MembershipAssigned;
        }
        else if (JsonValues.TryGetString(node, out var text) && Canonical(text, MembershipAssigned, MembershipDynamic) is { } canonical)
        {
            copy[key] = canonical;
        }

        return copy;
    }

    /// <summary>True when the unit is dynamic (membership computed from the rule): manual members are rejected.</summary>
    public static bool IsDynamic(JsonObject? properties)
    {
        return string.Equals(JsonObjects.GetString(properties, "membershipType"), MembershipDynamic, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the unit restricts member management to scoped role holders.</summary>
    public static bool IsRestricted(JsonObject? properties)
    {
        return JsonObjects.TryGetIgnoreCase(properties, "isMemberManagementRestricted", out var node) && JsonValues.TryGetBool(node, out bool restricted) && restricted;
    }

    private static string? Canonical(string value, params string[] allowed)
    {
        foreach (string candidate in allowed)
        {
            if (candidate.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }
}
