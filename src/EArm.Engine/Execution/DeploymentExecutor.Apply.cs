using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Diagnostics;
using EArm.Engine.Planning;
using EArm.Engine.Planning.Nested;
using EArm.Engine.Providers;
using EArm.Expressions;
using EArm.Graph;

namespace EArm.Engine.Execution;

public sealed partial class DeploymentExecutor
{
    private sealed partial class Run
    {
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

        /// <summary>
        /// A languageVersion 2.0 <c>"existing": true</c> resource: read by its key and published for <c>reference()</c>, never written. A missing
        /// object fails the resource (its dependents are skipped), as ARM fails a deployment that reads a resource that does not exist.
        /// </summary>
        private async Task ReadExistingAsync(ResourceInstance instance, IResourceProvider provider, int batchIndex, Stopwatch stopwatch, CancellationToken cancellationToken)
        {
            var key = provider.ResolveKey(instance);
            if (!key.IsResolved)
            {
                RecordFailure(instance, batchIndex, EngineDiagnosticCodes.ResourceKeyUnresolved, $"{instance.Describe()}: the Graph key of the existing resource cannot be resolved ({key.Describe()}).", stopwatch.Elapsed);
                return;
            }

            var existing = await _retry.WithRetryAsync(ct => provider.ReadAsync(key, _owner._graph, ct), instance.RetryHint, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                RecordFailure(instance, batchIndex, EngineDiagnosticCodes.ExistingResourceNotFound, DeploymentPlanner.ExistingNotFound(instance), stopwatch.Elapsed);
                return;
            }

            _changes[instance] = ResourceChange.ExistingRead(instance.Id, instance.Type, existing);
            SecretRegistration.RegisterTainted(existing.Raw);
            _plan.Resolver.Publish(instance.Id, existing.Raw, existing.GraphId, instance.PlannedId);
            _operations[instance] = new ResourceOperation(instance.Id, instance.Type, batchIndex, OperationStatus.Succeeded, ChangeType.NoChange, ChangeMode.None, existing.GraphId, null, null, stopwatch.Elapsed);
        }

        /// <summary>
        /// A nested deployment writes nothing: every resource of its template was applied in an earlier batch, so its outputs are evaluated and published
        /// for the <c>reference('&lt;deployment&gt;')</c> calls of later batches. An output that cannot be evaluated fails the deployment (its dependents are skipped).
        /// </summary>
        private void ApplyDeployment(ResourceInstance deployment, int batchIndex, Stopwatch stopwatch)
        {
            var result = NestedDeploymentOutputs.Evaluate(deployment, _plan.Evaluator);
            string? failure = result.Problems.Count > 0
                ? NestedDeploymentOutputs.Describe(deployment, result)
                : result.HasUnknown ? $"Nested deployment '{deployment.Id.Name}': some outputs depend on values that were not resolved during this run." : null;
            if (failure is not null)
            {
                RecordFailure(deployment, batchIndex, EngineDiagnosticCodes.OutputEvaluationFailed, failure, stopwatch.Elapsed);
                return;
            }

            NestedDeploymentOutputs.Publish(_plan.Resolver, deployment, result);
            _operations[deployment] = new ResourceOperation(deployment.Id, deployment.Type, batchIndex, OperationStatus.Succeeded, ChangeType.NoChange, ChangeMode.None, null, null, null, stopwatch.Elapsed);
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
                _plan.Resolver.MarkConditionFalse(instance.Id);
                _changes[instance] = ResourceChange.ConditionFalse(instance.Id, instance.Type);
                _operations[instance] = new ResourceOperation(instance.Id, instance.Type, batchIndex, OperationStatus.Ignored, ChangeType.Ignore, ChangeMode.None, null, null, null, stopwatch.Elapsed);
                return;
            }

