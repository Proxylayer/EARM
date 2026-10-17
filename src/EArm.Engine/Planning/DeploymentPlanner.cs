using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Validation;
using EArm.Engine.Edges;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using EArm.Expressions;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Validation;
using Microsoft.Extensions.Logging;

namespace EArm.Engine.Planning;

/// <summary>
/// Validation (core + expression validators; a missing parameter value is an error
/// here), expansion, dependency graph, then (in <see cref="PlanMode.WhatIf"/>) per batch and in parallel:
/// <c>ResolveKey</c> → <c>ReadAsync</c> → <c>Plan</c>. Nothing is written. Live objects that exist are published
/// to the resolver so later batches evaluate <c>reference()</c> against them; resources that do not exist yet
/// stay placeholders and surface as unknown (<c>(deploy-time)</c>) values in the rows that use them.
/// </summary>
public sealed class DeploymentPlanner
{
    private readonly ProviderRegistry _registry;
    private readonly IGraphClient _graph;
    private readonly PlannerOptions _options;
    private readonly ILogger _logger;
    private readonly KeyAbsenceCheck _keyAbsenceCheck;

    public DeploymentPlanner(ProviderRegistry registry, IGraphClient graph, PlannerOptions options, ILogger<DeploymentPlanner> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _graph = graph;
        _options = options;
        _logger = logger;
        var check = options.KeyAbsenceCheck ?? KeyAbsenceCheck.Default;
        _keyAbsenceCheck = options.ConfirmKeyAbsence ? check : check with { ExtraReads = 0 };
    }

    /// <summary>
    /// The offline part of planning: core and expression validation with the deployment promotions (a missing
    /// parameter value is an error; a Key Vault reference is an error for create, a warning for what-if).
    /// Returns the remaining warnings. The CLI runs this before preflight so a broken template never needs
    /// a sign-in to be reported; <see cref="PlanAsync"/> runs it again (cheap, offline).
    /// </summary>
    /// <exception cref="ValidationFailedException">The template has errors.</exception>
    public IReadOnlyList<Diagnostic> Validate(TemplateDocument document, PlanMode planMode)
    {
        ArgumentNullException.ThrowIfNull(document);
        var (diagnostics, _) = ValidateCore(document, planMode);
        return diagnostics;
    }

    private (List<Diagnostic> Diagnostics, ExpressionAnalysis Analysis) ValidateCore(TemplateDocument document, PlanMode planMode)
    {
        var catalog = _options.Catalog;
        var diagnostics = new List<Diagnostic>(new TemplateValidator(catalog).Validate(document));
        var analysis = new ExpressionValidator(catalog, _options.Functions).Analyze(document);
        diagnostics.AddRange(analysis.Diagnostics);
        PromoteForDeployment(document, diagnostics, analysis, planMode);
        ThrowIfErrors(diagnostics);
        return (diagnostics, analysis);
    }

