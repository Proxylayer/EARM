using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

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

    // Authorization scheme words swallowed after "name:" so the credential that follows is masked, not just the scheme.
    private static readonly string[] AuthorizationSchemes = ["Bearer", "Basic", "Negotiate", "NTLM"];

    // Property-name fragments that make a JSON property secret in the text pass (matched anywhere in the name, case-insensitively).
    private static readonly string[] JsonSecretNameFragments = ["password", "secret", "token", "authorization", "credential"];

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

        var result = RedactJsonProperties(text);
        result = RedactKeyValuePairs(result);
        result = RedactBearerTokens(result);
        return RedactJwts(result);
    }

    /// <summary>
    /// Masks the string value of every JSON property whose name contains <c>password</c>, <c>secret</c>, <c>token</c>, <c>authorization</c> or
    /// <c>credential</c>: <c>"name"  :  "value"</c> becomes <c>"name": "***"</c>. The value may contain escapes; a value that is cut off
    /// before its closing quote (truncated stderr) is masked to the end of the text. Each quote is examined as a possible start once.
    /// </summary>
    private static string RedactJsonProperties(string text)
    {
        StringBuilder? output = null;
        int copied = 0;
        int i = 0;
        while (true)
        {
            int open = text.IndexOf('"', i);
            if (open < 0)
            {
                break;
            }

            int close = text.IndexOf('"', open + 1);
            if (close < 0)
            {
                break;
            }

            // The name is exactly the text up to the next quote; a failed candidate resumes at that quote.
            var name = text.AsSpan(open + 1, close - open - 1);
            int colon = SkipWhitespace(text, close + 1);
            if (!ContainsSecretFragment(name) || colon >= text.Length || text[colon] != ':')
            {
                i = close;
                continue;
            }

            int valueQuote = SkipWhitespace(text, colon + 1);
            if (valueQuote >= text.Length || text[valueQuote] != '"')
            {
                i = close;
                continue;
            }

            int end = -1;
            bool cutOff = false;
            for (int q = valueQuote + 1; ; q++)
            {
                if (q >= text.Length)
                {
                    cutOff = true;
                    break;
                }

                char c = text[q];
                if (c == '"')
                {
                    end = q + 1;
                    break;
                }

                if (c == '\\')
                {
                    if (q + 1 >= text.Length)
                    {
                        cutOff = true;
                        break;
                    }

                    if (text[q + 1] == '\n')
                    {
                        break;
                    }

                    q++;
                }
            }

            if (end < 0 && !cutOff)
            {
                i = close;
                continue;
            }

            output ??= new StringBuilder(text.Length);
            output.Append(text, copied, open - copied).Append('"').Append(name).Append("\": \"").Append(Mask);
            if (cutOff)
            {
                copied = text.Length;
                i = text.Length;
                break;
            }

            output.Append('"');
            copied = end;
            i = end;
        }

        return output is null ? text : output.Append(text, copied, text.Length - copied).ToString();
    }

    private static bool ContainsSecretFragment(ReadOnlySpan<char> name)
    {
        foreach (var fragment in JsonSecretNameFragments)
        {
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Masks <c>Bearer &lt;token&gt;</c> (case-insensitive scheme word at a word boundary, whitespace, then token characters and optional
    /// <c>=</c> padding) as <c>Bearer ***</c>.
    /// </summary>
    private static string RedactBearerTokens(string text)
    {
        const string Scheme = "Bearer";
        StringBuilder? output = null;
        int copied = 0;
        int i = 0;
        while (i < text.Length)
        {
            int at = text.IndexOf(Scheme, i, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                break;
            }

            // Cheap checks first: the token run is only scanned once the match is certain, so failed candidates cost O(1) + whitespace.
            int tokenStart = SkipWhitespace(text, at + Scheme.Length);
            if ((at > 0 && IsWordChar(text[at - 1])) || tokenStart == at + Scheme.Length || tokenStart >= text.Length || !IsBearerTokenChar(text[tokenStart]))
            {
                i = at + 1;
                continue;
            }

            int tokenEnd = tokenStart;
            while (tokenEnd < text.Length && IsBearerTokenChar(text[tokenEnd]))
            {
                tokenEnd++;
            }

            while (tokenEnd < text.Length && text[tokenEnd] == '=')
            {
                tokenEnd++;
            }

            output ??= new StringBuilder(text.Length);
            output.Append(text, copied, at - copied).Append("Bearer ").Append(Mask);
            copied = tokenEnd;
            i = tokenEnd;
        }

        return output is null ? text : output.Append(text, copied, text.Length - copied).ToString();
    }

    /// <summary>
    /// Masks JWT-shaped strings: <c>eyJ</c> at a word boundary, then three dot-separated runs of at least eight base64url characters
    /// (the first run includes the characters after <c>eyJ</c>). A run of base64url characters has one end, so whether the dots and runs
    /// follow it is computed once per run and shared by every <c>eyJ</c> inside it.
    /// </summary>
    private static string RedactJwts(string text)
    {
        const string Prefix = "eyJ";
        StringBuilder? output = null;
        int copied = 0;
        int i = 0;
        int runStart = -1;
        int runEnd = -1;
        int matchEnd = -1;
        while (i < text.Length)
        {
            int at = text.IndexOf(Prefix, i, StringComparison.Ordinal);
            if (at < 0)
            {
                break;
            }

            if (at > 0 && IsWordChar(text[at - 1]))
            {
                i = at + 1;
                continue;
            }

            int first = at + Prefix.Length;
            if (runStart < 0 || first < runStart || first > runEnd)
            {
                runStart = first;
                runEnd = SkipBase64Url(text, first);
                matchEnd = JwtTailEnd(text, runEnd);
            }

            if (runEnd - first < 8 || matchEnd < 0)
            {
                i = at + 1;
                continue;
            }

            output ??= new StringBuilder(text.Length);
            output.Append(text, copied, at - copied).Append(Mask);
            copied = matchEnd;
            i = matchEnd;
            runStart = -1;
        }

        return output is null ? text : output.Append(text, copied, text.Length - copied).ToString();
    }

    // The end of the third segment when a JWT's first segment ends at firstEnd (a '.', then two more runs of 8+ characters each), else -1.
    private static int JwtTailEnd(string text, int firstEnd)
    {
        if (firstEnd >= text.Length || text[firstEnd] != '.')
        {
            return -1;
        }

        int secondEnd = SkipBase64Url(text, firstEnd + 1);
        if (secondEnd - (firstEnd + 1) < 8 || secondEnd >= text.Length || text[secondEnd] != '.')
        {
            return -1;
        }

        int thirdEnd = SkipBase64Url(text, secondEnd + 1);
        return thirdEnd - (secondEnd + 1) >= 8 ? thirdEnd : -1;
    }

    private static int SkipBase64Url(string text, int index)
    {
        while (index < text.Length && (char.IsAsciiLetterOrDigit(text[index]) || text[index] is '_' or '-'))
        {
            index++;
        }

        return index;
    }

    private static bool IsBearerTokenChar(char c)
    {
        return char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '+' or '/';
    }

    // The regex word class \w: letters, decimal digits, nonspacing marks and connector punctuation (which includes '_').
    private static bool IsWordChar(char c)
    {
        return char.IsLetterOrDigit(c) || (c > 127 ? char.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.ConnectorPunctuation : c == '_');
    }

    /// <summary>
    /// Masks every <c>name=value</c> / <c>name: value</c> pair whose name <see cref="IsSecretName"/> flags, so this pass
    /// and the name check share a single definition of "secret" and cannot drift apart. A name is a run of letters, digits and
    /// <c>_ $ . -</c> that is followed by optional whitespace and <c>=</c> or <c>:</c> (a name can only start where the run starts, so
    /// each run is examined once); the value is the following run up to whitespace, <c>,</c>, <c>;</c> or a quote, with an optional
    /// leading <c>Bearer</c> / <c>Basic</c> / <c>Negotiate</c> / <c>NTLM</c> scheme word swallowed so <c>Authorization: Bearer &lt;opaque&gt;</c>
    /// masks the credential rather than only the scheme. A pair with a non-secret name is kept as-is and scanning resumes at its value
    /// (the value charset is wide enough to have swallowed a later pair, <c>WARNING: secretText=...</c>). It is one forward scan: no
    /// recursion, and the end of a value run is computed once for every pair nested in it, so the cost is linear in the text.
    /// </summary>
    private static string RedactKeyValuePairs(string text)
    {
        StringBuilder? output = null;
        int copied = 0;
        int runStart = 0;
        int runEnd = 0;
        int i = 0;
        while (i < text.Length)
        {
            if (!IsNameChar(text[i]))
            {
                i++;
                continue;
            }

            int nameEnd = i + 1;
            while (nameEnd < text.Length && IsNameChar(text[nameEnd]))
            {
                nameEnd++;
            }

            int separator = SkipWhitespace(text, nameEnd);
            if (separator >= text.Length || (text[separator] != '=' && text[separator] != ':'))
            {
                i = nameEnd;
                continue;
            }

            int valueRegion = SkipWhitespace(text, separator + 1);
            int valueStart = valueRegion;
            if (StartsWithScheme(text, valueRegion, out int schemeEnd))
            {
                int afterScheme = SkipWhitespace(text, schemeEnd);
                if (afterScheme > schemeEnd && afterScheme < text.Length && IsValueChar(text[afterScheme]))
                {
                    valueStart = afterScheme;
                }
            }

            if (valueStart >= text.Length || !IsValueChar(text[valueStart]))
            {
                i = nameEnd;
                continue;
            }

            if (!IsSecretName(text.Substring(i, nameEnd - i)))
            {
                // Not a secret: keep the key and rescan from the value region, where a later name may be hiding.
                i = valueRegion;
                continue;
            }

            if (valueStart < runStart || valueStart >= runEnd)
            {
                runStart = valueStart;
                runEnd = valueStart;
                while (runEnd < text.Length && IsValueChar(text[runEnd]))
                {
                    runEnd++;
                }
            }

            output ??= new StringBuilder(text.Length);
            output.Append(text, copied, valueRegion - copied).Append(Mask);
            copied = runEnd;
            i = runEnd;
        }

        return output is null ? text : output.Append(text, copied, text.Length - copied).ToString();
    }

    // Same character class as the former [\w$.-]: letters, digits, marks and connector punctuation (including '_'), plus '$', '.', '-'.
    private static bool IsNameChar(char c)
    {
        return char.IsLetterOrDigit(c)
            || c is '_' or '$' or '.' or '-'
            || (c > 127 && char.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.ConnectorPunctuation);
    }

    private static bool IsValueChar(char c)
    {
        return !char.IsWhiteSpace(c) && c is not (',' or ';' or '"' or '\'');
    }

    private static int SkipWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index;
    }

    private static bool StartsWithScheme(string text, int index, out int end)
    {
        foreach (var scheme in AuthorizationSchemes)
        {
            if (text.AsSpan(index).StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                end = index + scheme.Length;
                return true;
            }
        }

        end = index;
        return false;
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
