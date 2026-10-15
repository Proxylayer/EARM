using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Graph;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Types;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Engine.Edges;

public enum EdgeAction
{
    Add,
    Remove,
}

/// <summary>One edge write the pass will perform, attributed to the rows (declaring resources) that asked for it.</summary>
public sealed class EdgeOperation
{
    public EdgeOperation(Edge edge, EdgeAction action, IEdgeKindHandler handler, GraphApi api, RetryHint hint, JsonObject? liveExtra)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentNullException.ThrowIfNull(handler);
        Edge = edge;
        Action = action;
        Handler = handler;
        Api = api;
        Hint = hint;
        LiveExtra = liveExtra;
    }

    public Edge Edge { get; }

    public EdgeAction Action { get; }

    public IEdgeKindHandler Handler { get; }

    public GraphApi Api { get; }

    /// <summary><see cref="RetryHint.ExpectReplicationLag"/> when either side was created in this run.</summary>
    public RetryHint Hint { get; }

    /// <summary>What the live read returned for the edge (assignment ids); <c>null</c> for adds.</summary>
    public JsonObject? LiveExtra { get; }

    public string Identity => Handler.IdentityOf(Edge);

    /// <summary>The declaring rows this operation is attributed to (failure and what-if reporting).</summary>
    public List<EdgeRow> Rows { get; } = [];

    /// <summary>A warning attached to the operation (removing the signed-in principal from owners).</summary>
    public string? Warning { get; set; }

    public BatchRequest ToRequest()
    {
        return Action == EdgeAction.Add ? Handler.AddRequest(Edge, Api) : Handler.RemoveRequest(Edge, LiveExtra, Api);
    }

    public string Describe()
    {
        return $"{(Action == EdgeAction.Add ? "add" : "remove")} {Handler.Describe(Edge)}";
    }
}

/// <summary>One relationship property of a declaring resource: its declarations, the previously recorded edges and the what-if children.</summary>
public sealed class EdgePropertyPlan
{
    public EdgePropertyPlan(RelationshipSpec spec, IEdgeKindHandler handler, bool isDeclared)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(handler);
        Spec = spec;
        Handler = handler;
        IsDeclared = isDeclared;
    }

    public RelationshipSpec Spec { get; }

    public IEdgeKindHandler Handler { get; }

    public string Name => Spec.Property;

    /// <summary>False when the template does not declare the property this run (only state records exist).</summary>
    public bool IsDeclared { get; set; }

    /// <summary>
    /// True when the property was left alone with a warning (<c>EdgeUnsupportedInAppMode</c>, <c>ManagerReadForbidden</c>): no
    /// operations are planned for it and the state keeps its previous record.
    /// </summary>
    public bool IsSkipped { get; set; }

    public List<EdgeDeclaration> Declarations { get; } = [];

    /// <summary>The raw records of the state entry's <c>relationships[property]</c>.</summary>
    public List<JsonNode?> PreviousRecords { get; } = [];

    /// <summary>The records that could be interpreted as edges (for removal and for "created by this deployment" checks).</summary>
    public List<Edge> PreviouslyRecorded { get; } = [];

    /// <summary>The identities of previously recorded edges the planner saw are no longer live (nothing to remove, nothing left to record).</summary>
    public HashSet<string> GoneRecorded { get; } = new(StringComparer.Ordinal);

    /// <summary>Per-entry children of the what-if <c>Array</c> delta (multi-valued kinds).</summary>
    public List<PropertyDelta> Children { get; } = [];

    /// <summary>The delta of a single-valued kind (<c>manager</c>).</summary>
    public PropertyDelta? SingleDelta { get; set; }

    public bool HasUnknown { get; set; }
}

/// <summary>Everything the edge pass computed for one declaring resource.</summary>
public sealed class EdgeRow
{
    public EdgeRow(ResourceInstance declarer)
    {
        ArgumentNullException.ThrowIfNull(declarer);
        Declarer = declarer;
    }

