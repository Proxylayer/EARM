using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using EntraArm.Core.Json;
using EntraArm.Engine.Planning;
using EntraArm.Engine.Providers;

namespace EntraArm.Engine.WhatIf;

/// <summary>How <see cref="WhatIfText"/> renders.</summary>
/// <param name="Format"><c>ResourceIdOnly</c> prints resource ids and reasons only (no payloads, no property deltas).</param>
/// <param name="Verbose">Also print the payload of <c>NoChange</c> rows and the <c>NoEffect</c> deltas.</param>
/// <param name="Colors">Wrap rows in ANSI colours (the caller decides: TTY and no <c>NO_COLOR</c>).</param>
public sealed record WhatIfTextOptions(ResultFormat Format = ResultFormat.FullResourcePayloads, bool Verbose = false, bool Colors = false)
{
    public static WhatIfTextOptions Default { get; } = new();
}

/// <summary>
/// The human-readable what-if (plan section 1.8), laid out like <c>az deployment group what-if</c>: a header, the
/// symbol legend, one block per change (<c>+ ~ - = *</c>, <c>?</c> for deploy-time values, <c>!</c> for changes that
/// cannot be applied as planned) with the resource payload or the property deltas indented under it, then the
/// summary counts, the edge counts, the required permissions and the warnings. Lines are never padded into
/// columns, so the output reads the same at any terminal width.
/// </summary>
public static class WhatIfText
{
    private const string RowIndent = "  ";
    private const string BodyIndent = "      ";
    private const string Step = "  ";
    private const string Reset = "\u001b[0m";

    public static void Render(DeploymentPlan plan, TextWriter writer)
    {
        Render(plan, writer, WhatIfTextOptions.Default);
    }

