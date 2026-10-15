using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Engine.State;

namespace EArm.Cli.Output;

/// <summary>What <c>deployment show --refresh</c> found for one state entry.</summary>
internal sealed record DriftReport(string Status, string? LiveGraphId, string? Detail)
{
    public const string InSync = "InSync";

    public const string Missing = "Missing";

    public const string Replaced = "Replaced";

    public const string Unknown = "Unknown";
}

/// <summary>One row of <c>deployment list</c>.</summary>
internal sealed record DeploymentListItem(string Name, StateFile? State, StateLockInfo? Lock, string? Error);

/// <summary>Renders state files as tables or JSON for <c>deployment show / list / operation list</c>.</summary>
internal static class StateWriter
{
    public static void WriteShow(StateFile state, string path, StateLockInfo? lockInfo, IReadOnlyDictionary<string, DriftReport>? drift, OutputFormat format, bool prettyPrint, TextWriter stdout)
    {
        ArgumentNullException.ThrowIfNull(state);
        switch (format)
        {
            case OutputFormat.Json:
                stdout.WriteLine(ShowJson(state, path, lockInfo, drift).ToJsonString(JsonOutput.Options(prettyPrint)));
                break;
            case OutputFormat.None:
                break;
            default:
                WriteShowTable(state, path, lockInfo, drift, stdout);
                break;
        }
    }

    public static void WriteList(IReadOnlyList<DeploymentListItem> items, string directory, OutputFormat format, bool prettyPrint, TextWriter stdout)
    {
        ArgumentNullException.ThrowIfNull(items);
        switch (format)
        {
            case OutputFormat.Json:
                {
                    var array = new JsonArray();
                    foreach (var item in items)
                    {
                        array.Add(new JsonObject
                        {
                            ["name"] = item.Name,
                            ["tenantId"] = item.State?.TenantId,
                            ["lastStatus"] = item.State?.LastStatus,
                            ["lastMode"] = item.State?.LastMode,
                            ["lastDeployedOn"] = item.State?.LastDeployedOn?.ToString("O", CultureInfo.InvariantCulture),
                            ["resourceCount"] = item.State?.Resources.Count,
                            ["locked"] = item.Lock is not null,
                            ["lockHolder"] = item.Lock?.Describe(),
                            ["error"] = item.Error,
                        });
                    }

                    stdout.WriteLine(new JsonObject { ["stateDirectory"] = directory, ["deployments"] = array }.ToJsonString(JsonOutput.Options(prettyPrint)));
                    break;
                }

            case OutputFormat.None:
                break;

            default:
                {
                    stdout.WriteLine($"State directory: {directory}");
                    if (items.Count == 0)
                    {
                        stdout.WriteLine("No deployments.");
                        return;
                    }

                    var table = new TextTable("Name", "Status", "Mode", "Last deployed", "Resources", "Tenant", "Lock");
                    foreach (var item in items)
                    {
                        table.AddRow(
                            item.Name,
                            item.Error is not null ? "unreadable" : item.State?.LastStatus ?? "-",
                            item.State?.LastMode ?? "-",
                            StateSerializer.Timestamp(item.State?.LastDeployedOn),
                            item.State is null ? "-" : item.State.Resources.Count.ToString(CultureInfo.InvariantCulture),
                            item.State?.TenantId ?? "-",
                            item.Lock is null ? "-" : "locked");
                    }

                    table.Write(stdout);
                    foreach (var item in items.Where(i => i.Error is not null))
                    {
                        stdout.WriteLine($"  {item.Name}: {item.Error}");
                    }

                    break;
                }
        }
    }

    public static void WriteOperations(StateFile state, OutputFormat format, bool prettyPrint, TextWriter stdout)
    {
        ArgumentNullException.ThrowIfNull(state);
        switch (format)
        {
            case OutputFormat.Json:
                {
                    var array = new JsonArray();
                    foreach (var operation in state.Operations)
                    {
                        array.Add(OperationJson(operation));
                    }

                    stdout.WriteLine(new JsonObject
                    {
                        ["deploymentName"] = state.DeploymentName,
                        ["lastStatus"] = state.LastStatus,
                        ["lastMode"] = state.LastMode,
                        ["lastDeployedOn"] = state.LastDeployedOn?.ToString("O", CultureInfo.InvariantCulture),
                        ["operations"] = array,
                    }.ToJsonString(JsonOutput.Options(prettyPrint)));
                    break;
                }

            case OutputFormat.None:
                break;

            default:
                {
                    stdout.WriteLine($"Deployment '{state.DeploymentName}': last run {state.LastStatus ?? "-"} ({state.LastMode ?? "-"}) on {StateSerializer.Timestamp(state.LastDeployedOn)}; {state.Operations.Count} operation(s).");
                    if (state.Operations.Count == 0)
                    {
                        return;
                    }

                    var table = new TextTable("", "Resource", "Status", "Change", "Batch", "Started", "Duration", "Graph id / error");
                    foreach (var operation in state.Operations)
                    {
                        table.AddRow(
                            operation.Symbol.ToString(),
                            operation.ResourceId,
                            operation.Status.ToString(),
                            operation.ChangeType?.ToString() ?? "-",
                            operation.Batch?.ToString(CultureInfo.InvariantCulture) ?? "-",
                            StateSerializer.Timestamp(operation.StartedOn),
                            operation.Duration is { } duration ? string.Create(CultureInfo.InvariantCulture, $"{duration.TotalMilliseconds:F0} ms") : "-",
                            operation.Error ?? operation.GraphId ?? "-");
                    }

                    table.Write(stdout);
                    break;
                }
        }
    }

