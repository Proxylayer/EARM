using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EArm.Cli.Output;

/// <summary>
/// Tab-separated text of a document for <c>-o tsv</c>, with the Azure CLI's rules (knack's TSV writer): a document that is not an array is one row; each
/// item of an array is a row; the cells of an object row are its values ordered by key (ordinal), of an array row its items; inside a row a nested array
/// is written as its length and a nested object as an empty cell (so rows keep the same columns); strings are written raw, numbers as in the JSON,
/// <c>null</c> as <c>None</c> and booleans as <c>True</c> / <c>False</c> (a row that is itself a boolean as <c>true</c> / <c>false</c>), as the Azure CLI
/// prints them. Use <c>--query</c> to pick the columns.
/// </summary>
internal static class TsvText
{
    public static string Write(JsonNode? document)
    {
        var text = new StringBuilder();
        IEnumerable<JsonNode?> rows = document is JsonArray array ? array : [document];
        foreach (var row in rows)
        {
            WriteRow(row, text);
        }

        return text.ToString();
    }

    private static void WriteRow(JsonNode? row, StringBuilder text)
    {
        IEnumerable<JsonNode?>? cells = row switch
        {
            JsonObject obj => obj.OrderBy(member => member.Key, StringComparer.Ordinal).Select(member => member.Value),
            JsonArray array => array,
            _ => null,
        };

        if (cells is null)
        {
            text.Append(row is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? value.ToJsonString() : Cell(row));
        }
        else
        {
            text.AppendJoin('\t', cells.Select(Cell));
        }

        text.Append('\n');
    }

    private static string Cell(JsonNode? node)
    {
        return node switch
        {
            null => "None",
            JsonArray array => array.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonObject => string.Empty,
            _ => node.GetValueKind() switch
            {
                JsonValueKind.String => node.GetValue<string>(),
                JsonValueKind.True => "True",
                JsonValueKind.False => "False",
                _ => node.ToJsonString(),
            },
        };
    }
}
