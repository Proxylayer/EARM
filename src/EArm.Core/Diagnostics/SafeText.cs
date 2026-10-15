using System;
using System.Text;

namespace EArm.Core.Diagnostics;

/// <summary>
/// Makes text that came from a template, a parameter file or a tenant safe to show on a terminal or in a CI log. Names, property keys and
/// values are echoed into diagnostics and messages, and a hostile one could carry a line break followed by a log command
/// (<c>##vso[task.setvariable ...]</c>, <c>::set-output ...</c>), an escape sequence that rewrites the screen, or a bidirectional override that
/// reorders the text around it. <see cref="Escape"/> replaces every such character with a visible escape (a backslash and n for a line feed,
/// a backslash and u001b for ESC, a backslash and u202e for a right-to-left override), so the text stays on its line and shows what it holds.
/// </summary>
public static class SafeText
{
    /// <summary>The marker put in front of a <c>::</c> that would otherwise start a line (GitHub Actions workflow commands).</summary>
    public const char NeutralizingMarker = '\'';

    /// <summary>
    /// True for a character that is shown escaped: C0 and C1 controls (line breaks and tabs included), DEL, the line and paragraph
    /// separators, and the bidirectional controls (marks, embeddings, overrides and isolates).
    /// </summary>
    public static bool NeedsEscape(char c)
    {
        return c < 0x20
            || (c >= 0x7F && c <= 0x9F)
            || c == 0x061C
            || c == 0x200E
            || c == 0x200F
            || c == 0x2028
            || c == 0x2029
            || (c >= 0x202A && c <= 0x202E)
            || (c >= 0x2066 && c <= 0x2069);
    }

