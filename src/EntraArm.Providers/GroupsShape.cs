using System;
using System.Text;
using System.Text.Json.Nodes;
using EntraArm.Engine.Providers;
using EntraArm.Expressions.Values;

namespace EntraArm.Providers;

/// <summary>
/// The pure rules of <c>Microsoft.Entra/groups</c> (plan section 3.2): which kind of group a set of properties
/// describes (security, Microsoft 365, dynamic, mail-enabled security, distribution), the kind-dependent create
/// defaults, the <c>mailNickname</c> derivation and the create-time validation. No Graph access.
/// </summary>
internal static class GroupsShape
{
    public const string Unified = "Unified";
    public const string DynamicMembership = "DynamicMembership";
    public const string HiddenMembership = "HiddenMembership";
    public const string ProcessingOn = "On";
    public const string ProcessingPaused = "Paused";
    public const int MailNicknameMaxLength = 64;

    /// <summary>What the declared (or live) properties say about the group's kind.</summary>
    public sealed record Analysis(bool IsUnified, bool IsDynamic, bool? MailEnabled, bool? SecurityEnabled, bool HasMembershipRule, string? Visibility)
    {
        /// <summary><c>mailEnabled</c> with the kind default (Microsoft 365 groups are mail-enabled, others not).</summary>
        public bool EffectiveMailEnabled => MailEnabled ?? IsUnified;

        /// <summary><c>securityEnabled</c> with the kind default (security groups are, Microsoft 365 groups are not).</summary>
        public bool EffectiveSecurityEnabled => SecurityEnabled ?? !IsUnified;

        /// <summary>Mail-enabled and security-enabled without <c>Unified</c>: Exchange-backed, not creatable through Graph.</summary>
        public bool IsMailEnabledSecurity => !IsUnified && EffectiveMailEnabled && EffectiveSecurityEnabled;

        /// <summary>Mail-enabled, not security-enabled, without <c>Unified</c>: a distribution list, not creatable through Graph.</summary>
        public bool IsDistributionList => !IsUnified && EffectiveMailEnabled && !EffectiveSecurityEnabled;

        /// <summary><c>security</c>, <c>microsoft365</c>, <c>mailEnabledSecurity</c> or <c>distribution</c> (recorded in the state's <c>extra.kind</c>).</summary>
        public string Kind => IsUnified ? "microsoft365" : IsMailEnabledSecurity ? "mailEnabledSecurity" : IsDistributionList ? "distribution" : "security";

        /// <summary>Only Microsoft 365 groups go to the 30-day recycle bin; every other kind is deleted permanently.</summary>
        public bool IsPermanentlyDeleted => !IsUnified;
    }

    public static Analysis Analyze(JsonObject? properties)
    {
        JsonObjects.TryGetIgnoreCase(properties, "groupTypes", out var groupTypes);
        JsonObjects.TryGetIgnoreCase(properties, "mailEnabled", out var mailEnabled);
        JsonObjects.TryGetIgnoreCase(properties, "securityEnabled", out var securityEnabled);
        JsonObjects.TryGetIgnoreCase(properties, "membershipRule", out var membershipRule);
        return new Analysis(
            GroupTypesContain(groupTypes, Unified),
            GroupTypesContain(groupTypes, DynamicMembership),
            JsonValues.TryGetBool(mailEnabled, out bool mail) ? mail : null,
            JsonValues.TryGetBool(securityEnabled, out bool security) ? security : null,
            JsonValues.TryGetString(membershipRule, out var rule) && !string.IsNullOrWhiteSpace(rule),
            JsonObjects.GetString(properties, "visibility"));
    }

