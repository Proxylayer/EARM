using System;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Diagnostics;
using EArm.Engine.Providers;
using EArm.Graph;
using Microsoft.Extensions.Logging;

namespace EArm.Engine.Execution;

/// <summary>
/// The create path of a deployment run: the create itself, the adoption of an object that already exists, the look for an object whose create had an
/// uncertain outcome, and the read-your-write wait of a provider whose key read lags a create.
/// </summary>
internal sealed class ResourceCreator
{
    private readonly IGraphClient _graph;
    private readonly ReplicationWait _replicationWait;
    private readonly GraphCallRetry _retry;
    private readonly RunDiagnostics _diagnostics;
    private readonly PlanOptions _planOptions;
    private readonly ILogger _logger;

    public ResourceCreator(IGraphClient graph, ReplicationWait replicationWait, GraphCallRetry retry, RunDiagnostics diagnostics, PlanOptions planOptions, ILogger logger)
    {
        _graph = graph;
        _replicationWait = replicationWait;
        _retry = retry;
        _diagnostics = diagnostics;
        _planOptions = planOptions;
        _logger = logger;
    }

    /// <summary>The outcome of <see cref="CreateAsync"/>: what exists now, and the failure to report when the object exists but the create did not complete.</summary>
    /// <param name="Applied">The object (created, found after an ambiguous failure, or found under its key after "already exists").</param>
    /// <param name="Failure">Set when the object exists but a later step failed: the object is recorded and the resource is reported failed.</param>
    /// <param name="AdoptedExisting">Set when the create was answered with "already exists" and the existing object was taken over.</param>
    /// <param name="FailureCode">The diagnostic code of <paramref name="Failure"/>; <c>null</c> for <see cref="EngineDiagnosticCodes.ResourceApplyFailed"/>.</param>
    public sealed record CreateOutcome(AppliedResource Applied, string? Failure, ExistingResource? AdoptedExisting, string? FailureCode = null);

