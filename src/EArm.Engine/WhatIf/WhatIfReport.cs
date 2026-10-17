using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Types;
using EArm.Engine.Planning;
using EArm.Engine.Providers;

namespace EArm.Engine.WhatIf;

/// <summary>ARM's <c>--result-format</c>.</summary>
public enum ResultFormat
{
    FullResourcePayloads,
    ResourceIdOnly,
}

/// <summary>Edge writes the what-if counts under the declaring resources: entries to add and entries to remove.</summary>
public sealed record EdgeSummary(int Add, int Remove)
{
    public static EdgeSummary Empty { get; } = new(0, 0);

    public bool HasChanges => Add + Remove > 0;

    public static EdgeSummary operator +(EdgeSummary left, EdgeSummary right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return new EdgeSummary(left.Add + right.Add, left.Remove + right.Remove);
    }
}

/// <summary>
/// One what-if row, ready to render: the planned change plus the ARM-shaped <c>before</c> /
/// <c>after</c> payloads (<c>name</c>, <c>type</c>, <c>apiVersion</c>, <c>properties</c>), the property deltas with
/// every secure value masked as <c>***</c> on both sides, the deploy-time-unknown flag and the edge counts.
/// </summary>
public sealed class WhatIfRow
{
    private WhatIfRow(ResourceChange change, ResourceInstance? instance, JsonObject? before, JsonObject? after, IReadOnlyList<PropertyDelta> deltas, bool isUnknown, EdgeSummary edges)
    {
        Change = change;
        Instance = instance;
        Before = before;
        After = after;
        Deltas = deltas;
        IsUnknown = isUnknown;
        Edges = edges;
    }

    public ResourceChange Change { get; }

    /// <summary>The planned instance; <c>null</c> for orphan report rows (live objects the template does not declare).</summary>
    public ResourceInstance? Instance { get; }

    public string ResourceId => Change.Id.Id;

    public string ResourceType => Change.ResourceType;

    public ChangeType Type => Change.Type;

    /// <summary>The live object before the change (<c>Modify</c>, <c>Delete</c>, <c>NoChange</c>); <c>null</c> otherwise.</summary>
    public JsonObject? Before { get; }

    /// <summary>The desired object after the change (<c>Create</c>, <c>Modify</c>, <c>NoChange</c>); <c>null</c> otherwise.</summary>
    public JsonObject? After { get; }

    /// <summary>The property deltas with secure values masked (<c>***</c> on both sides).</summary>
    public IReadOnlyList<PropertyDelta> Deltas { get; }

    /// <summary>True when the change or any delta carries a value only known at deployment time.</summary>
    public bool IsUnknown { get; }

    public string? UnsupportedReason => Change.UnsupportedReason;

    public bool IsUnsupported => Change.UnsupportedReason is not null;

    /// <summary>The change's reasons minus the unsupported reason (rendered separately).</summary>
    public IReadOnlyList<string> Reasons => Change.Reasons.Where(r => !string.Equals(r, Change.UnsupportedReason, StringComparison.Ordinal)).ToList();

    /// <summary>Edge entries this row adds and removes (relationship properties).</summary>
    public EdgeSummary Edges { get; }

    /// <summary>True for the rows of the orphan report (live objects no declared resource matches; never deleted). <see cref="ResourceChange.Orphan"/> says why.</summary>
    public bool IsOrphan => Change.IsOrphan;

    /// <summary>
    /// True when the row counts as a change for <c>--exit-code-on-changes</c>: Create, Modify or Delete, or any edge add / remove. Orphan report rows never do
    /// (nothing is written for them).
    /// </summary>
    public bool IsWrite => Change.IsWrite || Edges.HasChanges;

    /// <summary>The ARM symbol (<c>+ ~ - = *</c>) followed by <c>!</c> when unsupported, else <c>?</c> when deploy-time values are involved.</summary>
    public string Marker => Change.Symbol + (IsUnsupported ? "!" : IsUnknown ? "?" : string.Empty);

    /// <summary>True for a resource whose condition evaluated to false: not part of the deployment, listed only as skipped (<see cref="WhatIfReport.Skipped"/>).</summary>
    public bool IsSkipped => Change.IsConditionFalse;