            if (instance.IsDeployment)
            {
                ApplyDeployment(instance, batchIndex, stopwatch);
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
            if (instance.Declaration.IsExisting)
            {
                await ReadExistingAsync(instance, provider, batchIndex, stopwatch, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Name sugar and live-data validation (resourceAppId → resourceId, appRole → appRoleId, scopes → scope...) before the key is derived.
            await _retry.WithRetryAsync(async ct => { await provider.PrepareAsync(instance, _owner._graph, ct).ConfigureAwait(false); return true; }, RetryHint.Default, cancellationToken).ConfigureAwait(false);

            var key = provider.ResolveKey(instance);
            if (!key.IsResolved)
            {
                RecordFailure(instance, batchIndex, EngineDiagnosticCodes.ResourceKeyUnresolved, $"{instance.Describe()}: the Graph key cannot be resolved ({key.Describe()}).", stopwatch.Elapsed);
                return;
            }

            // Reads use the default hint: a 404 here means "create", not "not replicated yet". Identity is resolved live, by key, on every run
            // (a renamed object is a new one). A key read that can miss an existing object (KeyReadLagsCreate) is repeated before "nothing found"
            // becomes a create (KeyAbsenceCheck): its last read is the final look right before the POST, and an object any read finds is adopted
            // (NoChange or Modify) instead of being created a second time.
            var existing = await ReadBeforeApplyAsync(provider, instance, key, cancellationToken).ConfigureAwait(false);

            if (existing is not null && !TryClaim(instance, existing.GraphId, out var other))
            {
                EngineLog.DuplicateGraphObject(Logger, instance.Id.Id, other!.Id.Id, existing.GraphId);
                RecordFailure(instance, batchIndex, EngineDiagnosticCodes.DuplicateGraphObject, $"{instance.Describe()} and {other.Describe()} resolve to the same Graph object ({existing.GraphId}); two declarations must not manage one object. Remove one of them, or give them different keys.", stopwatch.Elapsed);
                return;
            }

            var change = AdoptOnlyCheck.Apply(_plan.Evaluator, instance, existing, provider.Plan(instance, existing, _planOptions));
            _changes[instance] = change;
            foreach (var reasonWarning in ProviderWarningReasons.DiagnosticsOf(instance, change))
            {
                _diagnostics.AddForInstance(instance, reasonWarning);
            }

            if (change.HasError)
            {
                RecordFailure(instance, batchIndex, change.UnsupportedCode ?? EngineDiagnosticCodes.ImmutablePropertyChanged, change.UnsupportedReason!, stopwatch.Elapsed);
                return;
            }

            EngineLog.Applying(Logger, instance.Id.Id, change.Type.ToString(), change.Mode.ToString());

            string? afterApplyFailure = null;
            string? afterApplyFailureCode = null;
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
                    var created = await _creator.CreateAsync(provider, instance, key, cancellationToken).ConfigureAwait(false);
                    if (created.AdoptedExisting is { } found && created.Failure is null)
                    {
                        // "Already exists": the object was adopted; the declared properties are applied now, in this run, instead of at the next one
                        // (or the provider refuses the object it found, and the resource fails with its reason).
                        created = await _creator.ApplyPropertiesToAdoptedAsync(provider, instance, found, created.Applied, cancellationToken).ConfigureAwait(false);
                    }

                    applied = created.Applied;
                    afterApplyFailure = created.Failure;
                    afterApplyFailureCode = created.FailureCode;
                    break;
                }

                case ChangeType.Modify when change.Mode == ChangeMode.Recreate:
                {
                    var recreated = await _replacer.RecreateAsync(provider, instance, key, existing!, cancellationToken).ConfigureAwait(false);
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
                {
                    // A partial update, or one cut short by a cancellation after a write may have committed, is recorded as it is (ResourceUpdater).
                    var updated = await _updater.UpdateAsync(provider, instance, key, existing!, change, cancellationToken).ConfigureAwait(false);
                    applied = updated.Applied;
                    afterApplyFailure = updated.Failure;
                    break;
                }

                default:
                    throw new InvalidOperationException($"{instance.Describe()}: the provider planned an unexpected change type {change.Type}.");
            }

            SecretRegistration.RegisterTainted(applied.Live);
            if (!TryClaim(instance, applied.GraphId, out var claimedBy))
            {
                EngineLog.DuplicateGraphObject(Logger, instance.Id.Id, claimedBy!.Id.Id, applied.GraphId);
                _diagnostics.AddForInstance(instance, Diagnostic.Error(EngineDiagnosticCodes.DuplicateGraphObject, $"{instance.Describe()} and {claimedBy.Describe()} resolve to the same Graph object ({applied.GraphId})."));
            }

            // A one-time secret exists only in this response: it goes to the owner-only file before anything else can fail (E-03d).
            await _secureOutputs.PersistOneTimeSecretsAsync(instance, applied).ConfigureAwait(false);

            foreach (var warning in applied.Warnings)
            {
                _diagnostics.AddForInstance(instance, Diagnostic.Warning(warning.Code, $"'{instance.Id.Id}': {SecretRegistration.Scrub(warning.Message)}"));
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
                RecordFailure(instance, batchIndex, afterApplyFailureCode ?? EngineDiagnosticCodes.ResourceApplyFailed, afterApplyFailure, stopwatch.Elapsed, applied.GraphId);
                return;
            }

            _operations[instance] = new ResourceOperation(instance.Id, instance.Type, batchIndex, OperationStatus.Succeeded, change.Type, change.Mode, applied.GraphId, null, null, stopwatch.Elapsed);
            EngineLog.Applied(Logger, instance.Id.Id, change.Type.ToString(), applied.GraphId, stopwatch.ElapsedMilliseconds);
        }

        /// <summary>
        /// The executor's read of <paramref name="key"/> before it plans and applies <paramref name="instance"/>. Normally the full <see cref="KeyAbsenceCheck"/>
        /// (first read, then the extra reads when it is empty and the key read lags). When the extra reads of this very key already ran during the earlier
        /// batches (<see cref="KeyAbsencePrefetch"/>) and were all empty, this waits for them if they are still running and then reads once more: that read is
        /// the final look before the POST, and an object it finds is adopted. Any other early outcome falls back to the full check. An adopt-only resource
        /// is read once: it is never created, so an empty read only fails it.
        /// </summary>
        private async Task<ExistingResource?> ReadBeforeApplyAsync(IResourceProvider provider, ResourceInstance instance, ResourceKey key, CancellationToken cancellationToken)
        {
            Task<ExistingResource?> Read(CancellationToken ct) => _retry.WithRetryAsync(c => provider.ReadAsync(key, _owner._graph, c), RetryHint.Default, ct);

            if (instance.Declaration.AdoptOnly is not null)
            {
                // An adopt-only resource is never created: an empty read fails it (AdoptOnlyNotFound) and writes nothing, so there is no duplicate for
                // the extra reads to prevent. One read decides.
                return await Read(cancellationToken).ConfigureAwait(false);
            }

            var check = _owner._keyAbsenceCheck;
            if (_absencePrefetch is { } prefetch && prefetch.TryTake(instance, key, out var early) && await early.WaitAsync(cancellationToken).ConfigureAwait(false))
            {
                var found = await Read(cancellationToken).ConfigureAwait(false);
                if (found is null)
                {
                    EngineLog.AbsenceConfirmedEarly(Logger, key.Describe(), check.ExtraReads, check.WaitedSeconds);
                }

                return found;
            }

            return await check.ReadAsync(provider, key, Read, Logger, batch: null, cancellationToken).ConfigureAwait(false);
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
    }
}
