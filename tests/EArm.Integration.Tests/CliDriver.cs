using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Cli;
using EArm.Cli.Hosting;

namespace EArm.Integration.Tests;

/// <summary>The captured outcome of one in-process <c>earm</c> invocation.</summary>
internal sealed record CliResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>Standard output and error, for failure messages and substring checks (the CLI redacts secrets).</summary>
    public string Combined => StandardOutput + Environment.NewLine + StandardError;

    /// <summary>Standard output parsed as a JSON object (for <c>-o json</c> runs).</summary>
    public JsonObject Json
    {
        get
        {
            return JsonNode.Parse(StandardOutput) as JsonObject
                ?? throw new InvalidOperationException("Standard output is not a JSON object: " + Truncate(Combined));
        }
    }

    /// <summary><c>changes[]</c> of a what-if document.</summary>
    public IReadOnlyList<JsonObject> Changes => ArrayOf("changes");

    /// <summary><c>operations[]</c> of a create document.</summary>
    public IReadOnlyList<JsonObject> Operations => ArrayOf("operations");

    /// <summary><c>diagnostics[]</c> of a what-if or create document.</summary>
    public IReadOnlyList<JsonObject> Diagnostics => ArrayOf("diagnostics");

    /// <summary>The change type (<c>Create</c>, <c>Modify</c>, <c>Delete</c>, <c>NoChange</c>, <c>Ignore</c>) of the what-if row whose resource id ends with the suffix.</summary>
    public string? ChangeTypeOf(string resourceIdSuffix)
    {
        return Changes
            .Where(c => ((string?)c["resourceId"])?.EndsWith(resourceIdSuffix, StringComparison.OrdinalIgnoreCase) == true)
            .Select(c => (string?)c["changeType"])
            .FirstOrDefault();
    }

    /// <summary>One entry per what-if row (<c>NoChange /providers/...</c>) for assertion messages.</summary>
    public string DescribeChanges()
    {
        return string.Join("; ", Changes.Select(c => $"{(string?)c["changeType"]} {(string?)c["resourceId"]}"));
    }

    /// <summary>One entry per applied operation (<c>Succeeded /providers/... [errorCode] error</c>) for assertion messages.</summary>
    public string DescribeOperations()
    {
        return string.Join("; ", Operations.Select(DescribeOperation));
    }

    /// <summary>
    /// The failed or skipped operations (resource id, error code, error) and the error diagnostics, one per line, for exit-code
    /// assertion messages; null when standard output is not a JSON document or nothing failed.
    /// </summary>
    public string? DescribeFailures()
    {
        JsonObject json;
        try
        {
            json = Json;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
        {
            return null;
        }

        var lines = new List<string>();
        if (json["operations"] is JsonArray operations)
        {
            lines.AddRange(operations.OfType<JsonObject>()
                .Where(o => (string?)o["status"] is not ("Succeeded" or "Ignored" or "Detached"))
                .Select(DescribeOperation));
        }

        // The delete verb reports per-object outcomes under a different array; include its failures too.
        foreach (var array in json.Where(p => p.Key != "operations" && p.Value is JsonArray).Select(p => (JsonArray)p.Value!))
        {
            lines.AddRange(array.OfType<JsonObject>()
                .Where(o => o["errorCode"] is not null || o["error"] is not null)
                .Select(DescribeOperation));
        }

        if (json["diagnostics"] is JsonArray diagnostics)
        {
            lines.AddRange(diagnostics.OfType<JsonObject>()
                .Where(d => string.Equals((string?)d["level"], "Error", StringComparison.OrdinalIgnoreCase))
                .Select(d => $"diagnostic {(string?)d["code"]} {(string?)d["target"]}: {(string?)d["message"]}"));
        }

        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines.Distinct(StringComparer.Ordinal));
    }

    /// <summary>
    /// The what-if rows that are not NoChange / Ignore, one block per row (change type, resource id, reasons, then one line per
    /// delta path with its property change type and before / after), followed by the edge counts when any edge would be added or
    /// removed; null when standard output is not a JSON document or nothing changes. Assertion messages put this before the
    /// truncated output tail, which can cut off the changed row.
    /// </summary>
    public string? DescribeChangedRows()
    {
        JsonObject json;
        try
        {
            json = Json;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
        {
            return null;
        }

        var lines = new List<string>();
        if (json["changes"] is JsonArray changes)
        {
            foreach (var change in changes.OfType<JsonObject>().Where(c => (string?)c["changeType"] is not ("NoChange" or "Ignore")))
            {
                lines.Add($"  {(string?)change["changeType"]} {(string?)change["resourceId"]}");
                if ((string?)change["unsupportedReason"] is { } unsupported)
                {
                    lines.Add("    unsupported: " + unsupported);
                }

                if (change["reasons"] is JsonArray reasons && reasons.Count > 0)
                {
                    lines.Add("    reasons: " + string.Join(" | ", reasons.Select(r => r?.ToString())));
                }

                if (change["delta"] is JsonArray delta)
                {
                    AddDeltaLines(lines, delta, string.Empty);
                }
            }
        }

        int add = (int?)json["summary"]?["edges"]?["add"] ?? 0;
        int remove = (int?)json["summary"]?["edges"]?["remove"] ?? 0;
        if (add != 0 || remove != 0)
        {
            lines.Add($"  edges: add {add}, remove {remove}");
        }

        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    private static void AddDeltaLines(List<string> lines, JsonArray deltas, string parentPath)
    {
        foreach (var delta in deltas.OfType<JsonObject>())
        {
            string relative = (string?)delta["path"] ?? string.Empty;
            string path = parentPath.Length == 0 ? relative : relative.StartsWith('[') ? parentPath + relative : parentPath + "." + relative;
            string values = string.Empty;
            if (delta.ContainsKey("before") || delta.ContainsKey("after"))
            {
                values = $": {Compact(delta, "before")} -> {Compact(delta, "after")}";
            }

            lines.Add($"    {(string?)delta["propertyChangeType"]} {path}{values}");
            if (delta["children"] is JsonArray children)
            {
                AddDeltaLines(lines, children, path);
            }
        }
    }

    private static string Compact(JsonObject delta, string key)
    {
        string text = !delta.TryGetPropertyValue(key, out var node) ? "(absent)" : node?.ToJsonString() ?? "null";
        return text.Length <= 200 ? text : text[..200] + "...";
    }

    private static string DescribeOperation(JsonObject o)
    {
        string? code = (string?)o["errorCode"];
        return $"{(string?)o["status"]} {(string?)o["resourceId"]} {(code is null ? string.Empty : "[" + code + "] ")}{(string?)o["error"]}".TrimEnd();
    }

    /// <summary>True when a diagnostic with the code is present.</summary>
    public bool HasDiagnostic(string code)
    {
        return Diagnostics.Any(d => string.Equals((string?)d["code"], code, StringComparison.Ordinal));
    }

    private IReadOnlyList<JsonObject> ArrayOf(string name)
    {
        return Json[name] is JsonArray array ? array.OfType<JsonObject>().ToList() : [];
    }

    private static string Truncate(string text)
    {
        return text.Length <= 600 ? text : text[..600] + "...";
    }
}

/// <summary>One deployment of one template: its name (a label), the template file and its parameters.</summary>
internal sealed record DeploymentRun(string Name, string TemplateFile, IReadOnlyList<string> ParameterTokens)
{
    public DeploymentRun WithParameter(string name, string value)
    {
        return this with { ParameterTokens = [.. ParameterTokens, "-p", name + "=" + value] };
    }

    public DeploymentRun WithTemplate(string templateFile)
    {
        return this with { TemplateFile = templateFile };
    }
}

/// <summary>
/// Drives the real command line in-process (<see cref="Program.BuildRootCommand"/> with the production parser
/// configuration) against the tenant. Nothing is kept between calls: every run resolves identity from the tenant. Sign-in comes from
/// the environment (<c>AZURE_*</c>, <c>EARM_AUTH</c>) or <c>earm auth login</c>; a user sign-in requests each verb's delegated scopes, as in production. Calls must not overlap: the global <c>-o</c> option is
/// static, so every test class shares one xUnit collection.
/// </summary>
internal sealed class CliDriver
{
    /// <summary>
    /// The in-process CLI never opens a consent prompt: the fixture consented the union of every enabled scenario's scopes before the first test, so a
    /// run that still lacks one fails with <c>ConsentRequired</c> (exit 3) instead of waiting for a browser no one may be watching.
    /// </summary>
    public CliDriver()
    {
        ConsentPrompting.TerminalIsInteractive = static () => false;
    }

    public async Task<CliResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var argv = new List<string>(args);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var configuration = new InvocationConfiguration { Output = stdout, Error = stderr };
        var parseResult = Program.BuildRootCommand().Parse(argv.ToArray(), Program.CreateParserConfiguration());
        int exitCode = await parseResult.InvokeAsync(configuration, cancellationToken);
        return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary><c>deployment what-if -f … -n … -p … -o json</c> plus the extra arguments.</summary>
    public Task<CliResult> WhatIfAsync(DeploymentRun run, CancellationToken cancellationToken, params string[] extra)
    {
        return RunAsync(["deployment", "what-if", "-f", run.TemplateFile, "-n", run.Name, .. run.ParameterTokens, "-o", "json", .. extra], cancellationToken);
    }

    /// <summary><c>deployment create -f … -n … -p … -y -o json</c> plus the extra arguments.</summary>
    public Task<CliResult> CreateAsync(DeploymentRun run, CancellationToken cancellationToken, params string[] extra)
    {
        return RunAsync(["deployment", "create", "-f", run.TemplateFile, "-n", run.Name, .. run.ParameterTokens, "-y", "-o", "json", .. extra], cancellationToken);
    }

    /// <summary><c>deployment delete -f … -n … -p … -y -o json</c>: deletes the objects the template declares.</summary>
    public Task<CliResult> DeleteAllAsync(DeploymentRun run, CancellationToken cancellationToken)
    {
        return RunAsync(["deployment", "delete", "-f", run.TemplateFile, "-n", run.Name, .. run.ParameterTokens, "-y", "-o", "json"], cancellationToken);
    }
}
