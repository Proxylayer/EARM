using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Types;
using EArm.Engine.Providers;
using EArm.Expressions;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Static;
using EArm.Expressions.Values;

namespace EArm.Engine.Planning;

/// <summary>Why one instance waits for another.</summary>
public enum DependencyKind
{
    /// <summary>An entry of <c>dependsOn</c>.</summary>
    Explicit,

    /// <summary>The parent of a child resource (nested or <c>parent/child</c> form).</summary>
    Parent,

    /// <summary>A same-template <c>reference()</c> / <c>resourceId()</c> outside the edge properties (ARM implicit dependency).</summary>
    Reference,

    /// <summary>The resource's own name is computed from the target (<c>[reference('app').appId]</c>).</summary>
    SymbolicName,

    /// <summary><c>dependsOn</c> naming a copy loop: every iteration of the loop.</summary>
    CopyLoop,

    /// <summary>A <c>mode: serial</c> loop: an iteration waits for the previous chunk.</summary>
    CopySerial,
}

/// <summary><paramref name="Dependent"/> must be applied after <paramref name="Dependency"/>.</summary>
public sealed record DependencyEdge(ResourceInstance Dependent, ResourceInstance Dependency, DependencyKind Kind, string? Detail = null);

/// <summary>Instances that may be applied together once every earlier batch is done; ordered by phase then declaration order.</summary>
public sealed record DeploymentBatch(int Index, IReadOnlyList<ResourceInstance> Instances)
{
    public override string ToString()
    {
        return $"batch {Index}: {string.Join(", ", Instances.Select(i => i.Id.Id))}";
    }
}

