using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Diagnostics;
using EArm.Engine.Edges;
using EArm.Engine.Execution;
using EArm.Engine.Planning.Nested;
using EArm.Engine.Providers;
using EArm.Expressions;
using EArm.Graph;
using Microsoft.Extensions.Logging;

namespace EArm.Engine.Planning;

/// <summary>
/// The what-if pass of <see cref="DeploymentPlanner"/> (<see cref="PlanMode.WhatIf"/>): reads and plans every batch, then the edge pass rows (read-only)
/// and the orphan report rows (read-only; nothing is ever deleted). Nothing is written.
/// </summary>
internal sealed class WhatIfReadPass
{
    private readonly IGraphClient _graph;
    private readonly RetryPolicy? _retryPolicy;
    private readonly GraphCallRetry _retry;
    private readonly ILogger _logger;
    private readonly KeyAbsenceCheck _keyAbsenceCheck;

    public WhatIfReadPass(IGraphClient graph, RetryPolicy? retryPolicy, ILogger logger, KeyAbsenceCheck keyAbsenceCheck)
    {
        _graph = graph;
        _retryPolicy = retryPolicy;
        _retry = new GraphCallRetry(retryPolicy);
        _logger = logger;
        _keyAbsenceCheck = keyAbsenceCheck;
    }

