using System;
using System.Collections.Generic;

namespace EArm.Core.Json;

/// <summary>
/// Multi-line strings in a template: a raw line break (or tab) inside a string literal, which strict JSON forbids. Azure CLI and Azure PowerShell
/// accept them in ARM templates ("You can break a string into multiple lines",
/// https://learn.microsoft.com/azure/azure-resource-manager/templates/syntax#multi-line-strings), typically to spread a long expression over
/// several lines; EARM, a command-line tool like them, accepts them too. Before parsing, each raw CR, LF or TAB inside a string becomes its JSON
/// escape (<c>\r</c>, <c>\n</c>, <c>\t</c>), so the value keeps the characters as written (whitespace, for an expression). Comments are skipped.
/// The positions of the inserted bytes are returned so locations can be mapped back to the original text.
/// </summary>
public static class MultiLineStrings
{
    /// <summary>
    /// The text with every raw line break and tab inside a string escaped, and the positions (in the returned text, ascending) of the bytes that
    /// were added; the input itself, with no positions, when nothing needed escaping. The input must not start with a byte-order mark.
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
        bool inString = false;
        for (int i = 0; i < span.Length; i++)
        {
            byte b = span[i];
            if (inString)
            {
                switch (b)
                {
                    case (byte)'\\':
                        output.Add(b);
                        if (i + 1 < span.Length)
                        {
                            output.Add(span[++i]);
                        }

                        continue;
                    case (byte)'"':
                        inString = false;
                        output.Add(b);
                        continue;
                    case (byte)'\n' or (byte)'\r' or (byte)'\t':
                        output.Add((byte)'\\');
                        added.Add(output.Count);
                        output.Add(b switch { (byte)'\n' => (byte)'n', (byte)'\r' => (byte)'r', _ => (byte)'t' });
                        continue;
                    default:
                        output.Add(b);
                        continue;
                }
            }

            if (b == (byte)'"')
            {
                inString = true;
            }
            else if (b == (byte)'/' && i + 1 < span.Length && (span[i + 1] == (byte)'/' || span[i + 1] == (byte)'*'))
            {
                int end = CommentEnd(span, i);
                for (; i < end; i++)
                {
                    output.Add(span[i]);
                }

                i--;
                continue;
            }

            output.Add(b);
        }

        insertions = added;
        return output.ToArray();
    }

    /// <summary>True when some string literal holds a raw CR, LF or TAB (a quick scan that allocates nothing).</summary>
    private static bool NeedsEscaping(ReadOnlySpan<byte> span)
    {
        bool inString = false;
        for (int i = 0; i < span.Length; i++)
        {
            byte b = span[i];
            if (inString)
            {
                if (b == (byte)'\\')
                {
                    i++;
                }
                else if (b == (byte)'"')
                {
                    inString = false;
                }
                else if (b is (byte)'\n' or (byte)'\r' or (byte)'\t')
                {
                    return true;
                }
            }
            else if (b == (byte)'"')
            {
                inString = true;
            }
            else if (b == (byte)'/' && i + 1 < span.Length && (span[i + 1] == (byte)'/' || span[i + 1] == (byte)'*'))
            {
                i = CommentEnd(span, i) - 1;
            }
        }

        return false;
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
