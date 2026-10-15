using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EArm.Engine.State;

/// <summary>
/// The file backend of <see cref="IStateStore"/>: <c>&lt;directory&gt;/&lt;deploymentName&gt;.json</c>, written to a
/// temp file and moved into place (<see cref="File.Move(string, string, bool)"/>), locked by
/// <c>&lt;deploymentName&gt;.lock</c> created with <see cref="FileMode.CreateNew"/> (pid, host, user, UTC time, lock id).
/// The holder rewrites the lock's heartbeat every <see cref="HeartbeatInterval"/> while it runs; a lock whose newest timestamp is
/// older than <see cref="StaleLockAge"/> (one hour) is taken over with a warning, unless a process of that pid still runs on
/// this host, and only if its content did not change since it was judged stale (review finding E-10). The state directory and
/// every state, lock and temp file must be ordinary (a symbolic link or junction is refused, review finding S-06). The default
/// directory is <c>.earm/state</c> next to the template (<see cref="StateDirectory"/>).
/// </summary>
public sealed class FileStateStore : IStateStore
{
    public const string StateExtension = ".json";

    public const string LockExtension = ".lock";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Reads a state file: malformed bytes throw instead of becoming U+FFFD.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public FileStateStore(string directory, TimeProvider? timeProvider = null, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = Path.GetFullPath(directory);
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The absolute state directory.</summary>
    public string Directory { get; }

    /// <summary>A lock whose newest timestamp is older than this is considered abandoned and taken over.</summary>
    public TimeSpan StaleLockAge { get; init; } = TimeSpan.FromHours(1);

    /// <summary>How often a held lock rewrites its heartbeat timestamp (real time, independent of the injected clock).</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A holder that is demonstrably alive (same host, other process) keeps its lock even when it has not heartbeated, but only up to this age.</summary>
    public TimeSpan MaxLiveHolderAge { get; init; } = TimeSpan.FromHours(24);

    /// <summary>How often a waiting <see cref="LockAsync"/> re-checks the lock.</summary>
    public TimeSpan LockPollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Test seam: runs between judging a lock stale and deleting it (a holder that heartbeats in that window must keep its lock).</summary>
    internal Action<string>? BeforeStaleDelete { get; init; }

    /// <summary>Overrides the liveness probe of a lock holder (tests); the default checks the pid on this host.</summary>
    public Func<StateLockInfo, bool>? IsHolderAlive { get; init; }

    public string PathFor(string deploymentName)
    {
        StateNames.Validate(deploymentName);
        return Path.Combine(Directory, deploymentName + StateExtension);
    }

    public string LockPathFor(string deploymentName)
    {
        StateNames.Validate(deploymentName);
        return Path.Combine(Directory, deploymentName + LockExtension);
    }

    /// <summary>The largest state file that is read (a committed file can be replaced by anything); a larger one is refused with a clear error.</summary>
    public const long MaxStateFileBytes = 16L * 1024 * 1024;

    /// <summary>The largest lock file that is read.</summary>
    public const long MaxLockFileBytes = 1024L * 1024;

    public Task<StateFile?> LoadAsync(string deploymentName, CancellationToken cancellationToken)
    {
        return LoadAsync(deploymentName, lenient: false, cancellationToken);
    }

    /// <param name="lenient">
    /// <c>true</c> only for the repair verbs (<c>earm state forget</c>): the entry integrity rules of <see cref="StateKeyRules"/> are not enforced, so an
    /// entry that fails them can still be removed. The state is never used to act on the tenant after a lenient load.
    /// </param>
    public async Task<StateFile?> LoadAsync(string deploymentName, bool lenient, CancellationToken cancellationToken)
    {
        string path = PathFor(deploymentName);
        EnsureDirectorySafe(deploymentName);
        if (!File.Exists(path))
        {
            return null;
        }

        EnsureNotReparsePoint(deploymentName, path);
        string? json = await ReadBoundedAsync(deploymentName, path, MaxStateFileBytes, "state file", cancellationToken).ConfigureAwait(false);
        return json is null ? null : StateSerializer.Deserialize(json, deploymentName, validateEntries: !lenient);
    }

    /// <summary>
    /// Reads a file for sharing with a writer that replaces it atomically (<see cref="File.Move(string, string, bool)"/>): the reader allows writers and
    /// deleters (so a save in progress is not blocked and does not fail the read), retries a sharing violation a few times and refuses a file over
    /// <paramref name="maxBytes"/>. <c>null</c> when the file vanished meanwhile.
    /// </summary>
    private static async Task<string?> ReadBoundedAsync(string deploymentName, string path, long maxBytes, string what, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
                if (stream.Length > maxBytes)
                {
                    throw new StateStoreException(deploymentName, $"The {what} '{path}' is {stream.Length / (1024 * 1024)} MiB, more than the {maxBytes / (1024 * 1024)} MiB EARM reads; it is not an EARM {what} (or it is corrupt).");
                }

                using var reader = new StreamReader(stream, StrictUtf8);
                string text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                if (text.Length > maxBytes)
                {
                    throw new StateStoreException(deploymentName, $"The {what} '{path}' grew beyond {maxBytes / (1024 * 1024)} MiB while it was read; it is not an EARM {what}.");
                }

                return text;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (DecoderFallbackException ex)
            {
                // Never decode with replacement characters: a state that is then saved back would carry silently altered ids and values.
                throw new StateStoreException(deploymentName, $"The {what} '{path}' is not valid UTF-8 ({ex.Message}); it is not an EARM {what} (or it is corrupt).", ex);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < ReadAttempts)
            {
                // A writer replaced the file at this very moment (Windows reports a sharing violation, or access denied while a delete is pending).
                await Task.Delay(JitteredBackoff(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Attempts a reader makes at one state file before the sharing violation is reported (about four seconds of jittered waiting in all).</summary>
    private const int ReadAttempts = 60;

    /// <summary>Attempts a writer makes to move the temp file into place (the writer waits a little longer than a reader: it must not lose a finished save).</summary>
    private const int MoveAttempts = 80;

    /// <summary>
    /// The wait before retry <paramref name="attempt"/>: a ramp capped at 100 ms with full jitter (a random share of the ramp, at least 2 ms), so a pack of
    /// readers and writers that collided once do not collide again in lockstep and a hot reader cannot starve the writer.
    /// </summary>
    private static TimeSpan JitteredBackoff(int attempt)
    {
        int ceiling = Math.Min(10 * attempt, 100);
        return TimeSpan.FromMilliseconds(Random.Shared.Next(2, ceiling + 3));
    }

    public async Task SaveAsync(StateFile state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        string path = PathFor(state.DeploymentName);
        string json = StateSerializer.Serialize(state);

        EnsureDirectorySafe(state.DeploymentName);
        System.IO.Directory.CreateDirectory(Directory);
        EnsureDirectorySafe(state.DeploymentName);
        EnsureNotReparsePoint(state.DeploymentName, path);
        string temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            // CreateNew: a pre-planted file or link at the temp name is refused instead of followed.
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await stream.WriteAsync(Utf8NoBom.GetBytes(json), cancellationToken).ConfigureAwait(false);
            }

            await MoveIntoPlaceAsync(temp, path, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        EngineLog.StateSaved(_logger, path, state.Resources.Count);
    }

    /// <summary>The atomic replace; a reader (or a scanner) holding the target for a moment makes Windows refuse it, so a refusal is retried a few times.</summary>
    private static async Task MoveIntoPlaceAsync(string temp, string path, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < MoveAttempts)
            {
                await Task.Delay(JitteredBackoff(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public Task<bool> DeleteAsync(string deploymentName, CancellationToken cancellationToken)
    {
        string path = PathFor(deploymentName);
        EnsureDirectorySafe(deploymentName);
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        EnsureNotReparsePoint(deploymentName, path);
        File.Delete(path);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken)
    {
        EnsureDirectorySafe(string.Empty);
        if (!System.IO.Directory.Exists(Directory))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var names = System.IO.Directory.EnumerateFiles(Directory, "*" + StateExtension)
            .Select(Path.GetFileName)
            .Where(f => f is not null && f.EndsWith(StateExtension, StringComparison.OrdinalIgnoreCase))
            .Select(f => f![..^StateExtension.Length])
            .Where(StateNames.IsValid)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(names);
    }

    public async Task<IAsyncDisposable> LockAsync(string deploymentName, TimeSpan wait, CancellationToken cancellationToken)
    {
        string path = LockPathFor(deploymentName);
        EnsureDirectorySafe(deploymentName);
        if (!System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.CreateDirectory(Directory);
            EnsureDirectorySafe(deploymentName);
        }

        var deadline = _time.GetUtcNow() + (wait < TimeSpan.Zero ? TimeSpan.Zero : wait);
        bool announced = false;
        bool linkChecked = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryCreateLock(path, out var lease, out var unexplained))
            {
                return lease;
            }

            if (!linkChecked)
            {
                // Only a lock file that already exists can be a link (CreateNew refuses to follow one): check it once, when there is a holder to read.
                linkChecked = true;
                EnsureNotReparsePoint(deploymentName, path);
            }

            StateLockInfo? holder = null;
            if (unexplained is null)
            {
                var read = ReadLock(path);
                holder = read.Holder;
                if (read.Age is null && !File.Exists(path))
                {
                    // The holder released between our failed create and the read: try again at once.
                    continue;
                }

                // A holder that is gone from this very host has not abandoned anything for an hour: its lock is taken over at once (review round 2, S2-11e).
                bool holderGone = HolderIsGoneFromThisHost(read.Holder);
                if (holderGone || (read.Age is { } lockAge && lockAge >= StaleLockAge && !HolderIsDemonstrablyAlive(read.Holder, lockAge)))
                {
                    EngineLog.StaleLockTakenOver(_logger, path, holder?.Describe() ?? "an unknown process", holderGone ? "its process no longer runs" : StaleLockAge.ToString("c", System.Globalization.CultureInfo.InvariantCulture));

                    // Serialised take-over: the lock file is moved aside under a unique name (only one taker can), judged there, and
                    // put back untouched when it turns out to have changed (a holder that heartbeated, or a new holder) meanwhile.
                    BeforeStaleDelete?.Invoke(path);
                    if (TakeOverStale(path, read) is not null)
                    {
                        continue;
                    }

                    // Another taker holds the take-over gate: wait for it like for any other holder.
                }
            }

            if (_time.GetUtcNow() >= deadline)
            {
                // No lock file but the create kept failing (permissions, a bad directory): that is the real error, not "locked".
                throw unexplained ?? new StateLockedException(deploymentName, path, holder);
            }

            if (!announced)
            {
                EngineLog.WaitingForLock(_logger, path, holder?.Describe() ?? "an unknown process");
                announced = true;
            }

            await Task.Delay(LockPollInterval, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<StateLockInfo?> GetLockAsync(string deploymentName, CancellationToken cancellationToken)
    {
        string path = LockPathFor(deploymentName);
        EnsureDirectorySafe(deploymentName);
        return Task.FromResult(File.Exists(path) ? ReadLock(path).Holder : null);
    }

    public Task<StateLockInfo?> ForceUnlockAsync(string deploymentName, CancellationToken cancellationToken)
    {
        string path = LockPathFor(deploymentName);
        EnsureDirectorySafe(deploymentName);
        if (!File.Exists(path))
        {
            return Task.FromResult<StateLockInfo?>(null);
        }

        EnsureNotReparsePoint(deploymentName, path);
        var holder = ReadLock(path).Holder;
        File.Delete(path);
        return Task.FromResult(holder);
    }

    // ---- path safety (S-06) ----

    /// <summary>Refuses a state directory that is a file or a symbolic link / junction.</summary>
    private void EnsureDirectorySafe(string deploymentName)
    {
        // A link anywhere above the directory redirects the writes just as well as one at its end (review round 2, S2-18).
        if (PathSafety.FindLinkedComponent(Directory) is { } linked && !string.Equals(linked, Directory, StringComparison.OrdinalIgnoreCase))
        {
            throw new StateStoreException(deploymentName, $"'{linked}' (part of the state directory '{Directory}') is a symbolic link or junction; EARM does not follow links in its state directory path (a link could redirect state writes elsewhere). Use a real directory.");
        }

        // One stat call: FileSystemInfo.Attributes is -1 for a missing path.
        var attributes = new FileInfo(Directory).Attributes;
        if ((int)attributes == -1)
        {
            return;
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new StateStoreException(deploymentName, $"The state directory '{Directory}' is a file, not a directory. Check --state-dir / EARM_STATE_DIR.");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new StateStoreException(deploymentName, $"'{Directory}' is a symbolic link or junction; EARM does not follow links in its state directory (a link could redirect state writes elsewhere). Use a real directory.");
        }
    }

    private static void EnsureNotReparsePoint(string deploymentName, string path)
    {
        // FileSystemInfo.Attributes is -1 for a missing path (File.GetAttributes would throw, which is slow on the lock hot path).
        FileAttributes attributes = new FileInfo(path).Attributes;
        if ((int)attributes != -1 && (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new StateStoreException(deploymentName, $"'{path}' is a symbolic link or junction; EARM does not follow links in its state directory (a link could redirect state writes elsewhere). Use a real directory.");
        }
    }

    // ---- lock internals ----

    /// <summary>
    /// Creates the lock file. False when it could not: <paramref name="unexplained"/> stays null when a lock file exists
    /// (a holder), and carries the exception when none does: a file that is being deleted is still "delete pending"
    /// on Windows while a reader has it open (create fails with access denied), which resolves itself, whereas a
    /// permissions or directory problem does not and is reported when the wait runs out.
    /// </summary>
    private bool TryCreateLock(string path, out IAsyncDisposable lease, out Exception? unexplained)
    {
        unexplained = null;
        string lockId = Guid.NewGuid().ToString("N");
        var now = _time.GetUtcNow();
        var document = new StateLockDocument
        {
            ProcessId = Environment.ProcessId,
            Host = Environment.MachineName,
            User = Environment.UserName,
            AcquiredOn = now,
            HeartbeatOn = now,
            LockId = lockId,
        };

        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, Utf8NoBom))
            {
                writer.Write(JsonSerializer.Serialize(document, StateJsonContext.Default.StateLockDocument));
            }

            lease = new Lease(path, _logger, StaleLockAge, lockId, HeartbeatInterval, _time);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lease = null!;
            unexplained = File.Exists(path) ? null : ex;
            return false;
        }
    }

    private sealed record LockRead(StateLockInfo? Holder, TimeSpan? Age, string? Raw, long WriteTicks);

    /// <summary>The holder recorded in the lock file (null when unreadable), the lock's age (null when neither the content nor the file time is usable) and what was read.</summary>
    private LockRead ReadLock(string path)
    {
        TimeSpan? age = null;
        StateLockInfo? holder = null;
        string? raw = null;
        long writeTicks = 0;
        try
        {
            // FileShare.Delete: a reader must never keep the holder from releasing (deleting) the lock file.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            raw = ReadCapped(stream);
            var document = raw is null ? null : JsonSerializer.Deserialize(raw, StateJsonContext.Default.StateLockDocument);
            if (document is not null)
            {
                holder = new StateLockInfo(document.ProcessId, document.Host, document.User, document.AcquiredOn, document.HeartbeatOn);
                var newest = document.HeartbeatOn is { } beat && beat > document.AcquiredOn ? beat : document.AcquiredOn;
                age = _time.GetUtcNow() - newest;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Released while we looked: no holder and no age (never the file time of a file that is not there).
            return new LockRead(null, null, null, 0);
        }
        catch (IOException)
        {
            // Being written; fall through to the file time.
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (JsonException)
        {
        }

        // The file time is only needed when the content gave no age, or to recognise an unreadable lock file again (compare-and-delete).
        if (age is not null && raw is not null)
        {
            return new LockRead(holder, age, raw, writeTicks);
        }

        try
        {
            var lastWrite = File.GetLastWriteTimeUtc(path);
            if (lastWrite.Year > 1700)
            {
                writeTicks = lastWrite.Ticks;

                // A missing file reads as 1601, which would look like a lock older than any threshold.
                if (age is null)
                {
                    age = _time.GetUtcNow() - new DateTimeOffset(lastWrite, TimeSpan.Zero);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return new LockRead(holder, age, raw, writeTicks);
    }

    /// <summary>
    /// True when the lock was taken on this host by another process that demonstrably no longer runs (no such pid, it has exited, or the pid now belongs to a
    /// process started after the lock was taken). A holder on another host, an unknown pid or this very process are never "gone".
    /// </summary>
    private bool HolderIsGoneFromThisHost(StateLockInfo? holder)
    {
        if (holder is null || holder.ProcessId <= 0 || holder.ProcessId == Environment.ProcessId
            || !string.Equals(holder.Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IsHolderAlive is { } probe)
        {
            return !probe(holder);
        }

        return IsProcessAlive(holder) == false;
    }

    /// <summary>True when the holder is another process of this host that still runs (so a lock that merely stopped heartbeating is not stolen from it).</summary>
    private bool HolderIsDemonstrablyAlive(StateLockInfo? holder, TimeSpan age)
    {
        if (holder is null || age >= MaxLiveHolderAge)
        {
            return false;
        }

        if (IsHolderAlive is { } probe)
        {
            return probe(holder);
        }

        if (!string.Equals(holder.Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase) || holder.ProcessId == Environment.ProcessId || holder.ProcessId <= 0)
        {
            // Another host cannot be probed; a lock of this very process that nobody holds is a leaked lease.
            return false;
        }

        return IsProcessAlive(holder) == true;
    }

    /// <summary><c>false</c> when the process of the lock holder certainly does not run any more, <c>true</c> when it does, <c>null</c> when the probe itself failed.</summary>
    private static bool? IsProcessAlive(StateLockInfo holder)
    {
        try
        {
            using var process = Process.GetProcessById(holder.ProcessId);
            if (process.HasExited)
            {
                return false;
            }

            try
            {
                // A process that started after the lock was taken is a different process that reused the pid.
                if (process.StartTime.ToUniversalTime() > holder.AcquiredOn.UtcDateTime.AddSeconds(5))
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
            }

            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The probe itself failed (access denied...): unknown, neither "alive" nor "gone".
            return null;
        }
    }

    /// <summary>The marker that serialises stale-lock take-overs: one taker at a time holds an exclusive handle on <c>&lt;lock&gt;.takeover</c> (released by the operating system when the holder dies).</summary>
    private const string TakeOverGateExtension = ".takeover";

    /// <summary>
    /// Takes over a lock judged abandoned, one taker at a time (review 5M). A taker judged the lock stale from a read it made earlier; by the time it acts, another
    /// taker may have removed that lock and a third process may hold a FRESH one, which the move-aside below would displace for a moment, long enough for a second
    /// process to create a lock of its own (two holders at once). So the taker first takes the gate, then re-reads the lock and acts only when it is still exactly what
    /// was judged abandoned. <c>null</c> when the gate is held by another taker (the caller waits and looks again); otherwise <c>true</c> when the lock file is gone afterwards.
    /// </summary>
    private static bool? TakeOverStale(string path, LockRead expected)
    {
        FileStream gate;
        try
        {
            gate = new FileStream(path + TakeOverGateExtension, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        using (gate)
        {
            if (!SameAsJudged(path, expected))
            {
                return false;
            }

            return MoveAsideAndJudge(path, expected);
        }
    }

    /// <summary>True when the lock file is still exactly the content (or, for an unreadable one, the file time) that was judged abandoned.</summary>
    private static bool SameAsJudged(string path, LockRead expected)
    {
        string? raw = null;
        long ticks = 0;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            raw = ReadCapped(stream);
            ticks = File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ticks = File.GetLastWriteTimeUtc(path).Ticks;
        }

        return expected.Raw is not null
            ? string.Equals(raw, expected.Raw, StringComparison.Ordinal)
            : raw is null && ticks == expected.WriteTicks;
    }

    /// <summary>
    /// Takes over a lock judged abandoned. The lock file is first moved aside under a unique name (an atomic rename only one taker can win), so the
    /// check "is it still the content that was judged abandoned" and the delete cannot interleave with another taker or a new holder (review round 2,
    /// S2-11b). When it changed (a holder heartbeated, a new holder arrived) it is moved back untouched. True when the lock file is gone afterwards.
    /// </summary>
    private static bool MoveAsideAndJudge(string path, LockRead expected)
    {
        string aside = path + "." + Guid.NewGuid().ToString("N")[..8] + ".stale";
        try
        {
            File.Move(path, aside);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        string? raw = null;
        long ticks = 0;
        try
        {
            using var stream = new FileStream(aside, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            raw = ReadCapped(stream);
            ticks = File.GetLastWriteTimeUtc(aside).Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ticks = File.GetLastWriteTimeUtc(aside).Ticks;
        }

        bool unchanged = expected.Raw is not null
            ? string.Equals(raw, expected.Raw, StringComparison.Ordinal)
            : raw is null && ticks == expected.WriteTicks;
        if (unchanged)
        {
            _ = TryDeleteOnce(aside);
            return true;
        }

        // Not what was judged abandoned: put it back (no overwrite: a lock created meanwhile wins and this displaced one is dropped, it was not abandoned).
        try
        {
            File.Move(aside, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = TryDeleteOnce(aside);
        }

        return false;
    }

    /// <summary>Reads a lock file but never more than <see cref="MaxLockFileBytes"/> characters; <c>null</c> when it is larger (it is not a lock file).</summary>
    private static string? ReadCapped(Stream stream)
    {
        using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        var buffer = new char[4096];
        var text = new StringBuilder();
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            text.Append(buffer, 0, read);
            if (text.Length > MaxLockFileBytes)
            {
                return null;
            }
        }

        return text.ToString();
    }

    private static void TryDelete(string path)
    {
        // Best effort: the caller loops (stale takeover) and re-reads the lock.
        _ = TryDeleteOnce(path);
    }

    /// <summary>One delete attempt: null when the file is gone afterwards, else the (transient) failure.</summary>
    private static Exception? TryDeleteOnce(string path)
    {
        try
        {
            File.Delete(path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return File.Exists(path) ? ex : null;
        }
    }

    /// <summary>A held lock. Internal so a test can drive a heartbeat deterministically (<see cref="Beat"/>).</summary>
    internal sealed class Lease : IAsyncDisposable
    {
        /// <summary>How long a release keeps retrying a delete that fails because the file is momentarily open elsewhere.</summary>
        private static readonly TimeSpan DeleteRetryBudget = TimeSpan.FromSeconds(2);

        private readonly string _path;
        private readonly ILogger _logger;
        private readonly TimeSpan _staleAfter;
        private readonly string _lockId;
        private readonly TimeProvider _time;
        private readonly object _beatGate = new();
        private readonly Timer? _heartbeat;
        private int _released;

        public Lease(string path, ILogger logger, TimeSpan staleAfter, string lockId, TimeSpan heartbeatInterval, TimeProvider time)
        {
            _path = path;
            _logger = logger;
            _staleAfter = staleAfter;
            _lockId = lockId;
            _time = time;
            if (heartbeatInterval > TimeSpan.Zero)
            {
                // Real time on purpose: the beat must keep ticking while the run is busy, whatever clock the age is read from.
                _heartbeat = new Timer(_ => Beat(), null, heartbeatInterval, heartbeatInterval);
            }
        }

        /// <summary>The random token of this acquisition (what a heartbeat and a release compare against).</summary>
        internal string LockId => _lockId;

        /// <summary>
        /// Rewrites the heartbeat of the lock file, in place through one open handle: the file is opened (never created), its <c>lockId</c> checked
        /// and only then the timestamp rewritten. A lock that was deleted (<c>state unlock</c>, a takeover) stays deleted - the heartbeat never
        /// re-creates it - and a lock another holder owns is never touched (review round 2, S2-11c).
        /// </summary>
        internal void Beat()
        {
            lock (_beatGate)
            {
                if (Volatile.Read(ref _released) != 0)
                {
                    return;
                }

                try
                {
                    using var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                    string? raw = ReadCapped(stream);
                    var document = raw is null ? null : JsonSerializer.Deserialize(raw, StateJsonContext.Default.StateLockDocument);
                    if (document is null || !string.Equals(document.LockId, _lockId, StringComparison.Ordinal))
                    {
                        // Broken or taken over (state unlock, --force-unlock): this lease no longer owns the file.
                        _heartbeat?.Change(Timeout.Infinite, Timeout.Infinite);
                        return;
                    }

                    document.HeartbeatOn = _time.GetUtcNow();
                    byte[] bytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(document, StateJsonContext.Default.StateLockDocument));
                    stream.Position = 0;
                    stream.Write(bytes, 0, bytes.Length);
                    if (stream.Length > bytes.Length)
                    {
                        stream.SetLength(bytes.Length);
                    }

                    stream.Flush();
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    // Deleted under us: stays deleted; nothing left to keep alive.
                    _heartbeat?.Change(Timeout.Infinite, Timeout.Infinite);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    // The next beat tries again; a lock that could not be refreshed for an hour is stale anyway.
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            if (_heartbeat is not null)
            {
                await _heartbeat.DisposeAsync().ConfigureAwait(false);
            }

            lock (_beatGate)
            {
                // A beat that was mid-write has finished once this lock is ours.
            }

            // A waiter polling the lock can hold the file open at this very instant (sharing violation on Windows),
            // so a failed delete is retried with a short backoff; a stale lock must never outlive a successful release.
            var clock = Stopwatch.StartNew();
            int delay = 2;
            Exception? last;
            while (true)
            {
                last = TryReleaseOnce(out bool finished);
                if (finished)
                {
                    return;
                }

                if (clock.Elapsed + TimeSpan.FromMilliseconds(delay) > DeleteRetryBudget)
                {
                    break;
                }

                await Task.Delay(delay).ConfigureAwait(false);
                delay = Math.Min(delay * 2, 100);
            }

            EngineLog.StateLockReleaseFailed(_logger, _path, last?.Message ?? "the lock file could not be read", _staleAfter.ToString("c", System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Compare-and-delete: only a lock file that still carries this lease's <c>lockId</c> is removed; one that is gone, or that another holder
        /// owns (after a <c>state unlock</c> / takeover), is left alone (review round 2, S2-11a). <paramref name="finished"/> is false for a transient
        /// failure the caller retries.
        /// </summary>
        private Exception? TryReleaseOnce(out bool finished)
        {
            finished = false;
            try
            {
                string? raw;
                using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    raw = ReadCapped(stream);
                }

                var document = raw is null ? null : JsonSerializer.Deserialize(raw, StateJsonContext.Default.StateLockDocument);
                if (document is null || !string.Equals(document.LockId, _lockId, StringComparison.Ordinal))
                {
                    // Someone else's lock (or not a lock at all): not ours to delete.
                    finished = true;
                    return null;
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                finished = true;
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ex;
            }
            catch (JsonException)
            {
                finished = true;
                return null;
            }

            var failure = TryDeleteOnce(_path);
            finished = failure is null;
            return failure;
        }
    }
}

/// <summary>Where the state directory is: <c>--state-dir</c>, else <c>EARM_STATE_DIR</c>, else <c>.earm/state</c> next to the template (or under the working directory).</summary>
public static class StateDirectory
{
    public const string EnvironmentVariable = "EARM_STATE_DIR";

    public const string DefaultRelativePath = ".earm/state";

    public static string Resolve(string? explicitDirectory, string? templatePath, string? workingDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
        {
            return Path.GetFullPath(explicitDirectory);
        }

        string? fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return Path.GetFullPath(fromEnvironment);
        }

        string baseDirectory = !string.IsNullOrWhiteSpace(templatePath)
            ? Path.GetDirectoryName(Path.GetFullPath(templatePath)) ?? System.IO.Directory.GetCurrentDirectory()
            : workingDirectory ?? System.IO.Directory.GetCurrentDirectory();
        return Path.GetFullPath(Path.Combine(baseDirectory, DefaultRelativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>
    /// True when <paramref name="stateDirectory"/> lies under the user's profile directory or under the template's directory
    /// (the places a state directory is expected to be); anything else deserves a warning (review finding S-06).
    /// </summary>
    public static bool IsExpectedLocation(string stateDirectory, string? templatePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        string full = Path.GetFullPath(stateDirectory);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile) && IsUnder(full, profile))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(templatePath) && Path.GetDirectoryName(Path.GetFullPath(templatePath)) is { } templateDirectory && IsUnder(full, templateDirectory))
        {
            return true;
        }

        return IsUnder(full, System.IO.Directory.GetCurrentDirectory());
    }

    private static bool IsUnder(string path, string parent)
    {
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return (path + Path.DirectorySeparatorChar).StartsWith(prefix, comparison);
    }
}