    /// <summary>
    /// The change type ARM reports for this row in <paramref name="format"/>: <c>Unsupported</c> for a change that cannot be applied as planned; with
    /// <see cref="ResultFormat.ResourceIdOnly"/> an existing declared resource (<c>Modify</c> / <c>NoChange</c>) is <c>Deploy</c> (a languageVersion 2.0 <c>"existing": true</c> resource, which is never deployed, stays <c>NoChange</c>), as ARM reports it when
    /// it computes no property changes.
    /// </summary>
    public WhatIfChangeType DisplayType(ResultFormat format)
    {
        if (IsUnsupported)
        {
            return WhatIfChangeType.Unsupported;
        }

        return Change.Type switch
        {
            ChangeType.Create => WhatIfChangeType.Create,
            ChangeType.Delete => WhatIfChangeType.Delete,
            ChangeType.Modify or ChangeType.NoChange when IsDeploy(format) => WhatIfChangeType.Deploy,
            ChangeType.Modify => WhatIfChangeType.Modify,
            ChangeType.NoChange => WhatIfChangeType.NoChange,
            _ => WhatIfChangeType.Ignore,
        };
    }

    /// <summary>The row symbol in <paramref name="format"/>: ARM's <c>+ ~ - = *</c>, and <c>!</c> for a <c>Deploy</c> row (<see cref="ResultFormat.ResourceIdOnly"/>).</summary>
    public char Symbol(ResultFormat format)
    {
        return IsDeploy(format) ? '!' : Change.Symbol;
    }

    /// <summary><see cref="Marker"/> in <paramref name="format"/>: the symbol followed by <c>!</c> when unsupported, else <c>?</c> when deploy-time values are involved.</summary>
    public string MarkerFor(ResultFormat format)
    {
        return Symbol(format) + (IsUnsupported ? "!" : IsUnknown ? "?" : string.Empty);
    }

    /// <summary>True when the row is an existing declared resource reported as <c>Deploy</c> (ResourceIdOnly): unsupported rows included, the summary counts them as deploys too.</summary>
    internal bool IsDeploy(ResultFormat format)
    {
        return format == ResultFormat.ResourceIdOnly && !IsOrphan && !Change.IsExistingRead && Change.Type is ChangeType.Modify or ChangeType.NoChange;
    }

    internal static WhatIfRow Create(ResourceChange change, ResourceInstance? instance, ResourceTypeDefinition? definition)
    {
        var secure = definition?.Secure ?? [];
        var deltas = WhatIfMasking.MaskDeltas(change.Deltas, secure);
        var before = Payload(change, instance, change.Before, null, secure);
        // The desired object is the template's declaration: the object properties the provider writes plus the
        // relationship properties the edge pass applies, rendered like the deltas.
        var after = Payload(change, instance, change.After, instance?.EdgeProperties, secure);
        bool unknown = change.IsUnknown || deltas.Any(WhatIfMasking.ContainsUnknown);
        var edges = CountEdges(deltas, definition);
        return new WhatIfRow(change, instance, before, after, deltas, unknown, edges);
    }

    private static JsonObject? Payload(ResourceChange change, ResourceInstance? instance, JsonObject? properties, JsonObject? edgeProperties, IReadOnlyList<JsonPath> secure)
    {
        if (properties is null)
        {
            return null;
        }

        var payload = new JsonObject
        {
            ["name"] = change.Id.Name,
            ["type"] = change.ResourceType,
        };

        if (instance is not null)
        {
            payload["apiVersion"] = instance.ApiVersion;
        }

        var masked = WhatIfMasking.MaskProperties(properties, secure);
        if (edgeProperties is { Count: > 0 })
        {
            foreach (var property in edgeProperties)
            {
                if (!masked.ContainsKey(property.Key))
                {
                    masked[property.Key] = JsonDiff.Render(property.Value);
                }
            }
        }

        payload["properties"] = masked;
        return payload;
    }

    private static EdgeSummary CountEdges(IReadOnlyList<PropertyDelta> deltas, ResourceTypeDefinition? definition)
    {
        if (definition is null)
        {
            return EdgeSummary.Empty;
        }

        int add = 0, remove = 0;
        foreach (var delta in deltas)
        {
            if (!IsRelationshipDelta(delta, definition))
            {
                continue;
            }

            if (delta.Kind == PropertyChangeType.Array)
            {
                foreach (var child in delta.Children ?? [])
                {
                    switch (child.Kind)
                    {
                        case PropertyChangeType.Create:
                            add++;
                            break;
                        case PropertyChangeType.Delete:
                            remove++;
                            break;
                    }
                }

                continue;
            }

            // Single-valued kinds (manager): a Modify replaces the edge with one write.
            switch (delta.Kind)
            {
                case PropertyChangeType.Create:
                case PropertyChangeType.Modify:
                    add++;
                    break;
                case PropertyChangeType.Delete:
                    remove++;
                    break;
            }
        }

        return new EdgeSummary(add, remove);
    }

