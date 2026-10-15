using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using EArm.Graph;

namespace EArm.Engine.Execution;

/// <summary>
/// The <c>--secure-outputs-file</c> (review findings E-03, E-06): one owner-only JSON object that may hold the only copy of a
/// secret (Graph returns a password credential's <c>secretText</c> once). So the file is validated BEFORE any resource is
/// written, one-time secrets go into it right after the apply that produced them (under <see cref="OneTimeSecretsKey"/>,
/// keyed by resource id) and the secure outputs are MERGED into what is there: a later run never replaces the file with a
/// smaller one, and a run without secure values leaves it alone.
/// </summary>
public static class SecureOutputsFile
{
    /// <summary>The reserved top-level key that holds one-time secrets by resource id.</summary>
    public const string OneTimeSecretsKey = "oneTimeSecrets";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Why <paramref name="path"/> cannot be written, or <c>null</c> when it can: it is a directory, its folder does not exist or is not
    /// writable, or it is a symbolic link / read-only file. Nothing is created or changed (a probe file is created and removed in the folder).
    /// </summary>
    public static string? Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "the secure outputs file path is empty.";
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"'{path}' is not a valid path: {ex.Message}";
        }

        if (Directory.Exists(full) || Path.EndsInDirectorySeparator(full))
        {
            return $"'{path}' is a directory; --secure-outputs-file must name a file.";
        }

        string? parent = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            return $"the folder of '{path}' does not exist; create it first.";
        }

        // A link anywhere above the file redirects the secrets just as well as one at the file (review round 2, S2-18).
        if (PathSafety.FindLinkedComponent(parent) is { } linked)
        {
            return $"{linked} (part of the folder of {path}) is a symbolic link or junction; EARM does not write secrets through links.";
        }

        if (File.Exists(full))
        {
            var attributes = File.GetAttributes(full);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return $"'{path}' is a symbolic link; EARM does not write secrets through links.";
            }

            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                return $"'{path}' is read-only.";
            }
        }

        string probe = Path.Combine(parent, ".earm-write-check-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"the folder of '{path}' is not writable: {ex.Message}";
        }

        return null;
    }

    /// <summary>
    /// Merges the secure outputs (<c>{ name: { type, value } }</c>, revealed) into the file. Returns false (and writes nothing, not even
    /// <c>{}</c>) when there are none. Existing keys that are not superseded stay.
    /// </summary>
    public static bool MergeOutputs(string path, JsonObject secureOutputs)
    {
        return MergeOutputs(path, secureOutputs, DefaultLockWait);
    }

    internal static bool MergeOutputs(string path, JsonObject secureOutputs, TimeSpan lockWait)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(secureOutputs);
        if (secureOutputs.Count == 0)
        {
            return false;
        }

        Update(path, document =>
        {
            foreach (var (name, value) in secureOutputs)
            {
                if (string.Equals(name, OneTimeSecretsKey, StringComparison.Ordinal))
                {
                    continue;
                }

                document[name] = value?.DeepClone();
            }

            // The one-time secrets stay the last member, so the outputs keep reading as the leading { name: { type, value } } entries.
            if (document[OneTimeSecretsKey] is { } bucket)
            {
                document.Remove(OneTimeSecretsKey);
                document[OneTimeSecretsKey] = bucket;
            }
        }, lockWait);
        return true;
    }

    /// <summary>Records the one-time secrets of a resource (<c>{ "secretText": "...", "keyId": "..." }</c>) under its resource id.</summary>
    public static void MergeOneTimeSecret(string path, string resourceId, JsonObject secrets)
    {
        MergeOneTimeSecret(path, resourceId, secrets, DefaultLockWait);
    }

    internal static void MergeOneTimeSecret(string path, string resourceId, JsonObject secrets, TimeSpan lockWait)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        ArgumentNullException.ThrowIfNull(secrets);
        Update(path, document =>
        {
            if (document[OneTimeSecretsKey] is not JsonObject bucket)
            {
                bucket = new JsonObject();
                document[OneTimeSecretsKey] = bucket;
            }

            bucket[resourceId] = secrets.DeepClone();
        }, lockWait);
    }

    /// <summary>How long a run waits for another run that is merging into the same file.</summary>
    private static readonly TimeSpan DefaultLockWait = TimeSpan.FromSeconds(30);

    /// <summary>Attempts at reading the existing file while a scanner or another reader holds it (about two seconds of jittered waiting in all).</summary>
    private const int ReadAttempts = 40;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// The read-merge-write of the file, serialised across processes by an exclusive handle on <c>&lt;file&gt;.lock</c> (released by the operating system even when
    /// the holder dies): two runs sharing one file can no longer lose the one-time secret of the other (review round 2, S2-19). The merge NEVER drops a secret: when
    /// the lock cannot be acquired within <paramref name="lockWait"/>, or the merge or the replace of the file fails, the new secrets are written to a uniquely
    /// named owner-only sibling <c>&lt;file&gt;.pending-&lt;guid&gt;</c> and the call fails loudly naming it (review 5M).
    /// </summary>
    private static void Update(string path, Action<JsonObject> mutate, TimeSpan lockWait)
    {
        string full = Path.GetFullPath(path);
        FileStream gate;
        try
        {
            gate = AcquireLock(full + ".lock", lockWait);
        }
        catch (IOException ex)
        {
            throw KeepPending(full, mutate, ex);
        }

        using (gate)
        {
            try
            {
                MergeLocked(full, mutate);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                throw KeepPending(full, mutate, ex);
            }
        }
    }

    private static void MergeLocked(string full, Action<JsonObject> mutate)
    {
        if (PathSafety.IsLink(full))
        {
            throw new InvalidOperationException($"{full} is a symbolic link; EARM does not write secrets through links.");
        }

        JsonObject document = new();
        if (File.Exists(full))
        {
            string existing = ReadExisting(full);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                JsonObject? parsed = null;
                try
                {
                    parsed = JsonNode.Parse(existing) as JsonObject;
                }
                catch (JsonException)
                {
                }

                if (parsed is not null)
                {
                    document = parsed;
                }
                else
                {
                    // Not ours to throw away: keep the unreadable content next to the new file instead of destroying it.
                    OwnerOnlyFile.WriteAllText(full + ".previous", existing);
                }
            }
        }

        mutate(document);
        OwnerOnlyFile.WriteAllText(full, document.ToJsonString(Indented));
    }

    /// <summary>Reads the existing file for sharing (a scanner may have it open), retrying a sharing violation; empty when it vanished.</summary>
    private static string ReadExisting(string full)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Utf8NoBom);
                return reader.ReadToEnd();
            }
            catch (FileNotFoundException)
            {
                return string.Empty;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < ReadAttempts)
            {
                Thread.Sleep(Backoff(attempt));
            }
        }
    }

    /// <summary>
    /// Saves what the failed merge would have added (never the earlier content of the file, which is still there) to <c>&lt;file&gt;.pending-&lt;guid&gt;</c> and returns the
    /// exception to throw: it names the file and the cause, never a secret value.
    /// </summary>
    private static IOException KeepPending(string full, Action<JsonObject> mutate, Exception cause)
    {
        string pending = full + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            JsonObject document = new();
            mutate(document);
            OwnerOnlyFile.WriteAllText(pending, document.ToJsonString(Indented));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new IOException($"The secrets of this run could not be merged into the secure outputs file '{full}' ({cause.Message}) and could not be saved next to it either ({ex.Message}); they were NOT written anywhere.", cause);
        }

        return new SecureOutputsPendingException(pending, $"The secrets of this run could not be merged into the secure outputs file '{full}' ({cause.Message}). They were saved to '{pending}' instead (owner-only); merge its content into '{full}' by hand, then delete it.", cause);
    }

    private static FileStream AcquireLock(string lockPath, TimeSpan wait)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // IOException: the lock is held. UnauthorizedAccessException: on Windows, the previous holder's delete-on-close is still pending (a scanner has the file open for a moment).
                if (clock.Elapsed >= wait)
                {
                    throw new IOException($"another earm run has been merging into the secure outputs file for {wait.TotalSeconds:0.#} seconds ({lockPath}); try again once it finished.", ex);
                }

                Thread.Sleep(Backoff(attempt));
            }
        }
    }

    /// <summary>The wait before retry <paramref name="attempt"/>: a ramp capped at 100 ms with full jitter (at least 2 ms), so contenders that collided once do not collide again in lockstep.</summary>
    private static int Backoff(int attempt)
    {
        return Random.Shared.Next(2, Math.Min(10 * attempt, 100) + 3);
    }
}

/// <summary>
/// The secure outputs file could not be updated (lock not acquired in time, or the merge or the replace failed) but the new secrets were NOT lost: they were
/// written to <see cref="PendingPath"/>, an owner-only file next to the target, and the message says so.
/// </summary>
public sealed class SecureOutputsPendingException : IOException
{
    public SecureOutputsPendingException(string pendingPath, string message, Exception? inner = null)
        : base(message, inner)
    {
        PendingPath = pendingPath;
    }

    /// <summary>The <c>&lt;file&gt;.pending-&lt;guid&gt;</c> file that holds the secrets of the failed merge.</summary>
    public string PendingPath { get; }
}
