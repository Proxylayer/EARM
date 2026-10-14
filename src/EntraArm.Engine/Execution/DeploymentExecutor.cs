using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Az;
using EntraArm.Core;
using EntraArm.Core.Diagnostics;
using EntraArm.Core.Json;
using EntraArm.Core.Secrets;
using EntraArm.Engine.Edges;
using EntraArm.Engine.Planning;
using EntraArm.Engine.Providers;
using EntraArm.Engine.State;
using EntraArm.Expressions;
using EntraArm.Expressions.Evaluation;
using EntraArm.Expressions.Values;
using Microsoft.Extensions.Logging;

namespace EntraArm.Engine.Execution;

/// <summary>
/// Plan section 2.4, steps 6-7: walks the plan's batches; before each batch resolves the symbolic names whose
/// inputs are now live; inside a batch applies instances in parallel (evaluate → key → read → plan → create/update),
/// publishing every live object to the resolver as it lands; fails fast at the batch boundary (in-flight work
/// finishes, everything later is skipped) unless <see cref="ExecuteOptions.ContinueOnError"/>, in which case only
/// the transitive dependents of a failure are skipped (recorded through the sink before their batch starts, never
/// attempted, their symbolic names never resolved); then evaluates the outputs. State persistence goes through
/// <see cref="IStateSink"/>.
/// </summary>
public sealed class DeploymentExecutor
{
    private readonly ProviderRegistry _registry;
    private readonly IGraphClient _graph;
    private readonly ILogger _logger;

    public DeploymentExecutor(ProviderRegistry registry, IGraphClient graph, ILogger<DeploymentExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _graph = graph;
        _logger = logger;
    }

    public async Task<DeploymentResult> ExecuteAsync(DeploymentPlan plan, ExecuteOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);

        var run = new Run(this, plan, options);
        return await run.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class Run
    {
        private readonly DeploymentExecutor _owner;
        private readonly DeploymentPlan _plan;
        private readonly ExecuteOptions _options;
        private readonly PlanOptions _planOptions;
        private readonly ConcurrentDictionary<ResourceInstance, ResourceOperation> _operations = new();
        private readonly ConcurrentDictionary<ResourceInstance, ResourceChange> _changes = new();
        private readonly ConcurrentDictionary<ResourceIdentifier, byte> _createdThisRun = new();
        private readonly Dictionary<ResourceInstance, string> _skipped = [];
        private readonly List<Diagnostic> _diagnostics = [];
        private readonly List<ResourceOperation> _modeOperations = [];
        private readonly List<ResourceChange> _modeChanges = [];

        public Run(DeploymentExecutor owner, DeploymentPlan plan, ExecuteOptions options)
        {
            _owner = owner;
            _plan = plan;
            _options = options;
            _planOptions = plan.Options with { PlanMode = PlanMode.Create, State = options.State };
        }

        private ILogger Logger => _owner._logger;

        public async Task<DeploymentResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            int parallelism = Math.Max(1, _options.Parallelism ?? _plan.Parallelism);
            var parallel = new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken };
            bool stop = false;

