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
/// before its POST, one batch after the other. When the key of such a planned create of a LATER batch is known when the run starts, its extra reads
/// start then, in the background, while the earlier batches deploy. A plan the what-if pass has read (<see cref="PlanMode.WhatIf"/>: a create without
/// <c>-y</c>, or with <c>-c</c>) knows the keys from its reads; a <see cref="PlanMode.Create"/> plan (<c>-y</c>) has evaluated nothing, so the keys come
/// from a sandboxed dry run (<see cref="SpeculativeKey"/>) that makes no live lookup and fills no cache of the run; a key that depends on a deploy-time
/// value is not prefetched.
/// <para>
/// Safety is unchanged: when the instance's turn comes, the executor resolves its key again; the early confirmation counts only for the SAME key
/// (<see cref="TryTake"/>) and only when every early extra read was empty, and the executor then still reads the key once more right before the POST
/// (that read is the final look, as the last extra read was before). An object any read finds is adopted, never created again. A key that changed, an
/// early read that found the object or failed, or an instance that was not prefetched falls back to the full check. A key that another instance of the
/// run may also lead to (the same key twice, or an instance of the type whose key is not known yet) is never prefetched: an object an earlier batch
/// creates under it is only seen through the lag by the full check.
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
    /// Starts the extra reads of every planned create of a later batch whose key read lags and whose key is known before the run (see the type).
    /// <paramref name="read"/> reads a key once (with the run's retry policy); at most <paramref name="parallelism"/> early reads are in flight at a time.
    /// Nothing is started when the check is disabled.
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
        if (check.ExtraReads == 0)
        {
            return prefetch;
        }

        foreach (var (instance, provider, key) in Candidates(plan))
        {
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

    /// <summary>
    /// The planned creates of the later batches (the first batch gains nothing: its instances reach their own check at once) whose type's key read lags
    /// and whose key is known before the run. A key that another instance of the plan may also lead to is never prefetched: when an earlier batch creates
    /// that object, only the full check right before the second POST can see it through the lag. So every deployed, writable instance of such a type
    /// must have a known key, and the candidate's key must be the only one of its kind.
    /// </summary>
    private static List<(ResourceInstance Instance, IResourceProvider Provider, ResourceKey Key)> Candidates(DeploymentPlan plan)
    {
        var later = new HashSet<ResourceInstance>(plan.Batches.Skip(1).SelectMany(b => b.Instances));
        var found = new List<(ResourceInstance Instance, IResourceProvider Provider, ResourceKey Key)>();
        var unknownKeyTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Graph compares these names without regard to case, so two keys that differ only in case are the same object.
        var keyCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in plan.Batches.SelectMany(b => b.Instances))
        {
            // Existing and adopt-only resources are never created: they cannot become a duplicate, nor make one.
            if (instance.IsDeployment || instance.Declaration.IsExisting || instance.Declaration.AdoptOnly is not null || instance.IsIgnored)
            {
                continue;
            }

            var provider = plan.Registry.Get(instance.Type);
            if (!provider.KeyReadLagsCreate)
            {
                continue;
            }

            var key = KnownKey(plan, instance, provider, out bool notDeployed);
            if (notDeployed)
            {
                continue;
            }

            if (key is null)
            {
                unknownKeyTypes.Add(instance.Type);
                continue;
            }

            string identity = Identity(key);
            keyCounts[identity] = keyCounts.GetValueOrDefault(identity) + 1;
            if (later.Contains(instance) && IsPlannedCreate(plan, instance))
            {
                found.Add((instance, provider, key));
            }
        }

        found.RemoveAll(c => unknownKeyTypes.Contains(c.Instance.Type) || keyCounts[Identity(c.Key)] > 1);
        return found;
    }

    /// <summary>
    /// The key the instance resolves to, known before the run: from the properties the what-if pass evaluated (<see cref="PlanMode.WhatIf"/>; the executor
    /// evaluates them again before it compares the key in <see cref="TryTake"/>), or from a sandboxed dry run of a plan that evaluated nothing
    /// (<see cref="PlanMode.Create"/>, <c>-y</c>: <see cref="SpeculativeKey"/>).
    /// </summary>
    private static ResourceKey? KnownKey(DeploymentPlan plan, ResourceInstance instance, IResourceProvider provider, out bool notDeployed)
    {
        notDeployed = false;
        if (instance.IsSymbolic)
        {
            return null;
        }

        if (plan.PlanMode == PlanMode.Create)
        {
            return SpeculativeKey.Resolve(plan, instance, provider, out notDeployed);
        }

        if (plan.ChangeFor(instance) is { IsConditionFalse: true })
        {
            notDeployed = true;
            return null;
        }

        if (instance.Condition != true || !instance.IsEvaluated)
        {
            return null;
        }

        try
        {
            var key = provider.ResolveKey(instance);
            return key.IsResolved ? key : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ProviderException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the executor is expected to create the instance: a clean Create row of the what-if pass, or any instance of a <see cref="PlanMode.Create"/>
    /// plan (it has no rows; an object that exists is found by the first early read, which ends the prefetch of that key).
    /// </summary>
    private static bool IsPlannedCreate(DeploymentPlan plan, ResourceInstance instance)
    {
        return plan.PlanMode == PlanMode.Create
            || plan.ChangeFor(instance) is { Type: ChangeType.Create, IsUnknown: false, IsSymbolicId: false, HasError: false, IsConditionFalse: false };
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
