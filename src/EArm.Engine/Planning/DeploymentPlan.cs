using System;
using System.Collections.Generic;
using System.Linq;
using EArm.Graph;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Types;
using EArm.Engine.Edges;
using EArm.Engine.Providers;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Static;

namespace EArm.Engine.Planning;

/// <summary>Counts per change type. <see cref="Orphans"/> counts the rows of the orphan report, which are <see cref="ChangeType.Ignore"/> rows but are not included in <see cref="Ignore"/>.</summary>
public sealed record ChangeSummary(int Create, int Modify, int Delete, int NoChange, int Ignore, int Unsupported, int Orphans = 0)
{
    public int Total => Create + Modify + Delete + NoChange + Ignore + Orphans;

    public bool HasChanges => Create + Modify + Delete > 0;

    public static ChangeSummary Of(IEnumerable<ResourceChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        int create = 0, modify = 0, delete = 0, noChange = 0, ignore = 0, unsupported = 0, orphans = 0;
        foreach (var change in changes)
        {
            if (change.IsOrphan)
            {
                orphans++;
                continue;
            }

            switch (change.Type)
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

            if (change.HasError)
            {
                unsupported++;
            }
        }

        return new ChangeSummary(create, modify, delete, noChange, ignore, unsupported, orphans);
    }
}

/// <summary>
/// The output of <see cref="DeploymentPlanner"/>: the expanded instances, their dependency graph and batches, the
/// what-if changes (in <see cref="PlanMode.WhatIf"/>), unresolved markers, the union of required permissions,
/// and the evaluation machinery the executor continues with (live resolver, index, context).
/// </summary>
public sealed class DeploymentPlan
{
    private readonly Dictionary<ResourceInstance, ResourceChange> _changes = [];
    private readonly List<ResourceChange> _orphans = [];
    private readonly Dictionary<ResourceIdentifier, ResourceInstance> _byId;
    private readonly object _gate = new();

    internal DeploymentPlan(
        TemplateDocument document,
        StaticContext staticContext,
        string deploymentName,
        DeploymentMode mode,
        PlanMode planMode,
        string? tenantId,
        string templateHash,
        IReadOnlyList<ResourceInstance> instances,
        DependencyGraph graph,
        IReadOnlyList<Diagnostic> diagnostics,
        IReadOnlySet<string> requiredPermissions,
        PlanOptions options,
        EvaluationContext context,
        LiveReferenceResolver resolver,
        TemplateResourceIndex index,
        ExpressionEvaluator evaluator,
        ResourceTypeCatalog catalog,
        ProviderRegistry registry,
        int parallelism,
        RetryPolicy? retryPolicy,
        EdgeKindRegistry? edgeKinds = null,
        string? signedInPrincipalId = null,
        bool appOnlyToken = false)
    {
        Document = document;
        Static = staticContext;
        DeploymentName = deploymentName;
        Mode = mode;
        PlanMode = planMode;
        TenantId = tenantId;
        TemplateHash = templateHash;
        Instances = instances;
        Graph = graph;
        Diagnostics = diagnostics;
        RequiredPermissions = requiredPermissions;
        Options = options;
        Context = context;
        Resolver = resolver;
        Index = index;
        Evaluator = evaluator;
        Catalog = catalog;
        Registry = registry;
        Parallelism = parallelism;
        RetryPolicy = retryPolicy;
        EdgeKinds = edgeKinds ?? EdgeKindRegistry.Default;
        SignedInPrincipalId = signedInPrincipalId;
        AppOnlyToken = appOnlyToken;
        _byId = new Dictionary<ResourceIdentifier, ResourceInstance>();
        foreach (var instance in instances)
        {
            _byId[instance.PlannedId] = instance;
        }
    }

    public TemplateDocument Document { get; }

    public StaticContext Static { get; }

    public string DeploymentName { get; }

    public DeploymentMode Mode { get; }

    public PlanMode PlanMode { get; }

    public string? TenantId { get; }

    /// <summary><c>sha256:&lt;hex&gt;</c> of the canonical template.</summary>
    public string TemplateHash { get; }

    public IReadOnlyList<ResourceInstance> Instances { get; }

