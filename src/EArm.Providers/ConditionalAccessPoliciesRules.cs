using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Core.Secrets;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>
/// The pure rules of <see cref="ConditionalAccessPoliciesProvider"/>: defaults, the
/// read-back normalisation (<c>authenticationStrength</c> reduced to <c>{ id }</c>, <c>null</c> / <c>[]</c> members
/// dropped), the required-shape checks Graph would otherwise reject with a 400, the <c>--allow-ca-enforce</c> hold-back
/// (<see cref="HoldBackEnforcement"/>), the lockout lint, the grant-control exclusivity rules, the preview-only properties, and the PATCH body fix-ups.
/// Testable without Graph.
/// </summary>
public static class ConditionalAccessPoliciesRules
{
    public const string EnabledState = "enabled";

    public const string DefaultState = "enabledForReportingButNotEnforced";

    public const string AllowCaEnforceFlag = "--allow-ca-enforce";

    public const string BlockControl = "block";

    public const string MfaControl = "mfa";

    public const string AllMarker = "All";

    /// <summary>The top-level complex properties Graph replaces whole (send the full object).</summary>
    public static readonly string[] ComplexProperties = ["conditions", "grantControls", "sessionControls"];

    /// <summary>Properties Graph assigns; never sent in a body.</summary>
    public static readonly string[] ServerAssignedProperties = ["id", "createdDateTime", "modifiedDateTime", "deletedDateTime"];

    /// <summary>Dotted paths only the beta endpoint accepts (preview-only).</summary>
    public static readonly string[] PreviewOnlyProperties = ["templateId", "partialEnablementStrategy", "sessionControls.continuousAccessEvaluation"];

    private static readonly string[] ApplicationIncludes = ["includeApplications", "includeUserActions", "includeAuthenticationContextClassReferences"];

    private static readonly string[] UserIncludes = ["includeUsers", "includeGroups", "includeRoles", "includeGuestsOrExternalUsers"];

    /// <summary>
    /// The problems of a FINAL create body (the revealed values that will actually be sent), judged again right before the write:
    /// every check of <see cref="Problems"/>. A PATCH body carries only the changed properties and is not judged as a whole.
    /// (The enforce rule is not a problem: <see cref="HoldBackEnforcement"/> rewrites the body instead.)
    /// </summary>
    public static IReadOnlyList<string> FinalBodyProblems(JsonObject body, GraphApi api, bool isCreate)
    {
        ArgumentNullException.ThrowIfNull(body);
        return isCreate ? Problems(body, api) : [];
    }