/// <summary>
/// The object-pass dependency graph (plan section 2.4, step 4): explicit <c>dependsOn</c> ∪ inferred parent ∪
/// implicit <c>reference()</c>/<c>resourceId()</c> occurrences outside the edge properties of section 1.4 ∪ the
/// inputs of symbolic names ∪ serial copy chaining. Cycles are reported with their path; the acyclic graph is
/// layered Kahn-style into <see cref="Batches"/> (level = 1 + the highest level among the dependencies), each
/// batch ordered by apply phase (section 3.3) then declaration order as a tie-breaker.
/// </summary>
public sealed class DependencyGraph
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
    public static DependencyGraph Build(IReadOnlyList<ResourceInstance> instances, StaticContext staticContext, ExpressionEvaluator evaluator, ResourceTypeCatalog catalog, List<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(staticContext);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var builder = new Builder(instances, staticContext, evaluator, catalog, diagnostics);
        return builder.Build();
    }

    private sealed class Builder
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

        public DependencyGraph Build()
        {
            foreach (var instance in _instances)
            {
                AddDeclaredDependencies(instance);
                AddParent(instance);
                AddReferences(instance);
                AddNameReferences(instance);
                AddSerialCopy(instance);
            }

            var batches = Layer();
            return new DependencyGraph(_instances, _edges, batches);
        }

        // ---- edges ----

        private void AddDeclaredDependencies(ResourceInstance instance)
        {
            var declaration = instance.Declaration;
            for (int i = 0; i < instance.Resolved.DependsOn.Count; i++)
            {
                var dependency = instance.Resolved.DependsOn[i];
                var kind = dependency.Source == DependencySource.Inferred ? DependencyKind.Parent : DependencyKind.Explicit;
                var path = dependency.Source == DependencySource.Inferred ? declaration.Path : declaration.Path.Append("dependsOn").Append(i);

                if (dependency.Target is { } target)
                {
                    if (_byPlannedId.TryGetValue(target, out var targetInstance))
                    {
                        AddEdge(instance, targetInstance, kind, dependency.Text, path);
                    }
                    else
                    {
                        Error(DiagnosticCodes.DependsOnTargetNotFound, $"dependsOn entry '{dependency.Text}' resolves to '{target.Id}', which no resource instance produces after expansion.", path);
                    }

                    continue;
                }

                if (dependency.CopyName is { } copyName)
                {
                    foreach (var iteration in LoopInstances(copyName))
                    {
                        if (!ReferenceEquals(iteration, instance))
                        {
                            AddEdge(instance, iteration, DependencyKind.CopyLoop, dependency.Text, path);
                        }
                    }

                    continue;
                }

                if (dependency.Deferred)
                {
                    foreach (var target2 in ResolveDeferred(instance, dependency.Text, path))
                    {
                        AddEdge(instance, target2, kind, dependency.Text, path);
                    }
                }
            }
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
                var scan = ReferenceScanner.Scan(_evaluator, declaration.Properties, _static, instance.Context, declaration.Path.Append("properties"));
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
            ReferenceScanner.ScanExpression(_evaluator, declaration.Name, _static, instance.Context, path, occurrences, scanDiagnostics);
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

        private IEnumerable<ResourceInstance> LoopInstances(string copyName)
        {
            return _instances.Where(i => i.Copy is not null && i.Copy.Name.Equals(copyName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Resolves a <c>dependsOn</c> entry that depends on <c>copyIndex()</c>, now with the concrete frame.</summary>
        private IEnumerable<ResourceInstance> ResolveDeferred(ResourceInstance instance, string text, JsonPath path)
        {
            JsonNode? value;
            try
            {
                value = _evaluator.EvaluateString(text, instance.Context);
            }
            catch (ExpressionException ex)
            {
                Error(ex.Code, ex.MessageWithOffset, path);
                return [];
            }

            if (UnknownJson.IsUnknown(value, out var unknown))
            {
                if (unknown.ResourceId is { } symbolic && _byPlannedId.TryGetValue(symbolic, out var symbolicTarget))
                {
                    return [symbolicTarget];
                }

                Error(DiagnosticCodes.DependsOnTargetNotFound, $"dependsOn entry '{text}' does not resolve to a resource of this template (its value is only known at deployment: {unknown.Symbol}).", path);
                return [];
            }

            if (!JsonValues.TryGetString(value, out var target))
            {
                Error(DiagnosticCodes.DependsOnTargetNotFound, $"dependsOn entry '{text}' must evaluate to a string, got {JsonValues.Describe(value)}.", path);
                return [];
            }

            ResourceIdentifier? id = null;
            if (target.StartsWith(ResourceIdentifier.ProvidersPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (!_catalog.TryParseResourceId(target, out id) || id is null)
                {
                    Error(DiagnosticCodes.InvalidResourceId, $"dependsOn entry '{target}' is not a valid resource id.", path);
                    return [];
                }
            }
            else if (!TryParseTypeAndName(target, out id))
            {
                var byName = _instances.Where(i => !i.IsSymbolic && string.Equals(i.Id.Name, target, StringComparison.Ordinal)).ToList();
                if (byName.Count == 0)
                {
                    byName = _instances.Where(i => !i.IsSymbolic && string.Equals(i.Id.Name, target, StringComparison.OrdinalIgnoreCase)).ToList();
                }

                if (byName.Count == 1)
                {
                    return byName;
                }

                if (byName.Count > 1)
                {
                    Error(DiagnosticCodes.AmbiguousDependsOn, $"dependsOn entry '{target}' names {byName.Count} resources; use a resource id or 'type/name'.", path);
                    return [];
                }

                var loop = LoopInstances(target).Where(i => !ReferenceEquals(i, instance)).ToList();
                if (loop.Count > 0)
                {
                    return loop;
                }

                Error(DiagnosticCodes.DependsOnTargetNotFound, $"dependsOn entry '{target}' does not name a resource of this template.", path);
                return [];
            }

            if (id is not null && _byPlannedId.TryGetValue(id, out var found))
            {
                return [found];
            }

            Error(DiagnosticCodes.DependsOnTargetNotFound, $"dependsOn entry '{target}' does not name a resource of this template.", path);
            return [];
        }

        private bool TryParseTypeAndName(string text, out ResourceIdentifier? identifier)
        {
            identifier = null;
            if (!text.StartsWith(ResourceIdentifier.ProviderNamespace + "/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            foreach (var definition in _catalog.Types.OrderByDescending(d => d.Type.Length))
            {
                string prefix = definition.Type + "/";
                if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var segments = text[prefix.Length..].Split('/');
                if (segments.Length == definition.Depth && !segments.Any(string.IsNullOrEmpty))
                {
                    identifier = new ResourceIdentifier(definition.Type, segments);
                    return true;
                }
            }

            return false;
        }

        // ---- layering ----

        private IReadOnlyList<DeploymentBatch> Layer()
        {
            if (FindCycle() is { } cycle)
            {
                string rendered = string.Join(" -> ", cycle.Select(i => i.Id.Id));
                Error(EngineDiagnosticCodes.DependencyCycle, $"The resources depend on each other in a cycle: {rendered}. Break the cycle by removing a dependsOn entry or moving a reference() into a relationship property, which is applied after all objects exist.", cycle[0].Declaration.Path);
                return [];
            }

            var dependencies = _instances.ToDictionary(i => i, _ => new List<ResourceInstance>());
            var dependents = _instances.ToDictionary(i => i, _ => new List<ResourceInstance>());
            foreach (var edge in _edges)
            {
                dependencies[edge.Dependent].Add(edge.Dependency);
                dependents[edge.Dependency].Add(edge.Dependent);
            }

            var remaining = _instances.ToDictionary(i => i, i => dependencies[i].Count);
            var level = new Dictionary<ResourceInstance, int>();
            var queue = new Queue<ResourceInstance>(_instances.Where(i => remaining[i] == 0));
            foreach (var root in queue)
            {
                level[root] = 1;
            }

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var dependent in dependents[current])
                {
                    level[dependent] = Math.Max(level.GetValueOrDefault(dependent), level[current] + 1);
                    if (--remaining[dependent] == 0)
                    {
                        queue.Enqueue(dependent);
                    }
                }
            }

            var batches = new List<DeploymentBatch>();
            foreach (var group in _instances.GroupBy(i => level[i]).OrderBy(g => g.Key))
            {
                var ordered = group
                    .OrderBy(i => i.Phase)
                    .ThenBy(i => i.DeclarationIndex)
                    .ThenBy(i => i.Copy?.Index ?? 0)
                    .ToList();
                batches.Add(new DeploymentBatch(batches.Count + 1, ordered));
            }

            return batches;
        }

        /// <summary>Depth-first search for a back edge; returns the cycle path (first node repeated at the end) or <c>null</c>.</summary>
        private List<ResourceInstance>? FindCycle()
        {
            var dependencies = _instances.ToDictionary(i => i, _ => new List<ResourceInstance>());
            foreach (var edge in _edges)
            {
                dependencies[edge.Dependent].Add(edge.Dependency);
            }

            var state = new Dictionary<ResourceInstance, int>();
            var stack = new List<ResourceInstance>();
            foreach (var start in _instances)
            {
                if (state.GetValueOrDefault(start) != 0)
                {
                    continue;
                }

                if (Visit(start, dependencies, state, stack) is { } cycle)
                {
                    return cycle;
                }
            }

            return null;
        }

        private static List<ResourceInstance>? Visit(ResourceInstance node, Dictionary<ResourceInstance, List<ResourceInstance>> dependencies, Dictionary<ResourceInstance, int> state, List<ResourceInstance> stack)
        {
            state[node] = 1;
            stack.Add(node);
            foreach (var next in dependencies[node])
            {
                int nextState = state.GetValueOrDefault(next);
                if (nextState == 1)
                {
                    int start = stack.IndexOf(next);
                    var cycle = stack.Skip(start).ToList();
                    cycle.Add(next);
                    return cycle;
                }

                if (nextState == 0 && Visit(next, dependencies, state, stack) is { } found)
                {
                    return found;
                }
            }

            stack.RemoveAt(stack.Count - 1);
            state[node] = 2;
            return null;
        }

        // ---- diagnostics ----

        private void Error(string code, string message, JsonPath path)
        {
            Report(Diagnostic.Error(code, message, path, _document.LocationOf(path), _document.SourcePath));
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
