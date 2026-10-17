using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EArm.Cli.Output;

/// <summary>
/// JSON text of a document for <c>-o json</c> after <c>--query</c> and for <c>-o jsonc</c>: two-space indentation (or one line), quotes and non-ASCII
/// written raw as in the what-if JSON, and with <c>colors</c> the keys, strings and literals painted (<see cref="DocumentColors"/>).
/// </summary>
internal static class JsonText
{
    private static readonly JsonSerializerOptions Scalar = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Write(JsonNode? node, bool indented, bool colors)
    {
        var text = new StringBuilder();
        WriteNode(node, text, indented, colors, 0);
        return text.ToString();
    }

    private static void WriteNode(JsonNode? node, StringBuilder text, bool indented, bool colors, int depth)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj.Count == 0)
                {
                    text.Append("{}");
                    return;
                }

                text.Append('{');
                bool firstMember = true;
                foreach (var member in obj)
                {
                    text.Append(firstMember ? string.Empty : ",");
                    firstMember = false;
                    NewLine(text, indented, depth + 1);
                    text.Append(DocumentColors.Paint(JsonSerializer.Serialize(member.Key, Scalar), DocumentColors.Key, colors)).Append(indented ? ": " : ":");
                    WriteNode(member.Value, text, indented, colors, depth + 1);
                }

                NewLine(text, indented, depth);
                text.Append('}');
                return;

            case JsonArray array:
                if (array.Count == 0)
                {
                    text.Append("[]");
                    return;
                }

                text.Append('[');
                for (int i = 0; i < array.Count; i++)
                {
                    text.Append(i == 0 ? string.Empty : ",");
                    NewLine(text, indented, depth + 1);
                    WriteNode(array[i], text, indented, colors, depth + 1);
                }

                NewLine(text, indented, depth);
                text.Append(']');
                return;

            case null:
                text.Append(DocumentColors.Paint("null", DocumentColors.Literal, colors));
                return;

            default:
                string json = node.ToJsonString(Scalar);
                text.Append(DocumentColors.Paint(json, node.GetValueKind() == JsonValueKind.String ? DocumentColors.String : DocumentColors.Literal, colors));
                return;
        }
    }

    private static void NewLine(StringBuilder text, bool indented, int depth)
    {
        if (indented)
        {
            text.Append('\n').Append(' ', depth * 2);
        }
    }
}
