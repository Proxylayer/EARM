using System;
using System.Collections.Generic;
using System.Buffers;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using EArm.Core.Diagnostics;

namespace EArm.Core.Json;

/// <summary>
/// Raised by <see cref="TemplateJson.Parse"/> when an object repeats a property name (<c>DuplicateJsonKey</c>). The first repeated key is the
/// primary one; <see cref="Duplicates"/> lists all of them.
/// </summary>
public sealed class DuplicateJsonKeyException : JsonException
{
    public DuplicateJsonKeyException(IReadOnlyList<DuplicateKey> duplicates)
        : base(Format(duplicates))
    {
        Duplicates = duplicates;
    }

    public IReadOnlyList<DuplicateKey> Duplicates { get; }

    public DuplicateKey First => Duplicates[0];

    private static string Format(IReadOnlyList<DuplicateKey> duplicates)
    {
        string message = duplicates[0].Describe();
        if (duplicates.Count == 1)
        {
            return message;
        }

        // Every repeat, so one run of the validator shows them all (the scan stops at the limit of DuplicateKeys.Find).
        string others = string.Join("; ", duplicates.Skip(1).Select(d => $"{d.Path} (line {d.Location.Line}, column {d.Location.Column})"));
        return $"{message} {duplicates.Count - 1} more repeated key(s): {others}.";
    }
}

/// <summary>
/// Raised by <see cref="TemplateJson.Parse"/> when a document is larger than <see cref="TemplateJson.MaxInputBytes"/> or holds more than
/// <see cref="DuplicateKeys.MaxNodes"/> JSON values (<c>InputTooLarge</c>), before anything is built from it.
/// </summary>
public sealed class InputTooLargeException : JsonException
{
    public InputTooLargeException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Shared JSON reader settings for template and parameter files: <c>//</c> and <c>/* */</c> comments are skipped
/// and trailing commas are allowed. Parses once into a <see cref="JsonElement"/> (kept for JSON Schema evaluation)
/// and builds the mutable <see cref="JsonNode"/> tree from it.
/// </summary>
public static class TemplateJson
{
    public static JsonDocumentOptions DocumentOptions { get; } = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static JsonReaderOptions ReaderOptions { get; } = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The largest template or parameter file that is read: 8 MiB. Larger input is refused with <c>InputTooLarge</c> before it is parsed.</summary>
    public const int MaxInputBytes = 8 * 1024 * 1024;

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>Removes a leading UTF-8 byte-order mark.</summary>
    public static ReadOnlyMemory<byte> StripBom(ReadOnlyMemory<byte> utf8)
    {
        return utf8.Span.StartsWith(Utf8Bom) ? utf8[Utf8Bom.Length..] : utf8;
    }

    /// <summary>
    /// Parses UTF-8 JSON text. The returned element is a standalone clone; the node is built from it.
    /// </summary>
    /// <exception cref="JsonException">The text is not valid JSON.</exception>
    /// <exception cref="DuplicateJsonKeyException">An object repeats a property name.</exception>
    public static (JsonElement Element, JsonNode? Node) Parse(ReadOnlyMemory<byte> utf8, IReadOnlySet<string>? nameMaps = null)
    {
        var bytes = StripBom(utf8);
        if (bytes.Length > MaxInputBytes)
        {
            throw new InputTooLargeException($"The document is {bytes.Length:N0} bytes; the limit is {MaxInputBytes:N0} bytes (8 MiB).");
        }

        RejectOtherEncodings(bytes.Span);
        RejectInvalidUtf8(bytes.Span);

        // One cheap pass first: it counts the values (so a document of millions of tiny nodes is refused before a parser builds them) and finds the repeated keys.
        var scan = DuplicateKeys.Scan(utf8, 50, nameMaps, DuplicateKeys.MaxNodes);
        if (scan.TooManyNodes)
        {
            throw new InputTooLargeException($"The document holds more than {DuplicateKeys.MaxNodes:N0} JSON values; the limit keeps the load within memory.");
        }

        using var document = JsonDocument.Parse(bytes, DocumentOptions);
        var duplicates = scan.Duplicates;
        if (duplicates.Count > 0)
        {
            throw new DuplicateJsonKeyException(duplicates);
        }

        var element = document.RootElement.Clone();
        return (element, ToNode(element));
    }

