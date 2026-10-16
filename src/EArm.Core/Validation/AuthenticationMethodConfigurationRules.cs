using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Core.Json;

namespace EArm.Core.Validation;

/// <summary>One authentication method configuration EARM manages: the resource name, the Graph id, the <c>@odata.type</c> and whether Graph can reset it.</summary>
/// <param name="Name">The resource name (the documented method id, camelCase): <c>fido2</c>, <c>temporaryAccessPass</c>...</param>
/// <param name="GraphId">The id Graph returns in the object (<c>Fido2</c>, <c>TemporaryAccessPass</c>...), also used as the URL segment.</param>
/// <param name="ODataType">The derived type Graph requires on every PATCH.</param>
/// <param name="CanReset">True when Graph documents a DELETE that reverts the configuration to Microsoft's default (fido2, temporaryAccessPass, x509Certificate).</param>
public sealed record AuthenticationMethod(string Name, string GraphId, string ODataType, bool CanReset);

/// <summary>A problem with a declared authentication method configuration.</summary>
/// <param name="Path">Where the problem is, relative to <c>properties</c>.</param>
/// <param name="Message">What is wrong.</param>
/// <param name="SchemaCovered">True for a shape problem the type's JSON Schema also reports offline (an enum, a type, a bound, a pattern, a required
/// member id); the offline checker leaves those to the schema and reports only the rules the schema cannot express.</param>
public sealed record AuthenticationMethodFinding(JsonPath Path, string Message, bool SchemaCovered = false);

/// <summary>
/// The pure rules of <c>Microsoft.Entra/authenticationMethodConfigurations</c>, shared by the offline validator (literal values) and the provider (evaluated
/// values): the method table (name, Graph id, <c>@odata.type</c>, resettable), the shape checks (state, AAGUIDs, an enforced allow-list without AAGUIDs,
/// Temporary Access Pass bounds, the certificate-based authentication mode) and the normalisation the provider applies before diffing (lower-case AAGUIDs and
/// enum values, the name-derived <c>id</c> / <c>@odata.type</c> removed). Values the caller cannot judge yet (a template expression offline, a deploy-time
/// value at plan time) are skipped through the <c>isOpaque</c> predicate.
/// </summary>
public static class AuthenticationMethodConfigurationRules
{
    public const string ResourceType = "Microsoft.Entra/authenticationMethodConfigurations";

    public const string ODataTypeProperty = "@odata.type";

    public const string StateProperty = "state";

    /// <summary>The resource names (method ids) Graph v1.0 documents as authenticationMethodConfiguration derived types, compared case-insensitively.</summary>
    public const string NamePattern = "^(?i:fido2|temporaryAccessPass|x509Certificate|microsoftAuthenticator|sms|voice|email|softwareOath)$";

    /// <summary>The Temporary Access Pass length bounds (Learn: "Must be between 8 and 48 characters").</summary>
    public const int MinimumPassLength = 8;

    public const int MaximumPassLength = 48;

    /// <summary>The Temporary Access Pass lifetime bounds in minutes (Learn: "between 10 and 43200 minutes (equivalent to 30 days)").</summary>
    public const int MinimumLifetime = 10;

    public const int MaximumLifetime = 43200;

    public static IReadOnlyList<AuthenticationMethod> Methods { get; } =
    [
        new("fido2", "Fido2", "#microsoft.graph.fido2AuthenticationMethodConfiguration", CanReset: true),
        new("temporaryAccessPass", "TemporaryAccessPass", "#microsoft.graph.temporaryAccessPassAuthenticationMethodConfiguration", CanReset: true),
        new("x509Certificate", "X509Certificate", "#microsoft.graph.x509CertificateAuthenticationMethodConfiguration", CanReset: true),
        new("microsoftAuthenticator", "MicrosoftAuthenticator", "#microsoft.graph.microsoftAuthenticatorAuthenticationMethodConfiguration", CanReset: false),
        new("sms", "Sms", "#microsoft.graph.smsAuthenticationMethodConfiguration", CanReset: false),
        new("voice", "Voice", "#microsoft.graph.voiceAuthenticationMethodConfiguration", CanReset: false),
        new("email", "Email", "#microsoft.graph.emailAuthenticationMethodConfiguration", CanReset: false),
        new("softwareOath", "SoftwareOath", "#microsoft.graph.softwareOathAuthenticationMethodConfiguration", CanReset: false),
    ];