    /// <summary>
    /// Creates the object. "Already exists" is an idempotent success (the object is re-read by key and adopted); so is an ambiguous
    /// failure (timeout, transport error, 5xx, a cancellation that is not the caller's) after which the object turns out to exist (E-05): the create is not repeated, and not by the outer retry
    /// either unless the create is an upsert (<see cref="IResourceProvider.CreateIsRepeatable"/>; review round 2 S2-07). A failure AFTER the object was written (a license, an owner, a restore step of the provider) is
    /// found again: the provider hands the partial result over (<see cref="ProviderException.Partial"/>), or the object is looked up by key; either way
    /// it is published and the resource is reported failed; a rerun finds it again by key and converges.
    /// </summary>
    public async Task<CreateOutcome> CreateAsync(IResourceProvider provider, ResourceInstance instance, ResourceKey key, CancellationToken cancellationToken)
    {
        AppliedResource created;
        try
        {
            created = await _retry.WithRetryAsync(ct => provider.CreateAsync(instance, _graph, ct), instance.RetryHint, cancellationToken, idempotent: provider.CreateIsRepeatable).ConfigureAwait(false);
        }
        catch (ProviderException ex) when (ex.Partial is { } partial)
        {
            return new CreateOutcome(partial, PartialFailure(instance, partial, ex, "created"), null);
        }
        catch (GraphException ex) when (ex.IsAlreadyExists)
        {
            EngineLog.AdoptingExisting(_logger, instance.Id.Id);
            var existing = await ReadAfterCreateAsync(provider, key, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                throw;
            }

            return new CreateOutcome(new AppliedResource(instance.Id, existing.GraphId, existing.Raw, key), null, existing);
        }
        catch (Exception ex) when (ProviderReads.IsAmbiguousWriteFailure(ex) || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // The write may have been committed although no answer came back: look for the object (waiting out replication) instead of failing or repeating the POST.
            // A cancellation the caller did not ask for (a raw HTTP timeout of a client or decorator that does not map it to GraphTransportException) is the same case.
            var found = await TryReadAfterCreateAsync(provider, key, cancellationToken).ConfigureAwait(false);
            if (found is null)
            {
                throw;
            }

            string error = ex is OperationCanceledException canceled ? NonCallerCancellation.Describe(canceled) : SecretRegistration.Scrub(ex.Message);
            EngineLog.CreateOutcomeUncertain(_logger, instance.Id.Id, error, found.GraphId);
            _diagnostics.AddForInstance(instance, Diagnostic.Warning(EngineDiagnosticCodes.CreateOutcomeUncertain, $"'{instance.Id.Id}': the create ended with an unknown outcome ({error}); the object exists (Graph id {found.GraphId}), so the create is counted as done. Values only returned at creation (a secret) are not available for it."));
            return new CreateOutcome(new AppliedResource(instance.Id, found.GraphId, found.Raw, key), null, null);
        }
        catch (Exception ex) when (ex is GraphException or GraphTransportException or ProviderException { Partial: null })
        {
            // A provider that writes in several steps (create, then owners, licenses, restore...) and failed after the first: the object exists even though the call failed.
            var found = await TryReadByKeyAsync(provider, key, cancellationToken).ConfigureAwait(false);
            if (found is null || (ex is ProviderException && Refuses(provider, instance, found)))
            {
                // Nothing there; or the provider refused the object it found under the key (it answered "already exists" and the live object is one it
                // must not manage, such as a permanent PIM assignment): nothing was created, and the provider's refusal is the outcome to report.
                throw;
            }

            var partial = new AppliedResource(instance.Id, found.GraphId, found.Raw, key);
            string failure = $"{instance.Describe()}: the object was created (Graph id {found.GraphId}) but the create did not complete: {SecretRegistration.Scrub(ex.Message)}. Run the deployment again to finish it.";
            _diagnostics.AddForInstance(instance, Diagnostic.Warning(EngineDiagnosticCodes.ResourceApplyFailed, $"'{instance.Id.Id}': the object exists in the tenant (Graph id {found.GraphId}) although the create failed; a rerun finds it by key."));
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
    /// (<see cref="IResourceProvider.KeyReadLagsCreate"/>: the <c>$filter=displayName eq</c> lists). The key is read again after each miss on the adaptive
    /// schedule (<see cref="ReplicationWait.IntervalAfter"/>) until it has returned the created id <see cref="ReplicationWait.ConsecutiveVisibleReads"/> times in a row
    /// (<see cref="ReplicationWait.StableGap"/> apart; the list flaps, so a miss after a hit starts the count again) or <see cref="ReplicationWait.Window"/>
    /// of waiting was scheduled, so a run started right after this one finds the object instead of planning (and POSTing) it again. Never fails the create: when the object does not
    /// show up in time (or the read itself fails), the <see cref="EngineDiagnosticCodes.CreatedObjectNotVisible"/> warning says so. The reads are not
    /// cancellable (the create is done; a cancelled read must not be mistaken for a cancelled create); a cancellation ends the wait with the warning.
    /// </summary>
    private async Task ConfirmCreatedReadableByKeyAsync(IResourceProvider provider, ResourceInstance instance, ResourceKey key, string graphId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(graphId))
        {
            return;
        }

        var wait = _replicationWait;
        var waited = TimeSpan.Zero;
        string? readFailure = null;
        int visibleInARow = 0;
        int misses = 0;
        while (true)
        {
            bool visible = false;
            try
            {
                var found = await _retry.WithRetryAsync(ct => provider.ReadAsync(key, _graph, ct), RetryHint.Default, CancellationToken.None).ConfigureAwait(false);
                visible = found is not null && string.Equals(found.GraphId, graphId, StringComparison.OrdinalIgnoreCase);
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

            visibleInARow = visible ? visibleInARow + 1 : 0;
            if (visibleInARow >= wait.ConsecutiveVisibleReads)
            {
                return;
            }

            // A miss waits on the adaptive schedule (1, 2, 4 s, then the interval); a hit that is not yet the last one in a row waits the short gap.
            var pause = visible ? wait.StableGap : wait.IntervalAfter(++misses);
            if (cancellationToken.IsCancellationRequested || waited + pause > wait.Window)
            {
                break;
            }

            try
            {
                await wait.Delay.DelayAsync(pause, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            waited += pause;
            if (!visible)
            {
                EngineLog.WaitingForReplication(_logger, key.Describe(), waited.TotalSeconds, wait.Window.TotalSeconds);
            }
        }

        string why = readFailure is not null
            ? $"looking it up by its key ({key.Describe()}) failed: {readFailure}"
            : $"a lookup by its key ({key.Describe()}) did not return it after {waited.TotalSeconds:0} s of waiting for Graph to replicate it";
        _diagnostics.AddForInstance(instance, Diagnostic.Warning(EngineDiagnosticCodes.CreatedObjectNotVisible, $"'{instance.Id.Id}': the object was created (Graph id {graphId}), but {why}. A run started before Graph catches up may not find it and plan to create it again (a duplicate); wait a minute before the next run, and check with 'deployment what-if' that it shows no create."));
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
            return await _retry.WithRetryAsync(ct => provider.ReadAsync(key, _graph, ct), hint, cancellationToken).ConfigureAwait(false);
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
    /// 400 - is read again on the adaptive schedule (<see cref="ReplicationWait.IntervalAfter"/>: 1, 2, 4 s, then every <see cref="ReplicationWait.Interval"/>)
    /// until the object turns up or <see cref="ReplicationWait.Window"/> of waiting was scheduled.
    /// The executor owns this wait whether or not the plan carries a retry policy: each read goes through the policy (when there is one) with
    /// <see cref="RetryHint.Default"/>, so the policy still waits out throttling and transient failures but never waits out replication a second time.
    /// The reads are not cancellable (the create may have committed: an object found is recorded, not orphaned); a cancellation cuts the WAIT short
    /// to one last read. Any other failure of a read propagates.
    /// </summary>
    private async Task<ExistingResource?> ReadAfterCreateAsync(IResourceProvider provider, ResourceKey key, CancellationToken cancellationToken)
    {
        var wait = _replicationWait;
        var waited = TimeSpan.Zero;
        for (int misses = 1; ; misses++)
        {
            var found = await ReadOnceAfterCreateAsync(provider, key).ConfigureAwait(false);
            var pause = wait.IntervalAfter(misses);
            if (found is not null || cancellationToken.IsCancellationRequested || waited + pause > wait.Window)
            {
                return found;
            }

            try
            {
                await wait.Delay.DelayAsync(pause, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return await ReadOnceAfterCreateAsync(provider, key).ConfigureAwait(false);
            }

            waited += pause;
            EngineLog.WaitingForReplication(_logger, key.Describe(), waited.TotalSeconds, wait.Window.TotalSeconds);
        }
    }

    /// <summary>One uncancellable read by key; "not there yet" (a 404, a replication-lag answer) is <c>null</c>.</summary>
    private async Task<ExistingResource?> ReadOnceAfterCreateAsync(IResourceProvider provider, ResourceKey key)
    {
        try
        {
            return await _retry.WithRetryAsync(ct => provider.ReadAsync(key, _graph, ct), RetryHint.Default, CancellationToken.None).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.IsNotFound || GraphException.IsReplicationError(ex.Status, ex.Code, ex.GraphMessage))
        {
            return null;
        }
    }

    public static string PartialFailure(ResourceInstance instance, AppliedResource partial, ProviderException ex, string verb)
    {
        return $"{instance.Describe()}: the object was {verb} (Graph id {partial.GraphId}), but a later step failed: {SecretRegistration.Scrub(ex.Message)}. Run the deployment again to finish it.";
    }

    /// <summary>
    /// After "already exists" was answered for a create: applies the declared properties to the adopted object now (one more PATCH where the provider supports it), instead of leaving it to the next run.
    /// An existing object the provider refuses to manage (its plan has an error, such as a permanent PIM assignment where a time-bound one is declared) is not adopted:
    /// the resource fails with the provider's reason and code, and the Graph id of the object it found.
    /// </summary>
    public async Task<CreateOutcome> ApplyPropertiesToAdoptedAsync(IResourceProvider provider, ResourceInstance instance, ExistingResource found, AppliedResource adopted, CancellationToken cancellationToken)
    {
        var followUp = provider.Plan(instance, found, _planOptions);
        if (followUp.Type == ChangeType.NoChange || followUp.Type == ChangeType.Ignore)
        {
            return new CreateOutcome(adopted, null, found);
        }

        if (followUp.HasError)
        {
            string refusal = $"{instance.Describe()}: Graph answered that the object already exists, and the existing object (Graph id {found.GraphId}) cannot be adopted: {followUp.UnsupportedReason}";
            return new CreateOutcome(adopted, refusal, found, followUp.UnsupportedCode ?? EngineDiagnosticCodes.ImmutablePropertyChanged);
        }

        if (followUp.Type != ChangeType.Modify || followUp.Mode != ChangeMode.Patch)
        {
            _diagnostics.AddForInstance(instance, Diagnostic.Warning(EngineDiagnosticCodes.ImmutablePropertyChanged, $"'{instance.Id.Id}': the existing object differs from the template in a way that cannot be changed in place ({string.Join("; ", followUp.Reasons)}); it was left as it is. Run 'deployment what-if' to see the difference."));
            return new CreateOutcome(adopted, null, found);
        }

        try
        {
            var updated = await _retry.WithRetryAsync(ct => provider.UpdateAsync(instance, found, followUp, _graph, ct), instance.RetryHint, cancellationToken).ConfigureAwait(false);
            return new CreateOutcome(updated, null, found);
        }
        catch (ProviderException ex) when (ex.Partial is { } partial)
        {
            _diagnostics.AddForInstance(instance, Diagnostic.Warning(EngineDiagnosticCodes.ResourceApplyFailed, $"'{instance.Id.Id}': {SecretRegistration.Scrub(ex.Message)}"));
            return new CreateOutcome(partial, null, found);
        }
    }

    /// <summary>True when the provider's plan for the object it found under the key has an error: it refuses to manage that object.</summary>
    private bool Refuses(IResourceProvider provider, ResourceInstance instance, ExistingResource found)
    {
        try
        {
            return provider.Plan(instance, found, _planOptions).HasError;
        }
        catch (Exception ex) when (ex is ProviderException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }
}
