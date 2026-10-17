using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EArm.Cli.Output;

/// <summary>
/// YAML text of a document for <c>-o yaml</c> / <c>-o yamlc</c>, in the block style the Azure CLI writes (PyYAML's <c>safe_dump</c> with
/// <c>default_flow_style=False</c>): mappings as <c>key: value</c>, sequences as <c>- item</c> at the indentation of their key, empty collections as
/// <c>{}</c> / <c>[]</c>. Unlike the Azure CLI, keys keep the document's order (it sorts them). A string is written plain only when it cannot be
/// read back as anything else (no indicator characters, not a number, boolean or null); otherwise single-quoted, or double-quoted with escapes when it
/// holds a line break or another control character. With <c>colors</c> keys, strings and literals are painted (<see cref="DocumentColors"/>).
/// </summary>
internal static class YamlText
{
    // PyYAML's resolver: the YAML 1.1 booleans it reads (not y / n) and null.
    private static readonly string[] Reserved = ["true", "false", "yes", "no", "on", "off", "null", "~"];

    public static string Write(JsonNode? node, bool colors)
    {
        var text = new StringBuilder();
        switch (node)
        {
            case JsonObject { Count: > 0 } obj:
                WriteMapping(obj, text, 0, colors);
                break;
            case JsonArray { Count: > 0 } array:
                WriteSequence(array, text, 0, colors);
                break;
            default:
                text.Append(Scalar(node, colors)).Append('\n');
                break;
        }

        return text.ToString();
    }

    /// <summary>Writes the members of <paramref name="obj"/> one per line at <paramref name="indent"/>; the first line's indentation is already written when <paramref name="inline"/>.</summary>
    private static void WriteMapping(JsonObject obj, StringBuilder text, int indent, bool colors, bool inline = false)
    {
        bool first = true;
        foreach (var member in obj)
        {
            if (!(first && inline))
            {
                text.Append(' ', indent);
            }

            first = false;
            text.Append(DocumentColors.Paint(Key(member.Key), DocumentColors.Key, colors)).Append(':');
            WriteValue(member.Value, text, indent, colors, nestedSequenceIndent: indent);
        }
    }

    private static void WriteSequence(JsonArray array, StringBuilder text, int indent, bool colors, bool inline = false)
    {
        bool first = true;
        foreach (var item in array)
        {
            if (!(first && inline))
            {
                text.Append(' ', indent);
            }

            first = false;
            text.Append('-');
            switch (item)
            {
                case JsonObject { Count: > 0 } obj:
                    text.Append(' ');
                    WriteMapping(obj, text, indent + 2, colors, inline: true);
                    break;
                case JsonArray { Count: > 0 } nested:
                    text.Append(' ');
                    WriteSequence(nested, text, indent + 2, colors, inline: true);
                    break;
                default:
                    text.Append(' ').Append(Scalar(item, colors)).Append('\n');
                    break;
            }
        }
    }

    /// <summary>The value after <c>key:</c>: a scalar on the same line, a mapping indented below, a sequence below at the key's indentation (PyYAML's style).</summary>
    private static void WriteValue(JsonNode? value, StringBuilder text, int indent, bool colors, int nestedSequenceIndent)
    {
        switch (value)
        {
            case JsonObject { Count: > 0 } obj:
                text.Append('\n');
                WriteMapping(obj, text, indent + 2, colors);
                break;
            case JsonArray { Count: > 0 } array:
                text.Append('\n');
                WriteSequence(array, text, nestedSequenceIndent, colors);
                break;
            default:
                text.Append(' ').Append(Scalar(value, colors)).Append('\n');
                break;
        }
    }

    private static string Scalar(JsonNode? node, bool colors)
    {
        switch (node)
        {
            case null:
                return DocumentColors.Paint("null", DocumentColors.Literal, colors);
            case JsonObject:
                return "{}";
            case JsonArray:
                return "[]";
        }

        var kind = node.GetValueKind();
        if (kind == JsonValueKind.String)
        {
            return DocumentColors.Paint(Quote(node.GetValue<string>()), DocumentColors.String, colors);
        }

        // Numbers as JSON writes them; booleans as true / false.
        return DocumentColors.Paint(node.ToJsonString(), DocumentColors.Literal, colors);
    }

    private static string Key(string key)
    {
        return Quote(key);
    }

    /// <summary>A string as a YAML scalar: plain when safe, else single-quoted, else (control characters) double-quoted with JSON-style escapes.</summary>
    internal static string Quote(string value)
    {
        if (value.Any(c => char.IsControl(c) || c is (char)0x2028 or (char)0x2029 or (char)0xFEFF))
        {
            return JsonSerializer.Serialize(value);
        }

        return IsPlainSafe(value) ? value : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static bool IsPlainSafe(string value)
    {
        if (value.Length == 0 || char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
        {
            return false;
        }

        if ("-?:,[]{}#&*!|>'\"%@`".Contains(value[0], StringComparison.Ordinal))
        {
            return false;
        }

        if (value.Contains(": ", StringComparison.Ordinal) || value.Contains(" #", StringComparison.Ordinal) || value.EndsWith(':'))
        {
            return false;
        }

        if (Reserved.Contains(value, StringComparer.OrdinalIgnoreCase) || LooksNumeric(value))
        {
            return false;
        }

        return value.All(c => char.IsLetterOrDigit(c) || c is ' ' or '_' or '-' or '.' or '/' or ':' or '@' or '+' or '=' or '(' or ')' || c > '\u007f');
    }

    /// <summary>True for anything a YAML 1.1 reader might take as a number, a date or a time (digits first, or a sign or dot then a digit).</summary>
    private static bool LooksNumeric(string value)
    {
        if (char.IsDigit(value[0]))
        {
            return true;
        }

        return value.Length > 1 && (value[0] is '+' or '.') && char.IsDigit(value[1])
            || value.Equals(".inf", StringComparison.OrdinalIgnoreCase) || value.Equals(".nan", StringComparison.OrdinalIgnoreCase)
            || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }
}
