using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Types;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Expressions;
using EArm.Expressions.Values;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Engine.Edges;

/// <summary>
/// The edge pass, planning half: re-evaluates the relationship properties of the
/// declaring resources against the live resolver (so <c>reference()</c> yields real ids), normalises every entry to
/// an <see cref="Edge"/>, de-duplicates declarations made from both sides, reads the live state and decides the
/// adds and removes:
/// <list type="bullet">
/// <item>a target-side property (<c>members</c>, <c>owners</c>) is the authoritative set for that target: live
/// edges not declared (on either side) are removed;</item>
/// <item>a principal-side property (<c>memberOf</c>, <c>ownerOf</c>, <c>administrativeUnits</c>) is ensure-only: it adds
/// the declared edges that are missing and never removes anything (undeclaring an edge leaves it in place; a target-side
/// declaration of the same target still prunes it);</item>
/// <item><c>manager</c> is single-valued: a value replaces, <c>null</c> removes, absent leaves it untouched.</item>
/// </list>
/// Validation errors (dynamic targets, invalid ids, placeholders at create time, conflicting extras) are attached
/// to the declaring row. Nothing is written here.
/// </summary>
public sealed class EdgePlanner
{
    private readonly DeploymentPlan _plan;
    private readonly EdgeGraphContext _context;
    private readonly EdgeKindRegistry _kinds;
    private readonly PlanMode _mode;
    private readonly IReadOnlySet<string> _createdGraphIds;
    private readonly IReadOnlyList<ResourceInstance> _unreliableDeclarers;
    private readonly Dictionary<string, EdgeTargetState> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EdgePrincipalState> _principals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EdgeOperation> _operations = new(StringComparer.Ordinal);
    private readonly Dictionary<ResourceInstance, EdgeRow> _rows = [];
    private readonly List<Diagnostic> _diagnostics = [];

