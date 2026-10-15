using System;
using System.Linq;
using EArm.Engine.Providers;

namespace EArm.Engine.Edges;

/// <summary>Folds the edge pass outcome of a declaring resource into its what-if / result row.</summary>
public static class EdgeChanges
{
    /// <summary>
    /// Appends the row's <c>properties.&lt;property&gt;</c> deltas to the object change; a <c>NoChange</c> row with
    /// effective edge deltas becomes <c>Modify</c>; validation errors make the row unsupported; warnings and errors
    /// are listed under the reasons.
    /// </summary>
    public static ResourceChange Merge(ResourceChange change, EdgeRow row)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(row);

        var edgeDeltas = row.Deltas;
        bool effect = edgeDeltas.Any(d => d.HasEffect);
        var type = change.Type == ChangeType.NoChange && effect ? ChangeType.Modify : change.Type;
        var mode = type == ChangeType.Modify && change.Mode == ChangeMode.None ? ChangeMode.Patch : change.Mode;
        return change with
        {
            Type = type,
            Mode = mode,
            Deltas = [.. change.Deltas, .. edgeDeltas],
            Reasons = [.. change.Reasons, .. row.Errors, .. row.Warnings],
            UnsupportedReason = change.UnsupportedReason ?? row.Errors.FirstOrDefault(),
            IsUnknown = change.IsUnknown || row.HasUnknown,
        };
    }
}
