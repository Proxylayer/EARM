using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
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
    private static readonly ConcurrentDictionary<string, string> Extracted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Hex SHA-256 of the embedded script.</summary>
    public static string Sha256 => Content.Value.Sha256;

    /// <summary>The embedded script text (for diagnostics and tests).</summary>
    public static string Text => SecureFile.Utf8NoBom.GetString(Content.Value.Bytes);

    /// <summary>Returns the path of the extracted script, extracting it if necessary.</summary>
    public static string Extract(SecureTempDirectory directory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(logger);
        return Extracted.GetOrAdd(directory.Path, _ => ExtractCore(directory, logger));
    }

    private static string ExtractCore(SecureTempDirectory directory, ILogger logger)
    {
        directory.EnsureCreated();
        var (bytes, sha256) = Content.Value;
        var path = directory.FilePath($"Invoke-AzRest-{sha256[..16]}.ps1");

        if (File.Exists(path) && string.Equals(HashFile(path), sha256, StringComparison.OrdinalIgnoreCase))
        {
            AzLog.ScriptExtracted(logger, path, sha256);
            return path;
        }

        var temp = directory.FilePath($"Invoke-AzRest-{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
        AzLog.ScriptExtracted(logger, path, sha256);
        return path;
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
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
