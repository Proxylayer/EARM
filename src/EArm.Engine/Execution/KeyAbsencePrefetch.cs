using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using Microsoft.Extensions.Logging;

namespace EArm.Engine.Execution;

/// <summary>
/// Overlaps the absence confirmation of <see cref="KeyAbsenceCheck"/> with the batches before it. Without it, every dependency batch that creates a
/// new object of a type whose key read lags (<see cref="IResourceProvider.KeyReadLagsCreate"/>) waits the whole check (3 extra reads, 12 s) right
/// before its POST, one batch after the other. With a plan that the what-if pass has read (<see cref="PlanMode.WhatIf"/>: a create without <c>-y</c>,
/// or with <c>-c</c>), the key of each such planned create of a LATER batch is already known when the run starts, so its extra reads start then, in
/// the background, while the earlier batches deploy.
/// <para>
/// Safety is unchanged: when the instance's turn comes, the executor resolves its key again; the early confirmation counts only for the SAME key
/// (<see cref="TryTake"/>) and only when every early extra read was empty, and the executor then still reads the key once more right before the POST
/// (that read is the final look, as the last extra read was before). An object any read finds is adopted, never created again. A key that changed, an
/// early read that found the object or failed, or an instance that was not prefetched falls back to the full check. A <see cref="PlanMode.Create"/> plan
/// (<c>-y</c>) has evaluated nothing before the run, so nothing is prefetched: the keys of later batches would need a speculative evaluation, which
/// would also run the live <c>reference()</c> lookups early.
/// </para>
/// </summary>
internal sealed class KeyAbsencePrefetch : IAsyncDisposable
{
    private readonly Dictionary<ResourceInstance, Entry> _entries;
    private readonly List<Task<bool>> _started = [];
    private readonly CancellationTokenSource _stop;
    private readonly SemaphoreSlim _reads;

    private KeyAbsencePrefetch(Dictionary<ResourceInstance, Entry> entries, CancellationTokenSource stop, SemaphoreSlim reads)
    {
        _entries = entries;
        _stop = stop;
        _reads = reads;
    }

    /// <summary>
    /// Starts the extra reads of every planned create of a later batch whose key read lags and whose key the what-if pass resolved. <paramref name="read"/>
    /// reads a key once (with the run's retry policy); at most <paramref name="parallelism"/> early reads are in flight at a time. Nothing is started for a
    /// plan that was not read by the what-if pass, or when the check is disabled.
    /// </summary>
    public static KeyAbsencePrefetch Start(
        DeploymentPlan plan,
        KeyAbsenceCheck check,
        Func<IResourceProvider, ResourceKey, CancellationToken, Task<ExistingResource?>> read,
        int parallelism,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(logger);

        var entries = new Dictionary<ResourceInstance, Entry>();
        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var reads = new SemaphoreSlim(Math.Max(1, parallelism));
        var prefetch = new KeyAbsencePrefetch(entries, stop, reads);
        if (plan.PlanMode != PlanMode.WhatIf || check.ExtraReads == 0)
        {
            return prefetch;
        }

        // The first batch gains nothing: its instances reach their own check at once.
        foreach (var instance in plan.Batches.Skip(1).SelectMany(b => b.Instances))
        {
            if (Candidate(plan, instance) is not { } candidate)
            {
                continue;
            }

            var (provider, key) = candidate;
            var confirmation = ConfirmAsync(check, provider, key, read, reads, logger, stop.Token);
            entries[instance] = new Entry(Identity(key), confirmation);
            prefetch._started.Add(confirmation);
        }

        if (entries.Count > 0)
        {
            EngineLog.AbsencePrefetchStarted(logger, entries.Count);
        }

        return prefetch;
    }

    /// <summary>
    /// The early confirmation of <paramref name="instance"/> when it was prefetched for exactly <paramref name="key"/>; otherwise <c>false</c> and the
    /// caller runs the full check. The task completes with <c>true</c> only when every early extra read was empty (never faults).
    /// </summary>
    public bool TryTake(ResourceInstance instance, ResourceKey key, out Task<bool> confirmedAbsent)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(key);
        lock (_entries)
        {
            if (_entries.Remove(instance, out var entry) && entry.KeyIdentity == Identity(key))
            {
                confirmedAbsent = entry.ConfirmedAbsent;
                return true;
            }
        }

        confirmedAbsent = Task.FromResult(false);
        return false;
    }

    /// <summary>Stops the early reads that are still running (a stopped run, or instances that never reached their turn) and waits for them to end.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        lock (_entries)
        {
            _entries.Clear();
        }

        // Every confirmation, taken or not, ends here (none faults): no read outlives the run.
        await Task.WhenAll(_started).ConfigureAwait(false);
        _stop.Dispose();
        _reads.Dispose();
    }

    private static (IResourceProvider Provider, ResourceKey Key)? Candidate(DeploymentPlan plan, ResourceInstance instance)
    {
        if (instance.IsDeployment || instance.IsSymbolic || instance.Declaration.IsExisting || instance.Condition != true)
        {
            return null;
        }

        if (plan.ChangeFor(instance) is not { Type: ChangeType.Create, IsUnknown: false, IsSymbolicId: false, HasError: false, IsConditionFalse: false })
        {
            return null;
        }

        var provider = plan.Registry.Get(instance.Type);
        if (!provider.KeyReadLagsCreate)
        {
            return null;
        }

        try
        {
            // The properties the what-if pass evaluated; the executor evaluates them again before it compares the key (TryTake).
            var key = provider.ResolveKey(instance);
            return key.IsResolved ? (provider, key) : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ProviderException or NotSupportedException)
        {
            return null;
        }
    }

    private static async Task<bool> ConfirmAsync(
        KeyAbsenceCheck check,
        IResourceProvider provider,
        ResourceKey key,
        Func<IResourceProvider, ResourceKey, CancellationToken, Task<ExistingResource?>> read,
        SemaphoreSlim reads,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Leave the caller (the start of the run) at once: the reads begin with a wait anyway.
        await Task.Yield();
        try
        {
            var found = await check.RereadAsync(key, async ct =>
            {
                await reads.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    return await read(provider, key, ct).ConfigureAwait(false);
                }
                finally
                {
                    reads.Release();
                }
            }, logger, cancellationToken).ConfigureAwait(false);
            return found is null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A failed or stopped early read proves nothing: the instance gets the full check when its turn comes.
            return false;
        }
    }

    /// <summary>What makes two keys the same lookup: type, endpoint, key values, parent key and parent id.</summary>
    internal static string Identity(ResourceKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var sb = new StringBuilder();
        Append(sb, key);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, ResourceKey key)
    {
        sb.Append(key.Type.ToUpperInvariant()).Append('|').Append(key.Api).Append('|').Append(key.ParentGraphId).Append('|');
        foreach (var pair in key.Values.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(pair.Key.ToUpperInvariant()).Append('=').Append(pair.Value.Length).Append(':').Append(pair.Value).Append(';');
        }

        if (key.ParentKey is not null)
        {
            sb.Append("parent(");
            Append(sb, key.ParentKey);
            sb.Append(')');
        }
    }

    private sealed record Entry(string KeyIdentity, Task<bool> ConfirmedAbsent);
}
