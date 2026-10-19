using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Expressions;
using EArm.Expressions.Values;

namespace EArm.Engine.Edges;

/// <summary>
/// The collection step of the edge pass (<see cref="EdgePlanner"/>): re-evaluates the relationship properties of one declaring resource against the live
/// resolver and normalises every entry to an <see cref="EdgeDeclaration"/> of its row. Validation errors are attached to the row; nothing is read or written.
/// </summary>
internal sealed class EdgeDeclarationCollector
{
    private readonly DeploymentPlan _plan;
    private readonly EdgeGraphContext _context;
    private readonly EdgeKindRegistry _kinds;
    private readonly List<Diagnostic> _diagnostics;

    /// <param name="diagnostics">The edge plan's diagnostics, where unsupported edge kinds are reported.</param>
    public EdgeDeclarationCollector(DeploymentPlan plan, EdgeGraphContext context, EdgeKindRegistry kinds, List<Diagnostic> diagnostics)
    {
        _plan = plan;
        _context = context;
        _kinds = kinds;
        _diagnostics = diagnostics;
    }

    public async Task<EdgeRow?> CollectAsync(ResourceInstance instance, CancellationToken cancellationToken)
    {
        if (instance.IsSymbolic || instance.IsIgnored)
        {
            return null;
        }

        var specs = instance.Definition.Relationships.Where(s => EdgeKinds.TryFromRelationship(s.Kind, out _)).ToList();
        if (specs.Count == 0)
        {
            return null;
        }

        bool declaresAny = instance.Declaration.Properties is JsonObject raw && specs.Any(s => PropertyCopyBlock.Declares(raw, s.Property));
        if (!declaresAny)
        {
            return null;
        }

        var row = new EdgeRow(instance) { SelfId = GraphIdOf(instance) };
        var evaluated = new JsonObject();
        try
        {
            evaluated = Evaluate(instance);
        }
        catch (ExpressionException ex)
        {
            row.Errors.Add($"{instance.Describe()}: {ex.MessageWithOffset}");
        }

        foreach (var spec in specs)
        {
            EdgeKinds.TryFromRelationship(spec.Kind, out var kind);
            bool declared = JsonObjects.TryGetIgnoreCase(evaluated, spec.Property, out var value);
            if (!declared)
            {
                continue;
            }

            if (!_kinds.TryGet(kind, out var handler))
            {
                _diagnostics.Add(Diagnostic.Warning(EngineDiagnosticCodes.EdgeKindNotSupported, $"{instance.Describe()}: properties.{spec.Property} ({kind}) is not applied by this version of the executor; the property was ignored.", instance.Declaration.Path.Append("properties").Append(spec.Property)));
                continue;
            }

            var property = new EdgePropertyPlan(spec, handler, isDeclared: true);
            row.Properties[spec.Property] = property;

            var basePath = JsonPath.Of("properties", spec.Property);
            if (handler.IsSingleValued)
            {
                var declaration = new EdgeDeclaration(instance, spec, kind, -1, basePath) { SelfId = row.SelfId };
                var item = await handler.ResolveItemAsync(value, declaration.Side, instance, instance.Api, _context, cancellationToken).ConfigureAwait(false);
                if (Apply(declaration, item, row, property))
                {
                    property.Declarations.Add(declaration);
                }

                continue;
            }

            if (EdgeReads.IsNull(value))
            {
                // null ≡ absent: the property is not declared this run.
                property.IsDeclared = false;
                continue;
            }

            if (UnknownJson.IsUnknown(value, out var unknownArray))
            {
                property.HasUnknown = true;
                property.Declarations.Add(new EdgeDeclaration(instance, spec, kind, 0, basePath) { SelfId = row.SelfId, IsUnknown = true, Symbol = unknownArray.Symbol });
                continue;
            }

            if (value is not JsonArray array)
            {
                row.Errors.Add($"{instance.Describe()}: properties.{spec.Property} must be an array of object ids, got {JsonValues.Describe(value)}.");
                property.IsDeclared = false;
                continue;
            }

            if (array.Count == 0 && spec.IsAuthoritativeSet && _plan.Mode == DeploymentMode.Incremental)
            {
                // Incremental mode: an empty authoritative list declares nothing (no adds, no removals). Only Complete mode empties the set.
                property.IsDeclared = false;
                _diagnostics.Add(Diagnostic.Info(EngineDiagnosticCodes.EmptyEdgeListIgnored, $"{instance.Describe()}: properties.{spec.Property} is an empty array, which declares nothing in Incremental mode: no entry is added or removed. An empty list removes every entry only in Complete mode.", instance.Declaration.Path.Append("properties").Append(spec.Property)));
                continue;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < array.Count; i++)
            {
                var declaration = new EdgeDeclaration(instance, spec, kind, i, basePath.Append(i)) { SelfId = row.SelfId };
                var item = await handler.ResolveItemAsync(array[i], declaration.Side, instance, instance.Api, _context, cancellationToken).ConfigureAwait(false);
                if (!Apply(declaration, item, row, property))
                {
                    continue;
                }

                if (declaration.OtherId is not null && !seen.Add(Edge.NormalizeId(declaration.OtherId)))
                {
                    // The same id twice in one array: one edge.
                    continue;
                }

                property.Declarations.Add(declaration);
            }
        }

        return row;
    }

    private static bool Apply(EdgeDeclaration declaration, EdgeItem item, EdgeRow row, EdgePropertyPlan property)
    {
        if (item.Error is not null)
        {
            row.Errors.Add($"{declaration.Describe()}: {item.Error}");
            return false;
        }

        declaration.OtherId = item.OtherId;
        declaration.TargetCollection = item.TargetCollection;
        declaration.Extra = item.Extra;
        declaration.IsUnknown = item.IsUnknown;
        declaration.Symbol = item.Symbol;
        declaration.IsClear = item.IsClear;
        if (item.IsUnknown)
        {
            property.HasUnknown = true;
        }

        return true;
    }

    /// <summary>Evaluates <c>properties</c> against the live resolver and keeps the relationship subset on the instance.</summary>
    private JsonObject Evaluate(ResourceInstance instance)
    {
        var evaluated = _plan.Evaluator.EvaluateTree(instance.Declaration.Properties, instance.Context) as JsonObject ?? new JsonObject();
        var edges = new JsonObject();
        foreach (var property in evaluated.ToList())
        {
            if (instance.Definition.IsRelationshipProperty(property.Key))
            {
                evaluated.Remove(property.Key);
                edges[property.Key] = property.Value;
            }
        }

        instance.EdgeProperties = (JsonObject)JsonValues.Clone(edges)!;
        return edges;
    }

    private string? GraphIdOf(ResourceInstance instance)
    {
        if (_plan.Resolver.TryGetGraphId(instance.Id, out var graphId) || _plan.Resolver.TryGetGraphId(instance.PlannedId, out graphId))
        {
            return graphId;
        }

        return null;
    }
}
