using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntraArm.Engine.State;

/// <summary>
/// The file backend of <see cref="IStateStore"/>: <c>&lt;directory&gt;/&lt;deploymentName&gt;.json</c>, written to a
/// temp file and moved into place (<see cref="File.Move(string, string, bool)"/>), locked by
/// <c>&lt;deploymentName&gt;.lock</c> created with <see cref="FileMode.CreateNew"/> (pid, host, user, UTC time). A lock
/// older than <see cref="StaleLockAge"/> (one hour) is taken over with a warning. The default directory is
/// <c>.entraarm/state</c> next to the template (<see cref="StateDirectory"/>).
/// </summary>
public sealed class FileStateStore : IStateStore
{
    public const string StateExtension = ".json";

    public const string LockExtension = ".lock";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

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

    /// <summary>A lock older than this is considered abandoned and taken over.</summary>
    public TimeSpan StaleLockAge { get; init; } = TimeSpan.FromHours(1);

    /// <summary>How often a waiting <see cref="LockAsync"/> re-checks the lock.</summary>
    public TimeSpan LockPollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

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

    public async Task<StateFile?> LoadAsync(string deploymentName, CancellationToken cancellationToken)
    {
        string path = PathFor(deploymentName);
        if (!File.Exists(path))
        {
            return null;
        }

        string json = await File.ReadAllTextAsync(path, Utf8NoBom, cancellationToken).ConfigureAwait(false);
        return StateSerializer.Deserialize(json, deploymentName);
    }

    public async Task SaveAsync(StateFile state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        string path = PathFor(state.DeploymentName);
        string json = StateSerializer.Serialize(state);

        System.IO.Directory.CreateDirectory(Directory);
        string temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, json, Utf8NoBom, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        EngineLog.StateSaved(_logger, path, state.Resources.Count);
    }

    public Task<bool> DeleteAsync(string deploymentName, CancellationToken cancellationToken)
    {
        string path = PathFor(deploymentName);
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken)
    {
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
        System.IO.Directory.CreateDirectory(Directory);
        var deadline = _time.GetUtcNow() + (wait < TimeSpan.Zero ? TimeSpan.Zero : wait);
        bool announced = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryCreateLock(path, out var lease, out var unexplained))
            {
                return lease;
            }

            StateLockInfo? holder = null;
            if (unexplained is null)
            {
                holder = ReadLock(path, out var age);
                if (age is null && !File.Exists(path))
                {
                    // The holder released between our failed create and the read: try again at once.
                    continue;
                }

                if (age is { } lockAge && lockAge >= StaleLockAge)
                {
                    EngineLog.StaleLockTakenOver(_logger, path, holder?.Describe() ?? "an unknown process", StaleLockAge.ToString("c", System.Globalization.CultureInfo.InvariantCulture));
                    TryDelete(path);
                    continue;
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
        return Task.FromResult(File.Exists(path) ? ReadLock(path, out _) : null);
    }

    public Task<StateLockInfo?> ForceUnlockAsync(string deploymentName, CancellationToken cancellationToken)
    {
        string path = LockPathFor(deploymentName);
        if (!File.Exists(path))
        {
            return Task.FromResult<StateLockInfo?>(null);
        }

        var holder = ReadLock(path, out _);
        File.Delete(path);
        return Task.FromResult(holder);
    }

    /// <summary>
    /// Creates the lock file. False when it could not: <paramref name="unexplained"/> stays null when a lock file exists
    /// (a holder), and carries the exception when none does: a file that is being deleted is still "delete pending"
    /// on Windows while a reader has it open (create fails with access denied), which resolves itself, whereas a
    /// permissions or directory problem does not and is reported when the wait runs out.
    /// </summary>
    private bool TryCreateLock(string path, out IAsyncDisposable lease, out Exception? unexplained)
    {
        unexplained = null;
        var document = new StateLockDocument
        {
            ProcessId = Environment.ProcessId,
            Host = Environment.MachineName,
            User = Environment.UserName,
            AcquiredOn = _time.GetUtcNow(),
        };

        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, Utf8NoBom))
            {
                writer.Write(JsonSerializer.Serialize(document, StateJsonContext.Default.StateLockDocument));
            }

            lease = new Lease(path, _logger, StaleLockAge);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lease = null!;
            unexplained = File.Exists(path) ? null : ex;
            return false;
        }
    }

    /// <summary>The holder recorded in the lock file (null when unreadable) and the lock's age (null when neither the content nor the file time is usable).</summary>
    private StateLockInfo? ReadLock(string path, out TimeSpan? age)
    {
        age = null;
        StateLockInfo? holder = null;
        try
        {
            // FileShare.Delete: a reader must never keep the holder from releasing (deleting) the lock file.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Utf8NoBom);
            var document = JsonSerializer.Deserialize(reader.ReadToEnd(), StateJsonContext.Default.StateLockDocument);
            if (document is not null)
            {
                holder = new StateLockInfo(document.ProcessId, document.Host, document.User, document.AcquiredOn);
                age = _time.GetUtcNow() - document.AcquiredOn;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Released while we looked: no holder and no age (never the file time of a file that is not there).
            return null;
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

        if (age is null)
        {
            try
            {
                var lastWrite = File.GetLastWriteTimeUtc(path);
                if (lastWrite.Year > 1700)
                {
                    // A missing file reads as 1601, which would look like a lock older than any threshold.
                    age = _time.GetUtcNow() - new DateTimeOffset(lastWrite, TimeSpan.Zero);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return holder;
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

    private sealed class Lease : IAsyncDisposable
    {
        /// <summary>How long a release keeps retrying a delete that fails because the file is momentarily open elsewhere.</summary>
        private static readonly TimeSpan DeleteRetryBudget = TimeSpan.FromSeconds(2);

        private readonly string _path;
        private readonly ILogger _logger;
        private readonly TimeSpan _staleAfter;
        private int _released;

        public Lease(string path, ILogger logger, TimeSpan staleAfter)
        {
            _path = path;
            _logger = logger;
            _staleAfter = staleAfter;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            // A waiter polling the lock can hold the file open at this very instant (sharing violation on Windows),
            // so a failed delete is retried with a short backoff; a stale lock must never outlive a successful release.
            var clock = Stopwatch.StartNew();
            int delay = 2;
            Exception? last;
            while (true)
            {
                last = TryDeleteOnce(_path);
                if (last is null)
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

            EngineLog.StateLockReleaseFailed(_logger, _path, last.Message, _staleAfter.ToString("c", System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}

/// <summary>Where the state directory is: <c>--state-dir</c>, else <c>ENTRAARM_STATE_DIR</c>, else <c>.entraarm/state</c> next to the template (or under the working directory).</summary>
public static class StateDirectory
{
    public const string EnvironmentVariable = "ENTRAARM_STATE_DIR";

    public const string DefaultRelativePath = ".entraarm/state";

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
}
