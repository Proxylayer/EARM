using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Diagnostics;
using EArm.Core.Json;

namespace EArm.Core.Loading;

/// <summary>
/// Reads a template or parameter file for loading: a missing file, a directory and a file larger than <see cref="TemplateJson.MaxInputBytes"/>
/// are each reported as what they are (<see cref="TemplateLoadException"/>) before anything is read into memory. The size is checked from the file
/// system first; the read itself is bounded too, so a file that reports no size (a pipe, a device, a file that grows while it is read) cannot
/// get past the limit.
/// </summary>
public static class InputFiles
{
    /// <summary>Reads <paramref name="path"/> (<paramref name="kind"/> is "Template" or "Parameter", for the messages).</summary>
    public static byte[] ReadAllBytes(string path, string kind)
    {
        Check(path, kind);
        using var stream = Open(path);
        return ReadLimited(stream, path, kind);
    }

    /// <summary>Asynchronous <see cref="ReadAllBytes"/>.</summary>
    public static async Task<byte[]> ReadAllBytesAsync(string path, string kind, CancellationToken ct)
    {
        Check(path, kind);
        var stream = Open(path);
        await using (stream.ConfigureAwait(false))
        {
            return await ReadLimitedAsync(stream, path, kind, ct).ConfigureAwait(false);
        }
    }

    private static void Check(string path, string kind)
    {
        if (Directory.Exists(path))
        {
            throw new TemplateLoadException(DiagnosticCodes.FileNotFound, $"{kind} file '{path}' is a directory, not a file.", path);
        }

        if (!File.Exists(path))
        {
            throw new TemplateLoadException(DiagnosticCodes.FileNotFound, $"{kind} file '{path}' was not found.", path);
        }

        long length = new FileInfo(path).Length;
        if (length > TemplateJson.MaxInputBytes)
        {
            throw TooLarge(path, kind, $"{length:N0} bytes");
        }
    }

    private static FileStream Open(string path)
    {
        return new FileStream(path, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite | FileShare.Delete, Options = FileOptions.Asynchronous });
    }

    private static byte[] ReadLimited(FileStream stream, string path, string kind)
    {
        var buffer = new byte[Math.Max(4096, (int)Math.Min(stream.CanSeek ? stream.Length : 0, TemplateJson.MaxInputBytes) + 1)];
        int used = 0;
        while (true)
        {
            if (used == buffer.Length)
            {
                buffer = Grow(buffer, path, kind);
            }

            int read = stream.Read(buffer, used, buffer.Length - used);
            if (read == 0)
            {
                break;
            }

            used += read;
            if (used > TemplateJson.MaxInputBytes)
            {
                throw TooLarge(path, kind, "more than the limit");
            }
        }

        return buffer.AsSpan(0, used).ToArray();
    }

    private static async Task<byte[]> ReadLimitedAsync(FileStream stream, string path, string kind, CancellationToken ct)
    {
        var buffer = new byte[Math.Max(4096, (int)Math.Min(stream.CanSeek ? stream.Length : 0, TemplateJson.MaxInputBytes) + 1)];
        int used = 0;
        while (true)
        {
            if (used == buffer.Length)
            {
                buffer = Grow(buffer, path, kind);
            }

            int read = await stream.ReadAsync(buffer.AsMemory(used), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            used += read;
            if (used > TemplateJson.MaxInputBytes)
            {
                throw TooLarge(path, kind, "more than the limit");
            }
        }

        return buffer.AsSpan(0, used).ToArray();
    }

    private static byte[] Grow(byte[] buffer, string path, string kind)
    {
        if (buffer.Length > TemplateJson.MaxInputBytes)
        {
            throw TooLarge(path, kind, "more than the limit");
        }

        var bigger = new byte[Math.Min((long)buffer.Length * 2, TemplateJson.MaxInputBytes + 1L)];
        Buffer.BlockCopy(buffer, 0, bigger, 0, buffer.Length);
        return bigger;
    }

    private static TemplateLoadException TooLarge(string path, string kind, string size)
    {
        return new TemplateLoadException(DiagnosticCodes.InputTooLarge, $"{kind} file '{path}' is too large ({size}); the limit is {TemplateJson.MaxInputBytes:N0} bytes (8 MiB).", path);
    }
}
