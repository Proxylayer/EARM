using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Core.Secrets;
using EArm.Engine.Providers;
using EArm.Expressions.Functions;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>Ids the provider derived for <c>appRoles</c> / <c>api.oauth2PermissionScopes</c> members declared without one (<c>value</c> → id).</summary>
public sealed record DerivedMemberIds(IReadOnlyDictionary<string, string> AppRoles, IReadOnlyDictionary<string, string> Scopes)
{
    public bool IsEmpty => AppRoles.Count == 0 && Scopes.Count == 0;

    /// <summary>The <c>extra</c> block recorded in the state file, or <c>null</c> when nothing was derived.</summary>
    public JsonObject? ToExtra()
    {
        if (IsEmpty)
        {
            return null;
        }

        var derived = new JsonObject();
        if (AppRoles.Count > 0)
        {
            derived["appRoles"] = ToObject(AppRoles);
        }

        if (Scopes.Count > 0)
        {
            derived["oauth2PermissionScopes"] = ToObject(Scopes);
        }

        return new JsonObject { ["derivedIds"] = derived };
    }

    private static JsonObject ToObject(IReadOnlyDictionary<string, string> map)
    {
        var obj = new JsonObject();
        foreach (var pair in map.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            obj[pair.Key] = pair.Value;
        }

        return obj;
    }
}

/// <summary>
/// The pure rules of the applications provider, kept free of Graph I/O so they can be
/// tested on their own:
/// <list type="bullet">
/// <item>deterministic ids for <c>appRoles</c> / <c>api.oauth2PermissionScopes</c> members declared without <c>id</c> (UUIDv5 of (uniqueName, value));</item>
/// <item>the normalisations that make template and live object comparable (<c>isEnabled</c> defaults, server-managed <c>origin</c>, <c>api.requestedAccessTokenVersion</c> null ≡ 1, <c>web.implicitGrantSettings</c> always <c>{false,false}</c>-shaped);</item>
/// <item><c>value</c> uniqueness across roles and scopes;</item>
/// <item>the <c>{appId}</c> placeholder in <c>identifierUris</c> (<c>api://{appId}</c>), applied once Graph has assigned the appId;</item>
/// <item>the first of the two PATCHes needed to remove a role/scope or change its <c>value</c> / <c>allowedMemberTypes</c> / <c>type</c> (the member must be disabled first).</item>
/// </list>
/// </summary>
public static class ApplicationsRules
{
    /// <summary>The token in an identifier URI that stands for the application's own appId (<c>api://{appId}</c>).</summary>
    public const string AppIdPlaceholder = "{appId}";

    /// <summary>The RFC 4122 v5 namespace of derived member ids; the per-application namespace is the v5 UUID of the uniqueName under it.</summary>
    public static readonly Guid DerivedIdNamespace = new("9d1f6c3a-5b2e-4f7a-8c4d-2e6b1a9f0c58");

    /// <summary>Member properties of an app role whose change requires the role to be disabled first.</summary>
    public static readonly IReadOnlyList<string> AppRoleBreakingProperties = ["value", "allowedMemberTypes"];

    /// <summary>Member properties of a permission scope whose change requires the scope to be disabled first.</summary>
    public static readonly IReadOnlyList<string> ScopeBreakingProperties = ["value", "type"];

    // ---- derived ids ----

    /// <summary>The deterministic id of a role/scope declared without one: v5(v5(namespace, uniqueName), value).</summary>
    public static Guid DeriveMemberId(string uniqueName, string value)
    {
        ArgumentNullException.ThrowIfNull(uniqueName);
        ArgumentNullException.ThrowIfNull(value);
        var applicationNamespace = Hashing.GuidV5(DerivedIdNamespace, uniqueName);
        return Hashing.GuidV5(applicationNamespace, value);
    }

    // ---- desired side ----

