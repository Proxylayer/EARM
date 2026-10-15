using System;
using System.IO;
using System.Text;

namespace EArm.Az;

/// <summary>
/// Creates the short-lived files of the request protocol with owner-only permissions (0600 on Unix; the directory
/// ACL covers Windows) and disposes of them by overwriting their contents with zeros before deletion.
/// </summary>
internal static class SecureFile
{
    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Creates a new file (fails if it exists) and writes the text as UTF-8 without BOM.</summary>
    public static void WriteAllText(string path, string content)
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
        using var writer = new StreamWriter(stream, Utf8NoBom);
        writer.Write(content);
    }

    /// <summary>Reads a UTF-8 file, or returns null when it does not exist.</summary>
    public static string? TryReadAllText(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return File.ReadAllText(path, Utf8NoBom);
    }

    /// <summary>
    /// Best-effort overwrite of the file content with zeros followed by deletion. Never throws: cleanup runs from
    /// <c>finally</c> blocks and must not mask the original failure.
    /// </summary>
    public static void ZeroAndDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                var remaining = stream.Length;
                if (remaining > 0)
                {
                    var zeros = new byte[(int)Math.Min(remaining, 64 * 1024)];
                    while (remaining > 0)
                    {
                        var chunk = (int)Math.Min(remaining, zeros.Length);
                        stream.Write(zeros, 0, chunk);
                        remaining -= chunk;
                    }

                    stream.Flush(flushToDisk: true);
                }
            }

            File.Delete(path);
        }
        catch (IOException)
        {
            TryDelete(path);
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Left behind in the owner-only temp directory; nothing further to do.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