    /// <param name="unreliableDeclarers">
    /// Resources that are declared (not ignored) but are not part of this pass because they failed, were skipped or could not be planned: their principal-side
    /// declarations (<c>memberOf</c>, <c>ownerOf</c>, <c>administrativeUnits</c>) are unknown, so a target-side set such a resource could belong to is never pruned
    /// (adds still happen). Without this, one failed principal under <c>--continue-on-error</c> would have its live memberships and ownerships removed.
    /// </param>
    public EdgePlanner(DeploymentPlan plan, EdgeGraphContext context, PlanMode mode, IReadOnlySet<string>? createdGraphIds = null, IReadOnlyList<ResourceInstance>? unreliableDeclarers = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);
        _plan = plan;
        _context = context;
        _kinds = plan.EdgeKinds;
        _mode = mode;
        _createdGraphIds = createdGraphIds ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _unreliableDeclarers = unreliableDeclarers ?? [];
    }

    /// <summary>True when the instance takes part in the edge pass: it declares a relationship property of an edge kind.</summary>
    public static bool DeclaresEdges(ResourceInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.Declaration.Properties is JsonObject properties)
        {
            foreach (var spec in instance.Definition.Relationships)
            {
                if (EdgeKinds.TryFromRelationship(spec.Kind, out _) && JsonObjects.TryGetIgnoreCase(properties, spec.Property, out _))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public async Task<EdgePlan> PlanAsync(IReadOnlyList<ResourceInstance> candidates, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var rows = new List<EdgeRow>();
        foreach (var instance in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = await CollectAsync(instance, cancellationToken).ConfigureAwait(false);
            if (row is not null)
            {
                rows.Add(row);
                _rows[instance] = row;
            }
        }

        var authoritative = BuildAuthoritativeSets(rows);
        foreach (var set in authoritative.Values)
        {
            await PlanAuthoritativeAsync(set, cancellationToken).ConfigureAwait(false);
        }

        foreach (var row in rows)
        {
            foreach (var property in row.Properties.Values.ToList())
            {
                if (property.Handler.IsSingleValued)
                {
                    await PlanSingleValuedAsync(row, property, cancellationToken).ConfigureAwait(false);
                }
                else if (property.Spec.Side == RelationshipSide.Principal)
                {
                    await PlanPrincipalSideAsync(row, property, cancellationToken).ConfigureAwait(false);
                }
                else if (row.SelfId is null)
                {
                    PlanTargetToBeCreated(property);
                }
            }
        }

        foreach (var row in rows)
        {
            FinishRow(row);
        }

        return new EdgePlan(rows, _operations.Values.ToList(), _diagnostics);
    }

    // ---- collection ----

    private async Task<EdgeRow?> CollectAsync(ResourceInstance instance, CancellationToken cancellationToken)
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

        bool declaresAny = instance.Declaration.Properties is JsonObject raw && specs.Any(s => JsonObjects.TryGetIgnoreCase(raw, s.Property, out _));
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

    // ---- authoritative sets (target side) ----

    private sealed class DesiredPrincipal
    {
        public DesiredPrincipal(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public List<EdgeDeclaration> Declarations { get; } = [];
    }

    private sealed class AuthoritativeSet
    {
        public AuthoritativeSet(EdgeRow row, EdgePropertyPlan property, string targetId, string? collection)
        {
            Row = row;
            Property = property;
            TargetId = targetId;
            Collection = collection;
        }

        public EdgeRow Row { get; }

        public EdgePropertyPlan Property { get; }

        public IEdgeKindHandler Handler => Property.Handler;

        public string TargetId { get; }

        public string? Collection { get; }

        /// <summary>Desired principals by normalised id: declared on the target, or on principals naming this target.</summary>
        public Dictionary<string, DesiredPrincipal> Desired { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string principalId, EdgeDeclaration declaration)
        {
            string key = Edge.NormalizeId(principalId);
            if (!Desired.TryGetValue(key, out var desired))
            {
                desired = new DesiredPrincipal(principalId);
                Desired[key] = desired;
            }

            desired.Declarations.Add(declaration);
        }
    }

    private static string SetKey(EdgeKind kind, string targetId)
    {
        return kind + "|" + Edge.NormalizeId(targetId);
    }

    private static Dictionary<string, AuthoritativeSet> BuildAuthoritativeSets(List<EdgeRow> rows)
    {
        var sets = new Dictionary<string, AuthoritativeSet>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.SelfId is null)
            {
                continue;
            }

            foreach (var property in row.Properties.Values)
            {
                if (property.Spec.Side != RelationshipSide.Target || !property.IsDeclared || property.Handler.IsSingleValued)
                {
                    continue;
                }

                string? collection = property.Declarations.FirstOrDefault(d => d.TargetCollection is not null)?.TargetCollection ?? EdgeGraphContext.CollectionOf(row.Declarer.Type);
                var set = new AuthoritativeSet(row, property, row.SelfId, collection);
                sets[SetKey(property.Handler.Kind, row.SelfId)] = set;
                foreach (var declaration in property.Declarations)
                {
                    if (declaration.OtherId is not null)
                    {
                        set.Add(declaration.OtherId, declaration);
                    }
                }
            }
        }

        // Principal-side declarations naming an authoritative target are merged into its set (de-duplication across sides).
        foreach (var row in rows)
        {
            foreach (var property in row.Properties.Values)
            {
                if (property.Spec.Side != RelationshipSide.Principal || !property.IsDeclared || property.Handler.IsSingleValued)
                {
                    continue;
                }

                foreach (var declaration in property.Declarations)
                {
                    if (declaration.OtherId is null || row.SelfId is null || !sets.TryGetValue(SetKey(property.Handler.Kind, declaration.OtherId), out var set))
                    {
                        continue;
                    }

                    declaration.GovernedByTarget = true;
                    set.Add(row.SelfId, declaration);
                }
            }
        }

        return sets;
    }

    private async Task PlanAuthoritativeAsync(AuthoritativeSet set, CancellationToken cancellationToken)
    {
        var handler = set.Handler;
        var api = set.Row.Declarer.Api;
        var state = await ReadTargetAsync(handler, set.TargetId, set.Collection, includeEdges: true, api, cancellationToken).ConfigureAwait(false);
        if (!state.Exists)
        {
            string message = $"{set.Row.Declarer.Describe()}: the live object ({set.TargetId}) could not be read to reconcile properties.{set.Property.Name}.";
            set.Row.Errors.Add(message);
            foreach (var governed in set.Desired.Values.SelectMany(d => d.Declarations).Where(d => d.GovernedByTarget))
            {
                RowOf(governed).Errors.Add($"{governed.Describe()}: the target '{set.TargetId}' could not be read.");
            }

            return;
        }

        if (state.IsDynamic && handler.RejectsDynamicTargets)
        {
            set.Row.Errors.Add($"{set.Row.Declarer.Describe()}: properties.{set.Property.Name} cannot be declared on a dynamic {Noun(handler.Kind)}; its membership is computed from the membership rule.");
            foreach (var governed in set.Desired.Values.SelectMany(d => d.Declarations).Where(d => d.GovernedByTarget))
            {
                RowOf(governed).Errors.Add($"{governed.Describe()}: '{set.TargetId}' is a dynamic {Noun(handler.Kind)} and does not accept manual membership.");
            }

            return;
        }

        var live = state.Edges ?? new Dictionary<string, JsonObject?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, desired) in set.Desired)
        {
            bool isLive = live.ContainsKey(key);
            if (!isLive)
            {
                foreach (var declaration in desired.Declarations)
                {
                    PropertyOf(declaration).Children.Add(EdgeRow.CreateChild(declaration));
                }

                var edge = new Edge(handler.Kind, set.TargetId, desired.Id, set.Collection, desired.Declarations[0].Extra);
                AddOperation(edge, EdgeAction.Add, handler, api, null, desired.Declarations.Select(RowOf).Distinct().ToList());
            }
        }

        foreach (var declaration in set.Property.Declarations.Where(d => d.IsUnknown))
        {
            set.Property.Children.Add(EdgeRow.CreateChild(declaration));
        }

        string? unsure = UnreliableContributor(set);
        if (unsure is not null && live.Keys.Any(k => !set.Desired.ContainsKey(k)))
        {
            string message = $"{set.Row.Declarer.Describe()}: properties.{set.Property.Name} was not pruned: {unsure} did not apply in this run and may declare entries of this set from the other side, so no live entry was removed. Run the deployment again once it applies.";
            set.Row.Warnings.Add(message);
            _diagnostics.Add(Diagnostic.Warning(EngineDiagnosticCodes.EdgePassSkipped, message, set.Row.Declarer.Declaration.Path.Append("properties").Append(set.Property.Name)));
        }

        foreach (var (key, extra) in live)
        {
            if (set.Desired.ContainsKey(key) || unsure is not null)
            {
                continue;
            }

            var edge = new Edge(handler.Kind, set.TargetId, key, set.Collection);
            var operation = AddOperation(edge, EdgeAction.Remove, handler, api, extra, [set.Row]);
            set.Property.Children.Add(EdgeRow.DeleteChild(key));
            if (operation is not null && handler.WarnOnSignedInPrincipalRemoval)
            {
                await WarnIfSignedInPrincipalAsync(operation, set.Row, api, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The description of a declared resource that did not apply and that has a principal-side property of the set's kind in its declaration; <c>null</c> when none could contribute.</summary>
    private string? UnreliableContributor(AuthoritativeSet set)
    {
        foreach (var instance in _unreliableDeclarers)
        {
            if (instance.Declaration.Properties is not JsonObject declared)
            {
                continue;
            }

            foreach (var spec in instance.Definition.Relationships)
            {
                if (spec.Side == RelationshipSide.Principal && EdgeKinds.TryFromRelationship(spec.Kind, out var kind) && kind == set.Handler.Kind && JsonObjects.TryGetIgnoreCase(declared, spec.Property, out _))
                {
                    return instance.Describe();
                }
            }
        }

        return null;
    }

    private EdgeRow RowOf(EdgeDeclaration declaration)
    {
        return _rows[declaration.Declarer];
    }

    private EdgePropertyPlan PropertyOf(EdgeDeclaration declaration)
    {
        return RowOf(declaration).Properties[declaration.Property];
    }

    // ---- principal side ----

    /// <summary>
    /// A principal-side property is ensure-only: every declared edge that is missing is added; nothing is ever removed from the principal's side (an edge
    /// that is no longer declared stays, unless a target-side declaration of that target prunes it).
    /// </summary>
    private async Task PlanPrincipalSideAsync(EdgeRow row, EdgePropertyPlan property, CancellationToken cancellationToken)
    {
        var handler = property.Handler;
        var api = row.Declarer.Api;
        if (_context.AppOnlyToken && handler.UnsupportedWithAppOnlyToken(row.Declarer.Type) is { } unsupported)
        {
            // An application token cannot read this side of the relationship (Learn: user-list-ownedobjects): report it and leave the property alone.
            SkipProperty(row, property, EngineDiagnosticCodes.EdgeUnsupportedInAppMode, $"{row.Declarer.Describe()}: {unsupported}");
            return;
        }

        if (row.SelfId is null)
        {
            // The principal does not exist yet (what-if create): every declared edge is an add.
            foreach (var declaration in property.Declarations)
            {
                property.Children.Add(EdgeRow.CreateChild(declaration));
            }

            return;
        }

        EdgePrincipalState? principal = null;
        foreach (var declaration in property.Declarations)
        {
            if (declaration.IsUnknown)
            {
                property.Children.Add(EdgeRow.CreateChild(declaration));
                continue;
            }

            if (declaration.GovernedByTarget)
            {
                continue;
            }

            principal ??= await ReadPrincipalAsync(handler, row.SelfId, row.Declarer.Type, api, cancellationToken).ConfigureAwait(false);
            if (principal.Edges.ContainsKey(Edge.NormalizeId(declaration.OtherId!)))
            {
                continue;
            }

            var target = await ReadTargetAsync(handler, declaration.OtherId!, declaration.TargetCollection, includeEdges: false, api, cancellationToken).ConfigureAwait(false);
            if (!target.Exists)
            {
                row.Errors.Add($"{declaration.Describe()}: '{declaration.OtherId}' does not exist or is not a {Noun(handler.Kind)}.");
                continue;
            }

            if (target.IsDynamic && handler.RejectsDynamicTargets)
            {
                row.Errors.Add($"{declaration.Describe()}: '{declaration.OtherId}' is a dynamic {Noun(handler.Kind)} and does not accept manual membership.");
                continue;
            }

            declaration.TargetCollection ??= target.Collection;
            AddOperation(declaration.Edge!, EdgeAction.Add, handler, api, null, [row]);
            property.Children.Add(EdgeRow.CreateChild(declaration));
        }
    }

    private static void PlanTargetToBeCreated(EdgePropertyPlan property)
    {
        foreach (var declaration in property.Declarations)
        {
            property.Children.Add(EdgeRow.CreateChild(declaration));
        }
    }

    // ---- single-valued (manager) ----

    private async Task PlanSingleValuedAsync(EdgeRow row, EdgePropertyPlan property, CancellationToken cancellationToken)
    {
        var declaration = property.Declarations.FirstOrDefault();
        if (declaration is null)
        {
            return;
        }

        var handler = property.Handler;
        var api = row.Declarer.Api;
        var path = JsonPath.Of("properties", property.Name);
        if (row.SelfId is null)
        {
            if (!declaration.IsClear)
            {
                property.SingleDelta = new PropertyDelta(path, PropertyChangeType.Create, null, AfterOf(declaration), declaration.IsUnknown);
            }

            return;
        }

        var principal = await ReadPrincipalAsync(handler, row.SelfId, row.Declarer.Type, api, cancellationToken).ConfigureAwait(false);
        if (principal.SkipReason is not null)
        {
            SkipProperty(row, property, EngineDiagnosticCodes.ManagerReadForbidden, $"{row.Declarer.Describe()}: {principal.SkipReason}");
            return;
        }

        string? live = principal.Edges.Keys.FirstOrDefault();
        if (declaration.IsClear)
        {
            if (live is not null)
            {
                AddOperation(new Edge(handler.Kind, live, row.SelfId), EdgeAction.Remove, handler, api, principal.Edges[live], [row]);
                property.SingleDelta = new PropertyDelta(path, PropertyChangeType.Delete, JsonValue.Create(live), null);
            }

            return;
        }

        if (declaration.IsUnknown)
        {
            property.SingleDelta = new PropertyDelta(path, live is null ? PropertyChangeType.Create : PropertyChangeType.Modify, live is null ? null : JsonValue.Create(live), AfterOf(declaration), true);
            return;
        }

        if (live is not null && Edge.IdEquals(live, declaration.OtherId))
        {
            return;
        }

        var target = await ReadTargetAsync(handler, declaration.OtherId!, null, includeEdges: false, api, cancellationToken).ConfigureAwait(false);
        if (!target.Exists)
        {
            row.Errors.Add($"{declaration.Describe()}: '{declaration.OtherId}' does not exist or is not a user.");
            return;
        }

        AddOperation(declaration.Edge!, EdgeAction.Add, handler, api, null, [row]);
        property.SingleDelta = new PropertyDelta(path, live is null ? PropertyChangeType.Create : PropertyChangeType.Modify, live is null ? null : JsonValue.Create(live), AfterOf(declaration));
    }

    /// <summary>Leaves a relationship property alone: no operations, no deltas; the reason is a row warning and a diagnostic.</summary>
    private void SkipProperty(EdgeRow row, EdgePropertyPlan property, string code, string message)
    {
        property.IsSkipped = true;
        property.Children.Clear();
        property.SingleDelta = null;
        row.Warnings.Add(message);
        _diagnostics.Add(Diagnostic.Warning(code, message, row.Declarer.Declaration.Path.Append("properties").Append(property.Name)));
    }

    private static JsonNode AfterOf(EdgeDeclaration declaration)
    {
        return declaration.IsUnknown ? JsonValue.Create(UnknownValue.Display) : JsonValue.Create(declaration.OtherId!);
    }

    // ---- shared ----

    private EdgeOperation? AddOperation(Edge edge, EdgeAction action, IEdgeKindHandler handler, GraphApi api, JsonObject? liveExtra, IReadOnlyList<EdgeRow> rows)
    {
        string identity = handler.IdentityOf(edge);
        if (_operations.TryGetValue(identity, out var existing))
        {
            if (existing.Action != action)
            {
                foreach (var row in rows.Concat(existing.Rows).Distinct())
                {
                    row.Errors.Add($"{handler.Describe(edge)} is both added and removed by this template; declarations conflict.");
                }

                return null;
            }

            if (!JsonDiff.AreEquivalent(existing.Edge.Extra, edge.Extra))
            {
                foreach (var row in rows.Concat(existing.Rows).Distinct())
                {
                    row.Errors.Add($"{handler.Describe(edge)} is declared more than once with conflicting values ({Render(existing.Edge.Extra)} vs {Render(edge.Extra)}).");
                }

                return null;
            }

            foreach (var row in rows)
            {
                if (!existing.Rows.Contains(row))
                {
                    existing.Rows.Add(row);
                }
            }

            return existing;
        }

        var hint = handler.GraphIdsOf(edge).Any(id => _createdGraphIds.Contains(Edge.NormalizeId(id)))
            ? RetryHint.ExpectReplicationLag
            : RetryHint.Default;
        var operation = new EdgeOperation(edge, action, handler, api, hint, liveExtra);
        operation.Rows.AddRange(rows.Distinct());
        _operations[identity] = operation;
        return operation;
    }

    private static string Render(JsonObject? extra)
    {
        return extra is null ? "none" : JsonDiff.Canonical(extra);
    }

    private async Task WarnIfSignedInPrincipalAsync(EdgeOperation operation, EdgeRow row, GraphApi api, CancellationToken cancellationToken)
    {
        if (operation.Warning is not null)
        {
            return;
        }

        string? me = await _context.GetSignedInPrincipalIdAsync(api, cancellationToken).ConfigureAwait(false);
        if (me is null || !Edge.IdEquals(me, operation.Edge.PrincipalId))
        {
            return;
        }

        string message = $"{row.Declarer.Describe()}: applying the template removes the signed-in principal ({me}) from the owners of {operation.Edge.TargetCollection ?? "object"} {operation.Edge.TargetId}; you may lose the right to manage it.";
        operation.Warning = message;
        row.Warnings.Add(message);
        _diagnostics.Add(Diagnostic.Warning(EngineDiagnosticCodes.SignedInPrincipalRemoved, message));
    }

    private async Task<EdgeTargetState> ReadTargetAsync(IEdgeKindHandler handler, string targetId, string? collectionHint, bool includeEdges, GraphApi api, CancellationToken cancellationToken)
    {
        string key = handler.Kind + "|" + Edge.NormalizeId(targetId);
        if (_targets.TryGetValue(key, out var cached) && (!includeEdges || cached.Edges is not null || !cached.Exists))
        {
            return cached;
        }

        var state = await handler.ReadTargetAsync(targetId, collectionHint, includeEdges, api, _context, cancellationToken).ConfigureAwait(false);
        _targets[key] = state;
        return state;
    }

    private async Task<EdgePrincipalState> ReadPrincipalAsync(IEdgeKindHandler handler, string principalId, string principalType, GraphApi api, CancellationToken cancellationToken)
    {
        string key = handler.Kind + "|" + Edge.NormalizeId(principalId);
        if (_principals.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var state = await handler.ReadPrincipalAsync(principalId, principalType, api, _context, cancellationToken).ConfigureAwait(false);
        _principals[key] = state;
        return state;
    }

    private void FinishRow(EdgeRow row)
    {
        if (_mode != PlanMode.Create)
        {
            return;
        }

        foreach (var property in row.Properties.Values)
        {
            var unknown = property.Declarations.Where(d => d.IsUnknown).Select(d => $"{d.Path} = {d.Symbol}").ToList();
            if (unknown.Count > 0)
            {
                row.Errors.Add($"{row.Declarer.Describe()}: properties.{property.Name} depends on values that are not available (a referenced resource was not deployed): {string.Join("; ", unknown)}");
            }
        }
    }

    private static string Noun(EdgeKind kind)
    {
        return kind switch
        {
            EdgeKind.GroupMembership => "group",
            EdgeKind.AdministrativeUnitMembership => "administrative unit",
            EdgeKind.Ownership => "ownable object",
            EdgeKind.Manager => "user",
            _ => "target",
        };
    }
}
