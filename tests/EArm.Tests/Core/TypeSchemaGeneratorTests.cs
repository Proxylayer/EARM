using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// <c>tools/New-TypeSchemas.ps1</c> supports <c>-SchemaRoot</c>, so it is run against a temp copy of the checked-in type schemas: the
/// output must be byte-identical to the checked-in files (nothing hand-edited drifted) and a second run must change nothing.
/// </summary>
public sealed class TypeSchemaGeneratorTests
{
    private static string SchemaDirectory => Path.Combine(TestPaths.RepoRoot, "schemas", "2026-10-01", "types", "Microsoft.Entra");

    private static string Script => Path.Combine(TestPaths.RepoRoot, "tools", "New-TypeSchemas.ps1");

    private static Dictionary<string, byte[]> Read(string directory)
    {
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(directory, f), f => File.ReadAllBytes(f), StringComparer.Ordinal);
    }

    /// <summary>True when a <c>pwsh</c> executable is on the PATH (the generator is a script; the product itself needs no PowerShell).</summary>
    private static bool PwshIsAvailable()
    {
        string[] names = OperatingSystem.IsWindows() ? ["pwsh.exe"] : ["pwsh"];
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string name in names)
            {
                try
                {
                    if (File.Exists(Path.Combine(directory.Trim('"'), name)))
                    {
                        return true;
                    }
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry: ignore it.
                }
            }
        }

        return false;
    }

    private static async Task RunGenerator(string schemaRoot)
    {
        var start = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-File", Script, "-SchemaRoot", schemaRoot })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(120));
        await process.WaitForExitAsync(timeout.Token);

        Assert.True(process.ExitCode == 0, "New-TypeSchemas.ps1 failed: " + await stderr + await stdout);
        Assert.Contains("Rewrote 36 type schema file(s)", await stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Regeneration_IsDeterministic_AndEqualsTheCheckedInSchemas()
    {
        Assert.SkipUnless(PwshIsAvailable(), "pwsh (PowerShell 7) is not on the PATH; the schema generator is a PowerShell script.");
        string root = Path.Combine(Path.GetTempPath(), "earm-tests-schemagen-" + Guid.NewGuid().ToString("N")[..10]);
        try
        {
            string copy = Path.Combine(root, "Microsoft.Entra");
            Directory.CreateDirectory(copy);
            foreach (string file in Directory.EnumerateFiles(SchemaDirectory))
            {
                File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
            }

            var original = Read(SchemaDirectory);

            await RunGenerator(copy);
            var first = Read(copy);
            await RunGenerator(copy);
            var second = Read(copy);

            Assert.Equal(original.Keys.Order(StringComparer.Ordinal).ToList(), first.Keys.Order(StringComparer.Ordinal).ToList());
            Assert.Equal(36, first.Count);
            foreach (var pair in first)
            {
                Assert.True(original[pair.Key].AsSpan().SequenceEqual(pair.Value), $"the generator output for {pair.Key} differs from the checked-in schema (run tools/New-TypeSchemas.ps1 and commit, or fix the DSL)");
                Assert.True(pair.Value.AsSpan().SequenceEqual(second[pair.Key]), $"a second run changed {pair.Key}");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