    /// <summary>
    /// The <c>--allow-ca-enforce</c> rule, applied in place to the normalised desired properties (or to a final request body). A declared
    /// <c>state: enabled</c> (case-insensitive; a secure value is judged revealed) is an enforce request; without
    /// <paramref name="allowCaEnforce"/> it is held back: on create the <c>state</c> becomes <see cref="DefaultState"/> (report-only),
    /// on update the <c>state</c> is removed so the PATCH leaves the live value as it is. An existing policy that is already
    /// <c>enabled</c> live holds nothing back (the declared value is in effect). Returns what was held back, or <c>null</c> when the
    /// properties were left as they are (the flag was passed, no enforce request, or the value is only known at deployment time:
    /// the executor plans again with the resolved value).
    /// </summary>
    public static EnforcementHold? HoldBackEnforcement(JsonObject properties, JsonObject? live, bool allowCaEnforce, bool isCreate)
    {
        ArgumentNullException.ThrowIfNull(properties);
        if (allowCaEnforce || JsonObjects.FindKey(properties, "state") is not { } key)
        {
            return null;
        }

        var declared = properties[key];
        if (!IsEnabledValue(SecretJson.Reveal(declared)))
        {
            return null;
        }

        bool secret = SecretJson.ContainsSecret(declared);
        if (isCreate)
        {
            properties[key] = JsonValue.Create(DefaultState);
            return new EnforcementHold(key, IsCreate: true, LiveState: null, DeclaredIsSecret: secret);
        }

        string? liveState = live is null ? null : JsonObjects.GetString(live, "state");
        if (liveState is not null && liveState.Trim().Equals(EnabledState, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        properties.Remove(key);
        return new EnforcementHold(key, IsCreate: false, LiveState: liveState, DeclaredIsSecret: secret);
    }

    /// <summary>
    /// Normalises the evaluated properties in place: <c>state</c> defaults to <see cref="DefaultState"/> when
    /// <paramref name="isCreate"/> (an existing policy's undeclared <c>state</c> is left as it is live),
    /// <c>conditions.clientAppTypes</c> to <c>["all"]</c> (when <c>conditions</c> is an object), <c>grantControls.operator</c>
    /// to <c>OR</c> (only when <c>grantControls</c> is declared: a policy may carry session controls alone), and a declared
    /// <c>grantControls.authenticationStrength</c> is reduced to <c>{ id }</c>. The two nested defaults apply on every run:
    /// Graph replaces <c>conditions</c> and <c>grantControls</c> whole, so a declared object without them would clear them.
    /// </summary>
    public static void NormalizeDesired(JsonObject properties, bool isCreate)
    {
        ArgumentNullException.ThrowIfNull(properties);
        if (isCreate)
        {
            JsonObjects.SetDefault(properties, "state", JsonValue.Create(DefaultState));
        }

        if (JsonObjects.TryGetIgnoreCase(properties, "conditions", out var conditions) && conditions is JsonObject conditionsObject)
        {
            JsonObjects.SetDefault(conditionsObject, "clientAppTypes", new JsonArray("all"));
        }

        if (JsonObjects.TryGetIgnoreCase(properties, "grantControls", out var grant) && grant is JsonObject grantObject)
        {
            JsonObjects.SetDefault(grantObject, "operator", JsonValue.Create("OR"));
            ReduceAuthenticationStrength(grantObject);
        }
    }

    /// <summary>
    /// The live object (already stripped) in the form a normalised desired object takes: <c>authenticationStrength</c>
    /// reduced to <c>{ id }</c>, and <c>null</c> / empty-array members dropped at every level (Graph returns every
    /// condition and control, set or not: <c>platforms: null</c>, <c>customAuthenticationFactors: []</c>,
    /// <c>termsOfUse: []</c>...).
    /// </summary>
    public static JsonObject NormalizeLive(JsonObject live)
    {
        ArgumentNullException.ThrowIfNull(live);
        var copy = (JsonObject)JsonValues.Clone(live)!;
        if (JsonObjects.TryGetIgnoreCase(copy, "grantControls", out var grant) && grant is JsonObject grantObject)
        {
            ReduceAuthenticationStrength(grantObject);
        }

        StripAnnotations(copy);
        PruneAbsent(copy);
        return copy;
    }

    /// <summary>
    /// Everything that makes the policy unsupported, after <see cref="NormalizeDesired"/>: a missing or empty <c>conditions.applications</c> / <c>conditions.users</c>
    /// include; a missing <c>conditions.clientAppTypes</c>; neither <c>grantControls</c> nor <c>sessionControls</c>;
    /// <c>block</c> combined with another control; <c>mfa</c> together with <c>authenticationStrength</c>; the lockout
    /// pattern (all users, all applications, <c>block</c>, no <c>excludeUsers</c> / <c>excludeGroups</c>); a preview-only
    /// property on the v1.0 endpoint. Deploy-time-unknown values pass (they are checked again right before the write).
    /// </summary>
    public static IReadOnlyList<string> Problems(JsonObject properties, GraphApi api)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var problems = new List<string>();

        // Review 1D / I-01: every lint judges the REVEALED values. A securestring parameter (or a Key Vault value) can carry
        // "All" or "block"; secret leaves never match a literal, so judging the tainted object let such a policy through.
        // The values are only compared here; no message below echoes them.
        properties = (JsonObject)SecretJson.Reveal(properties)!;

        JsonObjects.TryGetIgnoreCase(properties, "conditions", out var conditions);
        if (JsonObjects.IsAbsent(conditions))
        {
            problems.Add("conditions is required");
        }
        else if (conditions is JsonObject conditionsObject)
        {
            JsonObjects.TryGetIgnoreCase(conditionsObject, "applications", out var applications);
            if (!HasAnyInclude(applications, ApplicationIncludes))
            {
                problems.Add("conditions.applications must include at least one application ('All', an appId or a resource reference), user action or authentication context");
            }
            else if (applications is JsonObject applicationsObject && HasEntries(Member(applicationsObject, "includeApplications"))
                && (HasEntries(Member(applicationsObject, "includeUserActions")) || HasEntries(Member(applicationsObject, "includeAuthenticationContextClassReferences"))))
            {
                // Graph 400 (1012): 'applications' condition cannot include both 'includeApplications' and
                // 'includeUserActions'/'includeAuthenticationContextClassReferences'.
                problems.Add("conditions.applications cannot combine includeApplications with includeUserActions or includeAuthenticationContextClassReferences (Graph rejects it with 1012); target applications, user actions or authentication contexts, not more than one kind");
            }

            JsonObjects.TryGetIgnoreCase(conditionsObject, "users", out var users);
            if (!HasAnyInclude(users, UserIncludes))
            {
                problems.Add("conditions.users must include at least one user ('All' or an object id), group, role or guest/external user kind");
            }

            JsonObjects.TryGetIgnoreCase(conditionsObject, "clientAppTypes", out var clientAppTypes);
            if (JsonObjects.IsAbsent(clientAppTypes))
            {
                problems.Add("conditions.clientAppTypes is required (the default is [\"all\"])");
            }
        }
        else if (!UnknownJson.ContainsUnknown(conditions))
        {
            problems.Add("conditions must be an object");
        }

        JsonObjects.TryGetIgnoreCase(properties, "grantControls", out var grant);
        JsonObjects.TryGetIgnoreCase(properties, "sessionControls", out var session);
        if (JsonObjects.IsAbsent(grant) && JsonObjects.IsAbsent(session))
        {
            problems.Add("at least one of grantControls or sessionControls is required");
        }

        if (grant is JsonObject grantObject)
        {
            CheckGrantControls(grantObject, conditions as JsonObject, problems);
        }

        if (api != GraphApi.Beta)
        {
            foreach (var path in PreviewOnlyProperties)
            {
                if (JsonObjects.TryGetDotted(properties, path, out var preview) && !JsonObjects.IsAbsent(preview))
                {
                    problems.Add($"properties.{path} is only available on the beta endpoint; use apiVersion '2026-10-01-preview.beta' or remove the property");
                }
            }
        }

        return problems;
    }

    /// <summary>
    /// The declared controls are the whole desired set (an omitted control is removed with an explicit
    /// <c>null</c>): a <c>grantControls</c> / <c>sessionControls</c> the template does not declare but the live object
    /// carries is written as <c>null</c> into the desired properties, so the shared diff reports the removal and the
    /// PATCH body carries the <c>null</c>. Nothing is added when the live object lacks the control too, so what-if
    /// shows the <c>null</c> only when a control is actually being removed.
    /// </summary>
    public static void MarkRemovedControls(JsonObject desired, JsonObject? live)
    {
        ArgumentNullException.ThrowIfNull(desired);
        foreach (var control in new[] { "grantControls", "sessionControls" })
        {
            if (JsonObjects.FindKey(desired, control) is null && JsonObjects.TryGetIgnoreCase(live, control, out var liveValue) && !JsonObjects.IsAbsent(liveValue))
            {
                desired[control] = null;
            }
        }
    }

    /// <summary>
    /// Finishes a create body: the server-assigned properties and <c>null</c> members are removed, and a
    /// <c>grantControls</c> the overlay default conjured up (<c>{ operator: "OR" }</c> for a policy that declares
    /// session controls only) is dropped.
    /// </summary>
    public static void FinishCreateBody(JsonObject body, JsonObject declared)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(declared);
        RemoveServerAssigned(body);
        JsonObjects.TryGetIgnoreCase(declared, "grantControls", out var grant);
        if (JsonObjects.IsAbsent(grant) && JsonObjects.FindKey(body, "grantControls") is { } key)
        {
            body.Remove(key);
        }

        foreach (var name in new List<string>(KeysOf(body)))
        {
            if (body[name] is null || (body[name] is JsonValue value && value.GetValueKind() == JsonValueKind.Null))
            {
                body.Remove(name);
            }
        }
    }

