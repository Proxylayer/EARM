using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Secrets;
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
/// One what-if row (plan section 1.8), ready to render: the planned change plus the ARM-shaped <c>before</c> /
/// <c>after</c> payloads (<c>name</c>, <c>type</c>, <c>apiVersion</c>, <c>properties</c>), the property deltas with
/// every secure value masked as <c>***</c> on both sides, the deploy-time-unknown flag and the edge counts.
/// </summary>
public sealed class WhatIfRow
{
    private WhatIfRow(ResourceChange change, ResourceInstance? instance, JsonObject? before, JsonObject? after, IReadOnlyList<PropertyDelta> deltas, bool isUnknown, EdgeSummary edges, bool isModePass)
    {
        Change = change;
        Instance = instance;
        Before = before;
        After = after;
        Deltas = deltas;
        IsUnknown = isUnknown;
        Edges = edges;
        IsModePass = isModePass;
    }

    public ResourceChange Change { get; }

    /// <summary>The planned instance; <c>null</c> for mode-pass rows (state entries the template no longer declares).</summary>
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

    /// <summary>Edge entries this row adds and removes (relationship properties of plan section 1.4).</summary>
    public EdgeSummary Edges { get; }

    /// <summary>True for the rows of the mode pass (not declared in the template: Delete in Complete, Ignore in Incremental).</summary>
    public bool IsModePass { get; }

    /// <summary>True when the row counts as a change for <c>--exit-code-on-changes</c>: Create, Modify or Delete, or any edge add / remove.</summary>
    public bool IsWrite => Change.IsWrite || Edges.HasChanges;

    /// <summary>The ARM symbol (<c>+ ~ - = *</c>) followed by <c>!</c> when unsupported, else <c>?</c> when deploy-time values are involved.</summary>
    public string Marker => Change.Symbol + (IsUnsupported ? "!" : IsUnknown ? "?" : string.Empty);

    internal static WhatIfRow Create(ResourceChange change, ResourceInstance? instance, ResourceTypeDefinition? definition, bool isModePass)
    {
        var secure = definition?.Secure ?? [];
        var deltas = WhatIfMasking.MaskDeltas(change.Deltas, secure);
        var before = Payload(change, instance, change.Before, null, secure);
        // The desired object is the template's declaration: the object properties the provider writes plus the
        // relationship properties the edge pass applies (plan section 1.4), rendered like the deltas.
        var after = Payload(change, instance, change.After, instance?.EdgeProperties, secure);
        bool unknown = change.IsUnknown || deltas.Any(WhatIfMasking.ContainsUnknown);
        var edges = CountEdges(deltas, definition);
        return new WhatIfRow(change, instance, before, after, deltas, unknown, edges, isModePass);
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
/// The rendering model of a what-if (plan section 1.8), built once from a <see cref="DeploymentPlan"/> and shared
/// by the text and JSON renderers: rows in batch order (ties by resource id) followed by the mode-pass rows (by
/// resource id), the summary counts, the edge counts, the required permissions, the warnings and the unresolved
/// existing-resource lookups.
/// </summary>
public sealed class WhatIfReport
{
    private WhatIfReport(DeploymentPlan plan, IReadOnlyList<WhatIfRow> rows)
    {
        DeploymentName = plan.DeploymentName;
        Mode = plan.Mode;
        TenantId = plan.TenantId;
        Rows = rows;
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
                    rows.Add(WhatIfRow.Create(change, instance, plan.Catalog.Find(change.ResourceType), isModePass: false));
                }
            }
        }

        // Rows the batches do not reach (defensive: the plan lists them, so they are rendered).
        var modePass = new HashSet<ResourceChange>(plan.ModeChanges, ReferenceEqualityComparer.Instance);
        foreach (var change in plan.Changes)
        {
            if (!modePass.Contains(change) && seen.Add(change))
            {
                rows.Add(WhatIfRow.Create(change, plan.FindInstance(change.Id), plan.Catalog.Find(change.ResourceType), isModePass: false));
            }
        }

        foreach (var change in plan.ModeChanges.OrderBy(c => c.Id.Id, StringComparer.Ordinal))
        {
            rows.Add(WhatIfRow.Create(change, null, plan.Catalog.Find(change.ResourceType), isModePass: true));
        }

        return new WhatIfReport(plan, rows);
    }
}