    private static JsonObject ShowJson(StateFile state, string path, StateLockInfo? lockInfo, IReadOnlyDictionary<string, DriftReport>? drift)
    {
        var json = JsonNode.Parse(StateSerializer.Serialize(state)) as JsonObject ?? new JsonObject();
        json["stateFile"] = path;
        json["lock"] = lockInfo is null ? null : new JsonObject
        {
            ["processId"] = lockInfo.ProcessId,
            ["host"] = lockInfo.Host,
            ["user"] = lockInfo.User,
            ["acquiredOn"] = lockInfo.AcquiredOn.ToString("O", CultureInfo.InvariantCulture),
        };

        if (drift is not null)
        {
            var array = new JsonArray();
            foreach (var pair in drift.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                array.Add(new JsonObject
                {
                    ["resourceId"] = pair.Key,
                    ["status"] = pair.Value.Status,
                    ["liveGraphId"] = pair.Value.LiveGraphId,
                    ["detail"] = pair.Value.Detail,
                });
            }

            json["drift"] = array;
        }

        return json;
    }

    private static void WriteShowTable(StateFile state, string path, StateLockInfo? lockInfo, IReadOnlyDictionary<string, DriftReport>? drift, TextWriter stdout)
    {
        stdout.WriteLine($"Deployment:     {state.DeploymentName}");
        stdout.WriteLine($"State file:     {path}");
        stdout.WriteLine($"Tenant:         {state.TenantId ?? "-"}");
        stdout.WriteLine($"Created:        {StateSerializer.Timestamp(state.CreatedOn)}");
        stdout.WriteLine($"Last deployed:  {StateSerializer.Timestamp(state.LastDeployedOn)}");
        stdout.WriteLine($"Last status:    {state.LastStatus ?? "-"}");
        stdout.WriteLine($"Last mode:      {state.LastMode ?? "-"}");
        stdout.WriteLine($"Template hash:  {state.LastTemplateHash ?? "-"}");
        stdout.WriteLine($"Lock:           {(lockInfo is null ? "none" : lockInfo.Describe())}");
        stdout.WriteLine($"Resources:      {state.Resources.Count}");
        if (state.Resources.Count == 0)
        {
            return;
        }

        stdout.WriteLine();
        var table = drift is null
            ? new TextTable("Resource", "Graph id", "Provenance", "Last write", "Depends on")
            : new TextTable("Resource", "Graph id", "Provenance", "Last write", "Depends on", "Live");
        foreach (var pair in state.Resources.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var entry = pair.Value;
            var cells = new List<string>
            {
                pair.Key,
                entry.GraphId,
                StateProvenances.ToText(entry.Provenance),
                StateSerializer.Timestamp(entry.LastWriteOn),
                entry.DependsOn.Count.ToString(CultureInfo.InvariantCulture),
            };

            if (drift is not null)
            {
                cells.Add(drift.TryGetValue(pair.Key, out var report) ? report.Status + (report.Detail is null ? string.Empty : " (" + report.Detail + ")") : "-");
            }

            table.AddRow([.. cells]);
        }

        table.Write(stdout);
    }

    private static JsonObject OperationJson(OperationRecord operation)
    {
        return new JsonObject
        {
            ["resourceId"] = operation.ResourceId,
            ["type"] = operation.Type,
            ["status"] = operation.Status.ToString(),
            ["changeType"] = operation.ChangeType?.ToString(),
            ["batch"] = operation.Batch,
            ["startedOn"] = operation.StartedOn?.ToString("O", CultureInfo.InvariantCulture),
            ["finishedOn"] = operation.FinishedOn?.ToString("O", CultureInfo.InvariantCulture),
            ["durationMs"] = operation.Duration is { } duration ? (long)duration.TotalMilliseconds : null,
            ["graphId"] = operation.GraphId,
            ["error"] = operation.Error,
            ["errorCode"] = operation.ErrorCode,
        };
    }
}

/// <summary>A minimal left-aligned text table (two spaces between columns).</summary>
internal sealed class TextTable
{
    private readonly string[] _headers;
    private readonly List<string[]> _rows = [];

    public TextTable(params string[] headers)
    {
        _headers = headers;
    }

    public void AddRow(params string[] cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        if (cells.Length != _headers.Length)
        {
            throw new ArgumentException($"Expected {_headers.Length} cells, got {cells.Length}.", nameof(cells));
        }

        _rows.Add(cells);
    }

    public void Write(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var widths = new int[_headers.Length];
        for (int i = 0; i < _headers.Length; i++)
        {
            widths[i] = _headers[i].Length;
            foreach (var row in _rows)
            {
                widths[i] = Math.Max(widths[i], row[i].Length);
            }
        }

        writer.WriteLine(Format(_headers, widths));
        writer.WriteLine(Format(widths.Select(w => new string('-', w)).ToArray(), widths));
        foreach (var row in _rows)
        {
            writer.WriteLine(Format(row, widths));
        }
    }

    private static string Format(string[] cells, int[] widths)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < cells.Length; i++)
        {
            if (i > 0)
            {
                sb.Append("  ");
            }

            sb.Append(i == cells.Length - 1 ? cells[i] : cells[i].PadRight(widths[i]));
        }

        return sb.ToString().TrimEnd();
    }
}
