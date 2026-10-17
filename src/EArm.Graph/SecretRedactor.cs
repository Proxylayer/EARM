using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace EArm.Graph;

/// <summary>
/// Masks secret-looking values before anything reaches a log or an exception message: JSON properties named like
/// <c>secretText</c>, <c>password</c>, <c>client_secret</c>, <c>accessToken</c>, the <c>Authorization</c> header,
/// bearer tokens and JWT-shaped strings. Bodies are never logged at all; this is the safety net for everything else.
/// </summary>
/// <remarks>
/// TODO: once EArm.Core ships <c>SecretValue</c> and its redacting JSON converter, route values typed as
/// <c>SecretValue</c> through here as well. EArm.Graph deliberately has no Core reference today, and
/// bodies reach this layer as plain JSON written by the Engine's revealing converter.
/// </remarks>
public static class SecretRedactor
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

    /// <summary>
    /// True when a property, header or parameter name denotes a secret. The name is tested as a whole and part by part (split on
    /// <c>-</c>, <c>_</c>, <c>.</c>, <c>$</c>, so a leading <c>--</c> or <c>$</c> and prefixes such as <c>x-ms-</c> or <c>Proxy-</c> do not hide
    /// the meaningful word): <c>Proxy-Authorization</c>, <c>--authorization</c> and <c>x-ms-authorization</c> are secret like
    /// <c>Authorization</c>. The paging tokens (<c>$skiptoken</c>, <c>deltatoken</c>) are exempt whether they stand alone or as a part.
    /// </summary>
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

        ReadOnlySpan<char> whole = name;
        if (IsNonSecretTokenName(whole))
        {
            return false;
        }

        if (MatchesSecretRules(whole))
        {
            return true;
        }

        int start = 0;
        for (int i = 0; i <= whole.Length; i++)
        {
            if (i < whole.Length && !IsPartSeparator(whole[i]))
            {
                continue;
            }

            if (i > start)
            {
                var part = whole[start..i];
                if (!IsNonSecretTokenName(part) && MatchesSecretRules(part))
                {
                    return true;
                }
            }

            start = i + 1;
        }

        return false;
    }

    private static bool IsPartSeparator(char c)
    {
        return c is '-' or '_' or '.' or '$';
    }

    private static bool IsNonSecretTokenName(ReadOnlySpan<char> name)
    {
        foreach (var benign in NonSecretTokenNames)
        {
            if (name.Equals(benign, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesSecretRules(ReadOnlySpan<char> name)
    {
        return name.Contains("password", StringComparison.OrdinalIgnoreCase)
            || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || name.Contains("authorization", StringComparison.OrdinalIgnoreCase)
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
    /// <remarks>
    /// Four passes in order (JSON properties, key/value pairs, bearer tokens, JWTs), each a hand-written forward scan that is linear in the
    /// text, so one long unbroken token cannot stall a caller (exceptions redact stderr as they are built). They are scanners and not
    /// regular expressions on purpose: the backtracking engine is super-linear on these shapes, and the linear NonBacktracking engine of
    /// .NET 10.0.9 silently misses <c>\b</c> matches in texts of roughly 100,000 characters or more, which for a redactor is a leak.
    /// </remarks>
    public static string RedactText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // Registered secret VALUES first (see SecretScrubber): they have no recognisable shape, so the exact text is replaced before the shape scanners run.
        var result = SecretTextScanners.RedactJsonProperties(SecretScrubber.Scrub(text));
        result = SecretTextScanners.RedactKeyValuePairs(result);
        result = SecretTextScanners.RedactBearerTokens(result);
        return SecretTextScanners.RedactJwts(result);
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
}