    private static bool IsRelationshipDelta(PropertyDelta delta, ResourceTypeDefinition definition)
    {
        var segments = delta.Path.Segments;
        return segments.Count == 2
            && !segments[0].IsIndex && string.Equals(segments[0].Name, "properties", StringComparison.OrdinalIgnoreCase)
            && !segments[1].IsIndex && definition.IsRelationshipProperty(segments[1].Name!);
    }
}

/// <summary>
/// The rendering model of a what-if, built once from a <see cref="DeploymentPlan"/> and shared
/// by the text and JSON renderers: rows in batch order (ties by resource id) followed by the orphan report rows (by
/// type, then resource id), the summary counts, the edge counts, the required permissions, the warnings and the unresolved
/// existing-resource lookups. A resource whose condition is false is not part of the deployment: ARM's what-if does not list it, so it is not a row
/// either; it is kept apart in <see cref="Skipped"/> (shown with <c>--verbose</c> and in the JSON's <c>skipped[]</c>).
/// <see cref="WhatIfView"/> applies the result format and <c>--exclude-change-types</c> on top.
/// </summary>
public sealed class WhatIfReport
{
    private WhatIfReport(DeploymentPlan plan, IReadOnlyList<WhatIfRow> rows, IReadOnlyList<WhatIfRow> skipped)
    {
        DeploymentName = plan.DeploymentName;
        Mode = plan.Mode;
        TenantId = plan.TenantId;
        Rows = rows;
        Skipped = skipped;
        Summary = ChangeSummary.Of(rows.Select(r => r.Change));
        Edges = rows.Aggregate(EdgeSummary.Empty, (total, row) => total + row.Edges);
        RequiredPermissions = plan.RequiredPermissions.OrderBy(p => p, StringComparer.Ordinal).ToList();
        Diagnostics = plan.Diagnostics;
        UnresolvedReferences = plan.UnresolvedReferences;
    }

    public string DeploymentName { get; }

    public DeploymentMode Mode { get; }

    public string? TenantId { get; }

    public IReadOnlyList<WhatIfRow> Rows { get; }

    /// <summary>The resources whose condition evaluated to false (not part of the deployment, nothing read or written), in the order of the rows.</summary>
    public IReadOnlyList<WhatIfRow> Skipped { get; }

    public ChangeSummary Summary { get; }

    public EdgeSummary Edges { get; }

    /// <summary>Sorted union of the Graph application permissions the plan needs.</summary>
    public IReadOnlyList<string> RequiredPermissions { get; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public IReadOnlyList<UnresolvedReference> UnresolvedReferences { get; }

    /// <summary>True when a deployment would write something: any Create / Modify / Delete row or any edge add / remove (<c>--exit-code-on-changes</c>).</summary>
    public bool HasChanges => Rows.Any(r => r.IsWrite);

    /// <summary>Builds the report from a plan computed in <see cref="PlanMode.WhatIf"/>.</summary>
    public static WhatIfReport From(DeploymentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var rows = new List<WhatIfRow>();
        var seen = new HashSet<ResourceChange>(ReferenceEqualityComparer.Instance);
        foreach (var batch in plan.Batches)
        {
            foreach (var instance in batch.Instances.OrderBy(i => i.PlannedId.Id, StringComparer.Ordinal))
            {
                if (plan.ChangeFor(instance) is { } change && seen.Add(change))
                {
                    rows.Add(WhatIfRow.Create(change, instance, plan.Catalog.Find(change.ResourceType)));
                }
            }
        }

        // Rows the batches do not reach (defensive: the plan lists them, so they are rendered).
        var orphans = new HashSet<ResourceChange>(plan.Orphans, ReferenceEqualityComparer.Instance);
        foreach (var change in plan.Changes)
        {
            if (!orphans.Contains(change) && seen.Add(change))
            {
                rows.Add(WhatIfRow.Create(change, plan.FindInstance(change.Id), plan.Catalog.Find(change.ResourceType)));
            }
        }

        foreach (var change in plan.Orphans.OrderBy(c => c.ResourceType, StringComparer.Ordinal).ThenBy(c => c.Id.Id, StringComparer.Ordinal))
        {
            rows.Add(WhatIfRow.Create(change, null, plan.Catalog.Find(change.ResourceType)));
        }

        return new WhatIfReport(plan, rows.Where(r => !r.IsSkipped).ToList(), rows.Where(r => r.IsSkipped).ToList());
    }
}
