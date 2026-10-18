using System;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine.Providers;
using EArm.Graph;

namespace EArm.Engine.Execution;

/// <summary>
/// The in-place update (PATCH) path of a deployment run. An update cut short by a cancellation (Ctrl+C between two requests of the provider, or a raw
/// HTTP timeout) may have been committed: instead of reporting the resource as skipped (or as an unexpected error), the object is read back once by
/// key and planned again, so the outcome is reported as it is: applied when the object now matches the template, failed with the object recorded otherwise.
/// </summary>
internal sealed class ResourceUpdater
{
    private readonly IGraphClient _graph;
    private readonly GraphCallRetry _retry;
    private readonly PlanOptions _planOptions;

    public ResourceUpdater(IGraphClient graph, GraphCallRetry retry, PlanOptions planOptions)
    {
        _graph = graph;
        _retry = retry;
        _planOptions = planOptions;
    }

    /// <summary>The outcome of <see cref="UpdateAsync"/>: the object as it is now, and the failure to report when the update did not (or may not have) completed.</summary>
    public sealed record UpdateOutcome(AppliedResource Applied, string? Failure);

    public async Task<UpdateOutcome> UpdateAsync(IResourceProvider provider, ResourceInstance instance, ResourceKey key, ExistingResource existing, ResourceChange change, CancellationToken cancellationToken)
    {
        try
        {
            var applied = await _retry.WithRetryAsync(ct => provider.UpdateAsync(instance, existing, change, _graph, ct), instance.RetryHint, cancellationToken).ConfigureAwait(false);
            return new UpdateOutcome(applied, null);
        }
        catch (ProviderException ex) when (ex.Partial is { } partial)
        {
            // The primary write happened and a later step failed: what exists is recorded, the resource is reported failed.
            return new UpdateOutcome(partial, ResourceCreator.PartialFailure(instance, partial, ex, "updated"));
        }
        catch (OperationCanceledException ex)
        {
            // One last, uncancellable look by key. When the object is gone or is another one, nothing can be said about the update: the cancellation
            // stands (the caller's: the resource is reported skipped; a timeout: it fails with an unexpected error, as before).
            var found = await TryReadOnceAsync(provider, key).ConfigureAwait(false);
            if (found is null || !string.Equals(found.GraphId, existing.GraphId, StringComparison.OrdinalIgnoreCase))
            {
                throw;
            }

            var now = new AppliedResource(instance.Id, found.GraphId, found.Raw, key);
            var remaining = provider.Plan(instance, found, _planOptions);
            if (remaining.Type is ChangeType.NoChange or ChangeType.Ignore)
            {
                // The object matches the template: the update was applied before the cancellation.
                return new UpdateOutcome(now, null);
            }

            string why = cancellationToken.IsCancellationRequested
                ? "the deployment was cancelled while the object was being updated"
                : $"the update ended with an unknown outcome ({NonCallerCancellation.Describe(ex)})";
            return new UpdateOutcome(now, $"{instance.Describe()}: {why}; the object (Graph id {found.GraphId}) does not match the template yet, so the update may have been applied in part or not at all. Run the deployment again to finish it.");
        }
    }

    /// <summary>One uncancellable read by key; any failure of the read itself counts as "not found".</summary>
    private async Task<ExistingResource?> TryReadOnceAsync(IResourceProvider provider, ResourceKey key)
    {
        try
        {
            return await _retry.WithRetryAsync(ct => provider.ReadAsync(key, _graph, ct), RetryHint.Default, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception readFailure) when (readFailure is GraphException or GraphTransportException or ProviderException or InvalidOperationException or ArgumentException or OperationCanceledException)
        {
            return null;
        }
    }
}
