using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Az;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EntraArm.Az.Tests;

/// <summary>
/// <see cref="EmbeddedScript.Extract"/> under contention: once-per-directory extraction inside the process, reuse of a
/// matching file that another handle holds open, replacement of wrong content, and retry after a faulted attempt. The
/// extraction cache is process-wide, so every test uses its own fresh directory.
/// </summary>
public sealed class EmbeddedScriptConcurrencyTests
{
    private const int Iterations = 20;

    public static TheoryData<int> Repeat()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Iterations; i++)
        {
            data.Add(i);
        }

        return data;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string FreshRoot()
    {
        return Path.Combine(AppContext.BaseDirectory, "embedded-script-" + Guid.NewGuid().ToString("N")[..12]);
    }

    private static string ExpectedFileName()
    {
        return $"Invoke-AzRest-{EmbeddedScript.Sha256[..16]}.ps1";
    }

    private static byte[] EmbeddedBytes()
    {
        using var stream = typeof(EmbeddedScript).Assembly.GetManifestResourceStream(EmbeddedScript.ResourceName)!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string HashOf(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void Cleanup(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of a scratch directory.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    private static void AssertExtractedCleanly(string root, string path)
    {
        Assert.Equal(Path.Combine(Path.GetFullPath(root), ExpectedFileName()), path);
        Assert.True(File.Exists(path));
        Assert.Equal(EmbeddedScript.Sha256, HashOf(path), ignoreCase: true);
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        Assert.Single(Directory.GetFiles(root));
    }

    private static async Task<string[]> ExtractConcurrentlyAsync(SecureTempDirectory directory, int callers)
    {
        using var gate = new ManualResetEventSlim(initialState: false);
        var tasks = new List<Task<string>>();
        for (var i = 0; i < callers; i++)
        {
            tasks.Add(Task.Factory.StartNew(
                () =>
                {
                    gate.Wait(Ct);
                    return EmbeddedScript.Extract(directory, NullLogger.Instance);
                },
                Ct,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default));
        }

        gate.Set();
        return await Task.WhenAll(tasks);
    }

    [Theory]
    [MemberData(nameof(Repeat))]
    public async Task ThirtyTwoConcurrentCallers_SameDirectory_GetOnePath_AndOneIntactFile(int iteration)
    {
        Assert.InRange(iteration, 0, Iterations - 1);
        var root = FreshRoot();
        try
        {
            var paths = await ExtractConcurrentlyAsync(new SecureTempDirectory(root), 32);

            Assert.Equal(32, paths.Length);
            Assert.Single(paths.Distinct(StringComparer.OrdinalIgnoreCase));
            AssertExtractedCleanly(root, paths[0]);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Theory]
    [MemberData(nameof(Repeat))]
    public async Task EightDifferentDirectories_InParallel_EachGetTheirOwnIntactFile(int iteration)
    {
        Assert.InRange(iteration, 0, Iterations - 1);
        var roots = Enumerable.Range(0, 8).Select(_ => FreshRoot()).ToArray();
        try
        {
            var perDirectory = await Task.WhenAll(roots.Select(root => ExtractConcurrentlyAsync(new SecureTempDirectory(root), 4)));

            for (var i = 0; i < roots.Length; i++)
            {
                Assert.Single(perDirectory[i].Distinct(StringComparer.OrdinalIgnoreCase));
                AssertExtractedCleanly(roots[i], perDirectory[i][0]);
            }

            Assert.Equal(roots.Length, perDirectory.Select(p => p[0]).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        finally
        {
            foreach (var root in roots)
            {
                Cleanup(root);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Repeat))]
    public void MatchingFileHeldOpenWithoutShareDelete_IsReused_NotReplaced(int iteration)
    {
        Assert.InRange(iteration, 0, Iterations - 1);
        var root = FreshRoot();
        try
        {
            Directory.CreateDirectory(root);
            var target = Path.Combine(Path.GetFullPath(root), ExpectedFileName());
            File.WriteAllBytes(target, EmbeddedBytes());

            // Another process (a running pwsh, a second entraarm) holding the script open for read/write without
            // FileShare.Delete: a move over it is denied and a hash open with FileShare.Read alone is refused.
            using (var holder = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                var path = EmbeddedScript.Extract(new SecureTempDirectory(root), NullLogger.Instance);

                Assert.Equal(target, path);
                Assert.Equal(EmbeddedScript.Sha256, HashOf(path), ignoreCase: true);
                Assert.Empty(Directory.GetFiles(root, "*.tmp"));
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Theory]
    [MemberData(nameof(Repeat))]
    public void ExistingFileWithWrongContent_IsReplacedWithTheEmbeddedScript(int iteration)
    {
        Assert.InRange(iteration, 0, Iterations - 1);
        var root = FreshRoot();
        try
        {
            Directory.CreateDirectory(root);
            var target = Path.Combine(Path.GetFullPath(root), ExpectedFileName());
            File.WriteAllText(target, "Write-Output 'tampered'");

            var path = EmbeddedScript.Extract(new SecureTempDirectory(root), NullLogger.Instance);

            AssertExtractedCleanly(root, path);
            Assert.Equal(target, path);
            Assert.Equal(EmbeddedBytes(), File.ReadAllBytes(path));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Theory]
    [MemberData(nameof(Repeat))]
    public void FaultedFirstExtraction_IsNotCached_AndALaterCallSucceeds(int iteration)
    {
        Assert.InRange(iteration, 0, Iterations - 1);
        var root = FreshRoot();
        try
        {
            // The directory's parent is a regular file, so EnsureCreated (and therefore the first extraction) fails.
            Directory.CreateDirectory(root);
            var blocker = Path.Combine(root, "blocker");
            File.WriteAllText(blocker, "not a directory");
            var directory = new SecureTempDirectory(Path.Combine(blocker, "scripts"));

            Assert.ThrowsAny<IOException>(() => EmbeddedScript.Extract(directory, NullLogger.Instance));
            // Still faulting (not a cached exception from a stale Lazy that never re-runs): it really re-attempts.
            Assert.ThrowsAny<IOException>(() => EmbeddedScript.Extract(directory, NullLogger.Instance));

            File.Delete(blocker);

            var path = EmbeddedScript.Extract(directory, NullLogger.Instance);

            Assert.True(File.Exists(path));
            Assert.Equal(EmbeddedScript.Sha256, HashOf(path), ignoreCase: true);
            Assert.Empty(Directory.GetFiles(Path.Combine(blocker, "scripts"), "*.tmp"));
        }
        finally
        {
            Cleanup(root);
        }
    }
}
