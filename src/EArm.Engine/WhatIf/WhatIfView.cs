using System;
using System.Collections.Generic;
using System.Linq;
using EArm.Engine.Providers;

namespace EArm.Engine.WhatIf;

/// <summary>
/// The change types a what-if reports, with ARM's names and casing: the values of <c>--exclude-change-types</c> /
/// <c>--what-if-exclude-change-types</c> and of the JSON's <c>changeType</c>.
/// </summary>
public enum WhatIfChangeType
{
    Create,
    Delete,
    Deploy,
    Ignore,
    Modify,
    NoChange,
    Unsupported,
}

/// <summary>Counts of the rows a <see cref="WhatIfView"/> shows. <see cref="Orphans"/> rows are <c>Ignore</c> rows but are not included in <see cref="Ignore"/>;
/// a row that cannot be applied as planned counts under its planned type and under <see cref="Unsupported"/>.</summary>
public sealed record WhatIfSummary(int Create, int Modify, int Delete, int NoChange, int Deploy, int Ignore, int Unsupported, int Orphans, int Skipped);

/// <summary>
/// What a what-if shows: the rows of a <see cref="WhatIfReport"/> in one <see cref="ResultFormat"/>, minus the change types excluded with
/// <c>--exclude-change-types</c> (ARM filters on the reported change type, so <c>Unsupported</c> excludes the rows that cannot be applied as planned, and
/// <c>Deploy</c> exists only with <see cref="ResultFormat.ResourceIdOnly"/>). The filter changes what is shown, never what is deployed: the exit code of
/// <c>--exit-code-on-changes</c> keeps using the whole report. The summary and the edge counts describe the rows shown.
/// </summary>
public sealed class WhatIfView
{
    private WhatIfView(WhatIfReport report, ResultFormat format, IReadOnlySet<WhatIfChangeType> excluded)
    {
        Report = report;
        Format = format;
        Excluded = excluded;
        Rows = report.Rows.Where(row => !excluded.Contains(row.DisplayType(format))).ToList();
        Edges = Rows.Aggregate(EdgeSummary.Empty, (total, row) => total + row.Edges);
        Summary = Count(Rows, format, report.Skipped.Count);
    }

    public WhatIfReport Report { get; }

    public ResultFormat Format { get; }

    public IReadOnlySet<WhatIfChangeType> Excluded { get; }

    /// <summary>The rows shown, in report order.</summary>
    public IReadOnlyList<WhatIfRow> Rows { get; }

    public WhatIfSummary Summary { get; }

    /// <summary>The edge entries the rows shown add and remove.</summary>
    public EdgeSummary Edges { get; }

    /// <summary>True when rows of the report are hidden by <see cref="Excluded"/>.</summary>
    public bool IsFiltered => Rows.Count < Report.Rows.Count;

    /// <summary>
    /// True when a row shown reports a change: any change type other than <c>NoChange</c> and <c>Ignore</c>, or an edge add / remove. This is what
    /// <c>create -c --proceed-if-no-change</c> tests, as the Azure CLI does (on the filtered result).
    /// </summary>
    public bool ShowsChanges => Rows.Any(row => row.DisplayType(Format) is not (WhatIfChangeType.NoChange or WhatIfChangeType.Ignore) || row.Edges.HasChanges);

    public static WhatIfView Of(WhatIfReport report, ResultFormat format = ResultFormat.FullResourcePayloads, IEnumerable<WhatIfChangeType>? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new WhatIfView(report, format, exclude is null ? new HashSet<WhatIfChangeType>() : new HashSet<WhatIfChangeType>(exclude));
    }

    /// <summary>Parses the ARM names of <c>--exclude-change-types</c> case-insensitively (<c>nochange</c>, <c>NoChange</c>); false for an unknown name.</summary>
    public static bool TryParseChangeType(string text, out WhatIfChangeType type)
    {
        return Enum.TryParse(text?.Trim(), ignoreCase: true, out type) && Enum.IsDefined(type) && !int.TryParse(text, out _);
    }

    private static WhatIfSummary Count(IReadOnlyList<WhatIfRow> rows, ResultFormat format, int skipped)
    {
        int create = 0, modify = 0, delete = 0, noChange = 0, deploy = 0, ignore = 0, unsupported = 0, orphans = 0;
        foreach (var row in rows)
        {
            if (row.IsOrphan)
            {
                orphans++;
                continue;
            }

            if (row.IsDeploy(format))
            {
                deploy++;
            }
            else
            {
                switch (row.Type)
                {
                    case ChangeType.Create:
                        create++;
                        break;
                    case ChangeType.Modify:
                        modify++;
                        break;
                    case ChangeType.Delete:
                        delete++;
                        break;
                    case ChangeType.NoChange:
                        noChange++;
                        break;
                    default:
                        ignore++;
                        break;
                }
            }

            if (row.IsUnsupported)
            {
                unsupported++;
            }
        }

        return new WhatIfSummary(create, modify, delete, noChange, deploy, ignore, unsupported, orphans, skipped);
    }
}
