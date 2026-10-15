using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;

namespace EArm.Cli.Output;

/// <summary>
/// The failure of a command that has no report of its own (<c>auth</c>, <c>schema</c>): with <c>-o json</c> one JSON document on standard
/// output, <c>{ "status": "Failed", "diagnostics": [ { severity, code, message } ] }</c> (the shape of the other verbs), so a script that parses the
/// output never meets plain text; the message goes to standard error as ever. A message of several lines keeps them: <c>message</c> joins the
/// lines with a line feed (an ordinary JSON <c>\n</c>) and <c>lines</c> lists them. Every line is escaped (<see cref="SafeText.Escape"/>).
/// </summary>
internal static class FailureDocument
{
    /// <summary>The serializer settings of these documents (indented, readable text).</summary>
    public static JsonSerializerOptions Options { get; } = new() { WriteIndented = true, MaxDepth = JsonOutput.MaxDepth, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The document for one error.</summary>
    public static JsonObject Create(string code, string message)
    {
        string[] lines = (message ?? string.Empty).Replace("\r\n", "\n", System.StringComparison.Ordinal).Replace('\r', '\n').Split('\n').Select(SafeText.Escape).ToArray();
        var diagnostic = new JsonObject
        {
            ["severity"] = "Error",
            ["code"] = code,
            ["message"] = string.Join('\n', lines),
        };
        if (lines.Length > 1)
        {
            diagnostic["lines"] = new JsonArray(lines.Select(line => (JsonNode?)JsonValue.Create(line)).ToArray());
        }

        return new JsonObject
        {
            ["status"] = "Failed",
            ["diagnostics"] = new JsonArray(diagnostic),
        };
    }

    /// <summary>Reports the failure (the JSON document when <paramref name="format"/> is json, the message on standard error always) and returns <paramref name="exitCode"/>.</summary>
    public static int Write(OutputFormat format, TextWriter stdout, TextWriter stderr, string code, string message, int exitCode)
    {
        if (format == OutputFormat.Json)
        {
            stdout.WriteLine(Create(code, message).ToJsonString(Options));
        }

        stderr.WriteLine(message);
        return exitCode;
    }
}
