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

    /// <summary>Returns the integral value of a leaf node, or <c>null</c> when it is not an integral number.</summary>
    public static long? AsInt64(JsonNode? node)
    {
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
        {
            return null;
        }

        if (value.TryGetValue<JsonElement>(out var element))
        {
            return element.TryGetInt64(out long fromElement) ? fromElement : null;
        }

        if (value.TryGetValue<long>(out long l))
        {
            return l;
        }

        if (value.TryGetValue<int>(out int i))
        {
            return i;
        }

        if (value.TryGetValue<double>(out double d) && Math.Floor(d) == d && Math.Abs(d) < 9007199254740992d)
        {
            return (long)d;
        }

        return null;
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