    /// <summary>
    /// Normalises the evaluated template properties in place (idempotent): fills derived ids and <c>isEnabled: true</c>
    /// on roles/scopes, <c>enableAccessTokenIssuance</c> / <c>enableIdTokenIssuance</c> on a declared
    /// <c>web.implicitGrantSettings</c>, and turns an explicit <c>api.requestedAccessTokenVersion: null</c> into
    /// <c>1</c>. Returns the ids that were derived (also when an earlier call already filled them). Pass
    /// <paramref name="uniqueName"/> <c>null</c> while the name is still symbolic: no id is derived then.
    /// </summary>
    public static DerivedMemberIds NormalizeDesired(JsonObject properties, string? uniqueName)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        var scopes = new Dictionary<string, string>(StringComparer.Ordinal);

        if (TryGetArray(properties, "appRoles", out var roleArray))
        {
            NormalizeMembers(roleArray, uniqueName, roles);
        }

        if (TryGetObject(properties, "api", out var api))
        {
            if (TryGetArray(api, "oauth2PermissionScopes", out var scopeArray))
            {
                NormalizeMembers(scopeArray, uniqueName, scopes);
            }

            if (JsonObjects.FindKey(api, "requestedAccessTokenVersion") is { } versionKey && IsJsonNull(api[versionKey]))
            {
                api[versionKey] = 1;
            }
        }

        if (TryGetObject(properties, "web", out var web) && TryGetObject(web, "implicitGrantSettings", out var grant))
        {
            FillImplicitGrantSettings(grant);
        }

