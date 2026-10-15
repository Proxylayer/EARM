using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Engine.Execution;

namespace EArm.Cli.Output;

/// <summary>Renders a <see cref="DeploymentResult"/> as a table (one line per resource plus outputs) or as JSON.</summary>
internal static class DeploymentResultWriter
{
    public static void Write(DeploymentResult result, OutputFormat format, bool prettyPrint, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(result);
        switch (format)
        {
            case OutputFormat.Json:
                stdout.WriteLine(ToJson(result).ToJsonString(JsonOutput.Options(prettyPrint)));
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
            ["detached"] = result.DetachedCount,
            ["deleted"] = result.DeletedCount,
            ["operations"] = operations,
            ["outputs"] = result.Outputs.DeepClone(),
            ["secureOutputsFile"] = result.SecureOutputsFile,
            ["diagnostics"] = diagnostics,
        };
    }

    private static void WriteTable(DeploymentResult result, TextWriter stdout, TextWriter stderr)
    {
        var culture = CultureInfo.InvariantCulture;
        string modePass = result.DetachedCount + result.DeletedCount == 0
            ? string.Empty
            : string.Create(culture, $", {result.DetachedCount} detached, {result.DeletedCount} deleted");
        stdout.WriteLine(string.Create(culture, $"Deployment '{result.DeploymentName}' {result.Status} in {result.Duration.TotalSeconds:F1} s: {result.SucceededCount} succeeded, {result.FailedCount} failed, {result.SkippedCount} skipped, {result.IgnoredCount} ignored{modePass}."));
        foreach (var operation in result.Operations)
        {
            string detail = operation.Status switch
            {
                OperationStatus.Succeeded => operation.ChangeType?.ToString() ?? string.Empty,
                OperationStatus.Ignored => "condition false",
                OperationStatus.Detached => "not in template; detached from state",
                _ => operation.Error ?? operation.Status.ToString(),
            };

            var writer = operation.Status == OperationStatus.Failed ? stderr : stdout;
            writer.WriteLine($"  {operation.Symbol} {operation.Id.Id} ({detail})");
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
