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

/// <summary>One relationship property of a declaring resource: its declarations and the what-if children.</summary>
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

    /// <summary>False when the property evaluates to <c>null</c> (absent): nothing is declared for it this run.</summary>
    public bool IsDeclared { get; set; }

    /// <summary>
    /// True when the property was left alone with a warning (<c>EdgeUnsupportedInAppMode</c>, <c>ManagerReadForbidden</c>): no
    /// operations are planned for it.
    /// </summary>
    public bool IsSkipped { get; set; }

    public List<EdgeDeclaration> Declarations { get; } = [];

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
}