    public ResourceInstance Declarer { get; }

    /// <summary>The declarer's Graph id; <c>null</c> when it does not exist yet (what-if create).</summary>
    public string? SelfId { get; set; }

    public Dictionary<string, EdgePropertyPlan> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Validation errors (dynamic targets, invalid items, unresolved placeholders at create time).</summary>
    public List<string> Errors { get; } = [];

    public List<string> Warnings { get; } = [];

    public bool HasErrors => Errors.Count > 0;

    public bool HasUnknown => Properties.Values.Any(p => p.HasUnknown);

    /// <summary>True when the row has principal-side properties whose edges the state records.</summary>
    public bool RecordsEdges => Properties.Values.Any(p => p.Spec.Side == RelationshipSide.Principal);

    /// <summary>The what-if deltas: one <c>properties.&lt;property&gt;</c> row per property with changes.</summary>
    public IReadOnlyList<PropertyDelta> Deltas
    {
        get
        {
            var deltas = new List<PropertyDelta>();
            foreach (var property in Properties.Values)
            {
                if (property.Handler.IsSingleValued)
                {
                    if (property.SingleDelta is { } single)
                    {
                        deltas.Add(single);
                    }

                    continue;
                }

                if (property.Children.Count == 0)
                {
                    continue;
                }

                var children = new List<PropertyDelta>(property.Children.Count);
                for (int i = 0; i < property.Children.Count; i++)
                {
                    children.Add(property.Children[i] with { Path = JsonPath.Root.Append(i) });
                }

                deltas.Add(new PropertyDelta(JsonPath.Of("properties", property.Name), PropertyChangeType.Array, null, null, children.Any(c => c.IsUnknown), false, children));
            }

            return deltas;
        }
    }

    /// <summary>A <c>Create</c> child for a declared entry (<c>(deploy-time)</c> while unknown); re-pathed when the row is rendered.</summary>
    public static PropertyDelta CreateChild(EdgeDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        JsonNode after = declaration.IsUnknown ? JsonValue.Create(UnknownValue.Display) : JsonValue.Create(declaration.OtherId!);
        return new PropertyDelta(JsonPath.Root, PropertyChangeType.Create, null, after, declaration.IsUnknown);
    }

    public static PropertyDelta DeleteChild(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return new PropertyDelta(JsonPath.Root, PropertyChangeType.Delete, JsonValue.Create(id), null);
    }
}

/// <summary>The outcome of planning the edges of a set of declaring resources.</summary>
public sealed class EdgePlan
{
    private readonly Dictionary<ResourceInstance, EdgeRow> _rows;

    public EdgePlan(IReadOnlyList<EdgeRow> rows, IReadOnlyList<EdgeOperation> operations, IReadOnlyList<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(diagnostics);
        Rows = rows;
        Operations = operations;
        Diagnostics = diagnostics;
        _rows = rows.ToDictionary(r => r.Declarer, r => r);
    }

    public IReadOnlyList<EdgeRow> Rows { get; }

    /// <summary>Adds and removes, all kinds; the executor orders them (adds before removes, part A before part B).</summary>
    public IReadOnlyList<EdgeOperation> Operations { get; }

    /// <summary>Plan-level warnings (unsupported kinds, signed-in principal removals).</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public bool IsEmpty => Rows.Count == 0;

    public EdgeRow? RowFor(ResourceInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return _rows.TryGetValue(instance, out var row) ? row : null;
    }

    /// <summary>
    /// What the state should record for a principal-side row after the pass: per property, the declared edges this
    /// deployment created (now, per <paramref name="result"/>, or in an earlier run, per the previous records).
    /// A single-valued property that is not declared keeps its previous record (absent = untouched).
    /// <c>null</c> when nothing is recorded.
    /// </summary>
    public static JsonObject? RecordingsFor(EdgeRow row, EdgeApplyResult result)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(result);

