using System.Threading;

namespace EArm.Providers;

/// <summary>
/// Remembers that Graph refused the alternate-key form of a lookup (<c>/groups(uniqueName='…')</c>, <c>/applications(uniqueName='…')</c>) with a 400, so the
/// later lookups of the same provider instance go straight to the <c>$filter</c> query instead of repeating a request that will be refused again. A provider
/// instance lives for one run (the registry is built per run), so a run never inherits another run's answer. Thread-safe: reads run in parallel.
/// </summary>
internal sealed class AlternateKeyFallback
{
    private int _refused;

    /// <summary>True once Graph has refused the alternate-key form in this run.</summary>
    public bool Refused => Volatile.Read(ref _refused) != 0;

    /// <summary>Records the refusal.</summary>
    public void MarkRefused()
    {
        Interlocked.Exchange(ref _refused, 1);
    }
}