    /// <exception cref="ValidationFailedException">The template has errors (validation, expansion or graph).</exception>
    /// <exception cref="UnknownProviderException">A resource type has no registered provider.</exception>
    public async Task<DeploymentPlan> PlanAsync(TemplateDocument document, DeploymentMode mode, PlanMode planMode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        var catalog = _options.Catalog;
        var evaluator = _options.Evaluator;

        // Secure parameter values are masked from every message from here on (review finding S-02).
        SecretRegistration.RegisterDocument(document);

        // 1. Validate (core + expressions), promote what deployment cannot tolerate.
        var (diagnostics, analysis) = ValidateCore(document, planMode);

        // 2. The deployment-wide evaluation context: live resolver, tenant lookups for appRole()/permissionScope()/
        //    roleTemplate() (cached per run), real deployment info, fresh lazy stores.
        var resolver = new LiveReferenceResolver { CancellationToken = cancellationToken };
        var entraLookup = new GraphEntraLookup(_graph, _options.RetryPolicy) { CancellationToken = cancellationToken };
        var index = new TemplateResourceIndex();
        foreach (var entry in analysis.Static.Index.Entries)
        {
            index.Add(entry);
        }

        foreach (var loop in analysis.Static.Index.CopyLoops)
        {
            index.AddCopyLoop(loop);
        }

        string templateHash = "sha256:" + CanonicalJson.Hash(document.Root);
        var deployment = new DeploymentInfo(_options.DeploymentName, templateHash, _options.TenantId, mode.ToString())
        {
            Tenant = _options.Tenant,
            Deployer = _options.Deployer,
            Environment = _options.Environment,
            Parameters = DeploymentInfo.SuppliedParameters(document.ParameterValues),
        };

        var context = new EvaluationContext
        {
            Parameters = new LazyParameters(document.ParameterValues),
            Variables = new LazyVariables(document.Variables),
            References = resolver,
            EntraLookup = entraLookup,
            Deployment = deployment,
            Functions = _options.Functions,
            Catalog = catalog,
            UserFunctions = document.Functions,
            Resources = index,
            Scope = EvaluationScope.Resource,
        }.Bind(evaluator);

        // 3. Expand.
        var instances = new ResourceExpander(evaluator, catalog).Expand(analysis.Static, context, diagnostics);
        ThrowIfErrors(diagnostics);
        EngineLog.Expanded(_logger, document.Resources.Count, instances.Count);

        // 3b. The static index only knows ids computable without a copy frame; the concrete ids of the copy
        //     iterations join it now (the context shares this index), so the per-iteration reference() rescan of
        //     step 5 and every later evaluation resolve reference(format('g-{0}', copyIndex())) by name.
        foreach (var instance in instances)
        {
            if (instance.Copy is { } copy)
            {
                index.Add(new TemplateResourceIndex.Entry(instance.PlannedId, instance.IsSymbolic, instance.Declaration, copy.Name));
            }
        }

        // 4. Every type needs a provider (an unregistered type fails here).
        _registry.RequireAll(instances.Select(i => i.Type));

        // 5. Graph and batches.
        var graph = DependencyGraph.Build(instances, analysis.Static, evaluator, catalog, diagnostics, ApplyOrder);
        ThrowIfErrors(diagnostics);

        // 5a. Template-level conflicts no single row can see (IDeclarationConflictSource), before any Graph call.
        AddDeclarationConflicts(document, instances, diagnostics);
        ThrowIfErrors(diagnostics);
        EngineLog.GraphBuilt(_logger, graph.Edges.Count, graph.Batches.Count);
        foreach (var batch in graph.Batches)
        {
            EngineLog.BatchContents(_logger, batch.Index, string.Join(", ", batch.Instances.Select(i => i.PlannedId.Id)));
        }

        var permissions = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in instances)
        {
            foreach (var permission in _registry.Get(instance.Type).RequiredPermissions(instance))
            {
                permissions.Add(permission);
            }
        }

        var planOptions = new PlanOptions
        {
            Mode = mode,
            PlanMode = planMode,
            AllowCaEnforce = _options.AllowCaEnforce,
            OnSoftDeletedConflict = _options.OnSoftDeletedConflict,
        };

        var plan = new DeploymentPlan(
            document,
            analysis.Static,
            _options.DeploymentName,
            mode,
            planMode,
            _options.TenantId,
            templateHash,
            instances,
            graph,
            diagnostics.Where(d => !d.IsError).ToList(),
            permissions,
            planOptions,
            context,
            resolver,
            index,
            evaluator,
            catalog,
            _registry,
            Math.Max(1, _options.Parallelism),
            _options.RetryPolicy,
            _options.EdgeKinds,
            _options.SignedInPrincipalId,
            _options.AppOnlyToken);

        // From here on providers may be used: existing-resource lookups go through them.
        resolver.ExistingLookup = LookupExistingAsync;

        // 6. What-if: read and plan every batch; nothing is written. Then the edge pass rows
        //    (read-only) and the orphan report rows (read-only; nothing is ever deleted).
        if (planMode == PlanMode.WhatIf)
        {
            await PlanBatchesAsync(plan, cancellationToken).ConfigureAwait(false);
            await PlanEdgesAsync(plan, cancellationToken).ConfigureAwait(false);
            await PlanOrphansAsync(plan, cancellationToken).ConfigureAwait(false);
        }

