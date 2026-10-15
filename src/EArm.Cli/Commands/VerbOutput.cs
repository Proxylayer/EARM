using System;
using System.IO;
using System.Text.Json.Nodes;
using EArm.Cli.Output;

namespace EArm.Cli.Commands;

/// <summary>
/// The result of the state-backed verbs (<c>state unlock / forget / adopt / set-provenance</c>, <c>deployment delete / show / list / operation list</c>, the preflight and
/// state failures of <c>what-if</c> / <c>create</c>) in the output format asked for. With <c>-o json</c> stdout carries exactly ONE JSON document on success and on failure
/// (<c>{ "status": "Failed", "diagnostics": [ { "severity", "code", "message" } ] }</c>), never prose; the table format keeps the human text (failures on stderr). Line breaks of
/// a message are normalised to <c>\n</c> so a multi-line message (a sign-in failure with its hints) reads as lines in the JSON string instead of a literal <c>\r\n</c> (review round 2, S2-20).
/// </summary>
internal static class VerbOutput
{
    /// <summary>Reports a failure and returns <paramref name="exitCode"/>.</summary>
    public static int Fail(OutputFormat output, TextWriter stdout, TextWriter stderr, int exitCode, string code, string message)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        message = Normalize(message);
        if (output == OutputFormat.Json)
        {
            var document = new JsonObject
            {
                ["status"] = "Failed",
                ["diagnostics"] = new JsonArray(new JsonObject { ["severity"] = "Error", ["code"] = code, ["message"] = message }),
            };
            stdout.WriteLine(document.ToJsonString(JsonOutput.Options(true)));
        }
        else
        {
            stderr.WriteLine(message);
        }

        return exitCode;
    }

    /// <summary>Reports success: the human <paramref name="message"/> in the table format, a <c>{ "status": "Succeeded", "message": ..., ...fields }</c> document with <c>-o json</c>, nothing with <c>-o none</c>.</summary>
    public static int Succeed(OutputFormat output, TextWriter stdout, string message, Action<JsonObject>? fields = null)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        message = Normalize(message);
        switch (output)
        {
            case OutputFormat.Json:
                var document = new JsonObject { ["status"] = "Succeeded", ["message"] = message };
                fields?.Invoke(document);
                stdout.WriteLine(document.ToJsonString(JsonOutput.Options(true)));
                break;

            case OutputFormat.None:
                break;

            default:
                stdout.WriteLine(message);
                break;
        }

        return ExitCodes.Ok;
    }

    /// <summary>Line breaks as <c>\n</c> (a lone <c>\r</c> too).</summary>
    public static string Normalize(string message)
    {
        return message.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }
}
