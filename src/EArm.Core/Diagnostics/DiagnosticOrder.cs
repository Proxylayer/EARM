using System.Collections.Generic;

namespace EArm.Core.Diagnostics;

/// <summary>
/// The report order of diagnostics: template findings first (by line, then column), then parameter-file ones;
/// entries without a location last; errors before warnings at the same position; then by code.
/// Shared by every validator so lists from several passes can be merged and sorted once.
/// </summary>
public sealed class DiagnosticOrder : IComparer<Diagnostic>
{
    public static DiagnosticOrder Instance { get; } = new();

    public int Compare(Diagnostic? x, Diagnostic? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int bySource = string.CompareOrdinal(x.Source ?? string.Empty, y.Source ?? string.Empty);
        if (bySource != 0)
        {
            return bySource;
        }

        int lineA = x.Location?.Line ?? int.MaxValue;
        int lineB = y.Location?.Line ?? int.MaxValue;
        if (lineA != lineB)
        {
            return lineA.CompareTo(lineB);
        }

        int columnA = x.Location?.Column ?? int.MaxValue;
        int columnB = y.Location?.Column ?? int.MaxValue;
        if (columnA != columnB)
        {
            return columnA.CompareTo(columnB);
        }

        int bySeverity = y.Severity.CompareTo(x.Severity);
        return bySeverity != 0 ? bySeverity : string.CompareOrdinal(x.Code, y.Code);
    }
}