/// <summary>Path helpers shared by the what-if renderers.</summary>
internal static class WhatIfPaths
{
    /// <summary>
    /// A child delta's path relative to its parent delta (<c>properties.members[0]</c> under <c>properties.members</c>
    /// is <c>[0]</c>, <c>properties.appRoles['id']</c> under <c>properties.appRoles</c> is <c>['id']</c>); edge-pass
    /// children are already relative and come back unchanged; a path that does not nest under the parent is
    /// returned as is.
    /// </summary>
    public static string Relative(JsonPath path, JsonPath parent)
    {
        if (parent.IsRoot || !path.StartsWith(parent) || path.Depth == parent.Depth)
        {
            return path.ToString();
        }

        var relative = JsonPath.Root;
        for (int i = parent.Depth; i < path.Depth; i++)
        {
            relative = relative.Append(path.Segments[i]);
        }

        return relative.ToString();
    }
}

/// <summary>Secure-value masking and deploy-time detection shared by the what-if renderers.</summary>
internal static class WhatIfMasking
{
    private static readonly JsonPath PropertiesPath = JsonPath.Of("properties");

    /// <summary>A rendering copy of <paramref name="properties"/> with tainted leaves and the type's secure paths replaced by <c>***</c>.</summary>
    public static JsonObject MaskProperties(JsonObject properties, IReadOnlyList<JsonPath> secure)
    {
        var copy = (JsonObject)SecretJson.Redact(properties)!;
        foreach (var path in secure)
        {
            MaskAt(copy, path, 0);
        }

        return copy;
    }

    /// <summary>Deltas with <see cref="PropertyDelta.IsSecret"/> (or a path under a secure path) rendered as <c>***</c> on both sides, recursively.</summary>
    public static IReadOnlyList<PropertyDelta> MaskDeltas(IReadOnlyList<PropertyDelta> deltas, IReadOnlyList<JsonPath> secure)
    {
        if (deltas.Count == 0)
        {
            return deltas;
        }

        var masked = new List<PropertyDelta>(deltas.Count);
        foreach (var delta in deltas)
        {
            masked.Add(Mask(delta, secure, parentSecret: false));
        }

        return masked;
    }

    public static bool ContainsUnknown(PropertyDelta delta)
    {
        if (delta.IsUnknown)
        {
            return true;
        }

        return delta.Children is { Count: > 0 } children && children.Any(ContainsUnknown);
    }

    private static PropertyDelta Mask(PropertyDelta delta, IReadOnlyList<JsonPath> secure, bool parentSecret)
    {
        bool secret = parentSecret || delta.IsSecret || IsSecurePath(delta.Path, secure);
        IReadOnlyList<PropertyDelta>? children = delta.Children;
        if (children is { Count: > 0 })
        {
            var maskedChildren = new List<PropertyDelta>(children.Count);
            foreach (var child in children)
            {
                maskedChildren.Add(Mask(child, secure, secret));
            }

            children = maskedChildren;
        }

        if (!secret)
        {
            return ReferenceEquals(children, delta.Children) ? delta : delta with { Children = children };
        }

        return delta with
        {
            IsSecret = true,
            Before = delta.Before is null ? null : JsonValue.Create(SecretValue.Mask),
            After = delta.After is null ? null : JsonValue.Create(SecretValue.Mask),
            Children = children,
        };
    }

    /// <summary>True when the delta path (<c>properties.x.y</c>) is at or under one of the type's secure paths (relative to <c>properties</c>).</summary>
    private static bool IsSecurePath(JsonPath path, IReadOnlyList<JsonPath> secure)
    {
        if (secure.Count == 0 || !path.StartsWith(PropertiesPath))
        {
            return false;
        }

        foreach (var securePath in secure)
        {
            if (path.StartsWith(PropertiesPath.Append(securePath)))
            {
                return true;
            }
        }

        return false;
    }

    private static void MaskAt(JsonNode? node, JsonPath path, int index)
    {
        if (node is null)
        {
            return;
        }

        if (index == path.Depth)
        {
            return;
        }

        var segment = path.Segments[index];
        bool last = index == path.Depth - 1;
        switch (node)
        {
            case JsonObject obj when !segment.IsIndex:
                {
                    if (!obj.TryGetPropertyValue(segment.Name!, out var child) || child is null)
                    {
                        return;
                    }

                    if (last)
                    {
                        obj[segment.Name!] = JsonValue.Create(SecretValue.Mask);
                        return;
                    }

                    MaskAt(child, path, index + 1);
                    return;
                }

            case JsonArray array when segment.IsIndex:
                {
                    if (segment.Index < 0 || segment.Index >= array.Count || array[segment.Index] is null)
                    {
                        return;
                    }

                    if (last)
                    {
                        array[segment.Index] = JsonValue.Create(SecretValue.Mask);
                        return;
                    }

                    MaskAt(array[segment.Index], path, index + 1);
                    return;
                }

            case JsonArray array when !segment.IsIndex:
                {
                    // A secure path through an array of objects applies to every member.
                    foreach (var item in array)
                    {
                        MaskAt(item, path, index);
                    }

                    return;
                }
        }
    }
}