    public DependencyGraph Graph { get; }

    public IReadOnlyList<DeploymentBatch> Batches => Graph.Batches;

    /// <summary>Changes in batch order, followed by the orphan report rows; empty in <see cref="PlanMode.Create"/> (the executor computes them).</summary>
    public IReadOnlyList<ResourceChange> Changes { get; internal set; } = [];

    /// <summary>
    /// The what-if rows of the orphan report: live objects no declared resource matches, as <see cref="ChangeType.Ignore"/> rows marked
    /// <see cref="ResourceChange.Orphan"/> (never deleted). Also included at the end of <see cref="Changes"/>.
    /// </summary>
    public IReadOnlyList<ResourceChange> Orphans { get; private set; } = [];

    /// <summary>Warnings from validation and planning (errors throw <see cref="ValidationFailedException"/>).</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; internal set; }

    /// <summary>Existing-resource lookups that could not be performed.</summary>
    public IReadOnlyList<UnresolvedReference> UnresolvedReferences { get; internal set; } = [];

    /// <summary>Union of the providers' required Graph application permissions over every instance.</summary>
    public IReadOnlySet<string> RequiredPermissions { get; }

    public PlanOptions Options { get; }

    public ChangeSummary Summary => ChangeSummary.Of(Changes);

    public bool HasChanges => Changes.Any(c => c.IsWrite);

    /// <summary>The deployment-wide evaluation context (resource scope, no copy frame).</summary>
    public EvaluationContext Context { get; }

    public LiveReferenceResolver Resolver { get; }

    /// <summary>The resource index expressions resolve names against; concrete ids of resolved symbolic names are added here.</summary>
    public TemplateResourceIndex Index { get; }

    public ExpressionEvaluator Evaluator { get; }

    public ResourceTypeCatalog Catalog { get; }

    public ProviderRegistry Registry { get; }

    public int Parallelism { get; }

    /// <summary>Outer retry policy for provider calls (tests with a fake client); <c>null</c> when the Graph client retries itself.</summary>
    public RetryPolicy? RetryPolicy { get; }

    /// <summary>The edge-kind handlers the edge pass runs.</summary>
    public EdgeKindRegistry EdgeKinds { get; }

    /// <summary>The signed-in principal's object id when preflight supplied it (owner-removal warning); else looked up lazily.</summary>
    public string? SignedInPrincipalId { get; }

    /// <summary>True when the signed-in token is an application (app-only) token: some relationship reads Graph does not support are then reported, not attempted.</summary>
    public bool AppOnlyToken { get; }

    public ResourceChange? ChangeFor(ResourceInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        lock (_gate)
        {
            return _changes.TryGetValue(instance, out var change) ? change : null;
        }
    }

    /// <summary>The instance with the given id (current or planned); <c>null</c> when none.</summary>
    public ResourceInstance? FindInstance(ResourceIdentifier id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate)
        {
            return _byId.TryGetValue(id, out var instance) ? instance : null;
        }
    }

    internal void RecordChange(ResourceInstance instance, ResourceChange change)
    {
        lock (_gate)
        {
            _changes[instance] = change;
        }
    }

    internal void RegisterResolvedId(ResourceInstance instance)
    {
        lock (_gate)
        {
            _byId[instance.Id] = instance;
        }
    }

    internal void AddOrphan(ResourceChange change)
    {
        lock (_gate)
        {
            _orphans.Add(change);
        }
    }

    internal void AddDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        lock (_gate)
        {
            Diagnostics = [.. Diagnostics, .. diagnostics];
        }
    }

    /// <summary>Materialises <see cref="Changes"/> in batch order (then the orphan report rows) from the recorded per-instance changes.</summary>
    internal void FinishChanges()
    {
        var ordered = new List<ResourceChange>();
        lock (_gate)
        {
            foreach (var batch in Batches)
            {
                foreach (var instance in batch.Instances)
                {
                    if (_changes.TryGetValue(instance, out var change))
                    {
                        ordered.Add(change);
                    }
                }
            }

            Orphans = _orphans.ToList();
            ordered.AddRange(_orphans);
        }

        Changes = ordered;
        UnresolvedReferences = Resolver.Unresolved;
    }
}
