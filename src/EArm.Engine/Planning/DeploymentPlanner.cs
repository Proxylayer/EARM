using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EArm.Az;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Validation;
using EArm.Engine.Edges;
using EArm.Engine.Providers;
using EArm.Engine.State;
using EArm.Expressions;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Validation;
using Microsoft.Extensions.Logging;

namespace EArm.Engine.Planning;

/// <summary>
/// Plan section 2.4, steps 2-6: validation (core + expression validators; a missing parameter value is an error
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
    }

    /// <summary>
    /// The offline part of planning: core and expression validation with the deployment promotions (a missing
    /// parameter value is an error; a Key Vault reference is an error for create, a warning for what-if).
    /// Returns the remaining warnings. The CLI runs this before preflight so a broken template never needs
    /// an Azure CLI session to be reported; <see cref="PlanAsync"/> runs it again (cheap, offline).
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
    public async Task<DeploymentPlan> PlanAsync(TemplateDocument document, StateFile? state, DeploymentMode mode, PlanMode planMode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        var catalog = _options.Catalog;
        var evaluator = _options.Evaluator;

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
            Environment = _options.Environment,
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

        // 4. Every type needs a provider (expected to fail until the provider work items land).
        _registry.RequireAll(instances.Select(i => i.Type));

        // 5. Graph and batches.
        var graph = DependencyGraph.Build(instances, analysis.Static, evaluator, catalog, diagnostics);
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
            State = state,
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
            _options.SignedInPrincipalId);

        // From here on providers may be used: existing-resource lookups go through them.
        resolver.ExistingLookup = LookupExistingAsync;

        // 6. What-if: read and plan every batch; nothing is written. Then the edge pass rows (plan section 1.4,
        //    read-only) and the mode pass rows (plan section 1.8).
        if (planMode == PlanMode.WhatIf)
        {
            await PlanBatchesAsync(plan, state, cancellationToken).ConfigureAwait(false);
            await PlanEdgesAsync(plan, state, cancellationToken).ConfigureAwait(false);
            if (state is not null)
            {
                await PlanModePassAsync(plan, state, cancellationToken).ConfigureAwait(false);
            }
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
            string message = $"Parameter '{name}' is supplied as a Key Vault reference that has not been resolved. The CLI resolves it with 'az keyvault secret show' before planning (KeyVaultSecretResolver); otherwise pass the value with -p {name}=<value>.";
            diagnostics.Add(planMode == PlanMode.Create
                ? Diagnostic.Error(EngineDiagnosticCodes.KeyVaultReferenceUnresolved, message, path, document.LocationOf(path), document.SourcePath)
                : Diagnostic.Warning(EngineDiagnosticCodes.KeyVaultReferenceUnresolved, message + " In what-if its value is shown as a deploy-time placeholder.", path, document.LocationOf(path), document.SourcePath));
        }

        diagnostics.Sort(DiagnosticOrder.Instance);
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

    private async Task PlanBatchesAsync(DeploymentPlan plan, StateFile? state, CancellationToken cancellationToken)
    {
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = plan.Parallelism, CancellationToken = cancellationToken };
        foreach (var batch in plan.Batches)
        {
            // Instances whose name is still symbolic afterwards (their input does not exist yet) are planned as creates below.
            SymbolicNames.Resolve(plan, batch, _logger);

            await Parallel.ForEachAsync(batch.Instances, parallel, (instance, ct) => new ValueTask(PlanInstanceAsync(plan, instance, state, ct))).ConfigureAwait(false);
        }
    }

    private async Task PlanInstanceAsync(DeploymentPlan plan, ResourceInstance instance, StateFile? state, CancellationToken cancellationToken)
    {
        ResourceChange change;
        try
        {
            change = await PlanInstanceCoreAsync(plan, instance, state, cancellationToken).ConfigureAwait(false);
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
        catch (Exception ex) when (ex is GraphException or AzCliException or ToolchainException)
        {
            EngineLog.ReadFailed(_logger, instance.Id.Id, ex.Message);
            change = ResourceChange.Unsupported(instance.Id, instance.Type, ChangeType.Modify, $"{instance.Describe()} could not be read: {ex.Message}");
        }

        EngineLog.Planned(_logger, change.Id.Id, change.Type.ToString(), change.Mode.ToString(), change.Deltas.Count);
        plan.RecordChange(instance, change);
    }

    private async Task<ResourceChange> PlanInstanceCoreAsync(DeploymentPlan plan, ResourceInstance instance, StateFile? state, CancellationToken cancellationToken)
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

        string? stateId = state?.GraphIdOf(instance.Id.Id);
        var existing = await WithRetryAsync(ct => provider.ReadAsync(key, stateId, _graph, ct), RetryHint.Default, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            plan.Resolver.Publish(instance.Id, existing.Raw, existing.GraphId, instance.PlannedId);
        }

        return provider.Plan(instance, existing, plan.Options);
    }

    // ---- edge pass (what-if rows) ----

    /// <summary>
    /// Plan section 1.4, read-only: re-evaluates the relationship properties of the planned resources against the
    /// live resolver, reads the live edges and renders the adds and removes as <c>properties.&lt;property&gt;</c>
    /// array deltas under the declaring resource's row (a <c>NoChange</c> row becomes <c>Modify</c>). Validation
    /// errors mark the row unsupported. No-op for templates without relationship properties.
    /// </summary>
    private async Task PlanEdgesAsync(DeploymentPlan plan, StateFile? state, CancellationToken cancellationToken)
    {
        var candidates = plan.Instances
            .Where(i => !i.IsSymbolic && !i.IsIgnored && plan.ChangeFor(i) is { HasError: false, Type: ChangeType.Create or ChangeType.Modify or ChangeType.NoChange })
            .ToList();
        if (!candidates.Any(i => EdgePlanner.DeclaresEdges(i, state)))
        {
            return;
        }

        var context = new EdgeGraphContext(_graph, _options.RetryPolicy, plan.Resolver, plan.Instances, _logger, plan.SignedInPrincipalId);
        EdgePlan edgePlan;
        try
        {
            edgePlan = await new EdgePlanner(plan, context, state, PlanMode.WhatIf).PlanAsync(candidates, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GraphException or AzCliException or ToolchainException or ProviderException or ExpressionException)
        {
            EngineLog.ReadFailed(_logger, "edge pass", ex.Message);
            foreach (var instance in candidates.Where(i => EdgePlanner.DeclaresEdges(i, state)))
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

    // ---- mode pass (what-if rows) ----

    /// <summary>
    /// Plan section 1.8: state entries the template no longer declares become <c>Ignore</c> rows ("will be detached")
    /// in Incremental mode and <c>Delete</c> rows in Complete mode (with the live object as <c>before</c>); an entry
    /// whose object is already gone is an <c>Ignore</c> row in both modes.
    /// </summary>
    private async Task PlanModePassAsync(DeploymentPlan plan, StateFile state, CancellationToken cancellationToken)
    {
        var orphans = ModePass.FindOrphans(state, plan, out var undecided);
        var diagnostics = new List<Diagnostic>();
        foreach (var (type, plannedId) in undecided)
        {
            EngineLog.ModePassUndecided(_logger, type, plannedId);
            diagnostics.Add(Diagnostic.Warning(EngineDiagnosticCodes.ResourceDetached, $"State entries of type '{type}' were not checked against the template: the name of '{plannedId}' is only known at deployment time."));
        }

        foreach (var entry in orphans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (plan.Mode == DeploymentMode.Incremental)
            {
                plan.AddModeChange(ResourceChange.Ignore(entry.Id, entry.Type, ModePass.DetachReasonWhatIf));
                diagnostics.Add(Diagnostic.Warning(EngineDiagnosticCodes.ResourceDetached, $"'{entry.Id.Id}' is recorded in the state but no longer declared in the template; a deployment will detach it from the state and leave the object in the tenant (use --mode Complete to delete it)."));
                continue;
            }

            plan.AddModeChange(await PlanDeleteAsync(plan, entry, diagnostics, cancellationToken).ConfigureAwait(false));
        }

        if (diagnostics.Count > 0)
        {
            plan.AddDiagnostics(diagnostics);
        }
    }

    private async Task<ResourceChange> PlanDeleteAsync(DeploymentPlan plan, StateEntry entry, List<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        if (!_registry.TryGet(entry.Type, out var provider))
        {
            return ResourceChange.Unsupported(entry.Id, entry.Type, ChangeType.Delete, $"No resource provider is registered for '{entry.Type}'; the object cannot be deleted by this version.");
        }

        ExistingResource? existing;
        try
        {
            var key = entry.ToResourceKey(plan.Catalog);
            existing = await WithRetryAsync(ct => provider.ReadAsync(key, entry.GraphId, _graph, ct), RetryHint.Default, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProviderException ex)
        {
            return ResourceChange.Unsupported(entry.Id, entry.Type, ChangeType.Delete, ex.Message);
        }
        catch (Exception ex) when (ex is GraphException or AzCliException or ToolchainException)
        {
            EngineLog.ReadFailed(_logger, entry.Id.Id, ex.Message);
            return ResourceChange.Unsupported(entry.Id, entry.Type, ChangeType.Delete, $"'{entry.Id.Id}' could not be read: {ex.Message}");
        }

        if (existing is null)
        {
            return ResourceChange.Ignore(entry.Id, entry.Type, "not in template and no longer in the tenant; will be detached from state");
        }

        var reasons = new List<string> { "not in template; Complete mode deletes it" };
        if (provider is IDeleteWarningSource warnings && warnings.DescribeDeleteRisk(entry, existing) is { } risk)
        {
            reasons.Add(risk);
            diagnostics.Add(Diagnostic.Warning(EngineDiagnosticCodes.DestructiveDelete, $"'{entry.Id.Id}': {risk}"));
        }

        return new ResourceChange
        {
            Id = entry.Id,
            ResourceType = entry.Type,
            Type = ChangeType.Delete,
            Mode = ChangeMode.Delete,
            Before = JsonDiff.RenderObject(existing.Properties),
            GraphId = existing.GraphId,
            Reasons = reasons,
        };
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
        catch (Exception ex) when (ex is GraphException or AzCliException)
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
