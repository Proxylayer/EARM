using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using EntraArm.Core.Diagnostics;

namespace EntraArm.Core.Json;

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
    public static (JsonElement Element, JsonNode? Node) Parse(ReadOnlyMemory<byte> utf8)
    {
        using var document = JsonDocument.Parse(StripBom(utf8), DocumentOptions);
        var element = document.RootElement.Clone();
        return (element, ToNode(element));
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

    /// <summary>
    /// The literal string of a node: <c>null</c> for non-strings and for expressions; escaped literals are unescaped.
    /// </summary>
    public static string? LiteralString(JsonNode? node)
    {
        string? text = AsString(node);
        return text is null || ExpressionSyntax.IsExpression(text) ? null : ExpressionSyntax.LiteralValue(text);
    }
}
