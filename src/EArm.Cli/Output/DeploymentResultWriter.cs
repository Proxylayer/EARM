using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Engine.Execution;

namespace EArm.Cli.Output;

/// <summary>
/// Renders a <see cref="DeploymentResult"/> as a table (one line per resource, the orphan report, outputs) or as JSON. The JSON carries
/// <c>orphans[]</c> (<c>resourceId</c>, <c>type</c>, <c>graphId</c>, <c>orphan</c>: <c>undeclared</c> | <c>retained</c>, <c>reason</c>): live objects
/// no declared resource matches, reported only and never deleted (docs/cli.md).
/// </summary>
internal static class DeploymentResultWriter
{
    /// <param name="whatIf">The what-if document of <c>-c</c>, embedded as <c>whatIf</c> so <c>-o json</c> stays one document; <c>null</c> otherwise.</param>
    public static void Write(DeploymentResult result, OutputFormat format, bool prettyPrint, TextWriter stdout, TextWriter stderr, JsonObject? whatIf = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        switch (format)
        {
            case OutputFormat.Json:
                var document = ToJson(result);
                if (whatIf is not null)
                {
                    document["whatIf"] = whatIf;
                }

                stdout.WriteLine(document.ToJsonString(JsonOutput.Options(prettyPrint)));
                break;
            case OutputFormat.None:
                break;
            default:
                WriteTable(result, stdout, stderr);
                break;
        }
    }

    public static JsonObject ToJson(DeploymentResult result)
    {
        var operations = new JsonArray();
        foreach (var operation in result.Operations)
        {
            operations.Add(new JsonObject
            {
                ["resourceId"] = operation.Id.Id,
                ["type"] = operation.Type,
                ["batch"] = operation.Batch,
                ["status"] = operation.Status.ToString(),
                ["changeType"] = operation.ChangeType?.ToString(),
                ["mode"] = operation.Mode?.ToString(),
                ["graphId"] = operation.GraphId,
                ["error"] = operation.Error,
                ["errorCode"] = operation.ErrorCode,
                ["durationMs"] = (long)operation.Duration.TotalMilliseconds,
            });
        }

        var orphans = new JsonArray();
        foreach (var orphan in result.Orphans)
        {
            orphans.Add(new JsonObject
            {
                ["resourceId"] = orphan.Id.Id,
                ["type"] = orphan.ResourceType,
                ["graphId"] = orphan.GraphId,
                ["orphan"] = orphan.Orphan == EArm.Engine.Providers.OrphanReason.Retained ? "retained" : "undeclared",
                ["reason"] = orphan.Reasons.Count > 0 ? orphan.Reasons[0] : null,
            });
        }

        var diagnostics = new JsonArray();
        foreach (var diagnostic in result.Diagnostics)
        {
            diagnostics.Add(new JsonObject
            {
                ["level"] = diagnostic.Severity.ToString(),
                ["code"] = diagnostic.Code,
                ["target"] = diagnostic.Path?.ToString(),
                ["message"] = diagnostic.Message,
            });
        }

        return new JsonObject
        {
            ["status"] = result.Status.ToString(),
            ["deploymentName"] = result.DeploymentName,
            ["durationMs"] = (long)result.Duration.TotalMilliseconds,
            ["succeeded"] = result.SucceededCount,
            ["failed"] = result.FailedCount,
            ["skipped"] = result.SkippedCount,
            ["ignored"] = result.IgnoredCount,
            ["operations"] = operations,
            ["orphans"] = orphans,
            ["outputs"] = result.Outputs.DeepClone(),
            ["secureOutputsFile"] = result.SecureOutputsFile,
            ["diagnostics"] = diagnostics,
        };
    }

    private static void WriteTable(DeploymentResult result, TextWriter stdout, TextWriter stderr)
    {
        var culture = CultureInfo.InvariantCulture;
        string orphans = result.Orphans.Count == 0 ? string.Empty : string.Create(culture, $", {result.Orphans.Count} orphan(s) reported");
        stdout.WriteLine(string.Create(culture, $"Deployment '{result.DeploymentName}' {result.Status} in {result.Duration.TotalSeconds:F1} s: {result.SucceededCount} succeeded, {result.FailedCount} failed, {result.SkippedCount} skipped, {result.IgnoredCount} ignored{orphans}."));
        foreach (var operation in result.Operations)
        {
            string detail = operation.Status switch
            {
                OperationStatus.Succeeded => operation.ChangeType?.ToString() ?? string.Empty,
                OperationStatus.Ignored => "condition false",
                _ => operation.Error ?? operation.Status.ToString(),
            };

            var writer = operation.Status == OperationStatus.Failed ? stderr : stdout;
            writer.WriteLine($"  {operation.Symbol} {operation.Id.Id} ({detail})");
        }

        if (result.Orphans.Count > 0)
        {
            stdout.WriteLine("Not declared in the template (reported only; never deleted):");
            foreach (var orphan in result.Orphans)
            {
                string kind = orphan.Orphan == EArm.Engine.Providers.OrphanReason.Retained ? "retained" : "undeclared";
                stdout.WriteLine($"  * {orphan.Id.Id} [{orphan.ResourceType}] (orphan: {kind}){(orphan.GraphId is null ? string.Empty : $" Graph id {orphan.GraphId}")}");
            }
        }

        foreach (var diagnostic in result.Diagnostics)
        {
            (diagnostic.Severity == DiagnosticSeverity.Error ? stderr : stdout).WriteLine("  " + diagnostic);
        }

        if (result.Outputs.Count > 0)
        {
            stdout.WriteLine("Outputs:");
            foreach (var output in result.Outputs)
            {
                var value = (output.Value as JsonObject)?["value"];
                stdout.WriteLine($"  {output.Key}: {(value is null ? "null" : value.ToJsonString())}");
            }
        }

        if (result.SecureOutputsFile is not null)
        {
            stdout.WriteLine($"Secure outputs written to {result.SecureOutputsFile}.");
        }
    }
}