    /// <summary>The escape shown for a character <see cref="NeedsEscape"/> accepts.</summary>
    public static string EscapeOf(char c)
    {
        return c switch
        {
            '\n' => "\\n",
            '\r' => "\\r",
            '\t' => "\\t",
            _ => "\\u" + ((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// <paramref name="text"/> with every character <see cref="NeedsEscape"/> accepts replaced by its visible escape. Returns the same
    /// instance when nothing needs escaping; <c>null</c> becomes the empty string.
    /// </summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        int first = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (NeedsEscape(text[i]))
            {
                first = i;
                break;
            }
        }

        if (first < 0)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length + 16);
        sb.Append(text, 0, first);
        for (int i = first; i < text.Length; i++)
        {
            char c = text[i];
            if (NeedsEscape(c))
            {
                sb.Append(EscapeOf(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// <paramref name="text"/> as a double-quoted string with JSON-style escaping (quotes and backslashes escaped, controls and bidi characters
    /// shown as escapes): for an untrusted name inside a message, where a bare quote could otherwise end the quoted part early.
    /// </summary>
    public static string Quote(string? text)
    {
        // Quotes and backslashes of the text are escaped first; the escapes Escape then adds contain backslashes of their own that stay single.
        string source = text ?? string.Empty;
        if (source.AsSpan().IndexOfAny('"', '\\') >= 0)
        {
            source = source.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        }

        return "\"" + Escape(source) + "\"";
    }

    /// <summary>
    /// The text a break is made of: written inside <c>##vso[</c> and <c>##[</c> (the second <c>#</c> becomes the visible escape
    /// <see cref="TokenEscape"/>), so the token no longer reads as a log command; <see cref="NeutralizingMarker"/> is put in front of a <c>::</c>
    /// that starts a line.
    /// </summary>
    public const string TokenEscape = "\\u0023";

    /// <summary>The tokens an Azure Pipelines agent runs as a logging command wherever they appear in a line (compared ignoring case).</summary>
    private static readonly string[] CommandTokens = ["##vso[", "##["];

    /// <summary>The token a GitHub Actions runner runs as a workflow command when it is the first thing on a line (leading whitespace is trimmed first).</summary>
    private const string LineStartToken = "::";

    /// <summary>True when <paramref name="line"/>, after any leading whitespace, starts with <c>##vso[</c>, <c>##[</c> or <c>::</c>.</summary>
    public static bool StartsWithCommandPrefix(ReadOnlySpan<char> line)
    {
        var trimmed = line.TrimStart();
        foreach (string token in CommandTokens)
        {
            if (trimmed.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return trimmed.StartsWith(LineStartToken, StringComparison.Ordinal);
    }

    /// <summary>
    /// True when <paramref name="line"/> holds text a CI log parser treats as a command: <c>##vso[</c> or <c>##[</c> anywhere, or <c>::</c>
    /// after nothing but whitespace.
    /// </summary>
    public static bool ContainsCommandToken(ReadOnlySpan<char> line)
    {
        foreach (string token in CommandTokens)
        {
            if (line.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return line.TrimStart().StartsWith(LineStartToken, StringComparison.Ordinal);
    }

    /// <summary>
    /// How the text <paramref name="text"/>, which a line holds so far, relates to a command token: <see cref="TokenMatch.Complete"/> when it is
    /// one, <see cref="TokenMatch.Partial"/> when more characters could complete one, otherwise <see cref="TokenMatch.None"/>. <paramref name="atLineStart"/>
    /// admits the <c>::</c> token (only whitespace came before the text on its line).
    /// </summary>
    public static TokenMatch MatchCommandToken(ReadOnlySpan<char> text, bool atLineStart)
    {
        var result = TokenMatch.None;
        foreach (string token in CommandTokens)
        {
            if (text.Equals(token, StringComparison.OrdinalIgnoreCase))
            {
                return TokenMatch.Complete;
            }

            if (text.Length < token.Length && token.AsSpan(0, text.Length).Equals(text, StringComparison.OrdinalIgnoreCase))
            {
                result = TokenMatch.Partial;
            }
        }

        if (atLineStart)
        {
            if (text.Equals(LineStartToken, StringComparison.Ordinal))
            {
                return TokenMatch.Complete;
            }

            if (text.Length < LineStartToken.Length && LineStartToken.AsSpan(0, text.Length).Equals(text, StringComparison.Ordinal))
            {
                result = TokenMatch.Partial;
            }
        }

        return result;
    }

    /// <summary>The text written in place of a complete command token (see <see cref="MatchCommandToken"/>).</summary>
    public static string NeutralizedToken(string token)
    {
        return token == LineStartToken ? NeutralizingMarker + token : "#" + TokenEscape + token[2..];
    }

    /// <summary>
    /// <paramref name="line"/> with every command token neutralised: <c>##vso[</c> and <c>##[</c> anywhere in the line get their second <c>#</c> written
    /// as <see cref="TokenEscape"/> (an Azure Pipelines agent finds them in the middle of a line), and a <c>::</c> that is the first thing on the line
    /// (after any whitespace, which a GitHub Actions runner trims) gets <see cref="NeutralizingMarker"/> in front. Other text is unchanged.
    /// </summary>
    public static string NeutralizeCommandPrefix(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (!ContainsCommandToken(line))
        {
            return line;
        }

        var sb = new StringBuilder(line.Length + 16);
        bool atLineStart = true;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            string? matched = null;
            foreach (string token in CommandTokens)
            {
                if (c == '#' && line.AsSpan(i).StartsWith(token, StringComparison.OrdinalIgnoreCase))
                {
                    matched = line.Substring(i, token.Length);
                    break;
                }
            }

            if (matched is null && atLineStart && c == ':' && line.AsSpan(i).StartsWith(LineStartToken, StringComparison.Ordinal))
            {
                matched = LineStartToken;
            }

            if (matched is not null)
            {
                sb.Append(NeutralizedToken(matched));
                i += matched.Length - 1;
                atLineStart = false;
                continue;
            }

            sb.Append(c);
            if (!char.IsWhiteSpace(c))
            {
                atLineStart = false;
            }
        }

        return sb.ToString();
    }
}

/// <summary>The result of <see cref="SafeText.MatchCommandToken"/>.</summary>
public enum TokenMatch
{
    None,
    Partial,
    Complete,
}