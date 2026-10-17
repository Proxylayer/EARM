using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using EArm.Graph;

namespace EArm.Engine.Planning;

/// <summary>The rows and findings of one <see cref="OrphanReport"/> run.</summary>
public sealed record OrphanReportResult(IReadOnlyList<ResourceChange> Rows, IReadOnlyList<Diagnostic> Diagnostics)
{
    public static OrphanReportResult Empty { get; } = new([], []);
}

/// <summary>
/// The orphan report (docs/whatif.md, "Orphan report"): read-only, it never deletes anything. For every type the template declares whose provider can list
/// its live objects in a bounded way (<see cref="IOrphanCandidateSource"/>), the live candidates in scope are compared with the Graph ids of the declared
/// resources; a candidate no declared resource matches is reported as an <see cref="ChangeType.Ignore"/> row marked <see cref="ResourceChange.Orphan"/>.
/// <list type="bullet">
/// <item>Complete mode lists every such type: small collections whole, children of the declared parents, assignments of the declared principals.</item>
/// <item>Incremental mode lists only the assignments and grants of the declared principals and the eligibilities of the declared target groups (<see cref="OrphanReason.Retained"/>): an assignment whose tuple
/// changed is created anew and the old one stays, still granting access, so the what-if says so.</item>
/// </list>
/// A type is not listed (with an <see cref="EngineDiagnosticCodes.OrphanReportIncomplete"/> note) when one of its declared resources is not resolved (a
/// deploy-time name or condition, a failed read or apply): its live object could otherwise be mistaken for an orphan. Applications, groups and users are
/// tenant-wide collections and are never listed.
/// </summary>
internal sealed class OrphanReport
{
    private readonly DeploymentPlan _plan;
    private readonly IGraphClient _graph;

