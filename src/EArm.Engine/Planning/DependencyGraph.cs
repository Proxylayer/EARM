using System;
using System.Collections.Generic;
using System.Linq;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Types;
using EArm.Engine.Planning.Nested;
using EArm.Engine.Providers;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Static;

namespace EArm.Engine.Planning;

/// <summary>
/// The object-pass dependency graph: explicit <c>dependsOn</c> ∪ inferred parent ∪
/// implicit <c>reference()</c>/<c>resourceId()</c> occurrences outside the edge properties ∪ the
/// inputs of symbolic names ∪ serial copy chaining. Cycles are reported with their path; the acyclic graph is
/// layered Kahn-style into <see cref="Batches"/> (level = 1 + the highest level among the dependencies), each
/// batch ordered by apply phase (see <see cref="ApplyPhases"/>) then declaration order as a tie-breaker.
/// </summary>
public sealed partial class DependencyGraph
{
    private readonly Dictionary<ResourceInstance, List<ResourceInstance>> _dependencies;
    private readonly Dictionary<ResourceInstance, List<ResourceInstance>> _dependents;

    private DependencyGraph(IReadOnlyList<ResourceInstance> instances, IReadOnlyList<DependencyEdge> edges, IReadOnlyList<DeploymentBatch> batches)
    {
        Instances = instances;
        Edges = edges;
        Batches = batches;
        _dependencies = instances.ToDictionary(i => i, _ => new List<ResourceInstance>());
        _dependents = instances.ToDictionary(i => i, _ => new List<ResourceInstance>());
        foreach (var edge in edges)
        {
            _dependencies[edge.Dependent].Add(edge.Dependency);
            _dependents[edge.Dependency].Add(edge.Dependent);
        }
    }

    public IReadOnlyList<ResourceInstance> Instances { get; }

    public IReadOnlyList<DependencyEdge> Edges { get; }

    /// <summary>Empty when the graph has a cycle (reported as a diagnostic).</summary>
    public IReadOnlyList<DeploymentBatch> Batches { get; }

    public IReadOnlyList<ResourceInstance> DependenciesOf(ResourceInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return _dependencies.TryGetValue(instance, out var list) ? list : [];
    }

    public IReadOnlyList<ResourceInstance> DependentsOf(ResourceInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return _dependents.TryGetValue(instance, out var list) ? list : [];
    }

    /// <summary>Every instance that (transitively) depends on <paramref name="instance"/>.</summary>
    public IReadOnlySet<ResourceInstance> TransitiveDependents(ResourceInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var result = new HashSet<ResourceInstance>();
        var queue = new Queue<ResourceInstance>();
        queue.Enqueue(instance);
        while (queue.Count > 0)
        {
            foreach (var dependent in DependentsOf(queue.Dequeue()))
            {
                if (result.Add(dependent))
                {
                    queue.Enqueue(dependent);
                }
            }
        }

        return result;
    }

