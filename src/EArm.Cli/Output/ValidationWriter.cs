using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using EArm.Core.Diagnostics;

namespace EArm.Cli.Output;

/// <summary>Renders validation results as a table (one line per diagnostic plus a summary) or as JSON.</summary>
internal static class ValidationWriter
{
    public static void Fill(ValidationReport report, IReadOnlyList<Diagnostic> diagnostics)
    {
        report.Diagnostics.Clear();
        report.ErrorCount = 0;
        report.WarningCount = 0;
        foreach (var diagnostic in diagnostics)
        {
            switch (diagnostic.Severity)
            {
                case DiagnosticSeverity.Error:
                    report.ErrorCount++;
                    break;
                case DiagnosticSeverity.Warning:
                    report.WarningCount++;
                    break;
            }
        }

        // The counts above are totals; the listing is capped per code so one repeated finding cannot flood the output.
        var listed = DiagnosticCap.Apply(diagnostics, out var suppressed);
        foreach (var diagnostic in listed)
        {
            report.Diagnostics.Add(new DiagnosticReport
            {
                Severity = diagnostic.Severity.ToString(),
                Code = diagnostic.Code,
                Message = diagnostic.Message,
                Path = diagnostic.Path?.ToString(),
                Line = diagnostic.Location?.Line,
                Column = diagnostic.Location?.Column,
                Source = diagnostic.Source,
            });
        }

        report.SuppressedCount = suppressed.Sum(s => s.Count);
        report.Suppressed = suppressed.Count == 0
            ? null
            : [.. suppressed.Select(s => new SuppressedReport { Severity = s.Severity.ToString(), Code = s.Code, Count = s.Count })];
        report.Status = report.ErrorCount > 0 ? "Failed" : "Succeeded";
    }

    public static void Write(ValidationReport report, IReadOnlyList<Diagnostic> diagnostics, string templateFile, OutputFormat format, TextWriter stdout, TextWriter stderr)
    {
        switch (format)
        {
            case OutputFormat.Json:
                stdout.WriteLine(JsonSerializer.Serialize(report, CliJsonContext.Default.ValidationReport));
                break;

            case OutputFormat.None:
                break;

            default:
                WriteTable(report, diagnostics, templateFile, stdout, stderr);
                break;
        }
    }

    private static void WriteTable(ValidationReport report, IReadOnlyList<Diagnostic> diagnostics, string templateFile, TextWriter stdout, TextWriter stderr)
    {
        var listed = DiagnosticCap.Apply(diagnostics, out var suppressed);
        foreach (var diagnostic in listed)
        {
            string line = diagnostic.ToString();
            if (diagnostic.Source is not null && !string.Equals(diagnostic.Source, templateFile, StringComparison.OrdinalIgnoreCase))
            {
                line = Path.GetFileName(diagnostic.Source) + ": " + line;
            }

            (diagnostic.IsError ? stderr : stdout).WriteLine(line);
        }

        foreach (var capped in suppressed)
        {
            (capped.Severity == DiagnosticSeverity.Error ? stderr : stdout).WriteLine(DiagnosticCap.Describe(capped));
        }

        var culture = CultureInfo.InvariantCulture;
        if (report.ErrorCount > 0)
        {
            stderr.WriteLine(string.Create(culture, $"Validation failed: {report.ErrorCount} error(s), {report.WarningCount} warning(s)."));
        }
        else
        {
            stdout.WriteLine(string.Create(culture, $"Validation succeeded: {report.Resources.Count} resource(s), {report.WarningCount} warning(s)."));
        }
    }
}