        var recorded = new JsonObject();
        foreach (var property in row.Properties.Values)
        {
            if (property.Spec.Side != RelationshipSide.Principal)
            {
                continue;
            }

            var handler = property.Handler;
            if (property.IsSkipped)
            {
                // Nothing was read or written for it: the previous record stays as it was.
                if (property.PreviousRecords.Count > 0)
                {
                    recorded[property.Name] = handler.IsSingleValued
                        ? property.PreviousRecords[0]?.DeepClone()
                        : new JsonArray([.. property.PreviousRecords.Select(r => r?.DeepClone())]);
                }

                continue;
            }

            if (!property.IsDeclared)
            {
                if (handler.IsSingleValued && property.PreviousRecords.Count > 0 && property.PreviousRecords[0] is { } kept)
                {
                    recorded[property.Name] = kept.DeepClone();
                }
                else if (!handler.IsSingleValued)
                {
                    // Not declared (any more): the edges whose removal failed, was not attempted, or whose declaration moved to the target side are still recorded.
                    var stillThere = new JsonArray();
                    foreach (var previous in property.PreviouslyRecorded)
                    {
                        if (StillLive(property, previous, result))
                        {
                            stillThere.Add(handler.RecordEntry(previous, null));
                        }
                    }

                    if (stillThere.Count > 0)
                    {
                        recorded[property.Name] = stillThere;
                    }
                }

                continue;
            }

            if (handler.IsSingleValued)
            {
                var declaration = property.Declarations.FirstOrDefault();
                if (declaration?.Edge is { } single && !declaration.IsClear && IsCreatedByDeployment(property, single, result))
                {
                    recorded[property.Name] = handler.RecordEntry(single, result.BodyOf(handler.IdentityOf(single)));
                }
                else if (property.PreviouslyRecorded.FirstOrDefault(p => StillLive(property, p, result)) is { } keptSingle && property.PreviousRecords.Count > 0)
                {
                    // The previous edge is still there (its removal failed, or the replacement was not added): it stays recorded.
                    recorded[property.Name] = property.PreviousRecords[0]?.DeepClone() ?? handler.RecordEntry(keptSingle, null);
                }

                continue;
            }

            var entries = new JsonArray();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var declaration in property.Declarations)
            {
                if (declaration.Edge is { } edge && IsCreatedByDeployment(property, edge, result) && identities.Add(handler.IdentityOf(edge)))
                {
                    entries.Add(handler.RecordEntry(edge, result.BodyOf(handler.IdentityOf(edge))));
                }
            }

            // A previously recorded edge that is not declared (any more) and was not removed stays recorded while it is live: its removal failed or was not
            // attempted, or its declaration moved to the other side (a target's members) - dropping the record would leave a managed edge nobody can remove later.
            for (int i = 0; i < property.PreviouslyRecorded.Count; i++)
            {
                var previous = property.PreviouslyRecorded[i];
                if (StillLive(property, previous, result) && identities.Add(handler.IdentityOf(previous)))
                {
                    entries.Add(handler.RecordEntry(previous, null));
                }
            }

            if (entries.Count > 0)
            {
                recorded[property.Name] = entries;
            }
        }

        return recorded.Count == 0 ? null : recorded;
    }

    private static bool StillLive(EdgePropertyPlan property, Edge previous, EdgeApplyResult result)
    {
        string identity = property.Handler.IdentityOf(previous);
        return !result.WasRemoved(identity) && !property.GoneRecorded.Contains(identity);
    }

    private static bool IsCreatedByDeployment(EdgePropertyPlan property, Edge edge, EdgeApplyResult result)
    {
        var handler = property.Handler;
        string identity = handler.IdentityOf(edge);
        if (result.WasAdded(identity))
        {
            return true;
        }

        // The same edge as a previous record: compared by the kind's identity (part B records carry the Graph
        // assignment id in their extras, which a declaration cannot know; the identity leaves it out).
        return property.PreviouslyRecorded.Any(previous => string.Equals(handler.IdentityOf(previous), identity, StringComparison.Ordinal));
    }
}