        plan.FinishChanges();
        return plan;
    }

    private static void PromoteForDeployment(TemplateDocument document, List<Diagnostic> diagnostics, ExpressionAnalysis analysis, PlanMode planMode)
    {
        for (int i = 0; i < diagnostics.Count; i++)
        {
            var diagnostic = diagnostics[i];
            if (diagnostic.Code == DiagnosticCodes.MissingParameterValue && !diagnostic.IsError)
            {
                diagnostics[i] = diagnostic with { Severity = DiagnosticSeverity.Error, Message = diagnostic.Message + " Supply it with -p name=value or a parameter file." };
            }
        }

        foreach (var name in analysis.Static.Parameters.PendingReferenceNames)
        {
            var definition = document.Parameters.TryGetValue(name, out var found) ? found : null;
            var path = definition?.Path;
            string message = $"Parameter '{name}' is supplied as a Key Vault reference that has not been resolved. The CLI resolves it from the vault before planning (KeyVaultSecretResolver); otherwise pass the value with -p {name}=<value>.";
            diagnostics.Add(planMode == PlanMode.Create
                ? Diagnostic.Error(EngineDiagnosticCodes.KeyVaultReferenceUnresolved, message, path, document.LocationOf(path), document.SourcePath)
                : Diagnostic.Warning(EngineDiagnosticCodes.KeyVaultReferenceUnresolved, message + " In what-if its value is shown as a deploy-time placeholder.", path, document.LocationOf(path), document.SourcePath));
        }

        diagnostics.Sort(DiagnosticOrder.Instance);
    }

    /// <summary>The instances the provider of <paramref name="instance"/> applies after it (<see cref="IApplyOrderSource"/>); none for most types.</summary>
    private IEnumerable<ResourceInstance> ApplyOrder(ResourceInstance instance, IReadOnlyList<ResourceInstance> declared)
    {
        return _registry.Get(instance.Type) is IApplyOrderSource source ? source.AppliesBefore(instance, declared) : [];
    }

    /// <summary>The conflicts the providers that implement <see cref="IDeclarationConflictSource"/> find among the expanded instances, as errors.</summary>
    private void AddDeclarationConflicts(TemplateDocument document, IReadOnlyList<ResourceInstance> instances, List<Diagnostic> diagnostics)
    {
        foreach (var instance in instances)
        {
            if (instance.IsIgnored || _registry.Get(instance.Type) is not IDeclarationConflictSource source)
            {
                continue;
            }

            foreach (var conflict in source.DescribeConflicts(instance, instances))
            {
                diagnostics.Add(Diagnostic.Error(conflict.Code, conflict.Message, conflict.Path, document.LocationOf(conflict.Path), document.SourcePath));
            }
        }
    }

    private static void ThrowIfErrors(List<Diagnostic> diagnostics)
    {
        if (diagnostics.Any(d => d.IsError))
        {
            diagnostics.Sort(DiagnosticOrder.Instance);
            throw new ValidationFailedException(diagnostics.ToList());
        }
    }

    // ---- what-if pass ----

    private async Task PlanBatchesAsync(DeploymentPlan plan, CancellationToken cancellationToken)
    {
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = plan.Parallelism, CancellationToken = cancellationToken };
        var reads = new ReadContext();
        foreach (var batch in plan.Batches)
        {
            // Instances whose name is still symbolic afterwards (their input does not exist yet) are planned as creates below.
            SymbolicNames.Resolve(plan, batch, _logger);

            // One summary line per batch for the absences the key absence check confirmed (its extra reads log at Debug).
            var absences = new KeyAbsenceTally();
            await Parallel.ForEachAsync(batch.Instances, parallel, (instance, ct) => new ValueTask(PlanInstanceAsync(plan, instance, reads, absences, ct))).ConfigureAwait(false);
            _keyAbsenceCheck.LogBatchSummary(_logger, batch.Index, absences);

            // The warnings of the reads, in the order of the batch (the tasks above finish in any order): the what-if output must not depend on timing.
            foreach (var instance in batch.Instances)
            {
                if (reads.Diagnostics.TryRemove(instance, out var found))
                {
                    plan.AddDiagnostics(found);
                }
            }
        }
    }

    /// <summary>What the parallel reads of one what-if share: the Graph objects instances have claimed, and the warnings they raised (kept per instance so they can be reported in a stable order).</summary>
    private sealed class ReadContext
    {
        public ConcurrentDictionary<string, ResourceInstance> Claims { get; } = new(StringComparer.OrdinalIgnoreCase);

        public ConcurrentDictionary<ResourceInstance, List<Diagnostic>> Diagnostics { get; } = new();
    }

    private async Task PlanInstanceAsync(DeploymentPlan plan, ResourceInstance instance, ReadContext reads, KeyAbsenceTally absences, CancellationToken cancellationToken)
    {
        ResourceChange change;
        try
        {
            change = await PlanInstanceCoreAsync(plan, instance, reads, absences, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProviderException ex)
        {
            change = ResourceChange.Unsupported(instance.Id, instance.Type, ChangeType.Modify, ex.Message);
        }
        catch (ExpressionException ex)
        {
            change = ResourceChange.Unsupported(instance.Id, instance.Type, ChangeType.Modify, $"{instance.Describe()}: {ex.MessageWithOffset}");
        }
        catch (Exception ex) when (ex is GraphException or GraphTransportException)
        {
            string message = SecretRegistration.Scrub(ex.Message);
            EngineLog.ReadFailed(_logger, instance.Id.Id, message);
            change = ResourceChange.Unsupported(instance.Id, instance.Type, ChangeType.Modify, $"{instance.Describe()} could not be read: {message}");
        }

        EngineLog.Planned(_logger, change.Id.Id, change.Type.ToString(), change.Mode.ToString(), change.Deltas.Count);
        plan.RecordChange(instance, change);
    }

    private async Task<ResourceChange> PlanInstanceCoreAsync(DeploymentPlan plan, ResourceInstance instance, ReadContext reads, KeyAbsenceTally absences, CancellationToken cancellationToken)
    {
        if (instance.Condition is null)
        {
            instance.Condition = InstanceEvaluator.EvaluateCondition(plan.Evaluator, instance.Declaration, instance.Context, out var conditionError);
            if (conditionError is not null)
            {
                return ResourceChange.Unsupported(instance.Id, instance.Type, ChangeType.Ignore, $"{instance.Describe()}: {conditionError}");
            }

            if (instance.Condition is null)
            {
                return ResourceChange.Ignore(instance.Id, instance.Type, "the condition depends on a value that is only known at deployment time", isUnknown: true);
            }
        }

        if (instance.IsIgnored)
        {
            return ResourceChange.Ignore(instance.Id, instance.Type, "condition evaluated to false");
        }

        SymbolicNames.LinkParent(plan, instance);
        InstanceEvaluator.EvaluateProperties(plan.Evaluator, instance);
        SecretRegistration.RegisterTainted(instance.Properties);
        var provider = plan.Registry.Get(instance.Type);

        if (instance.IsSymbolic)
        {
            return provider.Plan(instance, null, plan.Options);
        }

        // Name sugar and live-data validation (resourceAppId → resourceId, appRole → appRoleId, scopes → scope...) before the key is derived.
        await WithRetryAsync(async ct => { await provider.PrepareAsync(instance, _graph, ct).ConfigureAwait(false); return true; }, RetryHint.Default, cancellationToken).ConfigureAwait(false);

        var key = provider.ResolveKey(instance);
        if (!key.IsResolved)
        {
            var planned = provider.Plan(instance, null, plan.Options);
            return planned with
            {
                IsUnknown = true,
                Reasons = [.. planned.Reasons, $"the key cannot be resolved before deployment: {key.Describe()}"],
            };
        }

        // Identity is resolved live, by key, on every run (a renamed object is a new one). A key read that can miss an existing object
        // (KeyReadLagsCreate) is repeated before "nothing found" is planned as a create (KeyAbsenceCheck), unless ConfirmKeyAbsence is off (what-if and the
        // planning pass of create: the executor repeats the full check right before every POST, so a false "absent" here never becomes a duplicate).
        var existing = await _keyAbsenceCheck
            .ReadAsync(provider, key, ct => WithRetryAsync(c => provider.ReadAsync(key, _graph, c), RetryHint.Default, ct), _logger, absences, cancellationToken)
            .ConfigureAwait(false);
        var warnings = new List<Diagnostic>();
        if (existing is not null)
        {
            var claimant = string.IsNullOrEmpty(existing.GraphId) ? instance : reads.Claims.GetOrAdd(existing.GraphId, instance);
            if (!ReferenceEquals(claimant, instance))
            {
                string message = $"{instance.Describe()} and {claimant.Describe()} resolve to the same Graph object ({existing.GraphId}); two declarations must not manage one object.";
                EngineLog.DuplicateGraphObject(_logger, instance.Id.Id, claimant.Id.Id, existing.GraphId);
                warnings.Add(Diagnostic.Error(EngineDiagnosticCodes.DuplicateGraphObject, message));
                reads.Diagnostics[instance] = warnings;
                return ResourceChange.Unsupported(instance.Id, instance.Type, ChangeType.Modify, message);
            }

            plan.Resolver.Publish(instance.Id, existing.Raw, existing.GraphId, instance.PlannedId);
        }

        var plannedChange = provider.Plan(instance, existing, plan.Options);
        if (existing is not null && plannedChange is { Type: ChangeType.Modify, Mode: ChangeMode.Recreate, HasError: false })
        {
            plannedChange = GuardRecreate(plan, instance, existing, plannedChange, warnings);
        }

        warnings.AddRange(ProviderWarningReasons.DiagnosticsOf(instance, plannedChange));
        if (warnings.Count > 0)
        {
            reads.Diagnostics[instance] = warnings;
        }

        return plannedChange;
    }

    /// <summary>
    /// A recreate deletes the existing object: the what-if applies the guards of every delete (<see cref="DeleteGuards"/>: never the signed-in principal or a service
    /// principal owned by another organization) and says in the row that the object is deleted. A deployment asks for confirmation of every recreate (<c>-y</c>).
    /// </summary>
    private static ResourceChange GuardRecreate(DeploymentPlan plan, ResourceInstance instance, ExistingResource existing, ResourceChange change, List<Diagnostic> warnings)
    {
        if (DeleteGuards.Refusal(instance.Id.Id, instance.Type, existing, plan.SignedInPrincipalId, plan.TenantId) is { } refusal)
        {
            string message = RecreateRefusal(instance, refusal);
            warnings.Add(Diagnostic.Error(EngineDiagnosticCodes.ProtectedObject, message));
            return change with { UnsupportedReason = message, Reasons = [.. change.Reasons, message] };
        }

        string note = $"the existing object ({existing.GraphId}) is DELETED and created again";
        warnings.Add(Diagnostic.Warning(EngineDiagnosticCodes.DestructiveDelete, $"{instance.Id.Id}: {note}; a deployment asks for confirmation (-y) before it replaces an object."));
        return change with { Reasons = [.. change.Reasons, note] };
    }

    /// <summary>The refusal text when the old object of a recreate may not be deleted (nothing is created or changed in that case).</summary>
    internal static string RecreateRefusal(ResourceInstance instance, string refusal)
    {
        return $"{instance.Describe()}: an immutable property changed, so the object must be deleted and created again, but {refusal} Nothing was changed.";
    }

    // ---- edge pass (what-if rows) ----

    /// <summary>
    /// Read-only: re-evaluates the relationship properties of the planned resources against the
    /// live resolver, reads the live edges and renders the adds and removes as <c>properties.&lt;property&gt;</c>
    /// array deltas under the declaring resource's row (a <c>NoChange</c> row becomes <c>Modify</c>). Validation
    /// errors mark the row unsupported. No-op for templates without relationship properties.
    /// </summary>
    private async Task PlanEdgesAsync(DeploymentPlan plan, CancellationToken cancellationToken)
    {
        var candidates = plan.Instances
            .Where(i => !i.IsSymbolic && !i.IsIgnored && plan.ChangeFor(i) is { HasError: false, Type: ChangeType.Create or ChangeType.Modify or ChangeType.NoChange })
            .ToList();
        if (!candidates.Any(EdgePlanner.DeclaresEdges))
        {
            return;
        }

        var context = new EdgeGraphContext(_graph, _options.RetryPolicy, plan.Resolver, plan.Instances, _logger, plan.SignedInPrincipalId) { AppOnlyToken = plan.AppOnlyToken };
        EdgePlan edgePlan;
        try
        {
            // Resources whose row is an error (or that are otherwise not planned) are unknown declarers: the target-side sets they could belong to are not pruned in the what-if either.
            var candidateSet = new HashSet<ResourceInstance>(candidates);
            var unreliable = plan.Instances.Where(i => !i.IsSymbolic && !i.IsIgnored && !candidateSet.Contains(i)).ToList();
            edgePlan = await new EdgePlanner(plan, context, PlanMode.WhatIf, null, unreliable).PlanAsync(candidates, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GraphException or GraphTransportException or ProviderException or ExpressionException)
        {
            EngineLog.ReadFailed(_logger, "edge pass", ex.Message);
            foreach (var instance in candidates.Where(EdgePlanner.DeclaresEdges))
            {
                if (plan.ChangeFor(instance) is { } change)
                {
                    plan.RecordChange(instance, change with { UnsupportedReason = $"the relationships of {instance.Describe()} could not be planned: {ex.Message}", Reasons = [.. change.Reasons, ex.Message] });
                }
            }

            return;
        }

        foreach (var row in edgePlan.Rows)
        {
            if (plan.ChangeFor(row.Declarer) is { } change)
            {
                plan.RecordChange(row.Declarer, EdgeChanges.Merge(change, row));
            }
        }

        if (edgePlan.Diagnostics.Count > 0)
        {
            plan.AddDiagnostics(edgePlan.Diagnostics);
        }
    }

    // ---- orphan report (what-if rows) ----

    /// <summary>
    /// The orphan report (<see cref="OrphanReport"/>): live objects of the declared types that no declared resource matches, as <see cref="ChangeType.Ignore"/>
    /// rows marked <see cref="ResourceChange.Orphan"/>. Complete mode lists every bounded type; Incremental mode only the assignments and grants of the declared
    /// principals that are retained. Read-only, like the rest of the what-if; nothing is ever deleted.
    /// </summary>
    private async Task PlanOrphansAsync(DeploymentPlan plan, CancellationToken cancellationToken)
    {
        var result = await new OrphanReport(plan, _graph).RunAsync(instance => IsSettled(plan, instance), cancellationToken).ConfigureAwait(false);
        foreach (var row in result.Rows)
        {
            plan.AddOrphan(row);
        }

        if (result.Diagnostics.Count > 0)
        {
            plan.AddDiagnostics(result.Diagnostics);
        }
    }

    /// <summary>True when the what-if knows the live identity of the instance: it was read without error and found (published), or its key is resolved and nothing exists under it.</summary>
    private static bool IsSettled(DeploymentPlan plan, ResourceInstance instance)
    {
        if (plan.ChangeFor(instance) is not { HasError: false } change)
        {
            return false;
        }

        if (plan.Resolver.TryGetGraphId(instance.Id, out _))
        {
            return true;
        }

        return change.Type == ChangeType.Create && instance.IsEvaluated && plan.Registry.Get(instance.Type).ResolveKey(instance).IsResolved;
    }

    // ---- existing-resource lookups ----

    private async Task<ExistingLookupResult> LookupExistingAsync(ResourceIdentifier id, string apiVersion, CancellationToken cancellationToken)
    {
        if (!_registry.TryGet(id.Type, out var provider))
        {
            EngineLog.ExistingLookupUnresolved(_logger, id.Id, apiVersion, "no provider is registered for the type");
            return ExistingLookupResult.Unresolvable($"no provider is registered for '{id.Type}'");
        }

        if (provider is not IExistingResourceLookup lookup)
        {
            EngineLog.ExistingLookupUnresolved(_logger, id.Id, apiVersion, "the provider does not support existing-resource lookups");
            return ExistingLookupResult.Unresolvable($"the provider for '{id.Type}' does not support existing-resource lookups");
        }

        try
        {
            var found = await WithRetryAsync(ct => lookup.ReadExistingAsync(id, apiVersion, _graph, ct), RetryHint.Default, cancellationToken).ConfigureAwait(false);
            return ExistingLookupResult.Of(found);
        }
        catch (ProviderException ex)
        {
            EngineLog.ExistingLookupUnresolved(_logger, id.Id, apiVersion, ex.Message);
            return ExistingLookupResult.Unresolvable(ex.Message);
        }
        catch (Exception ex) when (ex is GraphException or GraphTransportException)
        {
            EngineLog.ExistingLookupUnresolved(_logger, id.Id, apiVersion, ex.Message);
            return ExistingLookupResult.Unresolvable(ex.Message);
        }
    }

    private Task<T> WithRetryAsync<T>(Func<CancellationToken, Task<T>> operation, RetryHint hint, CancellationToken cancellationToken)
    {
        return _options.RetryPolicy is null
            ? operation(cancellationToken)
            : _options.RetryPolicy.ExecuteAsync(operation, hint, null, cancellationToken);
    }
}