    /// <summary>
    /// Finishes a PATCH body: the server-assigned properties are removed, and inside the complex properties an explicit
    /// <c>null</c> whose live counterpart is a collection becomes <c>[]</c> (Graph clears collection-valued properties
    /// with an empty array, and objects, such as a removed control, with <c>null</c>).
    /// </summary>
    public static void FinishPatchBody(JsonObject body, JsonObject? live)
    {
        ArgumentNullException.ThrowIfNull(body);
        RemoveServerAssigned(body);
        foreach (var property in ComplexProperties)
        {
            if (JsonObjects.TryGetIgnoreCase(body, property, out var desired) && desired is JsonObject desiredObject
                && JsonObjects.TryGetIgnoreCase(live, property, out var liveValue) && liveValue is JsonObject liveObject)
            {
                CoerceNullCollections(desiredObject, liveObject);
            }
        }
    }

    /// <summary><c>id</c>, <c>createdDateTime</c>, <c>modifiedDateTime</c> and <c>deletedDateTime</c> are assigned by Graph and rejected in bodies.</summary>
    public static void RemoveServerAssigned(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        foreach (string property in ServerAssignedProperties)
        {
            if (JsonObjects.FindKey(body, property) is { } key)
            {
                body.Remove(key);
            }
        }
    }