    private static readonly string[] States = ["enabled", "disabled"];

    private static readonly string[] EnforcementTypes = ["allow", "block"];

    private static readonly string[] X509Modes = ["x509CertificateSingleFactor", "x509CertificateMultiFactor"];

    private static readonly string[] TargetArrays = ["includeTargets", "excludeTargets"];

    /// <summary>The method a resource name names (the method id or the Graph id, any casing), or <c>null</c>.</summary>
    public static AuthenticationMethod? Find(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        return Methods.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || m.GraphId.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The problem with a resource name that names no supported method, or <c>null</c>.</summary>
    public static string? NameProblem(string? name)
    {
        return Find(name) is null
            ? $"'{name}' is not a supported authentication method configuration; the resource name is the method id, one of {string.Join(", ", Methods.Select(m => m.Name))}"
            : null;
    }

    /// <summary>The legacy FIDO2 properties the declaration uses (deprecated by Microsoft, removed in October 2027 in favour of <c>passkeyProfiles</c>).</summary>
    public static IReadOnlyList<string> DeprecatedProperties(AuthenticationMethod method, JsonObject properties)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(properties);
        if (!method.Name.Equals("fido2", StringComparison.Ordinal))
        {
            return [];
        }

        var found = new List<string>();
        foreach (string legacy in new[] { "isAttestationEnforced", "keyRestrictions" })
        {
            if (Property(properties, legacy) is (string key, var value) && !IsAbsent(value))
            {
                found.Add(key);
            }
        }

        return found;
    }

    /// <summary>The shape problems of a declaration. Nothing is changed.</summary>
    /// <param name="method">The method the resource name names.</param>
    /// <param name="properties">The declared (offline) or evaluated (plan time) properties.</param>
    /// <param name="isOpaque">True for a value that cannot be judged yet (a template expression, a deploy-time value, a secret).</param>
    public static IReadOnlyList<AuthenticationMethodFinding> Check(AuthenticationMethod method, JsonObject properties, Func<JsonNode?, bool> isOpaque)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(isOpaque);
        var findings = new List<AuthenticationMethodFinding>();
        var root = JsonPath.Root;

        if (Judged(properties, ODataTypeProperty, isOpaque) is (string typeKey, var odataType))
        {
            if (!TryString(odataType, out string? text) || !text.Equals(method.ODataType, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new(root.Append(typeKey), $"properties.{typeKey} must be '{method.ODataType}' for the method '{method.Name}' (the provider derives it from the name; omit it)"));
            }
        }

        if (Judged(properties, "id", isOpaque) is (string idKey, var id))
        {
            if (!TryString(id, out string? text) || Find(text) != method)
            {
                findings.Add(new(root.Append(idKey), $"properties.{idKey} conflicts with the resource name '{method.Name}'; the name is the method id, so omit the property"));
            }
        }

        if (Judged(properties, StateProperty, isOpaque) is (string stateKey, var state))
        {
            CheckEnum(state, States, root.Append(stateKey), "properties." + stateKey, findings);
        }

        foreach (string array in TargetArrays)
        {
            if (Judged(properties, array, isOpaque) is (string key, var targets))
            {
                CheckKeyedMembers(targets, root.Append(key), "properties." + key, "id", isOpaque, findings);
            }
        }

