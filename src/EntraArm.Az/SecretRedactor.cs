using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EntraArm.Az;

/// <summary>
/// Masks secret-looking values before anything reaches a log or an exception message: JSON properties named like
/// <c>secretText</c>, <c>password</c>, <c>client_secret</c>, <c>accessToken</c>, the <c>Authorization</c> header,
/// bearer tokens and JWT-shaped strings. Bodies are never logged at all; this is the safety net for everything else.
/// </summary>
/// <remarks>
/// TODO(WI-02): once EntraArm.Core ships <c>SecretValue</c> and its redacting JSON converter, route values typed as
/// <c>SecretValue</c> through here as well. EntraArm.Az deliberately has no Core reference today (plan §2.1), and
/// bodies reach this layer as plain JSON written by the Engine's revealing converter.
/// </remarks>
public static partial class SecretRedactor
{
    public const string Mask = "***";

    private static readonly HashSet<string> ExactSecretNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "secretText",
        "password",
        "authorization",
        "client_secret",
        "clientSecret",
        "accessToken",
        "access_token",
        "refreshToken",
        "refresh_token",
        "idToken",
        "id_token",
        "secret",
        "passwordProfile",
        "passwordCredential",
        "passwordCredentials",
        "keyCredentials",
    };

    private static readonly string[] NonSecretTokenNames = ["skiptoken", "$skiptoken", "deltatoken", "$deltatoken", "skipToken", "deltaToken"];

    /// <summary>True when a property, header or parameter name denotes a secret.</summary>
    public static bool IsSecretName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (ExactSecretNames.Contains(name))
        {
            return true;
        }

        foreach (var benign in NonSecretTokenNames)
        {
            if (string.Equals(name, benign, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return name.Contains("password", StringComparison.OrdinalIgnoreCase)
            || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("token", StringComparison.OrdinalIgnoreCase)
            || name.Contains("credential", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Masks the value when its name is secret-like; otherwise redacts embedded tokens in the value text.</summary>
    public static string RedactValue(string name, string? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        return IsSecretName(name) ? Mask : RedactText(value);
    }

    /// <summary>Redacts one argv element of the shape <c>name=value</c> or a bare value.</summary>
    public static string RedactArgument(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        var separator = argument.IndexOf('=', StringComparison.Ordinal);
        if (separator > 0 && IsSecretName(argument[..separator]))
        {
            return argument[..(separator + 1)] + Mask;
        }

        return RedactText(argument);
    }

    /// <summary>Masks secret-looking fragments in free text (stderr, messages, URLs).</summary>
    public static string RedactText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = JsonSecretPropertyPattern().Replace(text, static m => "\"" + m.Groups["name"].Value + "\": \"" + Mask + "\"");
        result = RedactKeyValuePairs(result);
        result = BearerPattern().Replace(result, "Bearer " + Mask);
        result = JwtPattern().Replace(result, Mask);
        return result;
    }

    /// <summary>
    /// Masks every <c>name=value</c> / <c>name: value</c> pair whose name <see cref="IsSecretName"/> flags, so this pass
    /// and the name check share a single definition of "secret" and cannot drift apart. A pair with a non-secret name is
    /// kept as-is, but its value is scanned again: the value charset is wide enough to have swallowed a later pair
    /// (<c>WARNING: secretText=...</c>), and each recursion operates on a strictly shorter string.
    /// </summary>
    private static string RedactKeyValuePairs(string text)
    {
        return KeyValueSecretPattern().Replace(text, static m =>
        {
            var key = m.Groups["key"];
            return IsSecretName(m.Groups["name"].Value)
                ? key.Value + Mask
                : key.Value + RedactKeyValuePairs(m.Value[key.Length..]);
        });
    }

    /// <summary>Deep-copies a JSON node with every secret-named property value replaced by <see cref="Mask"/>.</summary>
    public static JsonNode? RedactJson(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
            {
                var copy = new JsonObject();
                foreach (var pair in obj)
                {
                    copy[pair.Key] = IsSecretName(pair.Key) ? JsonValue.Create(Mask) : RedactJson(pair.Value);
                }

                return copy;
            }

            case JsonArray array:
            {
                var copy = new JsonArray();
                foreach (var item in array)
                {
                    copy.Add(RedactJson(item));
                }

                return copy;
            }

            default:
                return node.DeepClone();
        }
    }

    [GeneratedRegex("\"(?<name>[^\"]*(?:password|secret|token|authorization|credential)[^\"]*)\"\\s*:\\s*\"(?:[^\"\\\\]|\\\\.)*\"", RegexOptions.IgnoreCase)]
    private static partial Regex JsonSecretPropertyPattern();

    // Any identifier-ish name (letters, digits, '_', '$', '.', '-') followed by '=' or ':'; whether the pair is secret is decided
    // by IsSecretName in RedactKeyValuePairs, not here. An optional "Bearer " prefix is swallowed so "Authorization: Bearer <opaque>"
    // masks the token rather than only the scheme word.
    [GeneratedRegex("(?<key>(?<name>[\\w$.-]+)\\s*[=:]\\s*)(?:Bearer\\s+)?[^\\s,;\"']+", RegexOptions.IgnoreCase)]
    private static partial Regex KeyValueSecretPattern();

    [GeneratedRegex("\\bBearer\\s+[A-Za-z0-9\\-._~+/]+=*", RegexOptions.IgnoreCase)]
    private static partial Regex BearerPattern();

    [GeneratedRegex("\\beyJ[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}")]
    private static partial Regex JwtPattern();
}