    public OrphanReport(DeploymentPlan plan, IGraphClient graph)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(graph);
        _plan = plan;
        _graph = graph;
    }

    /// <param name="isSettled">True when the live identity of a declared (not ignored, concretely named) instance is known: its Graph id is published, or its key is resolved and nothing exists under it.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    public async Task<OrphanReportResult> RunAsync(Func<ResourceInstance, bool> isSettled, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(isSettled);
        bool complete = _plan.Mode == DeploymentMode.Complete;
        var rows = new List<ResourceChange>();
        var diagnostics = new List<Diagnostic>();
        var skippedTenantWide = new List<string>();

        foreach (var group in _plan.Instances.GroupBy(i => i.Type, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_plan.Registry.TryGet(group.Key, out var provider))
            {
                continue;
            }

            if (provider is not IOrphanCandidateSource source)
            {
                if (complete)
                {
                    skippedTenantWide.Add(group.Key);
                }

                continue;
            }

            if (!complete && !IsRetainedScope(source.OrphanScope))
            {
                continue;
            }

            // An "existing": true resource is only read: it neither puts its type in scope nor widens the listing (its children, its assignments).
            var declared = group.Where(i => !i.IsIgnored && !i.Declaration.IsExisting).ToList();
            if (declared.Count == 0 && group.Any(i => !i.IsIgnored && i.Declaration.IsExisting))
            {
                continue;
            }

            if (declared.FirstOrDefault(i => i.IsSymbolic || i.Condition is null || !isSettled(i)) is { } unsettled)
            {
                diagnostics.Add(Diagnostic.Warning(EngineDiagnosticCodes.OrphanReportIncomplete, $"Undeclared objects of type '{group.Key}' were not listed: the live object of {unsettled.Describe()} is not known in this run (a deploy-time name or condition, or a read or apply that failed)."));
                continue;
            }

            var query = BuildQuery(source, provider, group.Key, declared);
            if (query is null)
            {
                continue;
            }

            IReadOnlyList<OrphanCandidate> candidates;
            try
            {
                candidates = await WithRetryAsync(ct => source.ListOrphanCandidatesAsync(query, _graph, ct), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is GraphException or GraphTransportException or ProviderException)
            {
                diagnostics.Add(Diagnostic.Warning(EngineDiagnosticCodes.OrphanReportIncomplete, $"Undeclared objects of type '{group.Key}' could not be listed: {SecretRegistration.Scrub(ex.Message)}"));
                continue;
            }

            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var instance in group)
            {
                if (!instance.IsSymbolic && _plan.Resolver.TryGetGraphId(instance.Id, out var graphId))
                {
                    claimed.Add(graphId);
                }
            }

            var reason = IsRetainedScope(source.OrphanScope) ? OrphanReason.Retained : OrphanReason.Undeclared;
            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates.OrderBy(c => c.ParentName, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.GraphId, StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(candidate.GraphId) || claimed.Contains(candidate.GraphId) || !reported.Add(candidate.GraphId))
                {
                    continue;
                }

                var id = IdentifierOf(group.Key, candidate);
                rows.Add(ResourceChange.OrphanRow(id, group.Key, candidate.GraphId, reason, ReasonText(reason, candidate)));
                if (reason == OrphanReason.Retained)
                {
                    diagnostics.Add(Diagnostic.Warning(EngineDiagnosticCodes.AssignmentRetained, $"'{id.Id}' (Graph id {candidate.GraphId}) is not declared by the template but still grants access{Describe(candidate)}; EARM never removes it. Delete it yourself if the access should end."));
                }
            }
        }

        if (skippedTenantWide.Count > 0)
        {
            diagnostics.Add(Diagnostic.Info(EngineDiagnosticCodes.OrphanReportIncomplete, $"Complete mode does not list undeclared objects of the tenant-wide type(s) {string.Join(", ", skippedTenantWide.Select(t => $"'{t}'"))}; only the declared ones are compared."));
        }

        return new OrphanReportResult(rows, diagnostics);
    }

    /// <summary>
    /// True when a what-if Create's key is unresolved only because it holds values of objects this run creates (every unknown in its properties comes
    /// out of <c>reference()</c> of a declared resource planned as a Create): an eligibility of a group the template creates in the same run. No live
    /// object can carry such a key yet, so the instance cannot hide one from the listing, and its type is listed for the other declared principals
    /// instead of being skipped with <see cref="EngineDiagnosticCodes.OrphanReportIncomplete"/>.
    /// </summary>
    internal static bool KeyWaitsOnlyOnObjectsCreatedThisRun(DeploymentPlan plan, ResourceInstance instance)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(instance);
        if (!instance.IsEvaluated)
        {
            return false;
        }

        var unknowns = new List<UnknownValue>();
        CollectUnknowns(instance.Properties, unknowns);
        return unknowns.Count > 0 && unknowns.All(u =>
            u.Origins == UnknownOrigins.Reference
            && u.Source is { } source
            && plan.FindInstance(source) is { } target
            && !ReferenceEquals(target, instance)
            && plan.ChangeFor(target) is { Type: ChangeType.Create, HasError: false });
    }

    private static void CollectUnknowns(JsonNode? root, List<UnknownValue> unknowns)
    {
        var stack = new Stack<JsonNode?>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            switch (stack.Pop())
            {
                case JsonObject obj:
                    foreach (var property in obj)
                    {
                        stack.Push(property.Value);
                    }

                    break;
                case JsonArray array:
                    foreach (var item in array)
                    {
                        stack.Push(item);
                    }

                    break;
                case var leaf when UnknownJson.IsUnknown(leaf, out var unknown):
                    unknowns.Add(unknown);
                    break;
            }
        }
    }

    /// <summary>What bounds the listing of one type; <c>null</c> when nothing is in scope (no declared parent exists yet).</summary>
    private OrphanQuery? BuildQuery(IOrphanCandidateSource source, IResourceProvider provider, string type, IReadOnlyList<ResourceInstance> declared)
    {
        var api = declared.Count > 0 ? declared[0].Api : _plan.Catalog.Find(type) is { } definition && definition.ApiVersions.Count > 0 ? GraphApiMapping.ToAz(definition.ApiVersions.Values.First()) : GraphApi.V1;
        switch (source.OrphanScope)
        {
            case OrphanScope.ChildrenOfDeclaredParents:
            {
                string? parentType = source.OrphanParentType;
                if (parentType is null)
                {
                    return null;
                }

                var parents = new List<OrphanParent>();
                foreach (var parent in _plan.Instances.Where(i => ResourceIdentifier.TypeEquals(i.Type, parentType) && !i.IsSymbolic && !i.IsIgnored))
                {
                    if (_plan.Resolver.TryGetLive(parent.Id, out var live, out var graphId))
                    {
                        parents.Add(new OrphanParent(parent.Id.Name, graphId, live));
                    }
                }

                return parents.Count == 0 ? null : new OrphanQuery { Api = api, Parents = parents };
            }

            case OrphanScope.DeclaredPrincipals:
            case OrphanScope.DeclaredTargetGroups:
            {
                var keys = new List<ResourceKey>();
                foreach (var instance in declared.Where(i => i.IsEvaluated))
                {
                    var key = provider.ResolveKey(instance);
                    if (key.IsResolved)
                    {
                        keys.Add(key);
                    }
                }

                return keys.Count == 0 ? null : new OrphanQuery { Api = api, DeclaredKeys = keys };
            }

            default:
                return new OrphanQuery { Api = api };
        }
    }

    /// <summary>The scopes whose candidates still grant access when undeclared: listed in both modes and reported as retained, never deleted.</summary>
    private static bool IsRetainedScope(OrphanScope scope)
    {
        return scope is OrphanScope.DeclaredPrincipals or OrphanScope.DeclaredTargetGroups;
    }

    private static ResourceIdentifier IdentifierOf(string type, OrphanCandidate candidate)
    {
        string name = string.IsNullOrEmpty(candidate.Name) ? candidate.GraphId : candidate.Name;
        try
        {
            return candidate.ParentName is { Length: > 0 } parent ? new ResourceIdentifier(type, [parent, name]) : new ResourceIdentifier(type, [name]);
        }
        catch (ArgumentException)
        {
            // A name the identifier cannot carry: the Graph id always can.
            return new ResourceIdentifier(type, candidate.ParentName is { Length: > 0 } parent ? [parent, candidate.GraphId] : [candidate.GraphId]);
        }
    }

    private static string ReasonText(OrphanReason reason, OrphanCandidate candidate)
    {
        return reason == OrphanReason.Retained
            ? $"retained: no longer declared; still grants access{Describe(candidate)}. Assignments are never removed: delete it yourself if the access should end."
            : $"not declared in the template{Describe(candidate)}. Complete mode reports it and never deletes it: declare it to manage it, or delete it yourself.";
    }

    private static string Describe(OrphanCandidate candidate)
    {
        return string.IsNullOrWhiteSpace(candidate.Description) ? string.Empty : $" ({candidate.Description})";
    }

    private Task<T> WithRetryAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        return _plan.RetryPolicy is null
            ? operation(cancellationToken)
            : _plan.RetryPolicy.ExecuteAsync(operation, RetryHint.Default, null, cancellationToken);
    }
}
