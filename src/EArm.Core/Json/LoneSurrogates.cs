using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using EArm.Core.Diagnostics;

namespace EArm.Core.Json;

/// <summary>One lone surrogate escape (a high or low UTF-16 surrogate written as a backslash-u escape without its pair) found in a JSON document.</summary>
public readonly record struct LoneSurrogateEscape(JsonPath Path, SourceLocation Location);

/// <summary>
/// Handles JSON strings that contain a lone surrogate escape (for example a backslash-u escape of D800 with no low surrogate after it).
/// That is legal JSON text, but System.Text.Json accepts it at parse time and then throws <see cref="InvalidOperationException"/> on the
/// first read of the string (a <see cref="JsonElement"/>- or <see cref="System.Text.Json.Nodes.JsonNode"/>-backed value reads, compares,
/// writes or schema-evaluates it), far from where the data came in. The parse boundaries therefore run input through
/// <see cref="Replace(ReadOnlyMemory{byte}, out IReadOnlyList{LoneSurrogateEscape})"/> or <see cref="Replace(string)"/>: every lone
/// surrogate escape is rewritten to the escape of U+FFFD (same length, so offsets and the source map stay valid), and template and
/// parameter files additionally report what was found as an error with the path and position.
/// </summary>
public static class LoneSurrogates
{
    private static readonly byte[] ReplacementDigits = "fffd"u8.ToArray();