        return new DerivedMemberIds(roles, scopes);
    }

    private static void NormalizeMembers(JsonArray members, string? uniqueName, Dictionary<string, string> derived)
    {
        foreach (var item in members)
        {
            if (item is not JsonObject member)
            {
                continue;
            }

            if (JsonObjects.FindKey(member, "isEnabled") is null)
            {
                member["isEnabled"] = true;
            }

            string? value = JsonObjects.GetString(member, "value");
            if (uniqueName is null || string.IsNullOrEmpty(value))
            {
                continue;
            }

            string derivedId = DeriveMemberId(uniqueName, value).ToString("D");
            string idKey = JsonObjects.FindKey(member, "id") ?? "id";
            if (!JsonObjects.TryGetIgnoreCase(member, "id", out var id) || JsonObjects.IsAbsent(id))
            {
                member[idKey] = derivedId;
                derived[value] = derivedId;
            }
            else if (JsonObjects.KeyEquals(JsonObjects.GetString(member, "id"), derivedId))
            {
                derived[value] = derivedId;
            }
        }
    }

    private static void FillImplicitGrantSettings(JsonObject grant)
    {
        JsonObjects.SetDefault(grant, "enableAccessTokenIssuance", false);
        JsonObjects.SetDefault(grant, "enableIdTokenIssuance", false);
    }

    /// <summary>
    /// Every <c>value</c> that occurs more than once across <c>appRoles</c> and <c>api.oauth2PermissionScopes</c>
    /// (Graph requires uniqueness across both), in first-seen order. Empty values are ignored.
    /// </summary>
    public static IReadOnlyList<string> DuplicateValues(JsonObject properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = new List<string>();
        foreach (string value in MemberValues(properties))
        {
            if (!seen.Add(value) && !duplicates.Contains(value, StringComparer.Ordinal))
            {
                duplicates.Add(value);
            }
        }

        return duplicates;
    }

    private static IEnumerable<string> MemberValues(JsonObject properties)
    {
        if (TryGetArray(properties, "appRoles", out var roles))
        {
            foreach (var value in ValuesOf(roles))
            {
                yield return value;
            }
        }

        if (TryGetObject(properties, "api", out var api) && TryGetArray(api, "oauth2PermissionScopes", out var scopes))
        {
            foreach (var value in ValuesOf(scopes))
            {
                yield return value;
            }
        }

        static IEnumerable<string> ValuesOf(JsonArray members)
        {
            foreach (var item in members)
            {
                if (item is JsonObject member && JsonObjects.GetString(member, "value") is { Length: > 0 } value)
                {
                    yield return value;
                }
            }
        }
    }

    // ---- identifier URIs ----

    /// <summary>True when any <c>identifierUris</c> entry still carries the <c>{appId}</c> placeholder.</summary>
    public static bool HasDeferredIdentifierUris(JsonObject properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        return TryGetArray(properties, "identifierUris", out var uris) && uris.Any(IsDeferredUri);
    }

    /// <summary>Replaces <c>{appId}</c> in every <c>identifierUris</c> entry in place; returns true when something changed.</summary>
    public static bool SubstituteAppId(JsonObject properties, string appId)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        if (!TryGetArray(properties, "identifierUris", out var uris))
        {
            return false;
        }

        bool changed = false;
        for (int i = 0; i < uris.Count; i++)
        {
            if (IsDeferredUri(uris[i]) && JsonValues.TryGetString(uris[i], out var uri))
            {
                uris[i] = uri.Replace(AppIdPlaceholder, appId, StringComparison.OrdinalIgnoreCase);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>Drops the entries that still carry <c>{appId}</c> from a request body's <c>identifierUris</c> (the whole property when none remain); returns true when something was deferred.</summary>
    public static bool RemoveDeferredIdentifierUris(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!TryGetArray(body, "identifierUris", out var uris) || !uris.Any(IsDeferredUri))
        {
            return false;
        }

        var immediate = new JsonArray();
        foreach (var uri in uris)
        {
            if (!IsDeferredUri(uri))
            {
                immediate.Add(JsonValues.Clone(uri));
            }
        }

        string key = JsonObjects.FindKey(body, "identifierUris")!;
        if (immediate.Count == 0)
        {
            body.Remove(key);
        }
        else
        {
            body[key] = immediate;
        }

        return true;
    }

    private static bool IsDeferredUri(JsonNode? node)
    {
        return JsonValues.TryGetString(node, out var uri) && !SecretJsonOrUnknown(node) && uri.Contains(AppIdPlaceholder, StringComparison.OrdinalIgnoreCase);
    }

    // ---- live side ----

    /// <summary>
    /// The diff view of the live object (a copy of the stripped properties) made comparable with the template:
    /// server-managed <c>origin</c> removed from roles/scopes, <c>api.requestedAccessTokenVersion</c> null → 1,
    /// <c>web.implicitGrantSettings</c> present whenever <c>web</c> is, and the passthrough properties the template
    /// declares (<c>keyCredentials</c>, <c>appId</c>) copied back from the raw object so they take part in the diff.
    /// </summary>
    public static JsonObject NormalizeLive(ExistingResource existing, JsonObject desired)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(desired);
        var live = (JsonObject)JsonValues.Clone(existing.Properties)!;

        if (TryGetArray(live, "appRoles", out var roles))
        {
            StripOrigin(roles);
        }

        if (TryGetObject(live, "api", out var api))
        {
            if (TryGetArray(api, "oauth2PermissionScopes", out var scopes))
            {
                StripOrigin(scopes);
            }

            string versionKey = JsonObjects.FindKey(api, "requestedAccessTokenVersion") ?? "requestedAccessTokenVersion";
            if (JsonObjects.IsAbsent(api[versionKey]))
            {
                api[versionKey] = 1;
            }
        }

        if (TryGetObject(live, "web", out var web))
        {
            string grantKey = JsonObjects.FindKey(web, "implicitGrantSettings") ?? "implicitGrantSettings";
            if (web[grantKey] is not JsonObject grant)
            {
                grant = new JsonObject();
                web[grantKey] = grant;
            }

            FillImplicitGrantSettings(grant);
        }

        foreach (string passthrough in new[] { "keyCredentials", "appId" })
        {
            if (JsonObjects.FindKey(desired, passthrough) is not null && JsonObjects.TryGetIgnoreCase(existing.Raw, passthrough, out var raw) && !JsonObjects.IsAbsent(raw))
            {
                live[JsonObjects.FindKey(live, passthrough) ?? passthrough] = JsonValues.Clone(raw);
            }
        }

        return live;
    }

    private static void StripOrigin(JsonArray members)
    {
        foreach (var item in members)
        {
            if (item is JsonObject member && JsonObjects.FindKey(member, "origin") is { } key)
            {
                member.Remove(key);
            }
        }
    }

    // ---- two-step disable ----

    /// <summary>
    /// The array for the first of two PATCHes: a copy of the live members in which every enabled member that the
    /// template removes, or whose <paramref name="breakingProperties"/> differ from the template, is set
    /// <c>isEnabled: false</c>. <c>null</c> when the template does not declare the array or nothing needs disabling.
    /// Members are matched by <c>id</c>.
    /// </summary>
    public static JsonArray? DisableStep(JsonArray? desired, JsonArray? live, IReadOnlyList<string> breakingProperties)
    {
        ArgumentNullException.ThrowIfNull(breakingProperties);
        if (desired is null || live is null || live.Count == 0)
        {
            return null;
        }

        JsonArray? result = null;
        for (int i = 0; i < live.Count; i++)
        {
            if (live[i] is not JsonObject liveMember || JsonObjects.GetString(liveMember, "id") is not { } id)
            {
                continue;
            }

            if (JsonObjects.TryGetIgnoreCase(liveMember, "isEnabled", out var enabled) && JsonValues.TryGetBool(enabled, out bool isEnabled) && !isEnabled)
            {
                continue;
            }

            var match = FindById(desired, id);
            bool affected = match is null;
            if (match is not null)
            {
                foreach (string property in breakingProperties)
                {
                    JsonObjects.TryGetIgnoreCase(match, property, out var desiredValue);
                    JsonObjects.TryGetIgnoreCase(liveMember, property, out var liveValue);
                    if (!JsonDiff.AreEquivalent(desiredValue, liveValue))
                    {
                        affected = true;
                        break;
                    }
                }
            }

            if (!affected)
            {
                continue;
            }

            result ??= (JsonArray)JsonValues.Clone(live)!;
            var disabled = (JsonObject)result[i]!;
            disabled[JsonObjects.FindKey(disabled, "isEnabled") ?? "isEnabled"] = false;
        }

        return result;
    }

    private static JsonObject? FindById(JsonArray members, string id)
    {
        foreach (var item in members)
        {
            if (item is JsonObject member && JsonObjects.KeyEquals(JsonObjects.GetString(member, "id"), id))
            {
                return member;
            }
        }

        return null;
    }

    // ---- helpers ----

    /// <summary>The <c>appRoles</c> array of an object, when declared as an array.</summary>
    public static JsonArray? AppRolesOf(JsonObject? properties)
    {
        return TryGetArray(properties, "appRoles", out var roles) ? roles : null;
    }

    /// <summary>The <c>api.oauth2PermissionScopes</c> array of an object, when declared as an array.</summary>
    public static JsonArray? ScopesOf(JsonObject? properties)
    {
        return TryGetObject(properties, "api", out var api) && TryGetArray(api, "oauth2PermissionScopes", out var scopes) ? scopes : null;
    }

    private static bool TryGetArray(JsonObject? obj, string key, out JsonArray array)
    {
        array = null!;
        if (JsonObjects.TryGetIgnoreCase(obj, key, out var node) && node is JsonArray found)
        {
            array = found;
            return true;
        }

        return false;
    }

    private static bool TryGetObject(JsonObject? obj, string key, out JsonObject child)
    {
        child = null!;
        if (JsonObjects.TryGetIgnoreCase(obj, key, out var node) && node is JsonObject found)
        {
            child = found;
            return true;
        }

        return false;
    }

    private static bool IsJsonNull(JsonNode? node)
    {
        return node is null || (node is JsonValue value && !SecretJsonOrUnknown(value) && value.GetValueKind() == JsonValueKind.Null);
    }

    private static bool SecretJsonOrUnknown(JsonNode? node)
    {
        return SecretJson.IsSecret(node) || UnknownJson.IsUnknown(node);
    }
}
