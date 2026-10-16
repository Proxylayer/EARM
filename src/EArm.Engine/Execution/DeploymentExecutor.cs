using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Secrets;
using EArm.Engine.Edges;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Expressions;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Values;
using Microsoft.Extensions.Logging;

namespace EArm.Engine.Execution;

/// <summary>
/// Walks the plan's batches; before each batch resolves the symbolic names whose
/// inputs are now live; inside a batch applies instances in parallel (evaluate → key → read → plan → create/update),
/// publishing every live object to the resolver as it lands; fails fast at the batch boundary (in-flight work
/// finishes, everything later is skipped) unless <see cref="ExecuteOptions.ContinueOnError"/>, in which case only
/// the transitive dependents of a failure are skipped (recorded before their batch starts, never
/// attempted, their symbolic names never resolved); then evaluates the outputs. Nothing is persisted locally except the
/// secure outputs file. The end phase (edges, outputs, secure outputs file, orphan report, completion) never observes the
/// caller's cancellation: it records what already happened and only then reports the cancellation. Nothing the template does not
/// declare is ever deleted: the orphan report only lists it (<see cref="DeploymentResult.Orphans"/>).
/// </summary>
public sealed class DeploymentExecutor
{
    private readonly ProviderRegistry _registry;
    private readonly IGraphClient _graph;
    private readonly ILogger _logger;
    private readonly ReplicationWait _replicationWait;