        switch (method.Name)
        {
            case "fido2":
                CheckFido2(properties, isOpaque, findings);
                break;
            case "temporaryAccessPass":
                CheckTemporaryAccessPass(properties, isOpaque, findings);
                break;
            case "x509Certificate":
                CheckX509(properties, isOpaque, findings);
                break;
        }

        return findings;
    }

    /// <summary>
    /// Normalises a declaration in place before it is diffed and sent: the name-derived <c>@odata.type</c> and <c>id</c> are removed (the provider writes
    /// <c>@odata.type</c> into every PATCH body; the id is the URL segment), <c>state</c> and FIDO2 <c>enforcementType</c> values are lower-cased, and
    /// AAGUIDs are written in Graph's lower-case hyphenated form. Opaque values are left alone. Run <see cref="Check"/> first.
    /// </summary>
    public static void Normalize(AuthenticationMethod method, JsonObject properties, Func<JsonNode?, bool> isOpaque)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(isOpaque);

        foreach (string derived in new[] { ODataTypeProperty, "id" })
        {
            if (Property(properties, derived) is (string key, var value) && !isOpaque(value))
            {
                properties.Remove(key);
            }
        }

        LowerEnum(properties, StateProperty, isOpaque);
        if (!method.Name.Equals("fido2", StringComparison.Ordinal))
        {
            return;
        }

        if (Property(properties, "keyRestrictions") is (_, JsonObject restrictions))
        {
            NormalizeKeyRestrictions(restrictions, isOpaque);
        }

        if (Property(properties, "passkeyProfiles") is (_, JsonArray profiles))
        {
            foreach (var profile in profiles)
            {
                if (profile is JsonObject profileObject && Property(profileObject, "keyRestrictions") is (_, JsonObject profileRestrictions))
                {
                    NormalizeKeyRestrictions(profileRestrictions, isOpaque);
                }
            }
        }
    }

    // ---- per method ----

    private static void CheckFido2(JsonObject properties, Func<JsonNode?, bool> isOpaque, List<AuthenticationMethodFinding> findings)
    {
        var root = JsonPath.Root;
        if (Judged(properties, "keyRestrictions", isOpaque) is (string restrictionsKey, var restrictions))
        {
            CheckKeyRestrictions(restrictions, root.Append(restrictionsKey), "properties." + restrictionsKey, isOpaque, findings);
        }

        if (Judged(properties, "passkeyProfiles", isOpaque) is (string profilesKey, var profiles))
        {
            var path = root.Append(profilesKey);
            CheckKeyedMembers(profiles, path, "properties." + profilesKey, "id", isOpaque, findings);
            if (profiles is JsonArray array)
            {
                for (int i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonObject profile && Judged(profile, "keyRestrictions", isOpaque) is (string key, var profileRestrictions))
                    {
                        string where = string.Create(CultureInfo.InvariantCulture, $"properties.{profilesKey}[{i}].{key}");
                        CheckKeyRestrictions(profileRestrictions, path.Append(i).Append(key), where, isOpaque, findings);
                    }
                }
            }
        }

        foreach (string flag in new[] { "isAttestationEnforced", "isSelfServiceRegistrationAllowed" })
        {
            if (Judged(properties, flag, isOpaque) is (string key, var value) && !IsBoolean(value))
            {
                findings.Add(new(root.Append(key), $"properties.{key} must be a boolean", SchemaCovered: true));
            }
        }
    }

    private static void CheckKeyRestrictions(JsonNode? node, JsonPath path, string where, Func<JsonNode?, bool> isOpaque, List<AuthenticationMethodFinding> findings)
    {
        if (node is not JsonObject restrictions)
        {
            findings.Add(new(path, $"{where} must be an object ({{ isEnforced, enforcementType, aaGuids }})", SchemaCovered: true));
            return;
        }

        bool? enforced = null;
        if (Judged(restrictions, "isEnforced", isOpaque) is (string enforcedKey, var isEnforced))
        {
            if (IsBoolean(isEnforced))
            {
                enforced = isEnforced!.GetValueKind() == JsonValueKind.True;
            }
            else
            {
                findings.Add(new(path.Append(enforcedKey), $"{where}.{enforcedKey} must be a boolean", SchemaCovered: true));
            }
        }

        string? enforcement = null;
        if (Judged(restrictions, "enforcementType", isOpaque) is (string typeKey, var enforcementType))
        {
            if (CheckEnum(enforcementType, EnforcementTypes, path.Append(typeKey), $"{where}.{typeKey}", findings))
            {
                TryString(enforcementType, out enforcement);
            }
        }

        bool aaGuidsOpaque = false;
        int aaGuidCount = 0;
        var aaGuidsProperty = Property(restrictions, "aaGuids");
        if (aaGuidsProperty is (string guidsKey, var aaGuids) && !IsAbsent(aaGuids))
        {
            if (isOpaque(aaGuids))
            {
                aaGuidsOpaque = true;
            }
            else if (aaGuids is not JsonArray guidArray)
            {
                findings.Add(new(path.Append(guidsKey), $"{where}.{guidsKey} must be an array of AAGUIDs", SchemaCovered: true));
                aaGuidsOpaque = true;
            }
            else
            {
                aaGuidCount = guidArray.Count;
                for (int i = 0; i < guidArray.Count; i++)
                {
                    var item = guidArray[i];
                    if (isOpaque(item))
                    {
                        continue;
                    }

                    if (!TryString(item, out string? text) || !Guid.TryParseExact(text, "D", out _))
                    {
                        string shown = TryString(item, out string? raw) ? raw : item?.ToJsonString() ?? "null";
                        findings.Add(new(path.Append(guidsKey).Append(i), string.Create(CultureInfo.InvariantCulture, $"{where}.{guidsKey}[{i}] '{shown}' is not a valid AAGUID (an authenticator model GUID, for example cb69481e-8ff7-4039-93ec-0a2729a154a8)"), SchemaCovered: true));
                    }
                }
            }
        }

        if (enforced == true && string.Equals(enforcement, "allow", StringComparison.OrdinalIgnoreCase) && !aaGuidsOpaque && aaGuidCount == 0)
        {
            findings.Add(new(path.Append(aaGuidsProperty?.Key ?? "aaGuids"), $"{where} enforces an allow-list (isEnforced: true, enforcementType: allow) with no aaGuids, which blocks every passkey; list the allowed authenticator AAGUIDs, or set isEnforced: false"));
        }
    }

    private static void CheckTemporaryAccessPass(JsonObject properties, Func<JsonNode?, bool> isOpaque, List<AuthenticationMethodFinding> findings)
    {
        var root = JsonPath.Root;
        if (Judged(properties, "defaultLength", isOpaque) is (string lengthKey, var length))
        {
            CheckInteger(length, MinimumPassLength, MaximumPassLength, root.Append(lengthKey), "properties." + lengthKey, findings, out _);
        }

        var lifetimes = new Dictionary<string, (string Key, long Value)>(StringComparer.Ordinal);
        foreach (string name in new[] { "minimumLifetimeInMinutes", "defaultLifetimeInMinutes", "maximumLifetimeInMinutes" })
        {
            if (Judged(properties, name, isOpaque) is (string key, var value) && CheckInteger(value, MinimumLifetime, MaximumLifetime, root.Append(key), "properties." + key, findings, out long minutes))
            {
                lifetimes[name] = (key, minutes);
            }
        }

        Order(lifetimes, "minimumLifetimeInMinutes", "defaultLifetimeInMinutes", findings);
        Order(lifetimes, "defaultLifetimeInMinutes", "maximumLifetimeInMinutes", findings);
        Order(lifetimes, "minimumLifetimeInMinutes", "maximumLifetimeInMinutes", findings);

        if (Judged(properties, "isUsableOnce", isOpaque) is (string onceKey, var once) && !IsBoolean(once))
        {
            findings.Add(new(root.Append(onceKey), $"properties.{onceKey} must be a boolean", SchemaCovered: true));
        }
    }

    private static void Order(Dictionary<string, (string Key, long Value)> lifetimes, string lower, string upper, List<AuthenticationMethodFinding> findings)
    {
        if (lifetimes.TryGetValue(lower, out var low) && lifetimes.TryGetValue(upper, out var high) && low.Value > high.Value)
        {
            findings.Add(new(JsonPath.Root.Append(low.Key), string.Create(CultureInfo.InvariantCulture, $"properties.{low.Key} ({low.Value}) must not be greater than properties.{high.Key} ({high.Value}); Graph requires minimum <= default <= maximum")));
        }
    }

    private static void CheckX509(JsonObject properties, Func<JsonNode?, bool> isOpaque, List<AuthenticationMethodFinding> findings)
    {
        if (Judged(properties, "authenticationModeConfiguration", isOpaque) is not (string configKey, var configNode))
        {
            return;
        }

        var path = JsonPath.Root.Append(configKey);
        string where = "properties." + configKey;
        if (configNode is not JsonObject config)
        {
            findings.Add(new(path, $"{where} must be an object ({{ x509CertificateAuthenticationDefaultMode, rules }})", SchemaCovered: true));
            return;
        }

        if (Judged(config, "x509CertificateAuthenticationDefaultMode", isOpaque) is (string modeKey, var mode))
        {
            CheckEnum(mode, X509Modes, path.Append(modeKey), $"{where}.{modeKey}", findings);
        }

        if (Judged(config, "rules", isOpaque) is not (string rulesKey, var rules))
        {
            return;
        }

        if (rules is not JsonArray ruleArray)
        {
            findings.Add(new(path.Append(rulesKey), $"{where}.{rulesKey} must be an array of rules", SchemaCovered: true));
            return;
        }

        for (int i = 0; i < ruleArray.Count; i++)
        {
            if (isOpaque(ruleArray[i]))
            {
                continue;
            }

            string ruleWhere = string.Create(CultureInfo.InvariantCulture, $"{where}.{rulesKey}[{i}]");
            if (ruleArray[i] is not JsonObject rule)
            {
                findings.Add(new(path.Append(rulesKey).Append(i), ruleWhere + " must be an object", SchemaCovered: true));
                continue;
            }

            if (Judged(rule, "x509CertificateAuthenticationMode", isOpaque) is (string ruleModeKey, var ruleMode))
            {
                CheckEnum(ruleMode, X509Modes, path.Append(rulesKey).Append(i).Append(ruleModeKey), $"{ruleWhere}.{ruleModeKey}", findings);
            }
        }
    }

    // ---- shared checks ----

    /// <summary>An array of objects that each carry a unique string <paramref name="memberKey"/> (the provider compares the members by it).</summary>
    private static void CheckKeyedMembers(JsonNode? node, JsonPath path, string where, string memberKey, Func<JsonNode?, bool> isOpaque, List<AuthenticationMethodFinding> findings)
    {
        if (node is not JsonArray array)
        {
            findings.Add(new(path, $"{where} must be an array of objects", SchemaCovered: true));
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < array.Count; i++)
        {
            if (isOpaque(array[i]))
            {
                continue;
            }

            string itemWhere = string.Create(CultureInfo.InvariantCulture, $"{where}[{i}]");
            if (array[i] is not JsonObject member)
            {
                findings.Add(new(path.Append(i), itemWhere + " must be an object", SchemaCovered: true));
                continue;
            }

            var key = Property(member, memberKey);
            if (key is null || IsAbsent(key.Value.Value))
            {
                findings.Add(new(path.Append(i), $"{itemWhere} must declare '{memberKey}' (the members are matched by it)", SchemaCovered: true));
                continue;
            }

            if (isOpaque(key.Value.Value))
            {
                continue;
            }

            if (!TryString(key.Value.Value, out string? text) || text.Length == 0)
            {
                findings.Add(new(path.Append(i).Append(key.Value.Key), $"{itemWhere}.{key.Value.Key} must be a non-empty string", SchemaCovered: true));
                continue;
            }

            if (!seen.Add(Guid.TryParse(text, out var guid) ? guid.ToString("D") : text))
            {
                findings.Add(new(path.Append(i).Append(key.Value.Key), $"{itemWhere}.{key.Value.Key} '{text}' is listed more than once"));
            }
        }
    }

    private static bool CheckEnum(JsonNode? node, string[] allowed, JsonPath path, string where, List<AuthenticationMethodFinding> findings)
    {
        if (TryString(node, out string? text) && allowed.Any(a => a.Equals(text, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        findings.Add(new(path, $"{where} must be one of {string.Join(", ", allowed)}", SchemaCovered: true));
        return false;
    }

    private static bool CheckInteger(JsonNode? node, long minimum, long maximum, JsonPath path, string where, List<AuthenticationMethodFinding> findings, out long value)
    {
        value = 0;
        if (node is not JsonValue number || number.GetValueKind() != JsonValueKind.Number || !number.TryGetValue(out double raw) || raw != Math.Floor(raw))
        {
            findings.Add(new(path, $"{where} must be an integer", SchemaCovered: true));
            return false;
        }

        value = (long)raw;
        if (value < minimum || value > maximum)
        {
            findings.Add(new(path, string.Create(CultureInfo.InvariantCulture, $"{where} ({value}) must be between {minimum} and {maximum}"), SchemaCovered: true));
            return false;
        }

        return true;
    }

    // ---- normalisation ----

    private static void NormalizeKeyRestrictions(JsonObject restrictions, Func<JsonNode?, bool> isOpaque)
    {
        LowerEnum(restrictions, "enforcementType", isOpaque);
        if (Property(restrictions, "aaGuids") is not (_, JsonArray aaGuids))
        {
            return;
        }

        // Rebuilt in place: an opaque or invalid item (Check reports an invalid one) is kept as the same node.
        var items = aaGuids.ToList();
        aaGuids.Clear();
        foreach (var item in items)
        {
            aaGuids.Add(!isOpaque(item) && TryString(item, out string? text) && Guid.TryParseExact(text, "D", out var guid) ? JsonValue.Create(guid.ToString("D")) : item);
        }
    }

    private static void LowerEnum(JsonObject obj, string property, Func<JsonNode?, bool> isOpaque)
    {
        if (Property(obj, property) is (string key, var value) && !isOpaque(value) && TryString(value, out string? text))
        {
            obj[key] = text.ToLowerInvariant();
        }
    }

    // ---- JSON helpers (Core has no JsonObjects) ----

    /// <summary>The property (case-insensitive key) with its actual key, or <c>null</c>.</summary>
    private static (string Key, JsonNode? Value)? Property(JsonObject obj, string name)
    {
        foreach (var pair in obj)
        {
            if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return (pair.Key, pair.Value);
            }
        }

        return null;
    }

    /// <summary>The property when it is declared, not <c>null</c> and judgeable now; otherwise <c>null</c>.</summary>
    private static (string Key, JsonNode? Value)? Judged(JsonObject obj, string name, Func<JsonNode?, bool> isOpaque)
    {
        return Property(obj, name) is (string key, var value) && value is not null && !isOpaque(value) ? (key, value) : null;
    }

    private static bool IsAbsent(JsonNode? node)
    {
        return node is null || (node is JsonValue value && value.GetValueKind() == JsonValueKind.Null);
    }

    private static bool IsBoolean(JsonNode? node)
    {
        return node is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False;
    }

    private static bool TryString(JsonNode? node, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? text)
    {
        text = null;
        if (node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.TryGetValue(out string? s))
        {
            text = s;
            return true;
        }

        return false;
    }
}
