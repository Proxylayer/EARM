using System;
using System.Text;

namespace EArm.Az;

/// <summary>
/// Builds Graph URL path fragments. Query strings never belong here (they go through
/// <see cref="GraphRequest.UrlParameters"/>); these helpers only make path segments and alternate-key literals safe.
/// </summary>
public static class GraphPath
{
    /// <summary>
    /// An alternate-key segment such as <c>applications(uniqueName='it''s')</c>: single quotes in the value are
    /// doubled (OData literal escaping) and characters that cannot appear raw in a path are percent-encoded.
    /// </summary>
    public static string AlternateKey(string collection, string keyName, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);
        ArgumentNullException.ThrowIfNull(value);
        return collection.TrimEnd('/') + "(" + keyName + "='" + KeyValue(value) + "')";
    }

    /// <summary>The escaped form of a value for use inside <c>(...='...')</c>: <c>'</c> doubled, URL-unsafe characters percent-encoded.</summary>
    public static string KeyValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Encode(value.Replace("'", "''", StringComparison.Ordinal), keepApostrophe: true);
    }

    /// <summary>A plain path segment (object id, UPN, FIC name) with URL-unsafe characters percent-encoded.</summary>
    public static string Segment(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Encode(value, keepApostrophe: true);
    }

    /// <summary>Joins path fragments with single slashes, keeping a leading slash on the result.</summary>
    public static string Join(params string[] fragments)
    {
        ArgumentNullException.ThrowIfNull(fragments);
        var builder = new StringBuilder();
        foreach (var fragment in fragments)
        {
            if (string.IsNullOrEmpty(fragment))
            {
                continue;
            }

            builder.Append('/').Append(fragment.Trim('/'));
        }

        return builder.Length == 0 ? "/" : builder.ToString();
    }

    private static string Encode(string value, bool keepApostrophe)
    {
        var builder = new StringBuilder(value.Length + 8);
        Span<byte> bytes = stackalloc byte[4];
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.IsAscii && IsSafe((char)rune.Value, keepApostrophe))
            {
                builder.Append((char)rune.Value);
                continue;
            }

            var written = rune.EncodeToUtf8(bytes);
            for (var i = 0; i < written; i++)
            {
                builder.Append('%').Append(bytes[i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    private static bool IsSafe(char c, bool keepApostrophe)
    {
        if (char.IsAsciiLetterOrDigit(c))
        {
            return true;
        }

        return c switch
        {
            '-' or '.' or '_' or '~' => true,
            '@' or ':' or '(' or ')' or ',' or '=' or '$' or '!' or '*' => true,
            '\'' => keepApostrophe,
            _ => false,
        };
    }
}