    /// <param name="registry">The resource providers.</param>
    /// <param name="graph">The Graph client.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <param name="replicationWait">How long an object is looked for after a create with an uncertain outcome; default <see cref="ReplicationWait.Default"/>.</param>
    public DeploymentExecutor(ProviderRegistry registry, IGraphClient graph, ILogger<DeploymentExecutor> logger, ReplicationWait? replicationWait = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _graph = graph;
        _logger = logger;
        _replicationWait = replicationWait ?? ReplicationWait.Default;
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
        private readonly ConcurrentDictionary<string, ResourceInstance> _graphClaims = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ResourceInstance, string> _skipped = [];
        private readonly List<Diagnostic> _diagnostics = [];
        private readonly object _diagnosticsGate = new();
        private readonly ConcurrentDictionary<ResourceInstance, List<Diagnostic>> _instanceDiagnostics = new();
        private readonly SemaphoreSlim _secretsFileGate = new(1, 1);
        private readonly LaggingDeleteRetry _laggingDeletes;
        private IReadOnlyList<ResourceChange> _orphans = [];
        private bool _oneTimeSecretsWritten;
        private bool _cancelledInEndPhase;

        public Run(DeploymentExecutor owner, DeploymentPlan plan, ExecuteOptions options)
        {
            _owner = owner;
            _plan = plan;
            _options = options;
            _planOptions = plan.Options with { PlanMode = PlanMode.Create };
            _laggingDeletes = new LaggingDeleteRetry(owner._replicationWait, owner._logger);
        }

        private ILogger Logger => _owner._logger;

        public async Task<DeploymentResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();

            // Every secure parameter value is masked from here on in whatever an error message echoes (review finding S-02).
            SecretRegistration.RegisterDocument(_plan.Document);

            bool stop = false;
            bool cancelled = false;
            bool aborted = false;
            try
            {
                stop = await RunBatchesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                stop = true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // An unexpected failure of the engine itself (not of one resource: those are caught per resource): the end phase below must still run, so
                // what was applied is reported and the secrets reach the file (review round 2, S2-09).
                aborted = true;
                stop = true;
                string message = SecretRegistration.Scrub($"The run was aborted by an unexpected error ({ex.GetType().Name}): {ex.Message}");
                EngineLog.ApplyFailed(Logger, _plan.DeploymentName, message);
                AddDiagnostic(Diagnostic.Error(EngineDiagnosticCodes.ResourceApplyFailed, message));
            }

            // The end phase records work that already happened (objects written to the tenant, one-time secrets, the outcome of the run): recording
            // never observes the caller's cancellation and is guarded so an I/O failure cannot skip the steps after it (E-03). The steps that WRITE
            // to the tenant again (edges) do observe it, between operations: Ctrl-C stops new writes (review round 2, S2-10).
            var none = CancellationToken.None;
            var outputs = new JsonObject();
            var secureOutputs = new JsonObject();
            string? secureFile = null;
            List<ResourceOperation> operations = [];
            List<ResourceChange> changes = [];
            DeploymentStatus status = DeploymentStatus.Failed;
            if (cancelled || aborted)
            {
                string why = cancelled ? "the run was cancelled" : "the run was aborted by an unexpected error";
                foreach (var batch in _plan.Batches)
                {
                    foreach (var instance in batch.Instances.Where(i => !_operations.ContainsKey(i)))
                    {
                        RecordSkipped(instance, batch.Index, why);
                    }
                }
            }

            // Edge pass: after all objects exist, before the outputs. Part A kinds now;
            // part B kinds plug into the same pass through the plan's EdgeKinds registry (the providers add the part B kinds).
            await GuardedAsync(() => RunEdgePassAsync(stop, cancellationToken)).ConfigureAwait(false);

            (outputs, secureOutputs) = EvaluateOutputs(none);
            secureFile = WriteSecureOutputs(secureOutputs);

            // Orphan report (read-only): what the template does not declare is listed, never deleted.
            // A cancelled or aborted run does not know whether the instance set was applied in full, so it judges nothing.
            if (!cancelled && !aborted)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _cancelledInEndPhase = true;
                }
                else
                {
                    await GuardedAsync(() => RunOrphanReportAsync(stop, cancellationToken)).ConfigureAwait(false);
                }
            }

            cancelled |= _cancelledInEndPhase;

            FlushInstanceDiagnostics(_plan.Batches.SelectMany(b => b.Instances));
            operations = _plan.Batches.SelectMany(b => b.Instances).Where(_operations.ContainsKey).Select(i => _operations[i]).ToList();
            changes = _plan.Batches.SelectMany(b => b.Instances).Where(_changes.ContainsKey).Select(i => _changes[i]).ToList();
            bool anyFailure = aborted || operations.Any(o => o.Status is OperationStatus.Failed or OperationStatus.Skipped) || DiagnosticsSnapshot().Any(d => d.IsError);
            status = cancelled ? DeploymentStatus.Cancelled : anyFailure ? DeploymentStatus.Failed : DeploymentStatus.Succeeded;

            stopwatch.Stop();
            if (cancelled)
            {
                EngineLog.Completed(Logger, _plan.DeploymentName, "Cancelled", operations.Count(o => o.Status == OperationStatus.Succeeded), operations.Count(o => o.Status == OperationStatus.Failed), operations.Count(o => o.Status == OperationStatus.Skipped), operations.Count(o => o.Status == OperationStatus.Ignored), stopwatch.ElapsedMilliseconds);
                throw new OperationCanceledException("The deployment was cancelled; the result and the secure outputs file record what had already been applied.", cancellationToken);
            }

            var result = new DeploymentResult(_plan.DeploymentName, status, operations, changes, outputs, secureOutputs, DiagnosticsSnapshot(), secureFile, stopwatch.Elapsed, _orphans);
            EngineLog.Completed(Logger, _plan.DeploymentName, status.ToString(), result.SucceededCount, result.FailedCount, result.SkippedCount, result.IgnoredCount, stopwatch.ElapsedMilliseconds);
            return result;
        }

        /// <summary>Walks the batches; returns true when the run stopped at a batch boundary because a resource failed.</summary>
        private async Task<bool> RunBatchesAsync(CancellationToken cancellationToken)
        {
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
                        RecordSkipped(instance, batch.Index, "an earlier batch failed");
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
                        RecordSkipped(instance, batch.Index, skipReason);
                    }
                }

                var active = batch with { Instances = batch.Instances.Where(i => !_operations.ContainsKey(i)).ToList() };
                foreach (var (instance, reason) in SymbolicNames.Resolve(_plan, active, Logger))
                {
                    RecordFailure(instance, batch.Index, EngineDiagnosticCodes.SymbolicNameUnresolved, $"{instance.Type} with deploy-time name {instance.PlannedId.NameLeaf}: {reason}", TimeSpan.Zero);
                }

                var pending = active.Instances.Where(i => !_operations.ContainsKey(i)).ToList();
                try
                {
                    await Parallel.ForEachAsync(pending, parallel, (instance, ct) => new ValueTask(ApplyOneAsync(instance, batch.Index, ct))).ConfigureAwait(false);
                }
                finally
                {
                    FlushInstanceDiagnostics(batch.Instances);
                }

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

            return stop;
        }

        /// <summary>Runs one end-phase step; an unexpected failure is reported as an error diagnostic instead of ending the run (E-03c).</summary>
        private async Task GuardedAsync(Func<Task> step)
        {
            try
            {
                await step().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Anything else must not skip the steps after it either (the secure outputs, the completion record): report it and go on (review round 2, S2-09).
                string message = SecretRegistration.Scrub($"A step of the end phase failed unexpectedly ({ex.GetType().Name}): {ex.Message}");
                EngineLog.ApplyFailed(Logger, _plan.DeploymentName, message);
                AddDiagnostic(Diagnostic.Error(EngineDiagnosticCodes.ResourceApplyFailed, message));
            }
        }

        private void AddDiagnostic(Diagnostic diagnostic)
        {
            lock (_diagnosticsGate)
            {
                _diagnostics.Add(diagnostic);
            }
        }

        /// <summary>A finding about one resource, raised while resources are applied in parallel: kept per instance and reported in plan order (<see cref="FlushInstanceDiagnostics"/>), so the result does not depend on timing.</summary>
        private void AddInstanceDiagnostic(ResourceInstance instance, Diagnostic diagnostic)
        {
            var list = _instanceDiagnostics.GetOrAdd(instance, static _ => []);
            lock (list)
            {
                list.Add(diagnostic);
            }
        }

        private void FlushInstanceDiagnostics(IEnumerable<ResourceInstance> instances)
        {
            foreach (var instance in instances)
            {
                if (_instanceDiagnostics.TryRemove(instance, out var found))
                {
                    lock (_diagnosticsGate)
                    {
                        _diagnostics.AddRange(found);
                    }
                }
            }
        }

        private List<Diagnostic> DiagnosticsSnapshot()
        {
            lock (_diagnosticsGate)
            {
                return [.. _diagnostics];
            }
        }

        // ---- edge pass ----

        /// <summary>
        /// Reconciles the relationship properties of every resource applied in this run (target-side
        /// sets are authoritative, principal-side declarations are ensure-only: they add and never remove) and folds the deltas into the
        /// resource's change. A resource whose edges cannot be validated or applied is reported as failed. Skipped
        /// entirely (with a warning) when the run stopped at a batch boundary; a no-op for templates without relationship properties.
        /// </summary>
        private async Task RunEdgePassAsync(bool stoppedEarly, CancellationToken cancellationToken)
        {
            var candidates = _plan.Instances
                .Where(i => !i.IsSymbolic && !i.IsIgnored && _operations.TryGetValue(i, out var operation) && operation.Status == OperationStatus.Succeeded)
                .ToList();
            var declaring = candidates.Where(EdgePlanner.DeclaresEdges).ToList();
            if (declaring.Count == 0)
            {
                return;
            }

            if (stoppedEarly)
            {
                EngineLog.EdgePassSkipped(Logger);
                AddDiagnostic(Diagnostic.Warning(EngineDiagnosticCodes.EdgePassSkipped, "The edge pass did not run because an earlier batch failed; relationships (members, owners, memberOf, ownerOf, administrativeUnits, manager) were not reconciled. Run the deployment again once the failure is fixed."));
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                // Ctrl-C before the edge pass started: no new writes (the edges are reconciled by the next run).
                _cancelledInEndPhase = true;
                return;
            }

            // Resources that were declared but did not apply (failed, or skipped because of a failure under --continue-on-error): their principal-side
            // declarations (memberOf, ownerOf...) are unknown to this pass, so a target-side set they could belong to must not be pruned (review round 2, S2-05).
            var candidateSet = new HashSet<ResourceInstance>(candidates);
            var unreliable = _plan.Instances.Where(i => !i.IsSymbolic && !i.IsIgnored && !candidateSet.Contains(i)
                && (!_operations.TryGetValue(i, out var other) || other.Status is OperationStatus.Failed or OperationStatus.Skipped)).ToList();

            var createdIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in _createdThisRun.Keys)
            {
                if (_plan.Resolver.TryGetGraphId(id, out var graphId))
                {
                    createdIds.Add(Edge.NormalizeId(graphId));
                }
            }

            var context = new EdgeGraphContext(_owner._graph, _plan.RetryPolicy, _plan.Resolver, _plan.Instances, Logger, _plan.SignedInPrincipalId) { AppOnlyToken = _plan.AppOnlyToken };
            EdgePlan edgePlan;
            EdgeApplyResult result;
            try
            {
                edgePlan = await new EdgePlanner(_plan, context, PlanMode.Create, createdIds, unreliable).PlanAsync(candidates, cancellationToken).ConfigureAwait(false);
                EngineLog.EdgePassStarting(Logger, edgePlan.Rows.Count, edgePlan.Operations.Count);
                foreach (var diagnostic in edgePlan.Diagnostics)
                {
                    AddDiagnostic(diagnostic);
                }

                result = await EdgeExecutor.ApplyAsync(edgePlan, context, cancellationToken).ConfigureAwait(false);
                if (result.Cancelled)
                {
                    _cancelledInEndPhase = true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Cancelled while planning (read-only): nothing was written yet.
                _cancelledInEndPhase = true;
                return;
            }
            catch (Exception ex) when (ex is GraphException or GraphTransportException or ProviderException or ExpressionException or InvalidOperationException)
            {
                foreach (var instance in declaring)
                {
                    FailEdges(instance, EngineDiagnosticCodes.EdgeApplyFailed, $"the edge pass failed: {ex.Message}");
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
                    FailEdges(instance, code, string.Join(" ", errors));
                }
            }
        }

        private void FailEdges(ResourceInstance instance, string code, string message)
        {
            message = SecretRegistration.Scrub(message);
            EngineLog.EdgeValidationFailed(Logger, instance.Id.Id, message);
            if (_operations.TryGetValue(instance, out var operation))
            {
                _operations[instance] = operation with { Status = OperationStatus.Failed, Error = message, ErrorCode = code };
            }
        }

        // ---- orphan report ----

        /// <summary>
        /// The orphan report after the run (<see cref="OrphanReport"/>): live objects of the declared types that no declared resource matches, listed in
        /// <see cref="DeploymentResult.Orphans"/> and never deleted (Complete mode: every bounded type; Incremental mode: the retained assignments and grants of the
        /// declared principals). Read-only. Skipped when the run stopped at a batch boundary: the instance set was not applied in full.
        /// </summary>
        private async Task RunOrphanReportAsync(bool stoppedEarly, CancellationToken cancellationToken)
        {
            if (stoppedEarly)
            {
                if (_plan.Mode == DeploymentMode.Complete)
                {
                    AddDiagnostic(Diagnostic.Warning(EngineDiagnosticCodes.OrphanReportIncomplete, "The orphan report did not run because an earlier batch failed; undeclared objects were not listed. Nothing is ever deleted by Complete mode."));
                }

                return;
            }

            var result = await new OrphanReport(_plan, _owner._graph).RunAsync(IsSettled, cancellationToken).ConfigureAwait(false);
            _orphans = result.Rows;
            foreach (var diagnostic in result.Diagnostics)
            {
                AddDiagnostic(diagnostic);
            }
        }

        /// <summary>True when the instance was applied (or found unchanged) in this run: its live object is published.</summary>
        private bool IsSettled(ResourceInstance instance)
        {
            return _operations.TryGetValue(instance, out var operation) && operation.Status == OperationStatus.Succeeded;
        }

        // ---- one resource ----

        private async Task ApplyOneAsync(ResourceInstance instance, int batchIndex, CancellationToken cancellationToken)
        {
            // Dependents of a failure are recorded as skipped at the batch boundary (RunBatchesAsync) and never reach here.
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await ApplyCoreAsync(instance, batchIndex, stopwatch, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ProviderException ex)
            {
                RecordFailure(instance, batchIndex, ex.Code, ex.Message, stopwatch.Elapsed);
            }
            catch (ExpressionException ex)
            {
                RecordFailure(instance, batchIndex, ex.Code, $"{instance.Describe()}: {ex.MessageWithOffset}", stopwatch.Elapsed);
            }
            catch (Exception ex) when (ex is GraphException or GraphTransportException)
            {
                RecordFailure(instance, batchIndex, EngineDiagnosticCodes.ResourceApplyFailed, ex.Message, stopwatch.Elapsed);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or JsonException or NotSupportedException)
            {
                RecordFailure(instance, batchIndex, EngineDiagnosticCodes.ResourceApplyFailed, $"{ex.GetType().Name}: {ex.Message}", stopwatch.Elapsed);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Anything else (a null reference, a format error, a cancellation that is not the caller's such as an HTTP timeout) fails THIS resource only: it must
                // never escape into the parallel loop, where it would cancel the siblings and skip the end phase (review round 2, S2-09).
                RecordFailure(instance, batchIndex, EngineDiagnosticCodes.ResourceApplyFailed, $"{instance.Describe()}: unexpected error ({ex.GetType().Name}): {ex.Message}", stopwatch.Elapsed);
            }
        }

        private async Task ApplyCoreAsync(ResourceInstance instance, int batchIndex, Stopwatch stopwatch, CancellationToken cancellationToken)
        {
            if (instance.Condition is null)
            {
                instance.Condition = InstanceEvaluator.EvaluateCondition(_plan.Evaluator, instance.Declaration, instance.Context, out var conditionError);
                if (conditionError is not null || instance.Condition is null)
                {
                    RecordFailure(instance, batchIndex, EngineDiagnosticCodes.ConditionUnresolved, $"{instance.Describe()}: {conditionError ?? "the condition depends on a value that is not available at this point of the deployment"}", stopwatch.Elapsed);
                    return;
                }
            }

            if (instance.IsIgnored)
            {
                _changes[instance] = ResourceChange.Ignore(instance.Id, instance.Type, "condition evaluated to false");
                _operations[instance] = new ResourceOperation(instance.Id, instance.Type, batchIndex, OperationStatus.Ignored, ChangeType.Ignore, ChangeMode.None, null, null, null, stopwatch.Elapsed);
                return;
            }

            SymbolicNames.LinkParent(_plan, instance);
            InstanceEvaluator.EvaluateProperties(_plan.Evaluator, instance);
            SecretRegistration.RegisterTainted(instance.Properties);
            if (instance.HasUnknownProperties)
            {
                var leaves = InstanceEvaluator.UnknownLeaves(instance);
                string detail = string.Join("; ", leaves.Select(l => $"{l.Path} = {l.Symbol}"));
                RecordFailure(instance, batchIndex, EngineDiagnosticCodes.UnresolvedDeployTimeValue, $"{instance.Describe()}: some properties depend on values that are not available (a referenced resource was not deployed, or a function is not implemented yet): {detail}", stopwatch.Elapsed);
                return;
            }

            var provider = _owner._registry.Get(instance.Type);
            instance.RetryHint = instance.Dependencies.Any(_createdThisRun.ContainsKey) ? RetryHint.ExpectReplicationLag : RetryHint.Default;

            // Name sugar and live-data validation (resourceAppId → resourceId, appRole → appRoleId, scopes → scope...) before the key is derived.
            await WithRetryAsync(async ct => { await provider.PrepareAsync(instance, _owner._graph, ct).ConfigureAwait(false); return true; }, RetryHint.Default, cancellationToken).ConfigureAwait(false);

            var key = provider.ResolveKey(instance);
            if (!key.IsResolved)
            {
                RecordFailure(instance, batchIndex, EngineDiagnosticCodes.ResourceKeyUnresolved, $"{instance.Describe()}: the Graph key cannot be resolved ({key.Describe()}).", stopwatch.Elapsed);
                return;
            }

            // Reads use the default hint: a 404 here means "create", not "not replicated yet". Identity is resolved live, by key, on every run
            // (a renamed object is a new one).
            var existing = await WithRetryAsync(ct => provider.ReadAsync(key, _owner._graph, ct), RetryHint.Default, cancellationToken).ConfigureAwait(false);

            if (existing is not null && !TryClaim(instance, existing.GraphId, out var other))
            {
                EngineLog.DuplicateGraphObject(Logger, instance.Id.Id, other!.Id.Id, existing.GraphId);
                RecordFailure(instance, batchIndex, EngineDiagnosticCodes.DuplicateGraphObject, $"{instance.Describe()} and {other.Describe()} resolve to the same Graph object ({existing.GraphId}); two declarations must not manage one object. Remove one of them, or give them different keys.", stopwatch.Elapsed);
                return;
            }

            var change = provider.Plan(instance, existing, _planOptions);
            _changes[instance] = change;
            foreach (var reasonWarning in ProviderWarningReasons.DiagnosticsOf(instance, change))
            {
                AddInstanceDiagnostic(instance, reasonWarning);
            }

            if (change.HasError)
            {
                RecordFailure(instance, batchIndex, EngineDiagnosticCodes.ImmutablePropertyChanged, change.UnsupportedReason!, stopwatch.Elapsed);
                return;
            }

            EngineLog.Applying(Logger, instance.Id.Id, change.Type.ToString(), change.Mode.ToString());

            string? afterApplyFailure = null;
            AppliedResource applied;
            switch (change.Type)
            {
                case ChangeType.Ignore:
                    _operations[instance] = new ResourceOperation(instance.Id, instance.Type, batchIndex, OperationStatus.Ignored, ChangeType.Ignore, ChangeMode.None, null, null, null, stopwatch.Elapsed);
                    return;

                case ChangeType.NoChange:
                    // Nothing was written: the live object is republished as is.
                    applied = new AppliedResource(instance.Id, existing!.GraphId, existing.Raw, key);
                    break;

                case ChangeType.Create:
                {
                    var created = await CreateAsync(provider, instance, key, cancellationToken).ConfigureAwait(false);
                    applied = created.Applied;
                    afterApplyFailure = created.Failure;
                    if (created.AdoptedExisting is { } found && afterApplyFailure is null)
                    {
                        // "Already exists": the object was adopted; the declared properties are applied now, in this run, instead of at the next one.
                        applied = await ApplyPropertiesToAdoptedAsync(provider, instance, found, applied, cancellationToken).ConfigureAwait(false);
                    }

                    break;
                }

                case ChangeType.Modify when change.Mode == ChangeMode.Recreate:
                {
                    var recreated = await RecreateAsync(provider, instance, key, existing!, cancellationToken).ConfigureAwait(false);
                    if (recreated.Applied is null)
                    {
                        // Nothing was replaced (the old object is protected, or the replacement was not confirmed): the resource fails.
                        RecordFailure(instance, batchIndex, recreated.FailureCode ?? EngineDiagnosticCodes.ResourceApplyFailed, recreated.Failure ?? $"{instance.Describe()}: the object could not be replaced.", stopwatch.Elapsed);
                        return;
                    }

                    applied = recreated.Applied;
                    afterApplyFailure = recreated.Failure;
                    break;
                }

                case ChangeType.Modify:
                    try
                    {
                        applied = await WithRetryAsync(ct => provider.UpdateAsync(instance, existing!, change, _owner._graph, ct), instance.RetryHint, cancellationToken).ConfigureAwait(false);
                    }
                    catch (ProviderException ex) when (ex.Partial is { } partial)
                    {
                        // The primary write happened and a later step failed: what exists is recorded, the resource is reported failed.
                        applied = partial;
                        afterApplyFailure = PartialFailure(instance, partial, ex, "updated");
                    }

                    break;

                default:
                    throw new InvalidOperationException($"{instance.Describe()}: the provider planned an unexpected change type {change.Type}.");
            }

            SecretRegistration.RegisterTainted(applied.Live);
            if (!TryClaim(instance, applied.GraphId, out var claimedBy))
            {
                EngineLog.DuplicateGraphObject(Logger, instance.Id.Id, claimedBy!.Id.Id, applied.GraphId);
                AddInstanceDiagnostic(instance, Diagnostic.Error(EngineDiagnosticCodes.DuplicateGraphObject, $"{instance.Describe()} and {claimedBy.Describe()} resolve to the same Graph object ({applied.GraphId})."));
            }

            // A one-time secret exists only in this response: it goes to the owner-only file before anything else can fail (E-03d).
            await PersistOneTimeSecretsAsync(instance, applied).ConfigureAwait(false);

            foreach (var warning in applied.Warnings)
            {
                AddInstanceDiagnostic(instance, Diagnostic.Warning(warning.Code, $"'{instance.Id.Id}': {SecretRegistration.Scrub(warning.Message)}"));
            }

            _plan.Resolver.Publish(instance.Id, applied.Live, applied.GraphId, instance.PlannedId);
            if (change.Type == ChangeType.Create || change.Mode == ChangeMode.Recreate)
            {
                _createdThisRun[instance.PlannedId] = 1;
                _createdThisRun[instance.Id] = 1;
            }

            stopwatch.Stop();
            if (afterApplyFailure is not null)
            {
                RecordFailure(instance, batchIndex, EngineDiagnosticCodes.ResourceApplyFailed, afterApplyFailure, stopwatch.Elapsed, applied.GraphId);
                return;
            }

            _operations[instance] = new ResourceOperation(instance.Id, instance.Type, batchIndex, OperationStatus.Succeeded, change.Type, change.Mode, applied.GraphId, null, null, stopwatch.Elapsed);
            EngineLog.Applied(Logger, instance.Id.Id, change.Type.ToString(), applied.GraphId, stopwatch.ElapsedMilliseconds);
        }

        /// <summary>Claims a Graph id for an instance; false (with the claimant) when another instance of this run already manages that object.</summary>
        private bool TryClaim(ResourceInstance instance, string graphId, out ResourceInstance? other)
        {
            if (string.IsNullOrEmpty(graphId))
            {
                other = null;
                return true;
            }

            var claimant = _graphClaims.GetOrAdd(graphId, instance);
            other = ReferenceEquals(claimant, instance) ? null : claimant;
            return other is null;
        }

        /// <summary>The outcome of <see cref="CreateAsync"/>: what exists now, and the failure to report when the object exists but the create did not complete.</summary>
        /// <param name="Applied">The object (created, found after an ambiguous failure, or found under its key after "already exists").</param>
        /// <param name="Failure">Set when the object exists but a later step failed: the object is recorded and the resource is reported failed.</param>
        /// <param name="AdoptedExisting">Set when the create was answered with "already exists" and the existing object was taken over.</param>
        private sealed record CreateOutcome(AppliedResource Applied, string? Failure, ExistingResource? AdoptedExisting);

        /// <summary>The outcome of <see cref="RecreateAsync"/>; <see cref="Applied"/> is null when nothing was replaced.</summary>
        private sealed record RecreateOutcome(AppliedResource? Applied, string? Failure, string? FailureCode);

        /// <summary>
        /// Creates the object. "Already exists" is an idempotent success (the object is re-read by key and adopted); so is an ambiguous
        /// failure (timeout, transport error, 5xx) after which the object turns out to exist (E-05): the create is not repeated, and not by the outer retry
        /// either unless the create is an upsert (<see cref="IResourceProvider.CreateIsRepeatable"/>; review round 2 S2-07). A failure AFTER the object was written (a license, an owner, a restore step of the provider) is
        /// found again: the provider hands the partial result over (<see cref="ProviderException.Partial"/>), or the object is looked up by key; either way
        /// it is published and the resource is reported failed; a rerun finds it again by key and converges.
        /// </summary>
        private async Task<CreateOutcome> CreateAsync(IResourceProvider provider, ResourceInstance instance, ResourceKey key, CancellationToken cancellationToken)
        {
            AppliedResource created;
            try
            {
                created = await WithRetryAsync(ct => provider.CreateAsync(instance, _owner._graph, ct), instance.RetryHint, cancellationToken, idempotent: provider.CreateIsRepeatable).ConfigureAwait(false);
            }
            catch (ProviderException ex) when (ex.Partial is { } partial)
            {
                return new CreateOutcome(partial, PartialFailure(instance, partial, ex, "created"), null);
            }
            catch (GraphException ex) when (ex.IsAlreadyExists)
            {
                EngineLog.AdoptingExisting(Logger, instance.Id.Id);
                var existing = await ReadAfterCreateAsync(provider, key, cancellationToken).ConfigureAwait(false);
                if (existing is null)
                {
                    throw;
                }

                return new CreateOutcome(new AppliedResource(instance.Id, existing.GraphId, existing.Raw, key), null, existing);
            }
            catch (Exception ex) when (ProviderReads.IsAmbiguousWriteFailure(ex))
            {
                // The write may have been committed although no answer came back: look for the object (waiting out replication) instead of failing or repeating the POST.
                var found = await TryReadAfterCreateAsync(provider, key, cancellationToken).ConfigureAwait(false);
                if (found is null)
                {
                    throw;
                }

                string error = SecretRegistration.Scrub(ex.Message);
                EngineLog.CreateOutcomeUncertain(Logger, instance.Id.Id, error, found.GraphId);
                AddInstanceDiagnostic(instance, Diagnostic.Warning(EngineDiagnosticCodes.CreateOutcomeUncertain, $"'{instance.Id.Id}': the create ended with an unknown outcome ({error}); the object exists (Graph id {found.GraphId}), so the create is counted as done. Values only returned at creation (a secret) are not available for it."));
                return new CreateOutcome(new AppliedResource(instance.Id, found.GraphId, found.Raw, key), null, null);
            }
            catch (Exception ex) when (ex is GraphException or GraphTransportException or ProviderException { Partial: null })
            {
                // A provider that writes in several steps (create, then owners, licenses, restore...) and failed after the first: the object exists even though the call failed.
                var found = await TryReadByKeyAsync(provider, key, cancellationToken).ConfigureAwait(false);
                if (found is null)
                {
                    throw;
                }

                var partial = new AppliedResource(instance.Id, found.GraphId, found.Raw, key);
                string failure = $"{instance.Describe()}: the object was created (Graph id {found.GraphId}) but the create did not complete: {SecretRegistration.Scrub(ex.Message)}. Run the deployment again to finish it.";
                AddInstanceDiagnostic(instance, Diagnostic.Warning(EngineDiagnosticCodes.ResourceApplyFailed, $"'{instance.Id.Id}': the object exists in the tenant (Graph id {found.GraphId}) although the create failed; a rerun finds it by key."));
                return new CreateOutcome(partial, failure, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Ctrl+C reached the provider between two of its requests (a request already sent is never abandoned, see HttpGraphClient): an earlier
                // POST of this create may have committed. One last, uncancellable look by key records such an object instead of orphaning it; when
                // nothing is there, the create never happened and the cancellation stands (the resource is reported skipped).
                var found = await TryReadOnceByKeyAsync(provider, key, RetryHint.Default, CancellationToken.None).ConfigureAwait(false);
                if (found is null)
                {
                    throw;
                }

                var partial = new AppliedResource(instance.Id, found.GraphId, found.Raw, key);
                string failure = $"{instance.Describe()}: the deployment was cancelled while the object was being created; it exists (Graph id {found.GraphId}), but the create may not have completed. Run the deployment again to finish it.";
                return new CreateOutcome(partial, failure, null);
            }

            if (provider.KeyReadLagsCreate)
            {
                await ConfirmCreatedReadableByKeyAsync(provider, instance, key, created.GraphId, cancellationToken).ConfigureAwait(false);
            }

            return new CreateOutcome(created, null, null);
        }

        /// <summary>
        /// Read-your-write after a successful create, for a provider whose key read can miss a new object for a while
        /// (<see cref="IResourceProvider.KeyReadLagsCreate"/>: the <c>$filter=displayName eq</c> lists). The key is read again every
        /// <see cref="ReplicationWait.Interval"/> until it returns the created id or <see cref="ReplicationWait.Window"/> of waiting was scheduled, so a run
        /// started right after this one finds the object instead of planning (and POSTing) it again. Never fails the create: when the object does not
        /// show up in time (or the read itself fails), the <see cref="EngineDiagnosticCodes.CreatedObjectNotVisible"/> warning says so. The reads are not
        /// cancellable (the create is done; a cancelled read must not be mistaken for a cancelled create); a cancellation ends the wait with the warning.
        /// </summary>
        private async Task ConfirmCreatedReadableByKeyAsync(IResourceProvider provider, ResourceInstance instance, ResourceKey key, string graphId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(graphId))
            {
                return;
            }

            var wait = _owner._replicationWait;
            var waited = TimeSpan.Zero;
            string? readFailure = null;
            while (true)
            {
                try
                {
                    var found = await WithRetryAsync(ct => provider.ReadAsync(key, _owner._graph, ct), RetryHint.Default, CancellationToken.None).ConfigureAwait(false);
                    if (found is not null && string.Equals(found.GraphId, graphId, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
                catch (GraphException ex) when (ex.IsNotFound || GraphException.IsReplicationError(ex.Status, ex.Code, ex.GraphMessage))
                {
                    // Not replicated yet.
                }
                catch (ProviderException ex) when (ex.Code == ProviderException.AmbiguousMatch)
                {
                    // A replacement: the deleted object can still be listed next to the new one for a while.
                }
                catch (Exception ex) when (ex is GraphException or GraphTransportException or ProviderException or InvalidOperationException or ArgumentException)
                {
                    readFailure = SecretRegistration.Scrub(ex.Message);
                    break;
                }

                if (cancellationToken.IsCancellationRequested || waited + wait.Interval > wait.Window)
                {
                    break;
                }

                try
                {
                    await wait.Delay.DelayAsync(wait.Interval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                waited += wait.Interval;
                EngineLog.WaitingForReplication(Logger, key.Describe(), waited.TotalSeconds, wait.Window.TotalSeconds);
            }

            string why = readFailure is not null
                ? $"looking it up by its key ({key.Describe()}) failed: {readFailure}"
                : $"a lookup by its key ({key.Describe()}) did not return it after {waited.TotalSeconds:0} s of waiting for Graph to replicate it";
            AddInstanceDiagnostic(instance, Diagnostic.Warning(EngineDiagnosticCodes.CreatedObjectNotVisible, $"'{instance.Id.Id}': the object was created (Graph id {graphId}), but {why}. A run started before Graph catches up may not find it and plan to create it again (a duplicate); wait a minute before the next run, and check with 'deployment what-if' that it shows no create."));
        }

        /// <summary>One look for the object by its key after a failed write; any failure of the look itself counts as "not found".</summary>
        private async Task<ExistingResource?> TryReadByKeyAsync(IResourceProvider provider, ResourceKey key, CancellationToken cancellationToken)
        {
            return await TryReadOnceByKeyAsync(provider, key, RetryHint.ExpectReplicationLag, cancellationToken).ConfigureAwait(false);
        }

        private async Task<ExistingResource?> TryReadOnceByKeyAsync(IResourceProvider provider, ResourceKey key, RetryHint hint, CancellationToken cancellationToken)
        {
            try
            {
                return await WithRetryAsync(ct => provider.ReadAsync(key, _owner._graph, ct), hint, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception readFailure) when (readFailure is GraphException or GraphTransportException or ProviderException or InvalidOperationException or ArgumentException)
            {
                // The look itself failed: the failure that made us look stays the one that is reported.
                return null;
            }
        }

        /// <summary><see cref="ReadAfterCreateAsync"/> for a create whose answer was lost; any failure of the look itself counts as "not found".</summary>
        private async Task<ExistingResource?> TryReadAfterCreateAsync(IResourceProvider provider, ResourceKey key, CancellationToken cancellationToken)
        {
            try
            {
                return await ReadAfterCreateAsync(provider, key, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception readFailure) when (readFailure is GraphException or GraphTransportException or ProviderException or InvalidOperationException or ArgumentException)
            {
                // The look itself failed: the failure that made us look stays the one that is reported.
                return null;
            }
        }

        /// <summary>
        /// Looks for the object by its key after a create whose outcome is not known from its answer (lost, or "already exists"), waiting out replication
        /// (<see cref="ReplicationWait"/>): "not found" - <c>null</c> from the provider (an empty <c>$filter</c> answers 200, not 404), a 404 or a replication-lag
        /// 400 - is read again every <see cref="ReplicationWait.Interval"/> until the object turns up or <see cref="ReplicationWait.Window"/> of waiting was scheduled.
        /// The executor owns this wait whether or not the plan carries a retry policy: each read goes through the policy (when there is one) with
        /// <see cref="RetryHint.Default"/>, so the policy still waits out throttling and transient failures but never waits out replication a second time.
        /// The reads are not cancellable (the create may have committed: an object found is recorded, not orphaned); a cancellation cuts the WAIT short
        /// to one last read. Any other failure of a read propagates.
        /// </summary>
        private async Task<ExistingResource?> ReadAfterCreateAsync(IResourceProvider provider, ResourceKey key, CancellationToken cancellationToken)
        {
            var wait = _owner._replicationWait;
            var waited = TimeSpan.Zero;
            while (true)
            {
                var found = await ReadOnceAfterCreateAsync(provider, key).ConfigureAwait(false);
                if (found is not null || cancellationToken.IsCancellationRequested || waited + wait.Interval > wait.Window)
                {
                    return found;
                }

                try
                {
                    await wait.Delay.DelayAsync(wait.Interval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return await ReadOnceAfterCreateAsync(provider, key).ConfigureAwait(false);
                }

                waited += wait.Interval;
                EngineLog.WaitingForReplication(Logger, key.Describe(), waited.TotalSeconds, wait.Window.TotalSeconds);
            }
        }

        /// <summary>One uncancellable read by key; "not there yet" (a 404, a replication-lag answer) is <c>null</c>.</summary>
        private async Task<ExistingResource?> ReadOnceAfterCreateAsync(IResourceProvider provider, ResourceKey key)
        {
            try
            {
                return await WithRetryAsync(ct => provider.ReadAsync(key, _owner._graph, ct), RetryHint.Default, CancellationToken.None).ConfigureAwait(false);
            }
            catch (GraphException ex) when (ex.IsNotFound || GraphException.IsReplicationError(ex.Status, ex.Code, ex.GraphMessage))
            {
                return null;
            }
        }

        private static string PartialFailure(ResourceInstance instance, AppliedResource partial, ProviderException ex, string verb)
        {
            return $"{instance.Describe()}: the object was {verb} (Graph id {partial.GraphId}), but a later step failed: {SecretRegistration.Scrub(ex.Message)}. Run the deployment again to finish it.";
        }

        /// <summary>After "already exists" was answered for a create: applies the declared properties to the adopted object now (one more PATCH where the provider supports it), instead of leaving it to the next run.</summary>
        private async Task<AppliedResource> ApplyPropertiesToAdoptedAsync(IResourceProvider provider, ResourceInstance instance, ExistingResource found, AppliedResource adopted, CancellationToken cancellationToken)
        {
            var followUp = provider.Plan(instance, found, _planOptions);
            if (followUp.Type == ChangeType.NoChange || followUp.Type == ChangeType.Ignore)
            {
                return adopted;
            }

            if (followUp.Type != ChangeType.Modify || followUp.Mode != ChangeMode.Patch || followUp.HasError)
            {
                AddInstanceDiagnostic(instance, Diagnostic.Warning(EngineDiagnosticCodes.ImmutablePropertyChanged, $"'{instance.Id.Id}': the existing object differs from the template in a way that cannot be changed in place ({followUp.UnsupportedReason ?? string.Join("; ", followUp.Reasons)}); it was left as it is. Run 'deployment what-if' to see the difference."));
                return adopted;
            }

            try
            {
                var updated = await WithRetryAsync(ct => provider.UpdateAsync(instance, found, followUp, _owner._graph, ct), instance.RetryHint, cancellationToken).ConfigureAwait(false);
                return updated;
            }
            catch (ProviderException ex) when (ex.Partial is { } partial)
            {
                AddInstanceDiagnostic(instance, Diagnostic.Warning(EngineDiagnosticCodes.ResourceApplyFailed, $"'{instance.Id.Id}': {SecretRegistration.Scrub(ex.Message)}"));
                return partial;
            }
        }

        /// <summary>
        /// Replaces an object whose immutable properties changed: the old object (found by key, so the replacement has the same key and identity) is deleted first,
        /// then the object is created again. Guarded like every delete (<see cref="DeleteGuards"/>: never the signed-in principal or a service principal owned by
        /// another organization) and only done when confirmed (<see cref="ExecuteOptions.ApprovedRecreates"/>); otherwise nothing is deleted or created.
        /// </summary>
        private async Task<RecreateOutcome> RecreateAsync(IResourceProvider provider, ResourceInstance instance, ResourceKey key, ExistingResource existing, CancellationToken cancellationToken)
        {
            if (DeleteGuards.Refusal(instance.Id.Id, instance.Type, existing, _plan.SignedInPrincipalId, _plan.TenantId) is { } refusal)
            {
                return new RecreateOutcome(null, DeploymentPlanner.RecreateRefusal(instance, refusal), EngineDiagnosticCodes.ProtectedObject);
            }

            if (_options.ApprovedRecreates is { } approved && !approved.Contains(instance.Id.Id))
            {
                return new RecreateOutcome(null, $"{instance.Describe()}: an immutable property changed, so the object ({existing.GraphId}) must be deleted and created again, but that replacement was not confirmed. Confirm it at the prompt or pass -y. Nothing was changed.", EngineDiagnosticCodes.RecreateNotConfirmed);
            }

            if (provider is IDeleteWarningSource warnings && warnings.DescribeDeleteRisk(key, existing) is { } risk)
            {
                EngineLog.DeleteWarning(Logger, instance.Id.Id, risk);
                AddInstanceDiagnostic(instance, Diagnostic.Warning(EngineDiagnosticCodes.DestructiveDelete, $"'{instance.Id.Id}': {risk}"));
            }

            try
            {
                await DeleteOldAsync(provider, instance, key, existing, cancellationToken).ConfigureAwait(false);
            }
            catch (GraphException ex) when (ex.IsNotFound)
            {
                // Already gone: nothing left to delete.
            }

            var recreated = await CreateAsync(provider, instance, key, cancellationToken).ConfigureAwait(false);
            return new RecreateOutcome(recreated.Applied, recreated.Failure, null);
        }

        /// <summary>
        /// Deletes the old object of a replacement; a refusal that clears once an earlier write of this run reaches Graph (a named location still "trusted" right
        /// after the provider unmarked it) is repeated on the replication schedule (<see cref="LaggingDeleteRetry"/>).
        /// </summary>
        private async Task DeleteOldAsync(IResourceProvider provider, ResourceInstance instance, ResourceKey key, ExistingResource old, CancellationToken cancellationToken)
        {
            await _laggingDeletes.RunAsync(instance.Id.Id, () => WithRetryAsync(async ct =>
            {
                await provider.DeleteAsync(key, old, _owner._graph, ct).ConfigureAwait(false);
                return true;
            }, instance.RetryHint, cancellationToken), cancellationToken).ConfigureAwait(false);
            _laggingDeletes.RecordDeleted(instance.Type);
        }

        /// <param name="idempotent">
        /// <c>false</c> for a create that is a POST (not an upsert): the outer retry then never repeats it after a failure that may have followed a commit (a 5xx, a timeout,
        /// a dropped connection) - those are answered by the key re-read - and only waits out throttling and replication lag (review round 2, S2-07). Reads and deletes are idempotent.
        /// </param>
        private Task<T> WithRetryAsync<T>(Func<CancellationToken, Task<T>> operation, RetryHint hint, CancellationToken cancellationToken, bool idempotent = true)
        {
            return _plan.RetryPolicy is null
                ? operation(cancellationToken)
                : _plan.RetryPolicy.ExecuteAsync(operation, hint, null, cancellationToken, idempotent);
        }

        // ---- one-time secrets ----

        /// <summary>
        /// When a secure outputs file is configured, writes the one-time secrets of the object that was just applied (a password
        /// credential's <c>secretText</c>, a generated user password) to it at once, under <c>oneTimeSecrets</c> keyed by the
        /// resource id. Graph returns them exactly once: a crash, a Ctrl-C or a failure later in the run must not lose them (E-03d).
        /// </summary>
        private async Task PersistOneTimeSecretsAsync(ResourceInstance instance, AppliedResource applied)
        {
            string? path = _options.SecureOutputsFile;
            if (string.IsNullOrWhiteSpace(path) || ExtractOneTimeSecrets(applied) is not { } secrets)
            {
                return;
            }

            await _secretsFileGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                SecureOutputsFile.MergeOneTimeSecret(path, instance.Id.Id, secrets);
                _oneTimeSecretsWritten = true;
                EngineLog.OneTimeSecretSaved(Logger, instance.Id.Id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                string message = SecretRegistration.Scrub(ex.Message);
                EngineLog.SecureOutputsSaveFailed(Logger, message);
                string detail = ex is SecureOutputsPendingException ? string.Empty : ". It cannot be read from Graph again: rotate the credential (change rotationTag) to obtain a new one.";
                AddInstanceDiagnostic(instance, Diagnostic.Error(EngineDiagnosticCodes.SecureOutputsWriteFailed, $"The one-time secret of '{instance.Id.Id}' could not be saved to the secure outputs file '{path}': {message}{detail}"));
            }
            finally
            {
                _secretsFileGate.Release();
            }
        }

        private static JsonObject? ExtractOneTimeSecrets(AppliedResource applied)
        {
            var secrets = new JsonObject();
            if (JsonObjects.TryGetIgnoreCase(applied.Live, LiveSecrets.SecretTextProperty, out var secretText) && SecretJson.IsSecret(secretText, out var secret) && secret.Kind == JsonValueKind.String)
            {
                secrets["secretText"] = secret.Value;
                if (applied.Extra is { } extra && JsonObjects.GetString(extra, "keyId") is { } keyId)
                {
                    secrets["keyId"] = keyId;
                }
            }

            bool generated = applied.Extra is { } generatedExtra
                && JsonObjects.TryGetIgnoreCase(generatedExtra, "generatedPassword", out var flag)
                && flag is JsonValue flagValue
                && flagValue.TryGetValue<bool>(out bool isGenerated)
                && isGenerated;
            if (generated
                && JsonObjects.TryGetIgnoreCase(applied.Live, "passwordProfile", out var profile)
                && profile is JsonObject profileObject
                && JsonObjects.TryGetIgnoreCase(profileObject, "password", out var password)
                && SecretJson.IsSecret(password, out var passwordSecret)
                && passwordSecret.Kind == JsonValueKind.String)
            {
                secrets["generatedPassword"] = passwordSecret.Value;
            }

            return secrets.Count == 0 ? null : secrets;
        }

        // ---- bookkeeping ----

        /// <summary>
        /// Records a failed resource. <paramref name="graphId"/> is the object that exists in the tenant although the resource failed (a step after the
        /// primary write failed): with no local record, the operation is where the user finds it.
        /// </summary>
        private void RecordFailure(ResourceInstance instance, int batchIndex, string code, string message, TimeSpan elapsed, string? graphId = null)
        {
            message = SecretRegistration.Scrub(message);
            EngineLog.ApplyFailed(Logger, instance.Id.Id, message);
            _changes.TryGetValue(instance, out var change);
            _operations[instance] = new ResourceOperation(instance.Id, instance.Type, batchIndex, OperationStatus.Failed, change?.Type, change?.Mode, graphId ?? change?.GraphId, message, code, elapsed);
        }

        private void RecordSkipped(ResourceInstance instance, int batchIndex, string reason)
        {
            EngineLog.Skipped(Logger, instance.Id.Id, reason);
            _operations[instance] = new ResourceOperation(instance.Id, instance.Type, batchIndex, OperationStatus.Skipped, null, null, null, reason, EngineDiagnosticCodes.ResourceSkipped, TimeSpan.Zero);
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
                        AddDiagnostic(Diagnostic.Warning(
                            EngineDiagnosticCodes.SecretNotAvailableOnRerun,
                            $"Output '{output.Name}' is null: the secret of {sources} is only available in the run that creates it ({UnavailableSecretDetail(trace)}). The output is reported as null and left out of the secure outputs file.",
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

        /// <summary>Why the secrets an output read are not available, and what to do about it, per kind (password credential, user).</summary>
        private static string UnavailableSecretDetail(SecretAvailabilityTrace trace)
        {
            var parts = new List<string>(2);
            if (trace.UnavailableSecretTexts)
            {
                parts.Add("Graph returns a password credential's secretText once and it was not rotated in this run; change rotationTag to rotate the credential and obtain a new secret");
            }

            if (trace.UnavailableInitialPasswords)
            {
                parts.Add("Graph never returns a user's password, so the initial password is only known in the run that creates the user; it is in the secure outputs file of that run");
            }

            return string.Join("; ", parts);
        }

        private void OutputError(TemplateDocument document, string name, JsonPath path, string detail)
        {
            AddDiagnostic(Diagnostic.Error(EngineDiagnosticCodes.OutputEvaluationFailed, $"Output '{name}' could not be evaluated: {detail}", path, document.LocationOf(path), document.SourcePath));
        }

        /// <summary>
        /// Forwards to the plan's resolver and records the ids of published objects whose one-time secret is not
        /// available in this run (<see cref="LiveSecrets.IsOneTimeSecretUnavailable"/>), per output.
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

            /// <summary>One of <see cref="UnavailableSecrets"/> is a password credential's <c>secretText</c>.</summary>
            public bool UnavailableSecretTexts { get; private set; }

            /// <summary>One of <see cref="UnavailableSecrets"/> is a user's initial password.</summary>
            public bool UnavailableInitialPasswords { get; private set; }

            public void Reset()
            {
                _unavailable.Clear();
                UnavailableSecretTexts = false;
                UnavailableInitialPasswords = false;
            }

            public ReferenceResult Resolve(ResourceIdentifier id, string? apiVersion)
            {
                var result = _inner.Resolve(id, apiVersion);
                if (result is ReferenceResult.Live live && LiveSecrets.IsOneTimeSecretUnavailable(live.Object) && !_unavailable.Contains(id))
                {
                    _unavailable.Add(id);
                    UnavailableSecretTexts |= LiveSecrets.IsSecretTextUnavailable(live.Object);
                    UnavailableInitialPasswords |= LiveSecrets.IsInitialPasswordUnavailable(live.Object);
                }

                return result;
            }
        }

        /// <summary>
        /// Merges the secure outputs into the secure outputs file (it may already hold one-time secrets of this run or earlier runs, which
        /// stay). Writes nothing when there are no secure values (E-06). A failure to write is an error diagnostic, never a crash (E-03).
        /// </summary>
        private string? WriteSecureOutputs(JsonObject secureOutputs)
        {
            string? path = _options.SecureOutputsFile;
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                if (SecureOutputsFile.MergeOutputs(path, secureOutputs))
                {
                    EngineLog.SecureOutputsWritten(Logger, path);
                    return path;
                }

                return _oneTimeSecretsWritten ? path : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                string message = SecretRegistration.Scrub(ex.Message);
                EngineLog.SecureOutputsSaveFailed(Logger, message);
                string detail = ex is SecureOutputsPendingException ? string.Empty : ". The values were not saved anywhere; run the deployment again with a writable --secure-outputs-file (secure outputs of resources that already exist are available again; one-time secrets are not).";
                AddDiagnostic(Diagnostic.Error(EngineDiagnosticCodes.SecureOutputsWriteFailed, $"The secure outputs could not be written to '{path}': {message}{detail}"));
                return null;
            }
        }
    }
}