            foreach (var batch in _plan.Batches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stop)
                {
                    foreach (var instance in batch.Instances)
                    {
                        await RecordSkippedAsync(instance, batch.Index, "an earlier batch failed", cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                // --continue-on-error: the transitive dependents of an earlier failure are skipped before anything
                // else touches them, symbolic-name resolution included (a dependent's name usually depends on the
                // very resource that failed, and must not be reported as an unresolved name).
                foreach (var instance in batch.Instances)
                {
                    if (_skipped.TryGetValue(instance, out var skipReason))
                    {
                        await RecordSkippedAsync(instance, batch.Index, skipReason, cancellationToken).ConfigureAwait(false);
                    }
                }

                var active = batch with { Instances = batch.Instances.Where(i => !_operations.ContainsKey(i)).ToList() };
                foreach (var (instance, reason) in SymbolicNames.Resolve(_plan, active, Logger))
                {
                    RecordFailure(instance, batch.Index, EngineDiagnosticCodes.SymbolicNameUnresolved, $"{instance.Type} with deploy-time name {instance.PlannedId.NameLeaf}: {reason}", TimeSpan.Zero);
                }

                var pending = active.Instances.Where(i => !_operations.ContainsKey(i)).ToList();
                await Parallel.ForEachAsync(pending, parallel, (instance, ct) => new ValueTask(ApplyOneAsync(instance, batch.Index, ct))).ConfigureAwait(false);

                var failed = batch.Instances.Where(i => _operations.TryGetValue(i, out var op) && op.Status == OperationStatus.Failed).ToList();
                if (failed.Count == 0)
                {
                    continue;
                }

                if (!_options.ContinueOnError)
                {
                    EngineLog.StoppingAfterBatch(Logger, batch.Index, failed.Count);
                    stop = true;
                    continue;
                }

                foreach (var failure in failed)
                {
                    foreach (var dependent in _plan.Graph.TransitiveDependents(failure))
                    {
                        _skipped.TryAdd(dependent, $"it depends on {failure.Describe()}, which failed");
                    }
                }
            }

            // Plan section 2.4, step 4 (edge pass): after all objects exist, before the outputs. Part A kinds now;
            // part B kinds plug into the same pass through the plan's EdgeKinds registry.
            await RunEdgePassAsync(stop, cancellationToken).ConfigureAwait(false);

            var (outputs, secureOutputs) = EvaluateOutputs(cancellationToken);
            string? secureFile = WriteSecureOutputs(secureOutputs);

            // Plan section 2.4, step 8: detach (Incremental) or delete (Complete) what the template no longer declares.
            await RunModePassAsync(stop, cancellationToken).ConfigureAwait(false);

            var operations = _plan.Batches.SelectMany(b => b.Instances).Select(i => _operations[i]).Concat(_modeOperations).ToList();
            var changes = _plan.Batches.SelectMany(b => b.Instances).Where(_changes.ContainsKey).Select(i => _changes[i]).Concat(_modeChanges).ToList();
            bool anyFailure = operations.Any(o => o.Status is OperationStatus.Failed or OperationStatus.Skipped) || _diagnostics.Any(d => d.IsError);
            var status = anyFailure ? DeploymentStatus.Failed : DeploymentStatus.Succeeded;

            await _options.StateSink.CompletedAsync(status, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            var result = new DeploymentResult(_plan.DeploymentName, status, operations, changes, outputs, secureOutputs, _diagnostics, secureFile, stopwatch.Elapsed);
            EngineLog.Completed(Logger, _plan.DeploymentName, status.ToString(), result.SucceededCount, result.FailedCount, result.SkippedCount, result.IgnoredCount, stopwatch.ElapsedMilliseconds);
            return result;
        }

        // ---- edge pass ----

        /// <summary>
        /// Plan section 1.4: reconciles the relationship properties of every resource applied in this run (target-side
        /// sets are authoritative, principal-side declarations are additive with state-tracked removals), records the
        /// principal-side edges in the state and folds the deltas into the resource's change. A resource whose edges
        /// cannot be validated or applied is reported as failed. Skipped entirely (with a warning) when the run
        /// stopped at a batch boundary; a no-op for templates without relationship properties.
        /// </summary>
        private async Task RunEdgePassAsync(bool stoppedEarly, CancellationToken cancellationToken)
        {
            var candidates = _plan.Instances
                .Where(i => !i.IsSymbolic && !i.IsIgnored && _operations.TryGetValue(i, out var operation) && operation.Status == OperationStatus.Succeeded)
                .ToList();
            var declaring = candidates.Where(i => EdgePlanner.DeclaresEdges(i, _options.State)).ToList();
            if (declaring.Count == 0)
            {
                return;
            }

            if (stoppedEarly)
            {
                EngineLog.EdgePassSkipped(Logger);
                _diagnostics.Add(Diagnostic.Warning(EngineDiagnosticCodes.EdgePassSkipped, "The edge pass did not run because an earlier batch failed; relationships (members, owners, memberOf, ownerOf, administrativeUnits, manager) were not reconciled. Run the deployment again once the failure is fixed."));
                return;
            }

            var createdIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in _createdThisRun.Keys)
            {
                if (_plan.Resolver.TryGetGraphId(id, out var graphId))
                {
                    createdIds.Add(Edge.NormalizeId(graphId));
                }
            }

            var context = new EdgeGraphContext(_owner._graph, _plan.RetryPolicy, _plan.Resolver, _plan.Instances, Logger, _plan.SignedInPrincipalId);
            EdgePlan edgePlan;
            EdgeApplyResult result;
            try
            {
                edgePlan = await new EdgePlanner(_plan, context, _options.State, PlanMode.Create, createdIds).PlanAsync(candidates, cancellationToken).ConfigureAwait(false);
                EngineLog.EdgePassStarting(Logger, edgePlan.Rows.Count, edgePlan.Operations.Count);
                _diagnostics.AddRange(edgePlan.Diagnostics);
                result = await EdgeExecutor.ApplyAsync(edgePlan, context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is GraphException or AzCliException or ToolchainException or ProviderException or ExpressionException or InvalidOperationException)
            {
                foreach (var instance in declaring)
                {
                    await FailEdgesAsync(instance, EngineDiagnosticCodes.EdgeApplyFailed, $"the edge pass failed: {ex.Message}", cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            foreach (var row in edgePlan.Rows)
            {
                var instance = row.Declarer;
                if (_changes.TryGetValue(instance, out var change))
                {
                    _changes[instance] = EdgeChanges.Merge(change, row);
                }

                var errors = row.Errors.Concat(result.ErrorsFor(row)).ToList();
                if (errors.Count > 0)
                {
                    string code = row.HasErrors ? EngineDiagnosticCodes.EdgeValidationFailed : EngineDiagnosticCodes.EdgeApplyFailed;
                    await FailEdgesAsync(instance, code, string.Join(" ", errors), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (row.RecordsEdges)
                {
                    await _options.StateSink.ResourceEdgesAppliedAsync(instance, EdgePlan.RecordingsFor(row, result), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task FailEdgesAsync(ResourceInstance instance, string code, string message, CancellationToken cancellationToken)
        {
            EngineLog.EdgeValidationFailed(Logger, instance.Id.Id, message);
            if (_operations.TryGetValue(instance, out var operation))
            {
                _operations[instance] = operation with { Status = OperationStatus.Failed, Error = message, ErrorCode = code };
            }

            await _options.StateSink.ResourceEdgesFailedAsync(instance, message, cancellationToken).ConfigureAwait(false);
        }

        // ---- mode pass ----

        /// <summary>
        /// State entries absent from the expanded instance set: Incremental drops them from the state with a warning;
        /// Complete deletes the objects in reverse dependency order through their providers (inside the retry
        /// policy), stopping at the first failure unless <see cref="ExecuteOptions.ContinueOnError"/>. Skipped when the
        /// run already stopped at a batch boundary (the instance set may not have been applied in full) or when no
        /// state was supplied.
        /// </summary>
        private async Task RunModePassAsync(bool stoppedEarly, CancellationToken cancellationToken)
        {
            var state = _options.State;
            if (state is null)
            {
                return;
            }

            var orphans = ModePass.FindOrphans(state, _plan, out var undecided);
            foreach (var (type, plannedId) in undecided)
            {
                EngineLog.ModePassUndecided(Logger, type, plannedId);
            }

            int batchIndex = _plan.Batches.Count + 1;
            bool stop = stoppedEarly && _plan.Mode == DeploymentMode.Complete;
            EdgeGraphContext? edgeContext = null;
            foreach (var entry in orphans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_plan.Mode == DeploymentMode.Incremental)
                {
                    EngineLog.ResourceDetached(Logger, entry.Id.Id);
                    _modeChanges.Add(ResourceChange.Ignore(entry.Id, entry.Type, ModePass.DetachReason));
                    _modeOperations.Add(new ResourceOperation(entry.Id, entry.Type, batchIndex, OperationStatus.Detached, ChangeType.Ignore, ChangeMode.None, entry.GraphId, null, null, TimeSpan.Zero));
                    _diagnostics.Add(Diagnostic.Warning(EngineDiagnosticCodes.ResourceDetached, $"'{entry.Id.Id}' is recorded in the state but no longer declared in the template; it was detached from the state and the object was left in the tenant (use --mode Complete to delete it)."));
                    await _options.StateSink.ResourceDetachedAsync(entry, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (stop)
                {
                    _modeOperations.Add(new ResourceOperation(entry.Id, entry.Type, batchIndex, OperationStatus.Skipped, ChangeType.Delete, ChangeMode.Delete, entry.GraphId, "an earlier operation failed", EngineDiagnosticCodes.ResourceSkipped, TimeSpan.Zero));
                    continue;
                }

                EngineLog.DeletingOrphan(Logger, entry.Id.Id, entry.GraphId);
                if (_owner._registry.TryGet(entry.Type, out var provider) && provider is IDeleteWarningSource warnings && warnings.DescribeDeleteRisk(entry, null) is { } risk)
                {
                    EngineLog.DeleteWarning(Logger, entry.Id.Id, risk);
                    _diagnostics.Add(Diagnostic.Warning(EngineDiagnosticCodes.DestructiveDelete, $"'{entry.Id.Id}': {risk}"));
                }

                // Plan section 1.4: the edges this deployment recorded for the resource go before the resource itself.
                if (entry.Relationships is { Count: > 0 })
                {
                    edgeContext ??= new EdgeGraphContext(_owner._graph, _plan.RetryPolicy, _plan.Resolver, _plan.Instances, Logger, _plan.SignedInPrincipalId);
                    try
                    {
                        int removed = await RecordedEdges.RemoveAsync(entry, _plan.Catalog, _plan.EdgeKinds, edgeContext, cancellationToken).ConfigureAwait(false);
                        EngineLog.RecordedEdgesRemoved(Logger, entry.Id.Id, removed);
                    }
                    catch (Exception ex) when (ex is GraphException or AzCliException or ToolchainException)
                    {
                        EngineLog.EdgeApplyFailed(Logger, $"remove recorded edges of {entry.Id.Id}", ex.Message);
                    }
                }

                var outcome = await StateDeletion.DeleteAsync(entry, _owner._registry, _owner._graph, _plan.RetryPolicy, cancellationToken).ConfigureAwait(false);
                if (outcome.Succeeded)
                {
                    if (outcome.Status == DeleteStatus.AlreadyGone)
                    {
                        EngineLog.OrphanAlreadyGone(Logger, entry.Id.Id);
                    }

                    _modeChanges.Add(new ResourceChange { Id = entry.Id, ResourceType = entry.Type, Type = ChangeType.Delete, Mode = ChangeMode.Delete, GraphId = entry.GraphId, Reasons = [ModePass.DeleteReason] });
                    _modeOperations.Add(new ResourceOperation(entry.Id, entry.Type, batchIndex, OperationStatus.Succeeded, ChangeType.Delete, ChangeMode.Delete, entry.GraphId, null, null, outcome.Duration));
                    await _options.StateSink.ResourceDeletedAsync(entry, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                string error = outcome.Error ?? "unknown error";
                EngineLog.DeleteFailed(Logger, entry.Id.Id, error);
                _modeChanges.Add(ResourceChange.Unsupported(entry.Id, entry.Type, ChangeType.Delete, error));
                _modeOperations.Add(new ResourceOperation(entry.Id, entry.Type, batchIndex, OperationStatus.Failed, ChangeType.Delete, ChangeMode.Delete, entry.GraphId, error, outcome.ErrorCode ?? EngineDiagnosticCodes.ResourceDeleted, outcome.Duration));
                await _options.StateSink.ResourceDeleteFailedAsync(entry, error, cancellationToken).ConfigureAwait(false);
                stop = !_options.ContinueOnError;
            }
        }

        // ---- one resource ----

        private async Task ApplyOneAsync(ResourceInstance instance, int batchIndex, CancellationToken cancellationToken)
        {
            // Dependents of a failure are recorded as skipped at the batch boundary (ExecuteAsync) and never reach here.
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await _options.StateSink.ResourceStartingAsync(instance, cancellationToken).ConfigureAwait(false);
                await ApplyCoreAsync(instance, batchIndex, stopwatch, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ProviderException ex)
            {
                await FailAsync(instance, batchIndex, ex.Code, ex.Message, stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);
            }
            catch (ExpressionException ex)
            {
                await FailAsync(instance, batchIndex, ex.Code, $"{instance.Describe()}: {ex.MessageWithOffset}", stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is GraphException or AzCliException or ToolchainException)
            {
                await FailAsync(instance, batchIndex, EngineDiagnosticCodes.ResourceApplyFailed, ex.Message, stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or JsonException or NotSupportedException)
            {
                await FailAsync(instance, batchIndex, EngineDiagnosticCodes.ResourceApplyFailed, $"{ex.GetType().Name}: {ex.Message}", stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task ApplyCoreAsync(ResourceInstance instance, int batchIndex, Stopwatch stopwatch, CancellationToken cancellationToken)
        {
            if (instance.Condition is null)
            {
                instance.Condition = InstanceEvaluator.EvaluateCondition(_plan.Evaluator, instance.Declaration, instance.Context, out var conditionError);
                if (conditionError is not null || instance.Condition is null)
                {
                    await FailAsync(instance, batchIndex, EngineDiagnosticCodes.ConditionUnresolved, $"{instance.Describe()}: {conditionError ?? "the condition depends on a value that is not available at this point of the deployment"}", stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            if (instance.IsIgnored)
            {
                _changes[instance] = ResourceChange.Ignore(instance.Id, instance.Type, "condition evaluated to false");
                _operations[instance] = new ResourceOperation(instance.Id, instance.Type, batchIndex, OperationStatus.Ignored, ChangeType.Ignore, ChangeMode.None, null, null, null, stopwatch.Elapsed);
                await _options.StateSink.ResourceIgnoredAsync(instance, "condition evaluated to false", cancellationToken).ConfigureAwait(false);
                return;
            }

            SymbolicNames.LinkParent(_plan, instance);
            InstanceEvaluator.EvaluateProperties(_plan.Evaluator, instance);
            if (instance.HasUnknownProperties)
            {
                var leaves = InstanceEvaluator.UnknownLeaves(instance);
                string detail = string.Join("; ", leaves.Select(l => $"{l.Path} = {l.Symbol}"));
                await FailAsync(instance, batchIndex, EngineDiagnosticCodes.UnresolvedDeployTimeValue, $"{instance.Describe()}: some properties depend on values that are not available (a referenced resource was not deployed, or a function is not implemented yet): {detail}", stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);
                return;
            }

            var provider = _owner._registry.Get(instance.Type);
            instance.RetryHint = instance.Dependencies.Any(_createdThisRun.ContainsKey) ? RetryHint.ExpectReplicationLag : RetryHint.Default;

            // Name sugar and live-data validation (resourceAppId → resourceId, appRole → appRoleId, scopes → scope...) before the key is derived.
            await WithRetryAsync(async ct => { await provider.PrepareAsync(instance, _owner._graph, ct).ConfigureAwait(false); return true; }, RetryHint.Default, cancellationToken).ConfigureAwait(false);

            var key = provider.ResolveKey(instance);
            if (!key.IsResolved)
            {
                await FailAsync(instance, batchIndex, EngineDiagnosticCodes.ResourceKeyUnresolved, $"{instance.Describe()}: the Graph key cannot be resolved ({key.Describe()}).", stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Reads use the default hint: a 404 here means "create", not "not replicated yet". The pinned Graph id from
            // the state goes first; the provider falls back to the key when the pinned object is gone (drift).
            string? stateId = _options.State?.GraphIdOf(instance.Id.Id);
            var existing = await WithRetryAsync(ct => provider.ReadAsync(key, stateId, _owner._graph, ct), RetryHint.Default, cancellationToken).ConfigureAwait(false);
            if (stateId is not null && !string.Equals(existing?.GraphId, stateId, StringComparison.OrdinalIgnoreCase))
            {
                EngineLog.StateDrift(Logger, instance.Id.Id, stateId, existing is null ? "not found; it will be created again" : $"found under a different id ({existing.GraphId}); the state will be re-pinned");
            }

            var change = provider.Plan(instance, existing, _planOptions);
            _changes[instance] = change;
            if (change.HasError)
            {
                await FailAsync(instance, batchIndex, EngineDiagnosticCodes.ImmutablePropertyChanged, change.UnsupportedReason!, stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);
                return;
            }

            EngineLog.Applying(Logger, instance.Id.Id, change.Type.ToString(), change.Mode.ToString());
            AppliedResource applied;
            switch (change.Type)
            {
                case ChangeType.Ignore:
                    _operations[instance] = new ResourceOperation(instance.Id, instance.Type, batchIndex, OperationStatus.Ignored, ChangeType.Ignore, ChangeMode.None, null, null, null, stopwatch.Elapsed);
                    await _options.StateSink.ResourceIgnoredAsync(instance, change.Reasons.Count > 0 ? string.Join("; ", change.Reasons) : "ignored by the provider", cancellationToken).ConfigureAwait(false);
                    return;

                case ChangeType.NoChange:
                    // Nothing was written: the live object is republished as is and the entry keeps the extra data
                    // the provider recorded when it last wrote (keyId / endDateTime of a secret, derived ids...).
                    applied = new AppliedResource(instance.Id, existing!.GraphId, existing.Raw, key, _options.State?.Find(instance.Id.Id)?.Extra);
                    break;

                case ChangeType.Create:
                    applied = await CreateAsync(provider, instance, key, cancellationToken).ConfigureAwait(false);
                    break;

                case ChangeType.Modify when change.Mode == ChangeMode.Recreate:
                    await WithRetryAsync(async ct =>
                    {
                        await provider.DeleteAsync(StateEntryFor(instance, existing!, key), _owner._graph, ct).ConfigureAwait(false);
                        return true;
                    }, instance.RetryHint, cancellationToken).ConfigureAwait(false);
                    applied = await CreateAsync(provider, instance, key, cancellationToken).ConfigureAwait(false);
                    break;

                case ChangeType.Modify:
                    applied = await WithRetryAsync(ct => provider.UpdateAsync(instance, existing!, change, _owner._graph, ct), instance.RetryHint, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    throw new InvalidOperationException($"{instance.Describe()}: the provider planned an unexpected change type {change.Type}.");
            }

            _plan.Resolver.Publish(instance.Id, applied.Live, applied.GraphId, instance.PlannedId);
            if (change.Type == ChangeType.Create || change.Mode == ChangeMode.Recreate)
            {
                _createdThisRun[instance.PlannedId] = 1;
                _createdThisRun[instance.Id] = 1;
            }

            await _options.StateSink.ResourceAppliedAsync(instance, change, applied, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            _operations[instance] = new ResourceOperation(instance.Id, instance.Type, batchIndex, OperationStatus.Succeeded, change.Type, change.Mode, applied.GraphId, null, null, stopwatch.Elapsed);
            EngineLog.Applied(Logger, instance.Id.Id, change.Type.ToString(), applied.GraphId, stopwatch.ElapsedMilliseconds);
        }

        /// <summary>Creates, treating "already exists" as idempotent success: the object is re-read by key and adopted.</summary>
        private async Task<AppliedResource> CreateAsync(IResourceProvider provider, ResourceInstance instance, ResourceKey key, CancellationToken cancellationToken)
        {
            try
            {
                return await WithRetryAsync(ct => provider.CreateAsync(instance, _owner._graph, ct), instance.RetryHint, cancellationToken).ConfigureAwait(false);
            }
            catch (GraphException ex) when (ex.IsAlreadyExists)
            {
                EngineLog.AdoptingExisting(Logger, instance.Id.Id);
                var existing = await WithRetryAsync(ct => provider.ReadAsync(key, null, _owner._graph, ct), RetryHint.ExpectReplicationLag, cancellationToken).ConfigureAwait(false);
                if (existing is null)
                {
                    throw;
                }

                return new AppliedResource(instance.Id, existing.GraphId, existing.Raw, key);
            }
        }

        private static StateEntry StateEntryFor(ResourceInstance instance, ExistingResource existing, ResourceKey key)
        {
            return new StateEntry
            {
                Id = instance.Id,
                Type = instance.Type,
                ApiVersion = instance.ApiVersion,
                Key = key.Values,
                GraphId = existing.GraphId,
                ParentGraphId = key.ParentGraphId,
            };
        }

        private Task<T> WithRetryAsync<T>(Func<CancellationToken, Task<T>> operation, RetryHint hint, CancellationToken cancellationToken)
        {
            return _plan.RetryPolicy is null
                ? operation(cancellationToken)
                : _plan.RetryPolicy.ExecuteAsync(operation, hint, null, cancellationToken);
        }

        // ---- bookkeeping ----

        private async Task FailAsync(ResourceInstance instance, int batchIndex, string code, string message, TimeSpan elapsed, CancellationToken cancellationToken)
        {
            RecordFailure(instance, batchIndex, code, message, elapsed);
            await _options.StateSink.ResourceFailedAsync(instance, message, cancellationToken).ConfigureAwait(false);
        }

        private void RecordFailure(ResourceInstance instance, int batchIndex, string code, string message, TimeSpan elapsed)
        {
            EngineLog.ApplyFailed(Logger, instance.Id.Id, message);
            _changes.TryGetValue(instance, out var change);
            _operations[instance] = new ResourceOperation(instance.Id, instance.Type, batchIndex, OperationStatus.Failed, change?.Type, change?.Mode, change?.GraphId, message, code, elapsed);
        }

        private async Task RecordSkippedAsync(ResourceInstance instance, int batchIndex, string reason, CancellationToken cancellationToken)
        {
            EngineLog.Skipped(Logger, instance.Id.Id, reason);
            _operations[instance] = new ResourceOperation(instance.Id, instance.Type, batchIndex, OperationStatus.Skipped, null, null, null, reason, EngineDiagnosticCodes.ResourceSkipped, TimeSpan.Zero);
            await _options.StateSink.ResourceSkippedAsync(instance, reason, cancellationToken).ConfigureAwait(false);
        }

        // ---- outputs ----

        private (JsonObject Outputs, JsonObject SecureOutputs) EvaluateOutputs(CancellationToken cancellationToken)
        {
            var outputs = new JsonObject();
            var secure = new JsonObject();

            // Outputs are evaluated one at a time after every batch; the trace notes which published objects each
            // output's reference() calls touched, so a null that stands for a secret Graph only returned in an earlier
            // run (LiveSecrets) is reported as a warning instead of silently or fatally.
            var trace = new SecretAvailabilityTrace(_plan.Resolver);
            var context = _plan.Context.ForScope(EvaluationScope.Output) with { References = trace };
            var document = _plan.Document;

            foreach (var output in document.Outputs.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var valuePath = output.Path.Append("value");
                trace.Reset();
                try
                {
                    if (output.Condition is not null)
                    {
                        var condition = _plan.Evaluator.EvaluateTree(output.Condition, context);
                        if (!JsonValues.TryGetBool(condition, out bool include))
                        {
                            OutputError(document, output.Name, output.Path.Append("condition"), "the condition does not evaluate to a boolean.");
                            continue;
                        }

                        if (!include)
                        {
                            continue;
                        }
                    }

                    var value = _plan.Evaluator.EvaluateTree(output.Value, context);
                    if (UnknownJson.ContainsUnknown(value))
                    {
                        var leaves = JsonObjects.UnknownLeaves(value, JsonPath.Root);
                        OutputError(document, output.Name, valuePath, $"the value depends on something that was not resolved during this run: {string.Join("; ", leaves.Select(l => l.Symbol))}.");
                        continue;
                    }

                    string type = output.Type ?? "string";
                    bool isSecure = output.IsSecure || SecretJson.ContainsSecret(value);
                    if (trace.UnavailableSecrets.Count > 0 && LiveSecrets.ContainsNull(value))
                    {
                        // The value is (or holds) the null a provider published in place of a one-time secret
                        // (reference(...).secretText of a password credential that was not created in this run).
                        string sources = string.Join(", ", trace.UnavailableSecrets.Select(id => $"'{id.Id}'"));
                        _diagnostics.Add(Diagnostic.Warning(
                            EngineDiagnosticCodes.SecretNotAvailableOnRerun,
                            $"Output '{output.Name}' is null: the secret of {sources} is only available in the run that creates it (Graph returns secretText once and it was not rotated in this run). The output is reported as null and left out of the secure outputs file; change rotationTag to rotate the credential and obtain a new secret.",
                            valuePath,
                            document.LocationOf(valuePath),
                            document.SourcePath));
                        outputs[output.Name] = new JsonObject { ["type"] = type, ["value"] = isSecure ? SecretJson.Redact(value) : JsonValues.Clone(value) };
                        continue;
                    }

                    if (isSecure)
                    {
                        secure[output.Name] = new JsonObject { ["type"] = type, ["value"] = SecretJson.Reveal(value) };
                        outputs[output.Name] = new JsonObject { ["type"] = type, ["value"] = _options.RevealSecureOutputs ? SecretJson.Reveal(value) : SecretJson.Redact(value) };
                    }
                    else
                    {
                        outputs[output.Name] = new JsonObject { ["type"] = type, ["value"] = JsonValues.Clone(value) };
                    }
                }
                catch (ExpressionException ex)
                {
                    OutputError(document, output.Name, valuePath, ex.MessageWithOffset);
                }
            }

            return (outputs, secure);
        }

        private void OutputError(TemplateDocument document, string name, JsonPath path, string detail)
        {
            _diagnostics.Add(Diagnostic.Error(EngineDiagnosticCodes.OutputEvaluationFailed, $"Output '{name}' could not be evaluated: {detail}", path, document.LocationOf(path), document.SourcePath));
        }

        /// <summary>
        /// Forwards to the plan's resolver and records the ids of published objects whose one-time secret is not
        /// available in this run (<see cref="LiveSecrets.IsSecretTextUnavailable"/>), per output.
        /// </summary>
        private sealed class SecretAvailabilityTrace : IReferenceResolver
        {
            private readonly IReferenceResolver _inner;
            private readonly List<ResourceIdentifier> _unavailable = [];

            public SecretAvailabilityTrace(IReferenceResolver inner)
            {
                _inner = inner;
            }

            public IReadOnlyList<ResourceIdentifier> UnavailableSecrets => _unavailable;

            public void Reset()
            {
                _unavailable.Clear();
            }

            public ReferenceResult Resolve(ResourceIdentifier id, string? apiVersion)
            {
                var result = _inner.Resolve(id, apiVersion);
                if (result is ReferenceResult.Live live && LiveSecrets.IsSecretTextUnavailable(live.Object) && !_unavailable.Contains(id))
                {
                    _unavailable.Add(id);
                }

                return result;
            }
        }

        private string? WriteSecureOutputs(JsonObject secureOutputs)
        {
            if (string.IsNullOrWhiteSpace(_options.SecureOutputsFile))
            {
                return null;
            }

            string json = secureOutputs.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            OwnerOnlyFile.WriteAllText(_options.SecureOutputsFile, json);
            EngineLog.SecureOutputsWritten(Logger, _options.SecureOutputsFile);
            return _options.SecureOutputsFile;
        }
    }
}
