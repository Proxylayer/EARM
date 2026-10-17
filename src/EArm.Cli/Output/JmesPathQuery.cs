using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevLab.JmesPath;

namespace EArm.Cli.Output;

/// <summary>
/// A <c>--query</c> expression (JMESPath, https://jmespath.org, as the Azure CLI takes it), checked once and applied to the one JSON document a verb
/// writes. Evaluated by JmesPath.Net (Apache-2.0), which reads the document with date parsing off, so strings that look like dates come back as written.
/// </summary>
internal sealed class JmesPathQuery
{
    private static readonly JmesPath Engine = new();

    private JmesPathQuery(string expression)
    {
        Expression = expression;
    }

    public string Expression { get; }

    /// <summary>Checks <paramref name="expression"/>; false with the parser's message when it is not a valid JMESPath expression.</summary>
    public static bool TryCreate(string expression, out JmesPathQuery? query, out string? error)
    {
        ArgumentNullException.ThrowIfNull(expression);
        query = null;
        error = null;
        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "--query is empty.";
            return false;
        }

        try
        {
            Engine.Parse(expression);
        }
        catch (Exception ex)
        {
            error = $"--query '{expression}' is not a valid JMESPath expression: {ex.Message}";
            return false;
        }

        query = new JmesPathQuery(expression);
        return true;
    }

    /// <summary>The result of the expression on <paramref name="document"/>; <c>null</c> when the expression selects nothing.</summary>
    public JsonNode? Apply(JsonNode? document)
    {
        string? result = Engine.Transform(document?.ToJsonString() ?? "null", Expression);
        return string.IsNullOrEmpty(result) ? null : JsonNode.Parse(result, documentOptions: new JsonDocumentOptions { MaxDepth = JsonOutput.MaxDepth });
    }
}
