using System;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine.Providers;
using EArm.Graph;
using Microsoft.Extensions.Logging;

namespace EArm.Engine.Planning;

/// <summary>
/// Confirms that an object is really absent before a create is planned, for a provider whose key read can miss an existing object
/// (<see cref="IResourceProvider.KeyReadLagsCreate"/>: the <c>$filter=displayName eq</c> lists of Conditional Access policies, named locations,
/// authentication strengths, administrative units and custom role definitions). Graph's list endpoint for these types FLAPS for a while after a
/// write: one read lists the object, the next one (served by another replica) does not. A single empty read is therefore not proof of absence:
/// planning a create on it makes <c>what-if</c> show a create for an object that exists, and makes <c>create</c> POST a duplicate.
/// <para>
/// When the first read finds nothing, the key is read <see cref="ExtraReads"/> more times, <see cref="Interval"/> apart; the object counts as
/// absent only when EVERY read is empty, and the first read that finds it wins. Objects that exist cost nothing extra (the first read finds
/// them), and so do types keyed by an id or an alternate key (they never flap). The price is latency for a genuinely new object of these types:
/// <see cref="ExtraReads"/> x <see cref="Interval"/> (12 s by default) per new object, paid by the <c>create</c> executor right before its POST
/// (so the last read before a create is never older than the wait), and by the planner only when <see cref="PlannerOptions.ConfirmKeyAbsence"/>
/// asks for it (<c>deployment delete</c>, which deletes what the plan finds). <c>what-if</c> and the planning pass of <c>create</c> plan on the
/// first read: a false "absent" there only shows a create the executor's own check then turns into an adoption. Reads in a batch run in
/// parallel, so the cost is per batch, not per object.
/// </para>
/// </summary>
public sealed record KeyAbsenceCheck
{
    /// <summary>Three more reads, four seconds apart.</summary>
    public static KeyAbsenceCheck Default { get; } = new();

    /// <summary>The reads after the first empty one; zero disables the check.</summary>
    public int ExtraReads
    {
        get;
        init => field = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(ExtraReads), value, "The number of extra reads must not be negative.");
    } = 3;

    /// <summary>The wait before each extra read; must be positive.</summary>
    public TimeSpan Interval
    {
        get;
        init => field = value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(Interval), value, "The interval must be positive.");
    } = TimeSpan.FromSeconds(4);

    /// <summary>Waits between the reads; tests pass one that does not sleep.</summary>
    public IDelayProvider Delay
    {
        get;
        init => field = value ?? throw new ArgumentNullException(nameof(Delay));
    } = TaskDelayProvider.Instance;

    /// <summary>
    /// Reads the object by its key with <paramref name="read"/>; for a provider whose key read lags, an empty result is read again (see the type)
    /// before it is returned as "absent". Failures of a read and the caller's cancellation propagate.
    /// </summary>
    internal async Task<ExistingResource?> ReadAsync(IResourceProvider provider, ResourceKey key, Func<CancellationToken, Task<ExistingResource?>> read, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(logger);

        var found = await read(cancellationToken).ConfigureAwait(false);
        if (found is not null || !provider.KeyReadLagsCreate)
        {
            return found;
        }

        for (int attempt = 1; attempt <= ExtraReads; attempt++)
        {
            await Delay.DelayAsync(Interval, cancellationToken).ConfigureAwait(false);
            EngineLog.ConfirmingAbsence(logger, key.Describe(), attempt, ExtraReads);
            found = await read(cancellationToken).ConfigureAwait(false);
            if (found is not null)
            {
                EngineLog.FoundOnReread(logger, key.Describe(), found.GraphId, attempt);
                return found;
            }
        }

        return null;
    }
}
