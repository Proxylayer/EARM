using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EArm.Cli.Output;

/// <summary>The document formats of <c>-o</c> besides <c>table</c> and <c>none</c>, with the Azure CLI's names.</summary>
internal enum DocumentFormat
{
    Json,
    Jsonc,
    Yaml,
    Yamlc,
    Tsv,
}

/// <summary>
/// Turns the one JSON document a verb writes with <c>-o json</c> into what <c>-o jsonc|yaml|yamlc|tsv</c> and <c>--query</c> ask for. The verbs know only
/// the JSON document: for every document format they write it exactly as for <c>-o json</c>, standard output is captured, and <see cref="Write"/> parses
/// it, applies the query and renders the result. Plain <c>-o json</c> without <c>--query</c> is never captured: it is written as the verb produced it.
/// Output that is not one JSON document (help, the version) is written through unchanged. A <c>null</c> result (a query that selects nothing) prints
/// nothing, as with the Azure CLI. <c>jsonc</c> and <c>yamlc</c> are coloured only on a terminal with colours on, else they are <c>json</c> and <c>yaml</c>.
/// </summary>
internal sealed class StructuredOutput
{
    public StructuredOutput(DocumentFormat format, JmesPathQuery? query)
    {
        Format = format;
        Query = query;
    }

    public DocumentFormat Format { get; }

    public JmesPathQuery? Query { get; }

    /// <summary>Writes <paramref name="captured"/> (what the verb wrote to standard output) in <see cref="Format"/> after <see cref="Query"/>.</summary>
    public void Write(string captured, TextWriter stdout, TextWriter stderr, bool colors)
    {
        ArgumentNullException.ThrowIfNull(captured);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        if (captured.Length == 0)
        {
            return;
        }

        JsonNode? document;
        try
        {
            document = JsonNode.Parse(captured, documentOptions: new JsonDocumentOptions { MaxDepth = JsonOutput.MaxDepth });
        }
        catch (JsonException)
        {
            if (Query is not null)
            {
                stderr.WriteLine("--query was not applied: the command's output is not a JSON document.");
            }

            stdout.Write(captured);
            return;
        }

        if (Query is not null)
        {
            document = Query.Apply(document);
            if (document is null)
            {
                return;
            }
        }

        // A verb writes compact JSON for --no-pretty-print: a document on one line stays on one line.
        bool indented = captured.TrimEnd().Contains('\n', StringComparison.Ordinal);
        string text = Format switch
        {
            DocumentFormat.Json => JsonText.Write(document, indented, colors: false) + "\n",
            DocumentFormat.Jsonc => JsonText.Write(document, indented, colors) + "\n",
            DocumentFormat.Yaml => YamlText.Write(document, colors: false),
            DocumentFormat.Yamlc => YamlText.Write(document, colors),
            _ => TsvText.Write(document),
        };

        stdout.Write(text.Replace("\n", Environment.NewLine, StringComparison.Ordinal));
    }
}