    public static void Render(DeploymentPlan plan, TextWriter writer, WhatIfTextOptions options)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Render(WhatIfReport.From(plan), writer, options);
    }

    public static void Render(WhatIfReport report, TextWriter writer, WhatIfTextOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(writer);
        options ??= WhatIfTextOptions.Default;

        var culture = CultureInfo.InvariantCulture;
        writer.WriteLine(string.Create(culture, $"Deployment '{report.DeploymentName}' ({report.Mode}) on tenant {report.TenantId ?? "(unknown)"}"));
        writer.WriteLine();
        writer.WriteLine("Resource and property changes are indicated with these symbols:");
        writer.WriteLine(Colour(options, '+', RowIndent + "+ Create"));
        writer.WriteLine(Colour(options, '~', RowIndent + "~ Modify"));
        writer.WriteLine(Colour(options, '-', RowIndent + "- Delete"));
        writer.WriteLine(Colour(options, '=', RowIndent + "= NoChange"));
        writer.WriteLine(Colour(options, '*', RowIndent + "* Ignore"));
        writer.WriteLine(Colour(options, '?', RowIndent + "? Value only known at deployment time"));
        writer.WriteLine(Colour(options, '!', RowIndent + "! Change cannot be applied as planned"));
        writer.WriteLine();

        if (report.Rows.Count == 0)
        {
            writer.WriteLine("The template declares no resources and the state records none.");
            writer.WriteLine();
        }
        else
        {
            writer.WriteLine("The deployment would make the following changes:");
            writer.WriteLine();
            foreach (var row in report.Rows)
            {
                RenderRow(row, writer, options);
                writer.WriteLine();
            }
        }

        var summary = report.Summary;
        writer.WriteLine(string.Create(culture, $"Resource changes: {summary.Create} to create, {summary.Modify} to modify, {summary.Delete} to delete, {summary.NoChange} unchanged, {summary.Ignore} ignored."));
        writer.WriteLine(string.Create(culture, $"Edges: {report.Edges.Add} to add, {report.Edges.Remove} to remove."));
        writer.WriteLine(report.RequiredPermissions.Count == 0
            ? "Required permissions: none."
            : "Required permissions: " + string.Join(", ", report.RequiredPermissions) + ".");

        if (summary.Unsupported > 0)
        {
            writer.WriteLine(Colour(options, '!', string.Create(culture, $"{summary.Unsupported} change(s) cannot be applied as planned; see the rows marked '!'.")));
        }

        if (report.Diagnostics.Count > 0 || report.UnresolvedReferences.Count > 0)
        {
            writer.WriteLine();
            foreach (var diagnostic in report.Diagnostics)
            {
                writer.WriteLine(diagnostic.ToString());
            }

            foreach (var unresolved in report.UnresolvedReferences)
            {
                writer.WriteLine($"warning {EngineDiagnosticCodes.UnresolvedReference}: reference to existing resource '{unresolved.Id.Id}' (apiVersion {unresolved.ApiVersion}) could not be resolved: {unresolved.Reason}");
            }
        }
    }

    private static void RenderRow(WhatIfRow row, TextWriter writer, WhatIfTextOptions options)
    {
        char symbol = row.Change.Symbol;
        writer.WriteLine(Colour(options, symbol, $"{RowIndent}{row.Marker} {row.ResourceId} [{row.ResourceType}]"));

        bool full = options.Format == ResultFormat.FullResourcePayloads;
        switch (row.Type)
        {
            case ChangeType.Create:
                if (full && row.After is not null)
                {
                    WritePayload(row.After, writer, BodyIndent, options, symbol);
                }

                break;

            case ChangeType.Delete:
                if (full && row.Before is not null)
                {
                    WritePayload(row.Before, writer, BodyIndent, options, symbol);
                }

                break;

            case ChangeType.Modify:
                if (full)
                {
                    foreach (var delta in row.Deltas)
                    {
                        WriteDelta(delta, JsonPath.Root, writer, BodyIndent, options);
                    }
                }

                break;

            case ChangeType.NoChange:
                if (full && options.Verbose && (row.After ?? row.Before) is { } payload)
                {
                    WritePayload(payload, writer, BodyIndent, options, symbol);
                }

                break;
        }

        foreach (var reason in row.Reasons)
        {
            writer.WriteLine($"{BodyIndent}({reason})");
        }

        if (row.UnsupportedReason is { } unsupported)
        {
            writer.WriteLine(Colour(options, '!', $"{BodyIndent}! {unsupported}"));
        }
    }

    // ---- payloads ----

    private static void WritePayload(JsonObject payload, TextWriter writer, string indent, WhatIfTextOptions options, char symbol)
    {
        foreach (var property in payload)
        {
            WriteMember(property.Key, property.Value, writer, indent, options, symbol);
        }
    }

    private static void WriteMember(string key, JsonNode? value, TextWriter writer, string indent, WhatIfTextOptions options, char symbol)
    {
        switch (value)
        {
            case JsonObject obj when obj.Count > 0:
                writer.WriteLine(Colour(options, symbol, $"{indent}{key}:"));
                foreach (var property in obj)
                {
                    WriteMember(property.Key, property.Value, writer, indent + Step, options, symbol);
                }

                break;

            case JsonArray array when array.Count > 0:
                writer.WriteLine(Colour(options, symbol, $"{indent}{key}: ["));
                WriteItems(array, writer, indent + Step, options, symbol);
                writer.WriteLine(Colour(options, symbol, $"{indent}]"));
                break;

            default:
                writer.WriteLine(Colour(options, symbol, $"{indent}{key}: {Scalar(value)}"));
                break;
        }
    }

    private static void WriteItems(JsonArray array, TextWriter writer, string indent, WhatIfTextOptions options, char symbol)
    {
        for (int i = 0; i < array.Count; i++)
        {
            WriteMember(i.ToString(CultureInfo.InvariantCulture), array[i], writer, indent, options, symbol);
        }
    }

    // ---- deltas ----

    private static void WriteDelta(PropertyDelta delta, JsonPath parent, TextWriter writer, string indent, WhatIfTextOptions options)
    {
        if (delta.Kind == PropertyChangeType.NoEffect && !options.Verbose)
        {
            return;
        }

        char marker = delta.Kind switch
        {
            PropertyChangeType.Create => '+',
            PropertyChangeType.Delete => '-',
            PropertyChangeType.NoEffect => '=',
            _ => '~',
        };

        string path = WhatIfPaths.Relative(delta.Path, parent);
        if (delta.Children is { Count: > 0 } children)
        {
            bool array = delta.Kind == PropertyChangeType.Array;
            writer.WriteLine(Colour(options, marker, $"{indent}{marker} {path}:{(array ? " [" : string.Empty)}"));
            string childIndent = indent + Step + Step;
            foreach (var child in children)
            {
                WriteDelta(child, delta.Path, writer, childIndent, options);
            }

            if (array)
            {
                writer.WriteLine(Colour(options, marker, $"{indent}{Step}]"));
            }

            return;
        }

        switch (delta.Kind)
        {
            case PropertyChangeType.Create:
                WriteDeltaValue(marker, path, delta.After, writer, indent, options);
                break;

            case PropertyChangeType.Delete:
                WriteDeltaValue(marker, path, delta.Before, writer, indent, options);
                break;

            default:
                writer.WriteLine(Colour(options, marker, $"{indent}{marker} {path}: {Scalar(delta.Before)} => {Scalar(delta.After)}"));
                break;
        }
    }

    /// <summary>A one-sided delta (create / delete): scalars inline, objects and arrays as an indented block.</summary>
    private static void WriteDeltaValue(char marker, string path, JsonNode? value, TextWriter writer, string indent, WhatIfTextOptions options)
    {
        switch (value)
        {
            case JsonObject obj when obj.Count > 0:
                writer.WriteLine(Colour(options, marker, $"{indent}{marker} {path}:"));
                foreach (var property in obj)
                {
                    WriteMember(property.Key, property.Value, writer, indent + Step + Step, options, marker);
                }

                break;

            case JsonArray array when array.Count > 0:
                writer.WriteLine(Colour(options, marker, $"{indent}{marker} {path}: ["));
                WriteItems(array, writer, indent + Step + Step, options, marker);
                writer.WriteLine(Colour(options, marker, $"{indent}{Step}]"));
                break;

            default:
                writer.WriteLine(Colour(options, marker, $"{indent}{marker} {path}: {Scalar(value)}"));
                break;
        }
    }

    private static string Scalar(JsonNode? node)
    {
        return node switch
        {
            null => "null",
            JsonObject { Count: 0 } => "{}",
            JsonArray { Count: 0 } => "[]",
            _ => node.ToJsonString(),
        };
    }

    // ---- colours ----

    private static string Colour(WhatIfTextOptions options, char symbol, string line)
    {
        if (!options.Colors)
        {
            return line;
        }

        string? code = symbol switch
        {
            '+' => "\u001b[32m",
            '~' => "\u001b[35m",
            '-' => "\u001b[31m",
            '*' => "\u001b[90m",
            '=' => "\u001b[90m",
            '?' => "\u001b[36m",
            '!' => "\u001b[1;31m",
            _ => null,
        };

        if (code is null)
        {
            return line;
        }

        var sb = new StringBuilder(line.Length + code.Length + Reset.Length);
        return sb.Append(code).Append(line).Append(Reset).ToString();
    }
}