    public static bool GroupTypesContain(JsonNode? groupTypes, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (groupTypes is not JsonArray array)
        {
            return false;
        }

        foreach (var item in array)
        {
            if (JsonValues.TryGetString(item, out var text) && text.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Why the group cannot be created as declared (plan section 3.2): mail-enabled security groups and distribution
    /// lists are Exchange-backed and adopt-only; a dynamic group needs a <c>membershipRule</c>. <c>null</c> when fine.
    /// </summary>
    public static string? CreateError(Analysis analysis, string describe)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(describe);
        if (analysis.IsMailEnabledSecurity)
        {
            return $"{describe} cannot be created: mail-enabled security groups (mailEnabled and securityEnabled without 'Unified') cannot be created through Microsoft Graph. Create it in Exchange and adopt it, or declare a security group (mailEnabled: false) or a Microsoft 365 group (groupTypes: [\"Unified\"]).";
        }

        if (analysis.IsDistributionList)
        {
            return $"{describe} cannot be created: distribution lists (mailEnabled without securityEnabled or 'Unified') cannot be created through Microsoft Graph. Create it in Exchange and adopt it, or declare a Microsoft 365 group (groupTypes: [\"Unified\"]).";
        }

        if (analysis.IsDynamic && !analysis.HasMembershipRule)
        {
            return $"{describe} cannot be created: a dynamic group (groupTypes contains 'DynamicMembership') requires 'membershipRule'.";
        }

        return null;
    }

    /// <summary>
    /// Fills the kind defaults into a create body (only where the template is silent): <c>groupTypes: []</c>,
    /// <c>mailEnabled</c> / <c>securityEnabled</c> per kind, <c>mailNickname</c> derived from the name, and
    /// <c>membershipRuleProcessingState: On</c> for dynamic groups.
    /// </summary>
    public static void ApplyCreateDefaults(JsonObject body, string name, Analysis analysis)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(analysis);

        JsonObjects.SetDefault(body, "groupTypes", new JsonArray());
        JsonObjects.SetDefault(body, "mailEnabled", JsonValue.Create(analysis.EffectiveMailEnabled));
        JsonObjects.SetDefault(body, "securityEnabled", JsonValue.Create(analysis.EffectiveSecurityEnabled));
        if (!JsonObjects.TryGetIgnoreCase(body, "mailNickname", out var nickname) || JsonObjects.IsAbsent(nickname))
        {
            body[JsonObjects.FindKey(body, "mailNickname") ?? "mailNickname"] = DeriveMailNickname(name);
        }

        if (analysis.IsDynamic)
        {
            JsonObjects.SetDefault(body, "membershipRuleProcessingState", JsonValue.Create(ProcessingOn));
        }
    }

    /// <summary>
    /// <c>mailNickname</c> from the resource name (plan section 3.2): lower-case, every character outside
    /// <c>[a-z0-9-_]</c> becomes <c>-</c>, runs of <c>-</c> collapse, at most 64 characters.
    /// </summary>
    public static string DeriveMailNickname(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var sb = new StringBuilder(name.Length);
        bool lastDash = false;
        foreach (char c in name.ToLowerInvariant())
        {
            bool allowed = c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_';
            if (allowed)
            {
                sb.Append(c);
                lastDash = false;
                continue;
            }

            if (!lastDash)
            {
                sb.Append('-');
                lastDash = true;
            }
        }

        string nickname = sb.ToString();
        return nickname.Length > MailNicknameMaxLength ? nickname[..MailNicknameMaxLength] : nickname;
    }

    /// <summary>True when the two <c>groupTypes</c> arrays differ in their <c>Unified</c> membership (immutable).</summary>
    public static bool UnifiedPresenceDiffers(JsonNode? before, JsonNode? after)
    {
        return GroupTypesContain(before, Unified) != GroupTypesContain(after, Unified);
    }

    /// <summary>True when the two <c>groupTypes</c> arrays differ in their <c>DynamicMembership</c> membership (a dynamic toggle).</summary>
    public static bool DynamicPresenceDiffers(JsonNode? before, JsonNode? after)
    {
        return GroupTypesContain(before, DynamicMembership) != GroupTypesContain(after, DynamicMembership);
    }

    /// <summary>True when a <c>visibility</c> change involves <c>HiddenMembership</c> on either side (immutable).</summary>
    public static bool HiddenMembershipInvolved(JsonNode? before, JsonNode? after)
    {
        return IsHidden(before) || IsHidden(after);

        static bool IsHidden(JsonNode? node)
        {
            return JsonValues.TryGetString(node, out var text) && text.Equals(HiddenMembership, StringComparison.OrdinalIgnoreCase);
        }
    }
}
