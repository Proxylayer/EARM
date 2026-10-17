using System;
using System.Collections.Generic;

namespace EArm.Core.Json;

/// <summary>
/// Multi-line strings in a template: a raw line break inside a string literal, which strict JSON forbids. Azure CLI and Azure PowerShell
/// accept them in ARM templates ("You can break a string into multiple lines",
/// https://learn.microsoft.com/azure/azure-resource-manager/templates/syntax#multi-line-strings), typically to spread a long expression over
/// several lines; EARM, a command-line tool like them, accepts them too. Before parsing, each raw CR or LF inside a string becomes its JSON
/// escape (<c>\r</c>, <c>\n</c>), and so does a raw TAB in a string that spans lines (indentation), so the value keeps the characters as
/// written (whitespace, for an expression). A raw TAB in a single-line string stays invalid JSON. Comments are skipped. The positions of the
/// inserted bytes are returned so locations can be mapped back to the original text.
/// </summary>
public static class MultiLineStrings
{
    /// <summary>
    /// The text with the raw line breaks of every string escaped, and the positions (in the returned text, ascending) of the bytes that were
    /// added; the input itself, with no positions, when no string spans lines. The input must not start with a byte-order mark.
    /// </summary>
    public static ReadOnlyMemory<byte> Escape(ReadOnlyMemory<byte> utf8, out IReadOnlyList<int> insertions)
    {
        var span = utf8.Span;
        if (!NeedsEscaping(span))
        {
            insertions = [];
            return utf8;
        }

        var output = new List<byte>(span.Length + 64);
        var added = new List<int>();
        int i = 0;
        while (i < span.Length)
        {
            byte b = span[i];
            if (b == (byte)'"')
            {
                int end = StringEnd(span, i, out bool spansLines);
                for (int k = i; k < end; k++)
                {
                    byte c = span[k];
                    if (c == (byte)'\\' && k + 1 < end)
                    {
                        output.Add(c);
                        output.Add(span[++k]);
                    }
                    else if (spansLines && c is (byte)'\n' or (byte)'\r' or (byte)'\t')
                    {
                        output.Add((byte)'\\');
                        added.Add(output.Count);
                        output.Add(c switch { (byte)'\n' => (byte)'n', (byte)'\r' => (byte)'r', _ => (byte)'t' });
                    }
                    else
                    {
                        output.Add(c);
                    }
                }

                i = end;
                continue;
            }

            int next = b == (byte)'/' && i + 1 < span.Length && (span[i + 1] == (byte)'/' || span[i + 1] == (byte)'*') ? CommentEnd(span, i) : i + 1;
            for (; i < next; i++)
            {
                output.Add(span[i]);
            }
        }

        insertions = added;
        return output.ToArray();
    }

    /// <summary>True when some string literal holds a raw CR or LF (a quick scan that allocates nothing).</summary>
    private static bool NeedsEscaping(ReadOnlySpan<byte> span)
    {
        int i = 0;
        while (i < span.Length)
        {
            byte b = span[i];
            if (b == (byte)'"')
            {
                i = StringEnd(span, i, out bool spansLines);
                if (spansLines)
                {
                    return true;
                }
            }
            else if (b == (byte)'/' && i + 1 < span.Length && (span[i + 1] == (byte)'/' || span[i + 1] == (byte)'*'))
            {
                i = CommentEnd(span, i);
            }
            else
            {
                i++;
            }
        }

        return false;
    }

    /// <summary>The index just past the string literal that opens at <paramref name="start"/> (or the end of the text), and whether it holds a raw CR or LF.</summary>
    private static int StringEnd(ReadOnlySpan<byte> span, int start, out bool spansLines)
    {
        spansLines = false;
        for (int i = start + 1; i < span.Length; i++)
        {
            switch (span[i])
            {
                case (byte)'\\':
                    i++;
                    break;
                case (byte)'"':
                    return i + 1;
                case (byte)'\n' or (byte)'\r':
                    spansLines = true;
                    break;
            }
        }

        return span.Length;
    }

    /// <summary>The index just past the comment that starts at <paramref name="start"/> (<c>//</c> to the end of the line, <c>/* */</c> to its close).</summary>
    private static int CommentEnd(ReadOnlySpan<byte> span, int start)
    {
        if (span[start + 1] == (byte)'/')
        {
            int newline = span[start..].IndexOf((byte)'\n');
            return newline < 0 ? span.Length : start + newline;
        }

        int close = span[(start + 2)..].IndexOf("*/"u8);
        return close < 0 ? span.Length : start + 2 + close + 2;
    }

    /// <summary>Maps an offset in the escaped text back to the original text.</summary>
    public static int ToOriginal(int offset, IReadOnlyList<int> insertions)
    {
        ArgumentNullException.ThrowIfNull(insertions);
        int lo = 0;
        int hi = insertions.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (insertions[mid] < offset)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return offset - lo;
    }
}