    public async Task RunAsync(DeploymentPlan plan, CancellationToken cancellationToken)
    {
        await PlanBatchesAsync(plan, cancellationToken).ConfigureAwait(false);
        await PlanEdgesAsync(plan, cancellationToken).ConfigureAwait(false);
        await PlanOrphansAsync(plan, cancellationToken).ConfigureAwait(false);
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

    /// <summary>What the parallel reads of one what-if share: the Graph objects instances have claimed (by <see cref="IResourceProvider.ClaimIdentity"/>), and the warnings they raised (kept per instance so they can be reported in a stable order).</summary>
    private sealed class ReadContext
    {
        public ConcurrentDictionary<string, ResourceInstance> Claims { get; } = new(StringComparer.OrdinalIgnoreCase);

        public ConcurrentDictionary<ResourceInstance, List<Diagnostic>> Diagnostics { get; } = new();
    }

    private async Task PlanInstanceAsync(DeploymentPlan plan, ResourceInstance instance, ReadContext reads, KeyAbsenceTally absences, CancellationToken cancellationToken)
    {
        if (instance.IsDeployment)
        {
            PlanDeployment(plan, instance, reads);
            return;
        }

        ResourceChange change;
        try
        {
            change = await PlanInstanceCoreAsync(plan, instance, reads, absences, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
        catch (OperationCanceledException ex)
        {
            // Not the caller's cancellation (an HTTP timeout, a cancelled inner operation): this row is an error, the rest of the what-if goes on.
            string message = NonCallerCancellation.Describe(ex);
            EngineLog.ReadFailed(_logger, instance.Id.Id, message);
            change = ResourceChange.Unsupported(instance.Id, instance.Type, ChangeType.Modify, $"{instance.Describe()} could not be read: {message}");
        }

        EngineLog.Planned(_logger, change.Id.Id, change.Type.ToString(), change.Mode.ToString(), change.Deltas.Count);
        plan.RecordChange(instance, change);
    }

    /// <summary>
    /// A nested deployment is not a what-if row (ARM's what-if lists the resources of nested templates, not the deployments): its resources were
    /// planned in earlier batches, so its outputs are evaluated now and published for the <c>reference('&lt;deployment&gt;')</c> calls of later
    /// batches. An output that depends on a resource still to be created is a deploy-time placeholder.
    /// </summary>
    private static void PlanDeployment(DeploymentPlan plan, ResourceInstance deployment, ReadContext reads)
    {
        if (deployment.IsIgnored)
        {
            plan.Resolver.MarkConditionFalse(deployment.Id);
            return;
        }

        var result = NestedDeploymentOutputs.Evaluate(deployment, plan.Evaluator);
        NestedDeploymentOutputs.Publish(plan.Resolver, deployment, result);
        if (result.Problems.Count > 0)
        {
            reads.Diagnostics[deployment] = [Diagnostic.Warning(EngineDiagnosticCodes.OutputEvaluationFailed, NestedDeploymentOutputs.Describe(deployment, result))];
        }
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
            plan.Resolver.MarkConditionFalse(instance.Id);
            return ResourceChange.ConditionFalse(instance.Id, instance.Type);
        }

        SymbolicNames.LinkParent(plan, instance);
        if (InstanceEvaluator.WholePropertiesKnownOnlyAtDeployment(plan.Evaluator, instance) is { } symbol)
        {
            // "properties": "[union(..., reference('created-this-run'))]": its keys are known once the referenced resource exists (create evaluates it then).
            return ResourceChange.Ignore(instance.Id, instance.Type, $"its properties are one expression whose value is only known at deployment time ({symbol}); it is evaluated when the resource is deployed", isUnknown: true);
        }

        InstanceEvaluator.EvaluateProperties(plan.Evaluator, instance);
        SecretRegistration.RegisterTainted(instance.Properties);
        var provider = plan.Registry.Get(instance.Type);

        if (instance.IsSymbolic)
        {
            return instance.Declaration.IsExisting ? ResourceChange.ExistingRead(instance.Id, instance.Type, null, isUnknown: true) : provider.Plan(instance, null, plan.Options);
        }

        if (instance.Declaration.IsExisting)
        {
            return await PlanExistingAsync(plan, instance, provider, cancellationToken).ConfigureAwait(false);
        }

        // Name sugar and live-data validation (resourceAppId → resourceId, appRole → appRoleId, scopes → scope...) before the key is derived.
        await _retry.WithRetryAsync(async ct => { await provider.PrepareAsync(instance, _graph, ct).ConfigureAwait(false); return true; }, RetryHint.Default, cancellationToken).ConfigureAwait(false);

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
            .ReadAsync(provider, key, ct => _retry.WithRetryAsync(c => provider.ReadAsync(key, _graph, c), RetryHint.Default, ct), _logger, absences, cancellationToken)
            .ConfigureAwait(false);
        var warnings = new List<Diagnostic>();
        if (existing is not null)
        {
            var claimant = string.IsNullOrEmpty(existing.GraphId) ? instance : reads.Claims.GetOrAdd(provider.ClaimIdentity(key, existing.GraphId), instance);
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

        var plannedChange = AdoptOnlyCheck.Apply(plan.Evaluator, instance, existing, provider.Plan(instance, existing, plan.Options));
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
    /// A languageVersion 2.0 <c>"existing": true</c> resource is read by its key and published for <c>reference()</c>, never planned as a write
    /// (https://learn.microsoft.com/azure/azure-resource-manager/templates/resource-declaration#declare-existing-resources). It has no properties to
    /// prepare or compare; a missing object is an error, as in ARM.
    /// </summary>
    private async Task<ResourceChange> PlanExistingAsync(DeploymentPlan plan, ResourceInstance instance, IResourceProvider provider, CancellationToken cancellationToken)
    {
        var key = provider.ResolveKey(instance);
        if (!key.IsResolved)
        {
            return ResourceChange.Unsupported(instance.Id, instance.Type, ChangeType.NoChange, $"{instance.Describe()}: the Graph key of the existing resource cannot be resolved ({key.Describe()}).") with { UnsupportedCode = EngineDiagnosticCodes.ResourceKeyUnresolved };
        }

        var live = await _retry.WithRetryAsync(ct => provider.ReadAsync(key, _graph, ct), RetryHint.Default, cancellationToken).ConfigureAwait(false);
        if (live is null)
        {
            return ResourceChange.Unsupported(instance.Id, instance.Type, ChangeType.NoChange, DeploymentPlanner.ExistingNotFound(instance)) with { UnsupportedCode = EngineDiagnosticCodes.ExistingResourceNotFound };
        }

        plan.Resolver.Publish(instance.Id, live.Raw, live.GraphId, instance.PlannedId);
        return ResourceChange.ExistingRead(instance.Id, instance.Type, live);
    }

    /// <summary>
    /// A recreate deletes the existing object: the what-if applies the guards of every delete (<see cref="DeleteGuards"/>: never the signed-in principal or a service
    /// principal owned by another organization) and says in the row that the object is deleted. A deployment asks for confirmation of every recreate (<c>-y</c>).
    /// </summary>
    private static ResourceChange GuardRecreate(DeploymentPlan plan, ResourceInstance instance, ExistingResource existing, ResourceChange change, List<Diagnostic> warnings)
    {
        if (DeleteGuards.Refusal(instance.Id.Id, instance.Type, existing, plan.SignedInPrincipalId, plan.TenantId) is { } refusal)
        {
            string message = DeploymentPlanner.RecreateRefusal(instance, refusal);
            warnings.Add(Diagnostic.Error(EngineDiagnosticCodes.ProtectedObject, message));
            return change with { UnsupportedReason = message, Reasons = [.. change.Reasons, message] };
        }

        string note = $"the existing object ({existing.GraphId}) is DELETED and created again";
        warnings.Add(Diagnostic.Warning(EngineDiagnosticCodes.DestructiveDelete, $"{instance.Id.Id}: {note}; a deployment asks for confirmation (-y) before it replaces an object."));
        return change with { Reasons = [.. change.Reasons, note] };
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

        var context = new EdgeGraphContext(_graph, _retryPolicy, plan.Resolver, plan.Instances, _logger, plan.SignedInPrincipalId) { AppOnlyToken = plan.AppOnlyToken };
        EdgePlan edgePlan;
        try
        {
            // Resources whose row is an error (or that are otherwise not planned) are unknown declarers: the target-side sets they could belong to are not pruned in the what-if either.
            var candidateSet = new HashSet<ResourceInstance>(candidates);
            var unreliable = plan.Instances.Where(i => !i.IsSymbolic && !i.IsIgnored && !i.IsDeployment && !candidateSet.Contains(i)).ToList();
            edgePlan = await new EdgePlanner(plan, context, PlanMode.WhatIf, null, unreliable).PlanAsync(candidates, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GraphException or GraphTransportException or ProviderException or ExpressionException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            string message = ex is OperationCanceledException canceled ? NonCallerCancellation.Describe(canceled) : ex.Message;
            EngineLog.ReadFailed(_logger, "edge pass", message);
            foreach (var instance in candidates.Where(EdgePlanner.DeclaresEdges))
            {
                if (plan.ChangeFor(instance) is { } change)
                {
                    plan.RecordChange(instance, change with { UnsupportedReason = $"the relationships of {instance.Describe()} could not be planned: {message}", Reasons = [.. change.Reasons, message] });
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

    /// <summary>True when the what-if knows the live identity of the instance: it was read without error and found (published), or it is a Create whose key is resolved (nothing exists under it) or waits only on objects this run creates (nothing can exist under it yet).</summary>
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

        // A Create whose key waits on objects this run creates (an eligibility of a group created in the same run) has no live object to hide.
        return change.Type == ChangeType.Create && instance.IsEvaluated
            && (plan.Registry.Get(instance.Type).ResolveKey(instance).IsResolved || OrphanReport.KeyWaitsOnlyOnObjectsCreatedThisRun(plan, instance));
    }
}