    /// <summary>True when the array holds the literal (case-insensitive); unknown and secret leaves never match.</summary>
    public static bool ContainsLiteral(JsonNode? array, string literal)
    {
        ArgumentNullException.ThrowIfNull(literal);
        if (array is not JsonArray items)
        {
            return false;
        }

        foreach (var item in items)
        {
            if (item is JsonValue value && !UnknownJson.IsUnknown(value) && JsonValues.TryGetString(value, out var text) && text.Equals(literal, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // ---- helpers ----

    private static void CheckGrantControls(JsonObject grant, JsonObject? conditions, List<string> problems)
    {
        JsonObjects.TryGetIgnoreCase(grant, "builtInControls", out var builtIn);
        JsonObjects.TryGetIgnoreCase(grant, "authenticationStrength", out var strength);
        JsonObjects.TryGetIgnoreCase(grant, "customAuthenticationFactors", out var custom);
        JsonObjects.TryGetIgnoreCase(grant, "termsOfUse", out var terms);
        bool hasStrength = !JsonObjects.IsAbsent(strength);
        bool block = ContainsLiteral(builtIn, BlockControl);

        if (block)
        {
            int builtInCount = builtIn is JsonArray array ? array.Count : 0;
            if (builtInCount > 1 || hasStrength || !JsonObjects.IsAbsent(custom) || !JsonObjects.IsAbsent(terms))
            {
                problems.Add($"grantControls '{BlockControl}' cannot be combined with any other grant control (built-in, authenticationStrength, customAuthenticationFactors or termsOfUse)");
            }
        }

        // Learn (conditionalaccessgrantcontrols): passwordChange and riskRemediation must be used separately, not in combination.
        if (ContainsLiteral(builtIn, "passwordChange") && ContainsLiteral(builtIn, "riskRemediation"))
        {
            problems.Add("grantControls 'passwordChange' and 'riskRemediation' must be used separately, not in combination");
        }

        if (ContainsLiteral(builtIn, MfaControl) && hasStrength)
        {
            problems.Add($"grantControls '{MfaControl}' and authenticationStrength are mutually exclusive; declare one of them");
        }

        if (block && conditions is not null)
        {
            JsonObjects.TryGetIgnoreCase(conditions, "users", out var usersNode);
            JsonObjects.TryGetIgnoreCase(conditions, "applications", out var applicationsNode);
            if (usersNode is JsonObject users && applicationsNode is JsonObject applications
                && ContainsLiteral(Member(users, "includeUsers"), AllMarker)
                && ContainsLiteral(Member(applications, "includeApplications"), AllMarker)
                && !HasEntries(Member(users, "excludeUsers"))
                && !HasEntries(Member(users, "excludeGroups")))
            {
                problems.Add("lockout risk: the policy blocks all users on all applications with no excludeUsers or excludeGroups; add a break-glass exclusion");
            }
        }
    }

    /// <summary>True when the object (or an unknown leaf standing in for it) carries at least one non-empty include member.</summary>
    private static bool HasAnyInclude(JsonNode? node, string[] includes)
    {
        if (node is not JsonObject obj)
        {
            return node is not null && UnknownJson.ContainsUnknown(node);
        }

        foreach (var include in includes)
        {
            if (HasEntries(Member(obj, include)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the node carries something: not absent, and (for an array) at least one entry that is not blank. <c>excludeUsers: [""]</c>
    /// is not a break-glass exclusion, and <c>includeUsers: ["  "]</c> is not an include.
    /// </summary>
    private static bool HasEntries(JsonNode? node)
    {
        if (JsonObjects.IsAbsent(node))
        {
            return false;
        }

        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is null)
                {
                    continue;
                }

                if (item is JsonValue value && !UnknownJson.IsUnknown(value) && JsonValues.TryGetString(value, out var text))
                {
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return true;
                    }

                    continue;
                }

                if (!JsonObjects.IsAbsent(item))
                {
                    return true;
                }
            }

            return false;
        }

        return !(node is JsonValue leaf && !UnknownJson.IsUnknown(leaf) && JsonValues.TryGetString(leaf, out var scalar) && string.IsNullOrWhiteSpace(scalar));
    }

    private static JsonNode? Member(JsonObject obj, string key)
    {
        return JsonObjects.TryGetIgnoreCase(obj, key, out var value) ? value : null;
    }

    /// <summary>True for a known string <c>enabled</c> (case-insensitive, trimmed); unknown leaves never match.</summary>
    private static bool IsEnabledValue(JsonNode? node)
    {
        return node is JsonValue value && !UnknownJson.IsUnknown(value) && JsonValues.TryGetString(value, out var text)
            && text.Trim().Equals(EnabledState, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary><c>authenticationStrength</c> is declared and read back as the whole policy; only its id takes part in the diff and the body.</summary>
    private static void ReduceAuthenticationStrength(JsonObject grant)
    {
        if (JsonObjects.FindKey(grant, "authenticationStrength") is { } key && grant[key] is JsonObject strength && !UnknownJson.ContainsUnknown(strength))
        {
            if (JsonObjects.TryGetIgnoreCase(strength, "id", out var id) && !JsonObjects.IsAbsent(id))
            {
                grant[key] = new JsonObject { ["id"] = JsonValues.Clone(id) };
            }
        }
    }

    /// <summary>Removes OData annotations Graph adds next to navigation-like members (<c>authenticationStrength@odata.context</c>): they are not part of the policy.</summary>
    private static void StripAnnotations(JsonObject obj)
    {
        foreach (string key in new List<string>(KeysOf(obj)))
        {
            if (key.Contains("@odata.", StringComparison.Ordinal))
            {
                obj.Remove(key);
            }
            else if (obj[key] is JsonObject child)
            {
                StripAnnotations(child);
            }
        }
    }

    private static void PruneAbsent(JsonObject obj)
    {
        var remove = new List<string>();
        foreach (var property in obj)
        {
            if (JsonObjects.IsAbsent(property.Value))
            {
                remove.Add(property.Key);
            }
            else if (property.Value is JsonObject child)
            {
                PruneAbsent(child);
            }
        }

        foreach (var key in remove)
        {
            obj.Remove(key);
        }
    }

    private static void CoerceNullCollections(JsonObject desired, JsonObject live)
    {
        foreach (var key in new List<string>(KeysOf(desired)))
        {
            var value = desired[key];
            JsonObjects.TryGetIgnoreCase(live, key, out var liveValue);
            if ((value is null || (value is JsonValue leaf && leaf.GetValueKind() == JsonValueKind.Null)) && liveValue is JsonArray)
            {
                desired[key] = new JsonArray();
            }
            else if (value is JsonObject child && liveValue is JsonObject liveChild)
            {
                CoerceNullCollections(child, liveChild);
            }
        }
    }

    private static IEnumerable<string> KeysOf(JsonObject obj)
    {
        foreach (var property in obj)
        {
            yield return property.Key;
        }
    }
}

/// <summary>
/// A declared <c>state: enabled</c> held back by <see cref="ConditionalAccessPoliciesRules.HoldBackEnforcement"/> because
/// <c>--allow-ca-enforce</c> was not passed: <see cref="IsCreate"/> says the policy is created report-only, otherwise the live
/// <see cref="LiveState"/> is kept. <see cref="Key"/> is the declared property name (its casing). A secure declared value is never echoed.
/// </summary>
public sealed record EnforcementHold(string Key, bool IsCreate, string? LiveState, bool DeclaredIsSecret)
{
    private string Declared => DeclaredIsSecret ? "a secure value requesting enforcement" : ConditionalAccessPoliciesRules.EnabledState;

    /// <summary>The short note shown next to the <c>state</c> delta.</summary>
    public string Note => IsCreate
        ? $"declared {Declared}, kept {ConditionalAccessPoliciesRules.DefaultState}; pass {ConditionalAccessPoliciesRules.AllowCaEnforceFlag} to enforce"
        : $"declared {Declared}, live value kept; pass {ConditionalAccessPoliciesRules.AllowCaEnforceFlag} to enforce";

    /// <summary>The warning text (without the code prefix).</summary>
    public string Message => IsCreate
        ? $"state: declared {Declared}, kept {ConditionalAccessPoliciesRules.DefaultState}; the new policy is created report-only (pass {ConditionalAccessPoliciesRules.AllowCaEnforceFlag} to enforce)"
        : $"state: declared {Declared}, live value kept ({LiveState ?? "unknown"}); the update does not change the policy state (pass {ConditionalAccessPoliciesRules.AllowCaEnforceFlag} to enforce)";
}