    /// <summary>
    /// A UTF-16 or UTF-32 file (byte-order mark, or NUL bytes next to ASCII) is reported as such instead of as <c>'0xFF' is an invalid start of a value</c>.
    /// </summary>
    private static void RejectOtherEncodings(ReadOnlySpan<byte> bytes)
    {
        string? encoding = null;
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0)
        {
            encoding = "UTF-32";
        }
        else if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF) || (bytes[0] == 0 && bytes[1] != 0) || (bytes[0] != 0 && bytes[1] == 0)))
        {
            encoding = "UTF-16";
        }

        if (encoding is not null)
        {
            throw new JsonException($"The file must be UTF-8 ({encoding} detected); save it as UTF-8.");
        }
    }

    /// <summary>
    /// Strict UTF-8: the JSON reader accepts malformed bytes inside a string (<c>0xC3 0x28</c>) and only throws when the string is read as text, which
    /// surfaced as an unhandled <see cref="InvalidOperationException"/> in whichever stage read it first. The document is refused here instead, with the
    /// byte offset (and, as the exception's line and column, the position) of the first malformed sequence.
    /// </summary>
    private static void RejectInvalidUtf8(ReadOnlySpan<byte> bytes)
    {
        if (Utf8.IsValid(bytes))
        {
            return;
        }

        int offset = 0;
        while (offset < bytes.Length)
        {
            var status = Rune.DecodeFromUtf8(bytes[offset..], out _, out int consumed);
            if (status != OperationStatus.Done)
            {
                break;
            }

            offset += consumed;
        }

        long line = 0;
        int lineStart = 0;
        for (int i = 0; i < offset && i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                line++;
                lineStart = i + 1;
            }
        }

        int end = Math.Min(offset + 4, bytes.Length);
        string sample = string.Join(' ', bytes[offset..end].ToArray().Select(b => "0x" + b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
        throw new JsonException(
            $"The file must be valid UTF-8; an invalid byte sequence starts at byte offset {offset.ToString(System.Globalization.CultureInfo.InvariantCulture)} ({sample}).",
            path: null,
            lineNumber: line,
            bytePositionInLine: offset - lineStart);
    }

    /// <summary>Builds a node tree from an element (the element's own kind decides the node type).</summary>
    public static JsonNode? ToNode(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.Object => JsonObject.Create(element),
            JsonValueKind.Array => JsonArray.Create(element),
            _ => JsonValue.Create(element),
        };
    }

    /// <summary>Converts a <see cref="JsonException"/> position (0-based) to a 1-based <see cref="SourceLocation"/>.</summary>
    public static SourceLocation? LocationOf(JsonException exception)
    {
        if (exception.LineNumber is null)
        {
            return null;
        }

        long line = exception.LineNumber.Value + 1;
        long column = (exception.BytePositionInLine ?? 0) + 1;
        return new SourceLocation((int)Math.Min(line, int.MaxValue), (int)Math.Min(column, int.MaxValue));
    }

    /// <summary>
    /// Returns the string value of a leaf node, or <c>null</c> when the node is not a plain string.
    /// Tainted (secret) leaves report as <c>null</c>: their text is never read through this helper.
    /// </summary>
    public static string? AsString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    /// <summary>Returns the boolean value of a leaf node, or <c>null</c> when the node is not a boolean.</summary>
    public static bool? AsBool(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        return value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    /// <summary>
    /// Returns the integral value of a leaf node, or <c>null</c> when it is not a whole number in the <see cref="long"/> range.
    /// The answer depends only on the number's exact value, never on how the node was created: <c>3</c>, <c>3.0</c>, <c>30e-1</c>,
    /// a parsed <c>1.5e3</c> and a CLR <c>double</c>/<c>decimal</c> 3.0 all give the same result; <c>-0.0</c> is 0; fractions,
    /// values outside the <see cref="long"/> range and non-numbers give <c>null</c>.
    /// </summary>
    public static long? AsInt64(JsonNode? node)
    {
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
        {
            return null;
        }

        if (value.TryGetValue<JsonElement>(out var element))
        {
            return element.TryGetInt64(out long exact) ? exact : WholeNumberFromText(element.GetRawText());
        }

        if (value.TryGetValue<long>(out long l))
        {
            return l;
        }

        if (value.TryGetValue<int>(out int i))
        {
            return i;
        }

        if (value.TryGetValue<double>(out double d))
        {
            return FromDouble(d);
        }

        if (value.TryGetValue<decimal>(out decimal m))
        {
            return decimal.Truncate(m) == m && m >= long.MinValue && m <= long.MaxValue ? (long)m : null;
        }

        if (value.TryGetValue<float>(out float f))
        {
            return FromDouble(f);
        }

        if (value.TryGetValue<ulong>(out ulong ul))
        {
            return ul <= long.MaxValue ? (long)ul : null;
        }

        if (value.TryGetValue<uint>(out uint ui))
        {
            return ui;
        }

        if (value.TryGetValue<short>(out short s))
        {
            return s;
        }

        if (value.TryGetValue<ushort>(out ushort us))
        {
            return us;
        }

        if (value.TryGetValue<sbyte>(out sbyte sb))
        {
            return sb;
        }

        if (value.TryGetValue<byte>(out byte b))
        {
            return b;
        }

        return null;
    }

    private static long? FromDouble(double d)
    {
        // 2^63 is exactly representable, so the half-open range is exact; NaN and infinities fail IsFinite.
        return double.IsFinite(d) && Math.Floor(d) == d && d >= -9223372036854775808d && d < 9223372036854775808d ? (long)d : null;
    }

    /// <summary>
    /// The exact whole-number value of a JSON number token that <see cref="JsonElement.TryGetInt64"/> refused (a fraction part or an
    /// exponent, or out of range), or <c>null</c> when the value is not a whole number or does not fit a <see cref="long"/>. Works on the
    /// decimal digits themselves (no <see cref="double"/> or <see cref="decimal"/> rounding), so <c>9007199254740993.0</c> stays exact.
    /// </summary>
    private static long? WholeNumberFromText(string text)
    {
        int position = 0;
        bool negative = false;
        if (position < text.Length && text[position] == '-')
        {
            negative = true;
            position++;
        }

        int exponentAt = text.IndexOfAny(['e', 'E']);
        string mantissa = exponentAt < 0 ? text[position..] : text[position..exponentAt];
        int dot = mantissa.IndexOf('.', StringComparison.Ordinal);
        int fractionLength = dot < 0 ? 0 : mantissa.Length - dot - 1;
        string digits = dot < 0 ? mantissa : string.Concat(mantissa.AsSpan(0, dot), mantissa.AsSpan(dot + 1));
        digits = digits.TrimStart('0');
        if (digits.Length == 0)
        {
            return 0;
        }

        // Value = digits x 10^exponent10; drop trailing zeros so a whole number written as 1500.000 or 15e2 compares by its significant digits.
        string significant = digits.TrimEnd('0');
        long exponent = 0;
        if (exponentAt >= 0 && !long.TryParse(text.AsSpan(exponentAt + 1), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out exponent))
        {
            exponent = text[exponentAt + 1] == '-' ? -1_000_000_000_000L : 1_000_000_000_000L;
        }

        long scale = exponent - fractionLength + (digits.Length - significant.Length);
        if (scale < 0 || significant.Length + scale > 19)
        {
            return null;
        }

        string whole = (negative ? "-" : string.Empty) + significant + new string('0', (int)scale);
        return long.TryParse(whole, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out long result) ? result : null;
    }
    /// <summary>True when the node is a string holding an expression.</summary>
    public static bool IsExpression(JsonNode? node)
    {
        return ExpressionSyntax.IsExpression(AsString(node));
    }

    /// <summary>True when the node is, or (at any depth inside an object or array) contains, a string holding an expression.</summary>
    public static bool ContainsExpression(JsonNode? node)
    {
        var stack = new Stack<JsonNode?>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            switch (stack.Pop())
            {
                case JsonObject obj:
                    foreach (var property in obj)
                    {
                        stack.Push(property.Value);
                    }

                    break;
                case JsonArray array:
                    foreach (var item in array)
                    {
                        stack.Push(item);
                    }

                    break;
                case var leaf when IsExpression(leaf):
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The literal string of a node: <c>null</c> for non-strings and for expressions; escaped literals are unescaped.
    /// </summary>
    public static string? LiteralString(JsonNode? node)
    {
        string? text = AsString(node);
        return text is null || ExpressionSyntax.IsExpression(text) ? null : ExpressionSyntax.LiteralValue(text);
    }
}
