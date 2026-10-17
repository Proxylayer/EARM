using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine.Planning;
using EArm.Graph;

namespace EArm.Engine.Execution;

public sealed partial class DeploymentExecutor
{
    private sealed partial class Run
    {
        /// <summary>Walks the batches; returns true when the run stopped at a batch boundary because a resource failed.</summary>
        private async Task<bool> RunBatchesAsync(CancellationToken cancellationToken)
        {
            int parallelism = Math.Max(1, _options.Parallelism ?? _plan.Parallelism);
            var parallel = new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken };

            // The absence confirmation of the later batches' planned creates runs while the earlier batches deploy (KeyAbsencePrefetch).
            await using var prefetch = KeyAbsencePrefetch.Start(
                _plan,
                _owner._keyAbsenceCheck,
                (provider, key, ct) => _retry.WithRetryAsync(c => provider.ReadAsync(key, _owner._graph, c), RetryHint.Default, ct),
                parallelism,
                Logger,
                cancellationToken);
            _absencePrefetch = prefetch;
            try
            {
                return await RunBatchesCoreAsync(parallel, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _absencePrefetch = null;
            }
        }

        private async Task<bool> RunBatchesCoreAsync(ParallelOptions parallel, CancellationToken cancellationToken)
        {
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
                    _diagnostics.Flush(batch.Instances);
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
    }
}