    /// <summary>Builds the graph; cycles and unresolvable targets are appended to <paramref name="diagnostics"/> as errors.</summary>
    /// <param name="instances">The expanded instances.</param>
    /// <param name="staticContext">The static analysis of the template.</param>
    /// <param name="evaluator">The evaluator the per-iteration reference scans use.</param>
    /// <param name="catalog">The type catalog.</param>
    /// <param name="diagnostics">Where cycles and unresolvable targets are reported.</param>
    /// <param name="applyOrder">
    /// The provider-declared apply order (<see cref="IApplyOrderSource"/>), asked once every reference edge is known: for an instance, the instances that
    /// must be applied after it. An order that would close a cycle with the template's own dependencies is left out (the template's order wins).
    /// </param>
    public static DependencyGraph Build(IReadOnlyList<ResourceInstance> instances, StaticContext staticContext, ExpressionEvaluator evaluator, ResourceTypeCatalog catalog, List<Diagnostic> diagnostics, Func<ResourceInstance, IReadOnlyList<ResourceInstance>, IEnumerable<ResourceInstance>>? applyOrder = null)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(staticContext);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var builder = new Builder(instances, staticContext, evaluator, catalog, diagnostics);
        return builder.Build(applyOrder);
    }

    private sealed partial class Builder
    {
        private readonly IReadOnlyList<ResourceInstance> _instances;
        private readonly StaticContext _static;
        private readonly ExpressionEvaluator _evaluator;
        private readonly ResourceTypeCatalog _catalog;
        private readonly List<Diagnostic> _diagnostics;
        private readonly TemplateDocument _document;
        private readonly Dictionary<ResourceIdentifier, ResourceInstance> _byPlannedId = [];
        private readonly List<DependencyEdge> _edges = [];
        private readonly HashSet<(ResourceInstance, ResourceInstance)> _edgeSet = [];
        private readonly HashSet<(string Code, JsonPath? Path)> _reported;

        /// <summary>The instance whose edges are being added: diagnostics point into its template, and names resolve within it.</summary>
        private ResourceInstance? _current;

        public Builder(IReadOnlyList<ResourceInstance> instances, StaticContext staticContext, ExpressionEvaluator evaluator, ResourceTypeCatalog catalog, List<Diagnostic> diagnostics)
        {
            _instances = instances;
            _static = staticContext;
            _evaluator = evaluator;
            _catalog = catalog;
            _diagnostics = diagnostics;
            _document = staticContext.Document;
            _reported = diagnostics.Select(d => (d.Code, d.Path)).ToHashSet();
            foreach (var instance in instances)
            {
                _byPlannedId[instance.PlannedId] = instance;
            }
        }

        public DependencyGraph Build(Func<ResourceInstance, IReadOnlyList<ResourceInstance>, IEnumerable<ResourceInstance>>? applyOrder)
        {
            foreach (var instance in _instances)
            {
                _current = instance;
                AddDeclaredDependencies(instance);
                AddParent(instance);
                AddReferences(instance);
                AddNameReferences(instance);
                AddSerialCopy(instance);
            }

            // Nested deployments: a deployment waits for every resource of its template; those wait for what the deployment depends on.
            foreach (var (dependent, dependency, detail) in NestedDeploymentEdges.Compute(_instances, DependenciesNow))
            {
                _current = dependent;
                AddEdge(dependent, dependency, DependencyKind.NestedDeployment, detail, dependent.Declaration.Path);
            }

            _current = null;
            if (applyOrder is not null)
            {
                AddApplyOrder(applyOrder);
            }

            var batches = Layer();
            return new DependencyGraph(_instances, _edges, batches);
        }

        // ---- edges ----

        /// <summary>The instances <paramref name="instance"/> waits for so far.</summary>
        private IReadOnlyList<ResourceInstance> DependenciesNow(ResourceInstance instance)
        {
            return _edges.Where(e => ReferenceEquals(e.Dependent, instance)).Select(e => e.Dependency).ToList();
        }

        /// <summary>The static analysis of the template <paramref name="instance"/> belongs to.</summary>
        private StaticContext StaticOf(ResourceInstance instance)
        {
            return instance.Scope?.Static ?? _static;
        }

        /// <summary>True when <paramref name="instance"/> is declared in the same template as the instance being processed (names resolve within a template).</summary>
        private bool InCurrentTemplate(ResourceInstance instance)
        {
            return _current is null || ReferenceEquals(instance.Scope, _current.Scope);
        }


        private void AddParent(ResourceInstance instance)
        {
            if (instance.Parent is { } parent)
            {
                AddEdge(instance, parent, DependencyKind.Parent, parent.Id.Id, instance.Declaration.Path);
            }
        }

        private void AddReferences(ResourceInstance instance)
        {
            var declaration = instance.Declaration;
            IReadOnlyList<ReferenceOccurrence> occurrences;
            if (instance.Copy is null)
            {
                occurrences = instance.Resolved.References;
            }
            else
            {
                // Copy iterations: the targets depend on copyIndex(), so scan again with the concrete frame.
                var scan = ReferenceScanner.Scan(_evaluator, declaration.Properties, StaticOf(instance), instance.Context, declaration.Path.Append("properties"));
                foreach (var diagnostic in scan.Diagnostics)
                {
                    Report(diagnostic);
                }

                occurrences = scan.Occurrences;
            }

            var objectReferences = new List<ReferenceOccurrence>();
            var edgeReferences = new List<ReferenceOccurrence>();
            var existingReferences = new List<ReferenceOccurrence>();
            int propertyDepth = declaration.Path.Depth + 1;
            foreach (var occurrence in occurrences)
            {
                if (occurrence.Kind == ReferenceOccurrenceKind.ExistingReference)
                {
                    existingReferences.Add(occurrence);
                    continue;
                }

                if (!occurrence.IsSameTemplate || occurrence.Target is null)
                {
                    continue;
                }

                bool isEdge = occurrence.Path.Depth > propertyDepth
                    && !occurrence.Path.Segments[propertyDepth].IsIndex
                    && instance.Definition.IsRelationshipProperty(occurrence.Path.Segments[propertyDepth].Name!);
                if (isEdge)
                {
                    edgeReferences.Add(occurrence);
                    continue;
                }

                objectReferences.Add(occurrence);
                if (_byPlannedId.TryGetValue(occurrence.Target, out var target))
                {
                    AddEdge(instance, target, DependencyKind.Reference, occurrence.ExpressionText, occurrence.Path);
                }
            }

            instance.ObjectReferences = objectReferences;
            instance.EdgeReferences = edgeReferences;
            instance.ExistingReferences = existingReferences;
        }

        private void AddNameReferences(ResourceInstance instance)
        {
            var declaration = instance.Declaration;
            if (declaration.Name is null || !ExpressionSyntax.IsExpression(declaration.Name))
            {
                return;
            }

            var occurrences = new List<ReferenceOccurrence>();
            var scanDiagnostics = new List<Diagnostic>();
            var path = declaration.Path.Append("name");
            ReferenceScanner.ScanExpression(_evaluator, declaration.Name, StaticOf(instance), instance.Context, path, occurrences, scanDiagnostics);
            foreach (var diagnostic in scanDiagnostics)
            {
                Report(diagnostic);
            }

            instance.NameReferences = occurrences;
            foreach (var occurrence in occurrences)
            {
                if (occurrence.IsSameTemplate && occurrence.Target is not null && _byPlannedId.TryGetValue(occurrence.Target, out var target))
                {
                    AddEdge(instance, target, DependencyKind.SymbolicName, occurrence.ExpressionText, path);
                }
            }
        }

        private void AddSerialCopy(ResourceInstance instance)
        {
            if (instance.Copy is not { Mode: CopyMode.Serial } copy || copy.Chunk == 0)
            {
                return;
            }

            foreach (var other in _instances)
            {
                if (ReferenceEquals(other.Declaration, instance.Declaration) && other.Copy is { } otherCopy && otherCopy.Chunk == copy.Chunk - 1)
                {
                    AddEdge(instance, other, DependencyKind.CopySerial, $"serial copy '{copy.Name}' chunk {copy.Chunk}", instance.Declaration.Path.Append("copy"));
                }
            }
        }

        /// <summary>The provider-declared order: every instance it names waits for the instance that names it, unless the template orders them the other way.</summary>
        private void AddApplyOrder(Func<ResourceInstance, IReadOnlyList<ResourceInstance>, IEnumerable<ResourceInstance>> applyOrder)
        {
            foreach (var instance in _instances)
            {
                if (instance.IsIgnored)
                {
                    continue;
                }

                foreach (var later in applyOrder(instance, _instances).ToList())
                {
                    if (ReferenceEquals(later, instance) || later.IsIgnored || _edgeSet.Contains((later, instance)) || DependsOn(instance, later))
                    {
                        continue;
                    }

                    AddEdge(later, instance, DependencyKind.Ordering, $"{later.Describe()} is applied after {instance.Describe()}", later.Declaration.Path);
                }
            }
        }

        /// <summary>True when <paramref name="dependent"/> already waits (transitively) for <paramref name="dependency"/>.</summary>
        private bool DependsOn(ResourceInstance dependent, ResourceInstance dependency)
        {
            var seen = new HashSet<ResourceInstance>();
            var stack = new Stack<ResourceInstance>();
            stack.Push(dependent);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                foreach (var edge in _edges)
                {
                    if (ReferenceEquals(edge.Dependent, current) && seen.Add(edge.Dependency))
                    {
                        if (ReferenceEquals(edge.Dependency, dependency))
                        {
                            return true;
                        }

                        stack.Push(edge.Dependency);
                    }
                }
            }

            return false;
        }

        private void AddEdge(ResourceInstance dependent, ResourceInstance dependency, DependencyKind kind, string? detail, JsonPath path)
        {
            if (ReferenceEquals(dependent, dependency))
            {
                if (kind is DependencyKind.CopyLoop or DependencyKind.Parent)
                {
                    return;
                }

                Error(EngineDiagnosticCodes.DependencyCycle, $"{dependent.Describe()} depends on itself ({detail}).", path);
                return;
            }

            if (_edgeSet.Add((dependent, dependency)))
            {
                _edges.Add(new DependencyEdge(dependent, dependency, kind, detail));
                dependent.AddDependency(dependency.PlannedId);
            }
        }

        // ---- layering ----

        private IReadOnlyList<DeploymentBatch> Layer()
        {
            if (DependencyLayering.FindCycle(_instances, _edges) is { } cycle)
            {
                string rendered = string.Join(" -> ", cycle.Select(i => i.Id.Id));
                _current = cycle[0];
                Error(EngineDiagnosticCodes.DependencyCycle, $"The resources depend on each other in a cycle: {rendered}. Break the cycle by removing a dependsOn entry or moving a reference() into a relationship property, which is applied after all objects exist.", cycle[0].Declaration.Path);
                return [];
            }

            return DependencyLayering.Batches(_instances, _edges);
        }

        // ---- diagnostics ----

        private void Error(string code, string message, JsonPath path)
        {
            // A path of a nested template is reported within the file it sits in (TemplateScope.PathPrefix: the inline template's place in its file).
            var scope = _current?.Scope;
            var document = scope?.Document ?? _document;
            var reported = scope is null ? path : scope.PathPrefix.Append(path);
            Report(Diagnostic.Error(code, message, reported, document.LocationOf(path), document.SourcePath));
        }

        private void Report(Diagnostic diagnostic)
        {
            if (_reported.Add((diagnostic.Code, diagnostic.Path)))
            {
                _diagnostics.Add(diagnostic);
            }
        }
    }
}
