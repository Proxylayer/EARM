using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EArm.Engine.State;

/// <summary>Who holds a deployment's lock (the content of the <c>.lock</c> file).</summary>
public sealed record StateLockInfo(int ProcessId, string Host, string User, DateTimeOffset AcquiredOn, DateTimeOffset? HeartbeatOn = null)
{
    public string Describe()
    {
        return string.Create(CultureInfo.InvariantCulture, $"process {ProcessId} on {Host} ({User}) since {StateSerializer.Timestamp(AcquiredOn)}");
    }
}

/// <summary>
/// Persistence of <see cref="StateFile"/>s: one state per deployment name, atomic saves, a lock
/// per deployment while a run writes it. <see cref="FileStateStore"/> is the file backend; a remote (blob lease)
/// backend is v1.1.
/// </summary>
public interface IStateStore
{
    /// <summary>The state of a deployment, or <c>null</c> when it has never run.</summary>
    /// <exception cref="StateSchemaUnsupportedException">The stored state cannot be read by this version.</exception>
    Task<StateFile?> LoadAsync(string deploymentName, CancellationToken cancellationToken);

    /// <summary>Persists the state atomically (readers never see a partial file).</summary>
    Task SaveAsync(StateFile state, CancellationToken cancellationToken);

    /// <summary>Removes the state of a deployment; <c>false</c> when there was none.</summary>
    Task<bool> DeleteAsync(string deploymentName, CancellationToken cancellationToken);

    /// <summary>The deployment names that have a state, sorted.</summary>
    Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Acquires the deployment's lock, waiting up to <paramref name="wait"/> for a holder to release it (a lock older
    /// than the stale threshold is taken over). Dispose the result to release it.
    /// </summary>
    /// <exception cref="StateLockedException">The lock is still held when the wait expires.</exception>
    Task<IAsyncDisposable> LockAsync(string deploymentName, TimeSpan wait, CancellationToken cancellationToken);

    /// <summary>The current lock holder, or <c>null</c> when the deployment is not locked.</summary>
    Task<StateLockInfo?> GetLockAsync(string deploymentName, CancellationToken cancellationToken);

    /// <summary>Breaks the lock regardless of its holder (<c>state unlock</c>, <c>--force-unlock</c>); returns the former holder, or <c>null</c> when there was no lock.</summary>
    Task<StateLockInfo?> ForceUnlockAsync(string deploymentName, CancellationToken cancellationToken);
}

/// <summary>Deployment name rules: the name is also the state file name.</summary>
public static partial class StateNames
{
    public const int MaxLength = 128;

    /// <summary>
    /// True when the name is usable as a state file name: the letters/digits/<c>.</c>/<c>_</c>/<c>-</c> pattern (anchored with
    /// <c>\A..\z</c>: <c>$</c> would accept a trailing newline), no trailing dot (Windows drops it) and not a Windows reserved
    /// device name (<c>CON</c>, <c>PRN</c>, <c>AUX</c>, <c>NUL</c>, <c>COM1</c>-<c>COM9</c>, <c>LPT1</c>-<c>LPT9</c>, in any case, with or without an extension).
    /// </summary>
    public static bool IsValid(string? name)
    {
        return !string.IsNullOrEmpty(name) && name.Length <= MaxLength && NamePattern().IsMatch(name) && !name.EndsWith('.') && !IsReservedDeviceName(name);
    }

    /// <summary>True for <c>CON</c>, <c>PRN</c>, <c>AUX</c>, <c>NUL</c>, <c>COM1-9</c> and <c>LPT1-9</c> (any case), also when followed by <c>.</c> and an extension.</summary>
    public static bool IsReservedDeviceName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        int dot = name.IndexOf('.', StringComparison.Ordinal);
        string stem = (dot < 0 ? name : name[..dot]).TrimEnd();
        return ReservedDeviceNames().IsMatch(stem);
    }

    /// <summary>Throws <see cref="ArgumentException"/> when the name is not usable as a state file name.</summary>
    public static void Validate(string? name)
    {
        if (!IsValid(name))
        {
            throw new ArgumentException($"Deployment name '{name}' is not valid: use 1-{MaxLength} letters, digits, '.', '_' or '-' (it names the state file; no trailing '.', and not a reserved device name such as CON, NUL, COM1 or LPT1).", nameof(name));
        }
    }

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedDeviceNames();
}
