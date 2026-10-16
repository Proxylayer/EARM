using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Graph;

namespace EArm.TestSupport;

/// <summary>
/// The authentication methods policy of the fake tenant (<c>/policies/authenticationMethodsPolicy/authenticationMethodConfigurations</c>): one seeded
/// configuration per method with Microsoft's default values (Learn's GET samples, shortened), the Graph rules <see cref="FakeDirectory"/> enforces on it
/// (a PATCH without the method's <c>@odata.type</c> is a 400, a PATCH never creates, a POST is a 405) and the DELETE that reverts fido2,
/// temporaryAccessPass and x509Certificate to these defaults (any other method answers 400).
/// </summary>
public static class FakeAuthenticationMethodsPolicy
{
    public const string CollectionPath = "/policies/authenticationMethodsPolicy/authenticationMethodConfigurations";

    /// <summary>The default passkey profile id Graph creates on migration to passkey profiles.</summary>
    public const string DefaultPasskeyProfileId = "00000000-0000-0000-0000-000000000001";

    private static readonly Dictionary<string, string> ODataTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Fido2"] = "#microsoft.graph.fido2AuthenticationMethodConfiguration",
        ["TemporaryAccessPass"] = "#microsoft.graph.temporaryAccessPassAuthenticationMethodConfiguration",
        ["X509Certificate"] = "#microsoft.graph.x509CertificateAuthenticationMethodConfiguration",
        ["MicrosoftAuthenticator"] = "#microsoft.graph.microsoftAuthenticatorAuthenticationMethodConfiguration",
        ["Sms"] = "#microsoft.graph.smsAuthenticationMethodConfiguration",
        ["Voice"] = "#microsoft.graph.voiceAuthenticationMethodConfiguration",
        ["Email"] = "#microsoft.graph.emailAuthenticationMethodConfiguration",
        ["SoftwareOath"] = "#microsoft.graph.softwareOathAuthenticationMethodConfiguration",
    };

    private static readonly HashSet<string> Resettable = new(StringComparer.OrdinalIgnoreCase) { "Fido2", "TemporaryAccessPass", "X509Certificate" };

    /// <summary>The Graph ids of the seeded configurations.</summary>
    public static IReadOnlyCollection<string> Ids => ODataTypes.Keys;

    /// <summary>True for a collection path of the policy (any casing).</summary>
    public static bool IsCollection(string collectionPath)
    {
        return collectionPath.TrimEnd('/').Equals(CollectionPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The Microsoft default configurations, freshly built.</summary>
    public static IEnumerable<JsonObject> Defaults()
    {
        return Ids.Select(id => Default(id)!);
    }

    /// <summary>The Microsoft default of one method (by Graph id, any casing), or <c>null</c> for an unknown id.</summary>
    public static JsonObject? Default(string id)
    {
        string? canonical = ODataTypes.Keys.FirstOrDefault(k => k.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (canonical is null)
        {
            return null;
        }

        var config = new JsonObject
        {
            ["@odata.type"] = ODataTypes[canonical],
            ["id"] = canonical,
            ["state"] = canonical is "MicrosoftAuthenticator" ? "enabled" : "disabled",
        };

        switch (canonical)
        {
            case "Fido2":
                config["isSelfServiceRegistrationAllowed"] = true;
                config["isAttestationEnforced"] = false;
                config["keyRestrictions"] = new JsonObject { ["isEnforced"] = false, ["enforcementType"] = "block", ["aaGuids"] = new JsonArray() };
                config["defaultPasskeyProfile"] = DefaultPasskeyProfileId;
                config["passkeyProfiles"] = new JsonArray(new JsonObject
                {
                    ["id"] = DefaultPasskeyProfileId,
                    ["name"] = "Default passkey profile",
                    ["passkeyTypes"] = "deviceBound,synced",
                    ["attestationEnforcement"] = "disabled",
                    ["keyRestrictions"] = new JsonObject { ["isEnforced"] = false, ["enforcementType"] = "allow", ["aaGuids"] = new JsonArray() },
                });
                config["includeTargets"] = new JsonArray(AllUsers(new JsonObject { ["allowedPasskeyProfiles"] = new JsonArray(DefaultPasskeyProfileId) }));
                break;
            case "TemporaryAccessPass":
                config["defaultLifetimeInMinutes"] = 60;
                config["defaultLength"] = 8;
                config["minimumLifetimeInMinutes"] = 60;
                config["maximumLifetimeInMinutes"] = 480;
                config["isUsableOnce"] = false;
                config["includeTargets"] = new JsonArray(AllUsers());
                break;
            case "X509Certificate":
                config["certificateUserBindings"] = new JsonArray(
                    new JsonObject { ["x509CertificateField"] = "PrincipalName", ["userProperty"] = "onPremisesUserPrincipalName", ["priority"] = 1 },
                    new JsonObject { ["x509CertificateField"] = "RFC822Name", ["userProperty"] = "userPrincipalName", ["priority"] = 2 });
                config["authenticationModeConfiguration"] = new JsonObject { ["x509CertificateAuthenticationDefaultMode"] = "x509CertificateSingleFactor", ["rules"] = new JsonArray() };
                config["includeTargets"] = new JsonArray(AllUsers());
                break;
            case "MicrosoftAuthenticator":
                config["isSoftwareOathEnabled"] = false;
                config["featureSettings"] = new JsonObject
                {
                    ["displayAppInformationRequiredState"] = new JsonObject { ["state"] = "default" },
                    ["displayLocationInformationRequiredState"] = new JsonObject { ["state"] = "default" },
                };
                config["includeTargets"] = new JsonArray(AllUsers(new JsonObject { ["authenticationMode"] = "any" }));
                break;
            case "Sms":
                config["includeTargets"] = new JsonArray(AllUsers(new JsonObject { ["isUsableForSignIn"] = true }));
                break;
            case "Voice":
                config["isOfficePhoneAllowed"] = false;
                config["includeTargets"] = new JsonArray(AllUsers());
                break;
            case "Email":
                config["allowExternalIdToUseEmailOtp"] = "default";
                config["includeTargets"] = new JsonArray(AllUsers());
                break;
            default:
                config["includeTargets"] = new JsonArray(AllUsers());
                break;
        }

        config["excludeTargets"] = new JsonArray();
        return config;
    }

    /// <summary>Graph refuses a PATCH without the configuration's derived type ("The request body must specify @odata.type").</summary>
    internal static void RequireODataType(JsonObject entity, JsonNode? body, RetryHint hint)
    {
        string? id = entity["id"]?.GetValue<string>();
        string expected = id is not null && ODataTypes.TryGetValue(id, out var type) ? type : "?";
        string? declared = body is JsonObject payload && payload["@odata.type"] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
        if (declared is null)
        {
            throw FakeDirectory.BadRequest($"The request body must specify @odata.type ('{expected}') to update an authentication method configuration.", hint);
        }

        if (!declared.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw FakeDirectory.BadRequest($"The @odata.type '{declared}' does not match the authentication method configuration '{id}' ('{expected}').", hint);
        }
    }

    /// <summary>The default that replaces a configuration on DELETE; a method without a documented reset is a 400.</summary>
    internal static JsonObject ResetDefault(JsonObject entity, RetryHint hint)
    {
        string id = entity["id"]?.GetValue<string>() ?? string.Empty;
        if (!Resettable.Contains(id))
        {
            throw FakeDirectory.BadRequest($"DELETE is not supported on the authentication method configuration '{id}'.", hint);
        }

        return Default(id)!;
    }

    private static JsonObject AllUsers(JsonObject? extra = null)
    {
        var target = new JsonObject { ["targetType"] = "group", ["id"] = "all_users", ["isRegistrationRequired"] = false };
        if (extra is not null)
        {
            foreach (var pair in extra.ToList())
            {
                extra.Remove(pair.Key);
                target[pair.Key] = pair.Value;
            }
        }

        return target;
    }
}
