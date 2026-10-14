using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace EntraArm.Az;

/// <summary>
/// Extracts the embedded <c>Invoke-AzRest.ps1</c> into the secure temp directory. The file name carries the first
/// 16 hex digits of the script's SHA-256; an existing file is reused only when its full hash matches, and the
/// extraction happens once per process per directory.
/// </summary>
internal static class EmbeddedScript
{
    public const string ResourceName = "EntraArm.Az.Scripts.Invoke-AzRest.ps1";

    private static readonly Lazy<(byte[] Bytes, string Sha256)> Content = new(Load);
    private static readonly ConcurrentDictionary<string, Lazy<string>> Extracted = new(StringComparer.OrdinalIgnoreCase);

    private const int MoveAttempts = 10;
    private const int MoveBackoffStepMs = 20;
    private const int MoveBackoffMaxMs = 200;

    /// <summary>Hex SHA-256 of the embedded script.</summary>
    public static string Sha256 => Content.Value.Sha256;

    /// <summary>The embedded script text (for diagnostics and tests).</summary>
    public static string Text => SecureFile.Utf8NoBom.GetString(Content.Value.Bytes);

    /// <summary>Returns the path of the extracted script, extracting it if necessary.</summary>
    public static string Extract(SecureTempDirectory directory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(logger);

        // GetOrAdd may run the value factory more than once concurrently, so the dictionary stores a Lazy: only the
        // published instance ever executes ExtractCore, once, while every other caller blocks on its Value.
        var lazy = Extracted.GetOrAdd(
            directory.Path,
            _ => new Lazy<string>(() => ExtractCore(directory, logger), LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return lazy.Value;
        }
        catch
        {
            // A faulted Lazy caches its exception forever; drop exactly this instance so a later call can retry.
            Extracted.TryRemove(new KeyValuePair<string, Lazy<string>>(directory.Path, lazy));
            throw;
        }
    }

    private static string ExtractCore(SecureTempDirectory directory, ILogger logger)
    {
        directory.EnsureCreated();
        var (bytes, sha256) = Content.Value;
        var path = directory.FilePath($"Invoke-AzRest-{sha256[..16]}.ps1");

        // A matching file is never touched: another process (or a running pwsh) may have it open.
        if (HasMatchingContent(path, sha256))
        {
            AzLog.ScriptExtracted(logger, path, sha256);
            return path;
        }

        var temp = directory.FilePath($"Invoke-AzRest-{Guid.NewGuid():N}.tmp");
        try
        {
            WriteOwnerOnly(temp, bytes);

            Exception? last = null;
            for (var attempt = 1; attempt <= MoveAttempts; attempt++)
            {
                try
                {
                    File.Move(temp, path, overwrite: true);
                    AzLog.ScriptExtracted(logger, path, sha256);
                    return path;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    last = ex;

                    // Another thread or process may have won the race (or still holds the old copy open).
                    if (HasMatchingContent(path, sha256))
                    {
                        AzLog.ScriptExtracted(logger, path, sha256);
                        return path;
                    }

                    if (attempt < MoveAttempts)
                    {
                        Thread.Sleep(Math.Min(MoveBackoffStepMs * attempt, MoveBackoffMaxMs));
                    }
                }
            }

            throw new IOException($"Could not extract the embedded script to '{path}' after {MoveAttempts} attempts.", last);
        }
        finally
        {
            SecureFile.ZeroAndDelete(temp);
        }
    }

    /// <summary>Creates the file exclusively with the same owner-only creation as <see cref="SecureFile"/>.</summary>
    private static void WriteOwnerOnly(string path, byte[] bytes)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var stream = new FileStream(path, options);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static bool HasMatchingContent(string path, string sha256)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            return string.Equals(HashFile(path), sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable right now (e.g. exclusively locked or just replaced): treat as not matching and let the
            // caller fall back to writing; the move retry loop re-checks afterwards.
            return false;
        }
    }

    private static (byte[] Bytes, string Sha256) Load()
    {
        using var stream = typeof(EmbeddedScript).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' is missing from {typeof(EmbeddedScript).Assembly.FullName}.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        return (bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
