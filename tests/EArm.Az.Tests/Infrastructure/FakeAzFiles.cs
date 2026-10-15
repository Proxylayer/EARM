using System;
using System.IO;
using System.Threading;

namespace EArm.Az.Tests.Infrastructure;

/// <summary>Tolerant readers for the files several concurrent fake-az processes append to (see tests/fake-az/README.md).</summary>
internal static class FakeAzFiles
{
    private const int MaxAttempts = 50;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Whole text of a fake-written file, or null when it does not exist. Opens with <c>FileShare.ReadWrite | FileShare.Delete</c>
    /// so a writer is never blocked, retries sharing violations, and re-reads (bounded, 50 x 20 ms) while the last line is
    /// still unterminated, because the fake terminates every line it writes with <c>\n</c>.
    /// </summary>
    public static string? ReadSettledText(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            string text;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, FakeAzContext.Utf8NoBom);
                text = reader.ReadToEnd();
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (IOException) when (attempt < MaxAttempts)
            {
                Thread.Sleep(RetryDelay);
                continue;
            }

            if (text.Length > 0 && text[^1] != '\n' && attempt < MaxAttempts)
            {
                Thread.Sleep(RetryDelay);
                continue;
            }

            return text;
        }
    }
}
