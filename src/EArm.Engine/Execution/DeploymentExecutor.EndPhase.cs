using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Diagnostics;
using EArm.Engine.Edges;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Expressions;
using EArm.Graph;

namespace EArm.Engine.Execution;

public sealed partial class DeploymentExecutor
{
    private sealed partial class Run
    {
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
                _diagnostics.Add(Diagnostic.Warning(EngineDiagnosticCodes.EdgePassSkipped, "The edge pass did not run because an earlier batch failed; relationships (members, owners, memberOf, ownerOf, administrativeUnits, manager) were not reconciled. Run the deployment again once the failure is fixed."));
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
            var unreliable = _plan.Instances.Where(i => !i.IsSymbolic && !i.IsIgnored && !i.IsDeployment && !candidateSet.Contains(i)
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
                    _diagnostics.Add(diagnostic);
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
                    _diagnostics.Add(Diagnostic.Warning(EngineDiagnosticCodes.OrphanReportIncomplete, "The orphan report did not run because an earlier batch failed; undeclared objects were not listed. Nothing is ever deleted by Complete mode."));
                }

                return;
            }

            var result = await new OrphanReport(_plan, _owner._graph).RunAsync(IsSettled, cancellationToken).ConfigureAwait(false);
            _orphans = result.Rows;
            foreach (var diagnostic in result.Diagnostics)
            {
                _diagnostics.Add(diagnostic);
            }
        }

        /// <summary>True when the instance was applied (or found unchanged) in this run: its live object is published.</summary>
        private bool IsSettled(ResourceInstance instance)
        {
            return _operations.TryGetValue(instance, out var operation) && operation.Status == OperationStatus.Succeeded;
        }
    }
}
