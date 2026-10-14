using System;
using System.Text;

namespace EntraArm.Az;

/// <summary>
/// Rewrites lone surrogate escapes (a backslash-u escape of a code unit in D800-DFFF without its pair) inside JSON strings to the
/// escape of U+FFFD before parsing. System.Text.Json accepts such text at parse time and then throws
/// <see cref="InvalidOperationException"/> on the first read of the string, so one odd character in a Graph reply would otherwise
/// fail whichever later step reads, compares, hashes or renders that value. The replacement has the same length as the escape.
/// (EntraArm.Core has the fuller variant with paths and positions; this assembly does not reference Core.)
/// </summary>
internal static class JsonSurrogateSanitizer
{
    public static string Replace(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.IndexOf("\\u", StringComparison.Ordinal) < 0)
        {
            return json;
        }

        StringBuilder? builder = null;
        bool inString = false;
        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];
            if (!inString)
            {
                inString = c == '"';
                continue;
            }

            if (c == '"')
            {
                inString = false;
                continue;
            }

            if (c != '\\' || i + 1 >= json.Length)
            {
                continue;
            }

            if (json[i + 1] != 'u' || i + 5 >= json.Length || !TryHex4(json, i + 2, out int unit) || unit is < 0xD800 or > 0xDFFF)
            {
                i++;
                continue;
            }

            bool pair = unit <= 0xDBFF
                && i + 11 < json.Length
                && json[i + 6] == '\\'
                && json[i + 7] == 'u'
                && TryHex4(json, i + 8, out int low)
                && low is >= 0xDC00 and <= 0xDFFF;
            if (pair)
            {
                i += 11;
                continue;
            }

            builder ??= new StringBuilder(json);
            builder[i + 2] = 'f';
            builder[i + 3] = 'f';
            builder[i + 4] = 'f';
            builder[i + 5] = 'd';
            i += 5;
        }

        return builder?.ToString() ?? json;
    }

    private static bool TryHex4(string text, int start, out int value)
    {
        value = 0;
        for (int i = start; i < start + 4; i++)
        {
            int digit = text[i] switch
            {
                >= '0' and <= '9' => text[i] - '0',
                >= 'a' and <= 'f' => text[i] - 'a' + 10,
                >= 'A' and <= 'F' => text[i] - 'A' + 10,
                _ => -1,
            };
            if (digit < 0)
            {
                return false;
            }

            value = (value << 4) | digit;
        }

        return true;
    }
}