    /// <summary>
    /// True when <paramref name="utf8"/> holds an escape of a code unit in D800-DFFF (a cheap superset check: it neither knows string
    /// boundaries nor pairs, so it may say true for a valid pair).
    /// </summary>
    public static bool MayContain(ReadOnlySpan<byte> utf8)
    {
        for (int i = 0; i + 5 < utf8.Length; i++)
        {
            if (utf8[i] == (byte)'\\' && utf8[i + 1] == (byte)'u' && IsSurrogateStart(utf8[i + 2], utf8[i + 3]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The same check over UTF-16 text.</summary>
    public static bool MayContain(ReadOnlySpan<char> text)
    {
        for (int i = 0; i + 5 < text.Length; i++)
        {
            if (text[i] == '\\' && text[i + 1] == 'u' && text[i + 2] < 128 && text[i + 3] < 128 && IsSurrogateStart((byte)text[i + 2], (byte)text[i + 3]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <paramref name="json"/> with every lone surrogate escape inside a string or property name rewritten to the escape of U+FFFD;
    /// the same instance when there is none. Not JSON (or JSON the reader rejects part-way): returned with whatever was rewritten
    /// before the failure, the real parse reports the error.
    /// </summary>
    public static string Replace(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (!MayContain(json.AsSpan()))
        {
            return json;
        }

        var replaced = Replace(Encoding.UTF8.GetBytes(json), out var found);
        return found.Count == 0 ? json : Encoding.UTF8.GetString(replaced.Span);
    }

    /// <summary>
    /// <paramref name="utf8"/> with every lone surrogate escape inside a string or property name rewritten to the escape of U+FFFD, and the
    /// escapes found (document order, path and 1-based line/column of the backslash). Returns the same memory and an empty list when
    /// there is none. A leading UTF-8 byte-order mark is kept.
    /// </summary>
    public static ReadOnlyMemory<byte> Replace(ReadOnlyMemory<byte> utf8, out IReadOnlyList<LoneSurrogateEscape> found)
    {
        found = [];
        if (!MayContain(utf8.Span))
        {
            return utf8;
        }

        byte[] copy = utf8.ToArray();
        int bom = copy.Length >= 3 && copy[0] == 0xEF && copy[1] == 0xBB && copy[2] == 0xBF ? 3 : 0;
        var results = new List<LoneSurrogateEscape>();
        try
        {
            Scan(copy, bom, results);
        }
        catch (JsonException)
        {
            // Not parseable: the real parse reports it. Whatever was rewritten before the failure stays.
        }

        found = results;
        return results.Count == 0 ? utf8 : copy;
    }

    /// <summary>
    /// <paramref name="text"/> with every unpaired UTF-16 surrogate char replaced by U+FFFD (the same instance when there is none).
    /// For strings already in memory, as opposed to <see cref="Replace(string)"/> which rewrites JSON text.
    /// </summary>
    public static string Scrub(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (!char.IsSurrogate(c))
            {
                continue;
            }

            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                continue;
            }

            return ScrubFrom(text, i);
        }

        return text;
    }

    private static string ScrubFrom(string text, int first)
    {
        var builder = new StringBuilder(text, 0, first, text.Length);
        for (int i = first; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                builder.Append(c).Append(text[i + 1]);
                i++;
            }
            else
            {
                builder.Append(char.IsSurrogate(c) ? (char)0xFFFD : c);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// The string value of <paramref name="element"/> with any lone surrogate read as U+FFFD instead of throwing; <c>null</c> when it is not a string.
    /// </summary>
    public static string? GetStringOrReplacement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            return element.GetString();
        }
        catch (InvalidOperationException)
        {
            using var document = JsonDocument.Parse(Replace(element.GetRawText()));
            return document.RootElement.GetString();
        }
    }

    private sealed class Frame(bool isArray)
    {
        public bool IsArray { get; } = isArray;

        public string? Name { get; set; }

        public int Index { get; set; } = -1;
    }

    private static void Scan(byte[] buffer, int start, List<LoneSurrogateEscape> results)
    {
        // The reader runs over the very array that is patched, so a patched token is read back already replaced.
        var reader = new Utf8JsonReader(buffer.AsSpan(start), TemplateJson.ReaderOptions);
        var frames = new List<Frame>();
        while (reader.Read())
        {
            var token = reader.TokenType;
            if (token is JsonTokenType.EndObject or JsonTokenType.EndArray)
            {
                frames.RemoveAt(frames.Count - 1);
                continue;
            }

            if (token == JsonTokenType.PropertyName)
            {
                Fix(buffer, start, ref reader, frames, results, nameToken: true);
                frames[^1].Name = reader.GetString();
                continue;
            }

            if (frames.Count > 0 && frames[^1].IsArray)
            {
                frames[^1].Index++;
            }

            switch (token)
            {
                case JsonTokenType.StartObject:
                    frames.Add(new Frame(isArray: false));
                    break;
                case JsonTokenType.StartArray:
                    frames.Add(new Frame(isArray: true));
                    break;
                case JsonTokenType.String:
                    Fix(buffer, start, ref reader, frames, results, nameToken: false);
                    break;
            }
        }
    }

    /// <summary>Rewrites the lone surrogate escapes of the current string or property-name token in place and records them.</summary>
    private static void Fix(byte[] buffer, int start, ref Utf8JsonReader reader, List<Frame> frames, List<LoneSurrogateEscape> results, bool nameToken)
    {
        if (!reader.ValueIsEscaped || !MayContain(reader.ValueSpan))
        {
            return;
        }

        var positions = new List<int>();
        FindLone(reader.ValueSpan, positions);
        if (positions.Count == 0)
        {
            return;
        }

        // The token starts at its opening quote; ValueSpan begins right after it.
        int valueStart = start + (int)reader.TokenStartIndex + 1;
        var path = JsonPath.Root;
        for (int i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            if (frame.IsArray)
            {
                path = path.Append(frame.Index);
            }
            else if (i < frames.Count - 1 || !nameToken)
            {
                path = path.Append(frame.Name ?? string.Empty);
            }
        }

        if (nameToken && frames.Count > 0 && !frames[^1].IsArray)
        {
            // The name is being read right now and is not known as text yet: name the member by its replaced spelling.
            path = path.Append(NameWithReplacements(reader.ValueSpan, positions));
        }

        foreach (int position in positions)
        {
            int at = valueStart + position;
            results.Add(new LoneSurrogateEscape(path, LocationOf(buffer, start, at)));
            ReplacementDigits.CopyTo(buffer.AsSpan(at + 2, 4));
        }
    }

    private static string NameWithReplacements(ReadOnlySpan<byte> raw, List<int> positions)
    {
        byte[] quoted = new byte[raw.Length + 2];
        quoted[0] = (byte)'"';
        raw.CopyTo(quoted.AsSpan(1));
        quoted[^1] = (byte)'"';
        foreach (int position in positions)
        {
            ReplacementDigits.CopyTo(quoted.AsSpan(1 + position + 2, 4));
        }

        try
        {
            using var document = JsonDocument.Parse(quoted);
            return document.RootElement.GetString() ?? string.Empty;
        }
        catch (JsonException)
        {
            return Encoding.UTF8.GetString(quoted, 1, quoted.Length - 2);
        }
    }

    /// <summary>Offsets (of each backslash) of the lone surrogate escapes in a raw, still escaped string token.</summary>
    private static void FindLone(ReadOnlySpan<byte> raw, List<int> positions)
    {
        int i = 0;
        while (i < raw.Length)
        {
            if (raw[i] != (byte)'\\' || i + 1 >= raw.Length)
            {
                i++;
                continue;
            }

            if (raw[i + 1] != (byte)'u' || i + 5 >= raw.Length)
            {
                i += 2;
                continue;
            }

            int unit = ReadHex4(raw.Slice(i + 2, 4));
            if (unit < 0xD800 || unit > 0xDFFF)
            {
                i += 6;
                continue;
            }

            bool pair = unit <= 0xDBFF
                && i + 11 < raw.Length
                && raw[i + 6] == (byte)'\\'
                && raw[i + 7] == (byte)'u'
                && ReadHex4(raw.Slice(i + 8, 4)) is >= 0xDC00 and <= 0xDFFF;
            if (pair)
            {
                i += 12;
                continue;
            }

            positions.Add(i);
            i += 6;
        }
    }

    private static int ReadHex4(ReadOnlySpan<byte> digits)
    {
        int value = 0;
        foreach (byte digit in digits)
        {
            int nibble = digit switch
            {
                >= (byte)'0' and <= (byte)'9' => digit - '0',
                >= (byte)'a' and <= (byte)'f' => digit - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => digit - 'A' + 10,
                _ => -1,
            };
            if (nibble < 0)
            {
                return -1;
            }

            value = (value << 4) | nibble;
        }

        return value;
    }

    /// <summary>True for the 3rd and 4th characters of an escape that start a code unit in D800-DFFF.</summary>
    private static bool IsSurrogateStart(byte third, byte fourth)
    {
        bool d = third is (byte)'d' or (byte)'D';
        bool high = fourth is (>= (byte)'8' and <= (byte)'9') or (>= (byte)'a' and <= (byte)'f') or (>= (byte)'A' and <= (byte)'F');
        return d && high;
    }

    private static SourceLocation LocationOf(byte[] buffer, int start, int offset)
    {
        int line = 1;
        int lineStart = start;
        for (int i = start; i < offset; i++)
        {
            if (buffer[i] == (byte)'\n')
            {
                line++;
                lineStart = i + 1;
            }
        }

        // Columns count characters, not UTF-8 continuation bytes.
        int column = 1;
        for (int i = lineStart; i < offset; i++)
        {
            if ((buffer[i] & 0xC0) != 0x80)
            {
                column++;
            }
        }

        return new SourceLocation(line, column);
    }
}
