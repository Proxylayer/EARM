using System;
using System.Collections.Generic;
using System.Linq;
using EArm.Core.Diagnostics;

namespace EArm.Cli.Output;

/// <summary>
/// Keeps a hostile or generated template from flooding the output: a template with 12,000 variables that each exceed a limit would
/// otherwise print 12,000 identical diagnostics. At most <see cref="PerCodeLimit"/> diagnostics of each code and severity are listed
/// (the first ones, in their original order); the rest are counted and summarised in one line per code.
/// </summary>
internal static class DiagnosticCap
{
    /// <summary>How many diagnostics of one code and severity are listed before the rest is summarised.</summary>
    public const int PerCodeLimit = 50;

    /// <summary>The diagnostics to list, in their original order, and one count per code and severity that was capped.</summary>
    public static IReadOnlyList<Diagnostic> Apply(IReadOnlyList<Diagnostic> diagnostics, out IReadOnlyList<SuppressedCode> suppressed)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var seen = new Dictionary<(string Code, DiagnosticSeverity Severity), int>();
        List<Diagnostic>? kept = null;
        for (int i = 0; i < diagnostics.Count; i++)
        {
            var diagnostic = diagnostics[i];
            var key = (diagnostic.Code, diagnostic.Severity);
            int count = seen.GetValueOrDefault(key) + 1;
            seen[key] = count;
            if (count > PerCodeLimit)
            {
                // The first capped diagnostic: everything before it was kept, so the kept list starts from there.
                kept ??= [.. diagnostics.Take(i)];
                continue;
            }

            kept?.Add(diagnostic);
        }

        suppressed = [.. seen.Where(p => p.Value > PerCodeLimit).Select(p => new SuppressedCode(p.Key.Code, p.Key.Severity, p.Value - PerCodeLimit))];
        return kept ?? diagnostics;
    }

    /// <summary>The summary line for a capped code.</summary>
    public static string Describe(SuppressedCode code)
    {
        return $"... and {code.Count} more {code.Severity.ToString().ToLowerInvariant()}(s) of the same code {code.Code} (not shown).";
    }
}

/// <summary>A code and severity whose diagnostics were capped, with the number left out.</summary>
internal sealed record SuppressedCode(string Code, DiagnosticSeverity Severity, int Count);
