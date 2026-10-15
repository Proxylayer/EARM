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
using EArm.Engine.State;
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
/// the transitive dependents of a failure are skipped (recorded through the sink before their batch starts, never
/// attempted, their symbolic names never resolved); then evaluates the outputs. State persistence goes through
/// <see cref="IStateSink"/>. The end phase (edges, outputs, secure outputs file, mode pass, completion) never observes the
/// caller's cancellation: it records what already happened and only then reports the cancellation.
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
        private readonly List<ResourceOperation> _modeOperations = [];
        private readonly List<ResourceChange> _modeChanges = [];
        private bool _oneTimeSecretsWritten;
        private bool _cancelledInEndPhase;
        private int _deletesDone;
        private readonly DateTimeOffset _runStart = DateTimeOffset.UtcNow;

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
                // what was applied stays recorded, the secrets reach the file and the state does not stay "Running" (review round 2, S2-09).
                aborted = true;
                stop = true;
                string message = SecretRegistration.Scrub($"The run was aborted by an unexpected error ({ex.GetType().Name}): {ex.Message}");
                EngineLog.ApplyFailed(Logger, _plan.DeploymentName, message);
                AddDiagnostic(Diagnostic.Error(EngineDiagnosticCodes.ResourceApplyFailed, message));
            }

            // The end phase records work that already happened (objects written to the tenant, one-time secrets, the state of the run): recording
            // never observes the caller's cancellation and is guarded so an I/O failure cannot skip the steps after it (E-03). The steps that WRITE
            // to the tenant again (edges, Complete-mode deletes) do observe it, between operations: Ctrl-C stops new writes (review round 2, S2-10).
            var none = CancellationToken.None;
            var outputs = new JsonObject();
            var secureOutputs = new JsonObject();
            string? secureFile = null;
            bool completed = false;
            List<ResourceOperation> operations = [];
            List<ResourceChange> changes = [];
            DeploymentStatus status = DeploymentStatus.Failed;
            try
            {
                if (cancelled || aborted)
                {
                    string why = cancelled ? "the run was cancelled" : "the run was aborted by an unexpected error";
                    foreach (var batch in _plan.Batches)
                    {
                        foreach (var instance in batch.Instances.Where(i => !_operations.ContainsKey(i)))
                        {
                            await GuardedAsync(() => RecordSkippedAsync(instance, batch.Index, why, none)).ConfigureAwait(false);
                        }
                    }
                }

                // Edge pass: after all objects exist, before the outputs. Part A kinds now;
                // part B kinds plug into the same pass through the plan's EdgeKinds registry (the providers add the part B kinds).
                await GuardedAsync(() => RunEdgePassAsync(stop, cancellationToken)).ConfigureAwait(false);

                (outputs, secureOutputs) = EvaluateOutputs(none);
                secureFile = WriteSecureOutputs(secureOutputs);

                // Mode pass: detach (Incremental) or delete (Complete) what the template no longer declares.
                // A cancelled or aborted run does not know whether the instance set was applied in full, so it judges nothing.
                if (!cancelled && !aborted)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _cancelledInEndPhase = true;
                    }
                    else
                    {
                        await GuardedAsync(() => RunModePassAsync(stop, cancellationToken)).ConfigureAwait(false);
                    }
                }

                cancelled |= _cancelledInEndPhase;

                FlushInstanceDiagnostics(_plan.Batches.SelectMany(b => b.Instances));
                operations = _plan.Batches.SelectMany(b => b.Instances).Where(_operations.ContainsKey).Select(i => _operations[i]).Concat(_modeOperations).ToList();
                changes = _plan.Batches.SelectMany(b => b.Instances).Where(_changes.ContainsKey).Select(i => _changes[i]).Concat(_modeChanges).ToList();
                bool anyFailure = aborted || operations.Any(o => o.Status is OperationStatus.Failed or OperationStatus.Skipped) || DiagnosticsSnapshot().Any(d => d.IsError);
                status = cancelled ? DeploymentStatus.Cancelled : anyFailure ? DeploymentStatus.Failed : DeploymentStatus.Succeeded;

                await GuardedAsync(() => _options.StateSink.CompletedAsync(status, none)).ConfigureAwait(false);
                completed = true;
            }
            finally
            {
                if (!completed)
                {
                    // An unexpected failure above: the state must still not stay "Running".
                    await GuardedAsync(() => _options.StateSink.CompletedAsync(cancelled ? DeploymentStatus.Cancelled : DeploymentStatus.Failed, none)).ConfigureAwait(false);
                }
            }

            stopwatch.Stop();
            if (cancelled)
            {
                EngineLog.Completed(Logger, _plan.DeploymentName, "Cancelled", operations.Count(o => o.Status == OperationStatus.Succeeded), operations.Count(o => o.Status == OperationStatus.Failed), operations.Count(o => o.Status == OperationStatus.Skipped), operations.Count(o => o.Status == OperationStatus.Ignored), stopwatch.ElapsedMilliseconds);
                throw new OperationCanceledException("The deployment was cancelled; the state and the secure outputs file record what had already been applied.", cancellationToken);
            }

            var result = new DeploymentResult(_plan.DeploymentName, status, operations, changes, outputs, secureOutputs, DiagnosticsSnapshot(), secureFile, stopwatch.Elapsed);
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

        /// <summary>Runs one end-phase step; a failed state write is reported as an error diagnostic instead of ending the run (E-03c).</summary>
        private async Task GuardedAsync(Func<Task> step)
        {
            try
            {
                await step().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                string message = SecretRegistration.Scrub($"The state could not be saved: {ex.Message}");
                EngineLog.StateSaveFailed(Logger, message);
                AddDiagnostic(Diagnostic.Error(EngineDiagnosticCodes.StateSaveFailed, message));
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
        /// sets are authoritative, principal-side declarations are additive with state-tracked removals), records the
        /// principal-side edges in the state and folds the deltas into the resource's change. A resource whose edges
        /// cannot be validated or applied is reported as failed (the edges that were added before the failure are still recorded). Skipped
        /// entirely (with a warning) when the run stopped at a batch boundary; a no-op for templates without relationship properties.
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
                edgePlan = await new EdgePlanner(_plan, context, _options.State, PlanMode.Create, createdIds, unreliable).PlanAsync(candidates, cancellationToken).ConfigureAwait(false);
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
                    if (row.RecordsEdges && !row.HasErrors)
                    {
                        // Review finding E-11: the edges that were added before another operation of the row failed belong to this
                        // deployment; record them so that undeclaring them later can remove them.
                        await _options.StateSink.ResourceEdgesAppliedAsync(instance, EdgePlan.RecordingsFor(row, result), CancellationToken.None).ConfigureAwait(false);
                    }

                    string code = row.HasErrors ? EngineDiagnosticCodes.EdgeValidationFailed : EngineDiagnosticCodes.EdgeApplyFailed;
                    await FailEdgesAsync(instance, code, string.Join(" ", errors), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (row.RecordsEdges)
                {
                    await _options.StateSink.ResourceEdgesAppliedAsync(instance, EdgePlan.RecordingsFor(row, result), CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        private async Task FailEdgesAsync(ResourceInstance instance, string code, string message, CancellationToken cancellationToken)
        {
            message = SecretRegistration.Scrub(message);
            EngineLog.EdgeValidationFailed(Logger, instance.Id.Id, message);
            if (_operations.TryGetValue(instance, out var operation))
            {
                _operations[instance] = operation with { Status = OperationStatus.Failed, Error = message, ErrorCode = code };
            }

            await _options.StateSink.ResourceEdgesFailedAsync(instance, message, CancellationToken.None).ConfigureAwait(false);
        }

        // ---- mode pass ----

        /// <summary>
        /// State entries absent from the expanded instance set: Incremental drops them from the state with a warning;
        /// Complete deletes the objects in reverse dependency order through their providers (inside the retry
        /// policy), stopping at the first failure unless <see cref="ExecuteOptions.ContinueOnError"/>. Every delete is checked first
        /// (<see cref="DeleteGuard"/>): the live object found by KEY must be the pinned one, adopted objects stay unless
        /// <see cref="ExecuteOptions.DeleteAdopted"/>, and an object that a declared resource owns, the signed-in principal and
        /// first-party service principals are never deleted. Skipped when the run already stopped at a batch boundary (the instance set
        /// may not have been applied in full) or when no state was supplied.
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
            var guard = new DeleteGuard
            {
                Catalog = _plan.Catalog,
                SignedInPrincipalId = _plan.SignedInPrincipalId,
                TenantId = _plan.TenantId,
                DeleteAdopted = _options.DeleteAdopted,
                OwnedGraphIds = OwnedGraphIds(state),
                State = state,
            };

            var remaining = new Queue<StateEntry>(orphans);
            while (remaining.Count > 0)
            {
                var entry = remaining.Dequeue();
                if (cancellationToken.IsCancellationRequested)
                {
                    // Ctrl-C: no new delete (the one in flight finished); what is left is recorded as skipped and the run ends Cancelled (review round 2, S2-10).
                    _cancelledInEndPhase = true;
                    remaining.Enqueue(entry);
                    while (remaining.Count > 0)
                    {
                        var left = remaining.Dequeue();
                        _modeOperations.Add(new ResourceOperation(left.Id, left.Type, batchIndex, OperationStatus.Skipped, ChangeType.Delete, ChangeMode.Delete, left.GraphId, "the run was cancelled", EngineDiagnosticCodes.ResourceSkipped, TimeSpan.Zero));
                    }

                    break;
                }

                if (_plan.Mode == DeploymentMode.Incremental)
                {
                    EngineLog.ResourceDetached(Logger, entry.Id.Id);
                    _modeChanges.Add(ResourceChange.Ignore(entry.Id, entry.Type, ModePass.DetachReason));
                    _modeOperations.Add(new ResourceOperation(entry.Id, entry.Type, batchIndex, OperationStatus.Detached, ChangeType.Ignore, ChangeMode.None, entry.GraphId, null, null, TimeSpan.Zero));
                    AddDiagnostic(Diagnostic.Warning(EngineDiagnosticCodes.ResourceDetached, $"'{entry.Id.Id}' is recorded in the state but no longer declared in the template; it was detached from the state and the object was left in the tenant. It is not managed by this deployment any more: delete it yourself, or declare it again and deploy with --mode Complete to manage it."));
                    await _options.StateSink.ResourceDetachedAsync(entry, CancellationToken.None).ConfigureAwait(false);
                    continue;
                }

                if (stop)
                {
                    _modeOperations.Add(new ResourceOperation(entry.Id, entry.Type, batchIndex, OperationStatus.Skipped, ChangeType.Delete, ChangeMode.Delete, entry.GraphId, "an earlier operation failed", EngineDiagnosticCodes.ResourceSkipped, TimeSpan.Zero));
                    continue;
                }

                // The delete in flight is never cancelled (its outcome must be known and recorded); the token is checked between entries.
                var outcome = await StateDeletion.DeleteAsync(entry, _owner._registry, _owner._graph, _plan.RetryPolicy, guard, CancellationToken.None, async check =>
                {
                    // The object is verified and about to go: the delete budget first (--max-deletes is a hard limit of the run, whatever the estimate before it said), then the warnings, then the edges this deployment recorded, then the resource itself.
                    if (!TryReserveDelete())
                    {
                        throw new ProviderException(EngineDiagnosticCodes.DeleteLimitExceeded, DeleteLimitMessage(entry.Id.Id), entry.Id);
                    }

                    EngineLog.DeletingOrphan(Logger, entry.Id.Id, entry.GraphId);
                    if (check.Provider is IDeleteWarningSource warnings && warnings.DescribeDeleteRisk(entry.ToResourceKey(_plan.Catalog), check.Live!) is { } risk)
                    {
                        EngineLog.DeleteWarning(Logger, entry.Id.Id, risk);
                        AddDiagnostic(Diagnostic.Warning(EngineDiagnosticCodes.DestructiveDelete, $"'{entry.Id.Id}': {risk}"));
                    }

                    if (entry.Relationships is { Count: > 0 })
                    {
                        edgeContext ??= new EdgeGraphContext(_owner._graph, _plan.RetryPolicy, _plan.Resolver, _plan.Instances, Logger, _plan.SignedInPrincipalId) { AppOnlyToken = _plan.AppOnlyToken };
                        try
                        {
                            int removed = await RecordedEdges.RemoveAsync(entry, _plan.Catalog, _plan.EdgeKinds, edgeContext, CancellationToken.None).ConfigureAwait(false);
                            EngineLog.RecordedEdgesRemoved(Logger, entry.Id.Id, removed);
                        }
                        catch (Exception ex) when (ex is GraphException or GraphTransportException)
                        {
                            EngineLog.EdgeApplyFailed(Logger, $"remove recorded edges of {entry.Id.Id}", SecretRegistration.Scrub(ex.Message));
                        }
                    }
                }).ConfigureAwait(false);

                if (outcome.Succeeded)
                {
                    if (outcome.Status == DeleteStatus.AlreadyGone)
                    {
                        EngineLog.OrphanAlreadyGone(Logger, entry.Id.Id);
                    }

                    _modeChanges.Add(new ResourceChange { Id = entry.Id, ResourceType = entry.Type, Type = ChangeType.Delete, Mode = ChangeMode.Delete, GraphId = entry.GraphId, Reasons = [ModePass.DeleteReason] });
                    _modeOperations.Add(new ResourceOperation(entry.Id, entry.Type, batchIndex, OperationStatus.Succeeded, ChangeType.Delete, ChangeMode.Delete, entry.GraphId, null, null, outcome.Duration));
                    await _options.StateSink.ResourceDeletedAsync(entry, CancellationToken.None).ConfigureAwait(false);
                    continue;
                }

                string error = SecretRegistration.Scrub(outcome.Error ?? "unknown error");
                if (outcome.Status == DeleteStatus.Refused)
                {
                    EngineLog.DeleteRefused(Logger, entry.Id.Id, error);
                    AddDiagnostic(Diagnostic.Warning(outcome.ErrorCode ?? EngineDiagnosticCodes.AdoptedObjectNotDeleted, error));
                    _modeChanges.Add(ResourceChange.Ignore(entry.Id, entry.Type, error));
                    if (outcome.ErrorCode == EngineDiagnosticCodes.OrphanSharesObject)
                    {
                        // A stale duplicate of an object a declared resource now owns: drop the entry, the object is not touched.
                        _modeOperations.Add(new ResourceOperation(entry.Id, entry.Type, batchIndex, OperationStatus.Detached, ChangeType.Ignore, ChangeMode.None, entry.GraphId, null, null, TimeSpan.Zero));
                        await _options.StateSink.ResourceDetachedAsync(entry, CancellationToken.None).ConfigureAwait(false);
                    }
                    else
                    {
                        _modeOperations.Add(new ResourceOperation(entry.Id, entry.Type, batchIndex, OperationStatus.Ignored, ChangeType.Ignore, ChangeMode.None, entry.GraphId, null, outcome.ErrorCode, TimeSpan.Zero));
                    }

                    continue;
                }

                EngineLog.DeleteFailed(Logger, entry.Id.Id, error);
                if (outcome.ErrorCode is EngineDiagnosticCodes.StateEntryMismatch or EngineDiagnosticCodes.ProtectedObject or EngineDiagnosticCodes.StateEntryInvalid or EngineDiagnosticCodes.DeleteLimitExceeded)
                {
                    EngineLog.StateEntryMismatch(Logger, entry.Id.Id, error);
                    AddDiagnostic(Diagnostic.Error(outcome.ErrorCode, error));
                }

                _modeChanges.Add(ResourceChange.Unsupported(entry.Id, entry.Type, ChangeType.Delete, error));
                _modeOperations.Add(new ResourceOperation(entry.Id, entry.Type, batchIndex, OperationStatus.Failed, ChangeType.Delete, ChangeMode.Delete, entry.GraphId, error, outcome.ErrorCode ?? EngineDiagnosticCodes.ResourceDeleted, outcome.Duration));
                await _options.StateSink.ResourceDeleteFailedAsync(entry, error, CancellationToken.None).ConfigureAwait(false);
                stop = !_options.ContinueOnError || outcome.ErrorCode == EngineDiagnosticCodes.DeleteLimitExceeded;
            }
        }

        /// <summary>Takes one delete from the run's budget (<see cref="ExecuteOptions.MaxDeletes"/>); false when it is used up.</summary>
        private bool TryReserveDelete()
        {
            int done = Interlocked.Increment(ref _deletesDone);
            return _options.MaxDeletes is not { } max || done <= max;
        }

        private string DeleteLimitMessage(string resourceId)
        {
            return $"{resourceId} was not deleted: the run reached --max-deletes {_options.MaxDeletes}. Deletes are a hard limit of the run, whatever the plan said before it started; check the template and the state, or pass --allow-delete-all (or a higher --max-deletes) to confirm that this is intended.";
        }

        /// <summary>The Graph ids of the objects declared resources own (read or applied in this run, or pinned in the state): an orphan entry that points at one must not delete it.</summary>
        private HashSet<string> OwnedGraphIds(StateFile state)
        {
            var owned = new HashSet<string>(_graphClaims.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var instance in _plan.Instances)
            {
                if (instance.IsSymbolic)
                {
                    continue;
                }

                if (_plan.Resolver.TryGetGraphId(instance.Id, out var graphId))
                {
                    owned.Add(graphId);
                }

                if (state.GraphIdOf(instance.Id.Id) is { } pinned)
                {
                    owned.Add(pinned);
                }
            }

            return owned;
        }

        // ---- one resource ----

        private async Task ApplyOneAsync(ResourceInstance instance, int batchIndex, CancellationToken cancellationToken)
        {
            // Dependents of a failure are recorded as skipped at the batch boundary (RunBatchesAsync) and never reach here.
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await _options.StateSink.ResourceStartingAsync(instance, cancellationToken).ConfigureAwait(false);
                await ApplyCoreAsync(instance, batchIndex, stopwatch, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
            catch (Exception ex) when (ex is GraphException or GraphTransportException)
            {
                await FailAsync(instance, batchIndex, EngineDiagnosticCodes.ResourceApplyFailed, ex.Message, stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The state could not be written (after the sink's retries): the resource may exist in the tenant without an entry.
                await FailAsync(instance, batchIndex, EngineDiagnosticCodes.StateSaveFailed, $"{instance.Describe()}: the state could not be saved ({ex.Message}); the object may exist in the tenant but is not recorded in the state.", stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or JsonException or NotSupportedException)
            {
                await FailAsync(instance, batchIndex, EngineDiagnosticCodes.ResourceApplyFailed, $"{ex.GetType().Name}: {ex.Message}", stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Anything else (a null reference, a format error, a cancellation that is not the caller's such as an HTTP timeout) fails THIS resource only: it must
                // never escape into the parallel loop, where it would cancel the siblings and skip the end phase (review round 2, S2-09).
                await FailAsync(instance, batchIndex, EngineDiagnosticCodes.ResourceApplyFailed, $"{instance.Describe()}: unexpected error ({ex.GetType().Name}): {ex.Message}", stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);
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
            SecretRegistration.RegisterTainted(instance.Properties);
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

            // Reads use the default hint: a 404 here means "create", not "not replicated yet". Identity is resolved live, by key, on every run:
            // the state is never consulted to find the object (a renamed object is a new one).
            var existing = await WithRetryAsync(ct => provider.ReadAsync(key, _owner._graph, ct), RetryHint.Default, cancellationToken).ConfigureAwait(false);
            var previousEntry = _options.State?.Find(instance.Id.Id);
            if (previousEntry is not null && !string.Equals(existing?.GraphId, previousEntry.GraphId, StringComparison.OrdinalIgnoreCase))
            {
                EngineLog.StateDrift(Logger, instance.Id.Id, previousEntry.GraphId, existing is null ? "not found; it will be created again" : $"found under a different id ({existing.GraphId}); the state will be re-pinned");
            }

            if (existing is not null && !TryClaim(instance, existing.GraphId, out var other))
            {
                EngineLog.DuplicateGraphObject(Logger, instance.Id.Id, other!.Id.Id, existing.GraphId);
                await FailAsync(instance, batchIndex, EngineDiagnosticCodes.DuplicateGraphObject, $"{instance.Describe()} and {other.Describe()} resolve to the same Graph object ({existing.GraphId}); two declarations must not manage one object. Remove one of them, or give them different keys.", stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);
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
                await FailAsync(instance, batchIndex, EngineDiagnosticCodes.ImmutablePropertyChanged, change.UnsupportedReason!, stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);
                return;
            }

            EngineLog.Applying(Logger, instance.Id.Id, change.Type.ToString(), change.Mode.ToString());

            // Provenance (E-02): an object that was already there and that the state did not (yet) vouch for is adopted, not created.
            bool takenOver = existing is not null && (previousEntry is null || !string.Equals(previousEntry.GraphId, existing.GraphId, StringComparison.OrdinalIgnoreCase));
            var provenance = takenOver || previousEntry is null ? StateProvenance.Adopted : previousEntry.Provenance;
            string? afterApplyFailure = null;
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
                {
                    var created = await CreateAsync(provider, instance, key, cancellationToken).ConfigureAwait(false);
                    applied = created.Applied;
                    provenance = created.Provenance;
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
                    var recreated = await RecreateAsync(provider, instance, key, existing!, previousEntry, cancellationToken).ConfigureAwait(false);
                    if (recreated.Applied is null)
                    {
                        // Nothing was replaced (the old object is protected, adopted or over the delete budget): the resource fails and keeps its entry.
                        await FailAsync(instance, batchIndex, recreated.FailureCode ?? EngineDiagnosticCodes.ResourceApplyFailed, recreated.Failure ?? $"{instance.Describe()}: the object could not be replaced.", stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    applied = recreated.Applied;
                    afterApplyFailure = recreated.Failure;
                    provenance = StateProvenance.Created;
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

            applied = applied with { Provenance = provenance };
            SecretRegistration.RegisterTainted(applied.Live);
            if (!TryClaim(instance, applied.GraphId, out var claimedBy))
            {
                EngineLog.DuplicateGraphObject(Logger, instance.Id.Id, claimedBy!.Id.Id, applied.GraphId);
                AddInstanceDiagnostic(instance, Diagnostic.Error(EngineDiagnosticCodes.DuplicateGraphObject, $"{instance.Describe()} and {claimedBy.Describe()} resolve to the same Graph object ({applied.GraphId})."));
            }

            // A one-time secret exists only in this response: it goes to the owner-only file before anything else can fail (E-03d).
            await PersistOneTimeSecretsAsync(instance, applied).ConfigureAwait(false);

            if (provenance == StateProvenance.Adopted && (change.Type == ChangeType.Create || takenOver || previousEntry is null))
            {
                EngineLog.AdoptedExisting(Logger, instance.Id.Id, applied.GraphId);
                AddInstanceDiagnostic(instance, Diagnostic.Warning(EngineDiagnosticCodes.AdoptedExistingObject, $"'{instance.Id.Id}' adopted the existing {instance.Type} object {applied.GraphId}: it was not created by this deployment, so Complete mode will not delete it (--delete-adopted overrides)."));
            }

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

            // Recording a completed write is not cancellable: a cancellation that arrives right after the Graph call must not lose the entry.
            await _options.StateSink.ResourceAppliedAsync(instance, change, applied, CancellationToken.None).ConfigureAwait(false);
            stopwatch.Stop();
            if (afterApplyFailure is not null)
            {
                await FailAsync(instance, batchIndex, EngineDiagnosticCodes.ResourceApplyFailed, afterApplyFailure, stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);
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

        /// <summary>The outcome of <see cref="CreateAsync"/>: what exists now, how it came to be, and the failure to report when the object exists but the create did not complete.</summary>
        /// <param name="Applied">The object (created, found after an ambiguous failure, or adopted).</param>
        /// <param name="Provenance"><see cref="StateProvenance.Created"/> when this run created it.</param>
        /// <param name="Failure">Set when the object exists but a later step failed: the object is recorded and the resource is reported failed.</param>
        /// <param name="AdoptedExisting">Set when the create was answered with "already exists" and the existing object was adopted.</param>
        private sealed record CreateOutcome(AppliedResource Applied, StateProvenance Provenance, string? Failure, ExistingResource? AdoptedExisting);

        /// <summary>The outcome of <see cref="RecreateAsync"/>; <see cref="Applied"/> is null when nothing was replaced.</summary>
        private sealed record RecreateOutcome(AppliedResource? Applied, string? Failure, string? FailureCode);

        /// <summary>
        /// Creates the object. "Already exists" is an idempotent success (the object is re-read by key and adopted); so is an ambiguous
        /// failure (timeout, transport error, 5xx) after which the object turns out to exist (E-05): the create is not repeated, and not by the outer retry
        /// either unless the create is an upsert (<see cref="IResourceProvider.CreateIsRepeatable"/>; review round 2 S2-07). A failure AFTER the object was written (a license, an owner, a restore step of the provider) is
        /// found again: the provider hands the partial result over (<see cref="ProviderException.Partial"/>), or the object is looked up by key; either way
        /// it is recorded in the state and the resource is reported failed, so a rerun converges instead of conflicting with an unrecorded object.
        /// </summary>
        private async Task<CreateOutcome> CreateAsync(IResourceProvider provider, ResourceInstance instance, ResourceKey key, CancellationToken cancellationToken)
        {
            try
            {
                return new CreateOutcome(await WithRetryAsync(ct => provider.CreateAsync(instance, _owner._graph, ct), instance.RetryHint, cancellationToken, idempotent: provider.CreateIsRepeatable).ConfigureAwait(false), StateProvenance.Created, null, null);
            }
            catch (ProviderException ex) when (ex.Partial is { } partial)
            {
                return new CreateOutcome(partial, StateProvenance.Created, PartialFailure(instance, partial, ex, "created"), null);
            }
            catch (GraphException ex) when (ex.IsAlreadyExists)
            {
                EngineLog.AdoptingExisting(Logger, instance.Id.Id);
                var existing = await ReadAfterCreateAsync(provider, key, cancellationToken).ConfigureAwait(false);
                if (existing is null)
                {
                    throw;
                }

                return new CreateOutcome(new AppliedResource(instance.Id, existing.GraphId, existing.Raw, key), StateProvenance.Adopted, null, existing);
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
                return new CreateOutcome(new AppliedResource(instance.Id, found.GraphId, found.Raw, key), ProvenanceOfFound(found), null, null);
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
                string failure = $"{instance.Describe()}: the object was created (Graph id {found.GraphId}) and is recorded in the state, but the create did not complete: {SecretRegistration.Scrub(ex.Message)}. Run the deployment again to finish it.";
                AddInstanceDiagnostic(instance, Diagnostic.Warning(EngineDiagnosticCodes.ResourceApplyFailed, $"'{instance.Id.Id}': the object exists in the tenant (Graph id {found.GraphId}) although the create failed; it was recorded in the state."));
                return new CreateOutcome(partial, ProvenanceOfFound(found), failure, null);
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
                string failure = $"{instance.Describe()}: the deployment was cancelled while the object was being created; it exists (Graph id {found.GraphId}) and is recorded in the state, but the create may not have completed. Run the deployment again to finish it.";
                return new CreateOutcome(partial, ProvenanceOfFound(found), failure, null);
            }
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

        /// <summary>
        /// Whether an object found under the key after an uncertain create was created by this run: it carries a <c>createdDateTime</c> from (about) this run, or Graph
        /// did not report one (then the create is trusted). An older object was not created by us and is adopted (review round 2, S2-17).
        /// </summary>
        private StateProvenance ProvenanceOfFound(ExistingResource found)
        {
            return ProviderReads.CreatedDateTimeOf(found.Raw) is { } createdOn && createdOn < _runStart - TimeSpan.FromMinutes(5)
                ? StateProvenance.Adopted
                : StateProvenance.Created;
        }

        private static string PartialFailure(ResourceInstance instance, AppliedResource partial, ProviderException ex, string verb)
        {
            return $"{instance.Describe()}: the object was {verb} (Graph id {partial.GraphId}) and is recorded in the state, but a later step failed: {SecretRegistration.Scrub(ex.Message)}. Run the deployment again to finish it.";
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
                AddInstanceDiagnostic(instance, Diagnostic.Warning(EngineDiagnosticCodes.AdoptedExistingObject, $"'{instance.Id.Id}': the adopted object differs from the template in a way that cannot be changed in place ({followUp.UnsupportedReason ?? string.Join("; ", followUp.Reasons)}); it was left as it is. Run 'deployment what-if' to see the difference."));
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
        /// Replaces an object whose immutable properties changed. The old object is deleted like any state-driven delete (<see cref="StateDeletion.CheckAsync"/>:
        /// an adopted object is only replaced with <c>--delete-adopted</c>, the signed-in principal and first-party service principals never, the object found must be
        /// the recorded one) and counts against <c>--max-deletes</c> (review round 2, S2-03). When the replacement has another key than the old object (the key
        /// properties are the immutable ones), the new object is created FIRST and the old one deleted second, so a failed create keeps
        /// the old object and its state entry (E-06). When both share one key (same identity) the old object must go first.
        /// </summary>
        private async Task<RecreateOutcome> RecreateAsync(IResourceProvider provider, ResourceInstance instance, ResourceKey key, ExistingResource existing, StateEntry? previousEntry, CancellationToken cancellationToken)
        {
            var oldEntry = RecreateRules.OldEntryFor(instance, existing, key, previousEntry);

            // Every guard of a delete from the state applies to the object that is about to be replaced.
            var guard = new DeleteGuard
            {
                Catalog = _plan.Catalog,
                SignedInPrincipalId = _plan.SignedInPrincipalId,
                TenantId = _plan.TenantId,
                DeleteAdopted = _options.DeleteAdopted,
                State = _options.State,
            };
            var check = await StateDeletion.CheckAsync(oldEntry, _owner._registry, _owner._graph, _plan.RetryPolicy, guard, cancellationToken).ConfigureAwait(false);
            bool oldIsGone = false;
            if (check.Blocked is { } blocked)
            {
                if (blocked.Status != DeleteStatus.AlreadyGone)
                {
                    return new RecreateOutcome(null, RecreateRules.RefusalMessage(instance, oldEntry, blocked), blocked.ErrorCode ?? EngineDiagnosticCodes.ResourceApplyFailed);
                }

                oldIsGone = true;
            }

            if (!oldIsGone && !TryReserveDelete())
            {
                string limit = DeleteLimitMessage(oldEntry.Id.Id);
                return new RecreateOutcome(null, $"{instance.Describe()}: {limit} Nothing was changed.", EngineDiagnosticCodes.DeleteLimitExceeded);
            }

            if (oldIsGone || ProviderReads.KeyMatches(key, existing.Raw, out _))
            {
                if (!oldIsGone)
                {
                    await DeleteOldAsync(provider, instance, key, check.Live ?? existing, cancellationToken).ConfigureAwait(false);
                }

                var recreated = await CreateAsync(provider, instance, key, cancellationToken).ConfigureAwait(false);
                return new RecreateOutcome(recreated.Applied, recreated.Failure, null);
            }

            var replacement = await CreateAsync(provider, instance, key, cancellationToken).ConfigureAwait(false);
            try
            {
                await DeleteOldAsync(provider, instance, key, check.Live ?? existing, cancellationToken).ConfigureAwait(false);
            }
            catch (GraphException ex) when (ex.IsNotFound)
            {
                // Already gone: nothing left to clean up.
            }
            catch (Exception ex) when (ex is GraphException or GraphTransportException or ProviderException)
            {
                return new RecreateOutcome(replacement.Applied, $"{instance.Describe()}: the replacement was created (Graph id {replacement.Applied.GraphId}) but the old object ({existing.GraphId}) could not be deleted: {SecretRegistration.Scrub(ex.Message)}. Delete it manually.", null);
            }

            return new RecreateOutcome(replacement.Applied, replacement.Failure, null);
        }

        private Task DeleteOldAsync(IResourceProvider provider, ResourceInstance instance, ResourceKey key, ExistingResource old, CancellationToken cancellationToken)
        {
            return WithRetryAsync(async ct =>
            {
                await provider.DeleteAsync(key, old, _owner._graph, ct).ConfigureAwait(false);
                return true;
            }, instance.RetryHint, cancellationToken);
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
        /// resource id. Graph returns them exactly once: a crash, a Ctrl-C or a failed state save later in the run must not lose them (E-03d).
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

        private async Task FailAsync(ResourceInstance instance, int batchIndex, string code, string message, TimeSpan elapsed, CancellationToken cancellationToken)
        {
            message = SecretRegistration.Scrub(message);
            RecordFailure(instance, batchIndex, code, message, elapsed);
            try
            {
                await _options.StateSink.ResourceFailedAsync(instance, message, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                // The failure itself is already in the result; only its state record is lost (and nothing here may escape into the parallel loop).
                EngineLog.StateSaveFailed(Logger, SecretRegistration.Scrub(ex.Message));
            }
        }

        private void RecordFailure(ResourceInstance instance, int batchIndex, string code, string message, TimeSpan elapsed)
        {
            message = SecretRegistration.Scrub(message);
            EngineLog.ApplyFailed(Logger, instance.Id.Id, message);
            _changes.TryGetValue(instance, out var change);
            _operations[instance] = new ResourceOperation(instance.Id, instance.Type, batchIndex, OperationStatus.Failed, change?.Type, change?.Mode, change?.GraphId, message, code, elapsed);
        }

        private async Task RecordSkippedAsync(ResourceInstance instance, int batchIndex, string reason, CancellationToken cancellationToken)
        {
            EngineLog.Skipped(Logger, instance.Id.Id, reason);
            _operations[instance] = new ResourceOperation(instance.Id, instance.Type, batchIndex, OperationStatus.Skipped, null, null, null, reason, EngineDiagnosticCodes.ResourceSkipped, TimeSpan.Zero);
            try
            {
                await _options.StateSink.ResourceSkippedAsync(instance, reason, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The skip is already in the result; only its record in the state is lost.
                EngineLog.StateSaveFailed(Logger, SecretRegistration.Scrub(ex.Message));
            }
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
            AddDiagnostic(Diagnostic.Error(EngineDiagnosticCodes.OutputEvaluationFailed, $"Output '{name}' could not be evaluated: {detail}", path, document.LocationOf(path), document.SourcePath));
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
