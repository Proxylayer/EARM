using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
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
public sealed partial class DeploymentExecutor
{
    private readonly ProviderRegistry _registry;
    private readonly IGraphClient _graph;
    private readonly ILogger _logger;
    private readonly ReplicationWait _replicationWait;
    private readonly KeyAbsenceCheck _keyAbsenceCheck;

    /// <param name="registry">The resource providers.</param>
    /// <param name="graph">The Graph client.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <param name="replicationWait">How long an object is looked for after a create with an uncertain outcome; default <see cref="ReplicationWait.Default"/>.</param>
    /// <param name="keyAbsenceCheck">How an empty key read of a type whose key read lags is confirmed before it is created; default <see cref="KeyAbsenceCheck.Default"/>.</param>
    public DeploymentExecutor(ProviderRegistry registry, IGraphClient graph, ILogger<DeploymentExecutor> logger, ReplicationWait? replicationWait = null, KeyAbsenceCheck? keyAbsenceCheck = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _graph = graph;
        _logger = logger;
        _replicationWait = replicationWait ?? ReplicationWait.Default;
        _keyAbsenceCheck = keyAbsenceCheck ?? KeyAbsenceCheck.Default;
    }

    public async Task<DeploymentResult> ExecuteAsync(DeploymentPlan plan, ExecuteOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);

        var run = new Run(this, plan, options);
        return await run.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed partial class Run
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
        private readonly RunDiagnostics _diagnostics = new();
        private readonly ExecutionRetry _retry;
        private readonly ResourceCreator _creator;
        private readonly ResourceReplacer _replacer;
        private readonly SecureOutputsRecorder _secureOutputs;
        private IReadOnlyList<ResourceChange> _orphans = [];
        private KeyAbsencePrefetch? _absencePrefetch;
        private bool _cancelledInEndPhase;

        public Run(DeploymentExecutor owner, DeploymentPlan plan, ExecuteOptions options)
        {
            _owner = owner;
            _plan = plan;
            _options = options;
            _planOptions = plan.Options with { PlanMode = PlanMode.Create };
            _retry = new ExecutionRetry(plan.RetryPolicy);
            _creator = new ResourceCreator(owner._graph, owner._replicationWait, _retry, _diagnostics, _planOptions, owner._logger);
            var laggingDeletes = new LaggingDeleteRetry(owner._replicationWait, owner._logger);
            _replacer = new ResourceReplacer(plan, options, owner._graph, _creator, laggingDeletes, _retry, _diagnostics, owner._logger);
            _secureOutputs = new SecureOutputsRecorder(options.SecureOutputsFile, _diagnostics, owner._logger);
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
                _diagnostics.Add(Diagnostic.Error(EngineDiagnosticCodes.ResourceApplyFailed, message));
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

            (outputs, secureOutputs) = new OutputEvaluator(_plan, _options.RevealSecureOutputs, _diagnostics).Evaluate(none);
            secureFile = _secureOutputs.WriteSecureOutputs(secureOutputs);

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

            _diagnostics.Flush(_plan.Batches.SelectMany(b => b.Instances));
            operations = _plan.Batches.SelectMany(b => b.Instances).Where(_operations.ContainsKey).Select(i => _operations[i]).ToList();
            changes = _plan.Batches.SelectMany(b => b.Instances).Where(_changes.ContainsKey).Select(i => _changes[i]).ToList();
            bool anyFailure = aborted || operations.Any(o => o.Status is OperationStatus.Failed or OperationStatus.Skipped) || _diagnostics.Snapshot().Any(d => d.IsError);
            status = cancelled ? DeploymentStatus.Cancelled : anyFailure ? DeploymentStatus.Failed : DeploymentStatus.Succeeded;

            stopwatch.Stop();
            if (cancelled)
            {
                EngineLog.Completed(Logger, _plan.DeploymentName, "Cancelled", operations.Count(o => o.Status == OperationStatus.Succeeded), operations.Count(o => o.Status == OperationStatus.Failed), operations.Count(o => o.Status == OperationStatus.Skipped), operations.Count(o => o.Status == OperationStatus.Ignored), stopwatch.ElapsedMilliseconds);
                throw new OperationCanceledException("The deployment was cancelled; the result and the secure outputs file record what had already been applied.", cancellationToken);
            }

            var result = new DeploymentResult(_plan.DeploymentName, status, operations, changes, outputs, secureOutputs, _diagnostics.Snapshot(), secureFile, stopwatch.Elapsed, _orphans);
            EngineLog.Completed(Logger, _plan.DeploymentName, status.ToString(), result.SucceededCount, result.FailedCount, result.SkippedCount, result.IgnoredCount, stopwatch.ElapsedMilliseconds);
            return result;
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
                _diagnostics.Add(Diagnostic.Error(EngineDiagnosticCodes.ResourceApplyFailed, message));
            }
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
    }
}
