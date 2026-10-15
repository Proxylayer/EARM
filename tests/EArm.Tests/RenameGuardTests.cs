using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Regression guard for the product rename (the old name to EARM / earm / EArm): no file path and no text file of the repository
/// may contain the old name, case-insensitively, apart from the allow-listed files that mention it on purpose, and every sample
/// points its <c>$schema</c> at the new schema host.
/// </summary>
public sealed class RenameGuardTests
{
    // Built from pieces so that this file does not contain the old name itself.
    private static readonly string OldName = "entra" + "arm";

    private const string SchemaHost = "https://schema.earm.proxylayer.net/";

    /// <summary>Files that mention the old name deliberately: the deploy script uninstalls the old global tool, the CLI doc carries the migration note, the README carries the short "Renamed from" note.</summary>
    private static readonly string[] AllowedFiles = ["deploy.ps1", "docs/cli.md", "README.md"];

    private static readonly string[] ExcludedDirectories = [".git", ".claude", ".vs", ".idea", "bin", "obj", "artifacts", "TestResults", "node_modules"];

    [Fact]
    public void NoRepositoryPath_ContainsTheOldName()
    {
        string root = TestPaths.RepoRoot;
        var offenders = RepositoryFiles(root)
            .Select(path => RelativePath(root, path))
            .Where(relative => relative.Contains(OldName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(offenders.Count == 0, "Paths still carry the old name:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void NoTextFile_ContainsTheOldName_OutsideTheAllowList()
    {
        string root = TestPaths.RepoRoot;
        var offenders = new List<string>();
        foreach (string path in RepositoryFiles(root))
        {
            string relative = RelativePath(root, path);
            if (AllowedFiles.Contains(relative, StringComparer.Ordinal))
            {
                continue;
            }

            byte[] bytes = File.ReadAllBytes(path);
            if (Array.IndexOf(bytes, (byte)0) >= 0)
            {
                continue;
            }

            string text = Encoding.UTF8.GetString(bytes);
            int index = text.IndexOf(OldName, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                int line = text.AsSpan(0, index).Count('\n') + 1;
                offenders.Add($"{relative}:{line}");
            }
        }

        Assert.True(offenders.Count == 0, "Files still mention the old name:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void TheAllowListedFiles_StillExist()
    {
        foreach (string relative in AllowedFiles)
        {
            Assert.True(File.Exists(Path.Combine(TestPaths.RepoRoot, relative)), $"Allow-listed file '{relative}' is gone; remove it from the allow-list.");
        }
    }

    [Theory]
    [InlineData("*.entra.json", "deploymentTemplate.json#")]
    [InlineData("*.parameters.json", "deploymentParameters.json#")]
    public void EverySampleSchemaUrl_StartsWithTheNewHost(string pattern, string schemaFile)
    {
        var samples = Directory.EnumerateFiles(TestPaths.SamplesDirectory, pattern).ToList();
        Assert.NotEmpty(samples);

        foreach (string sample in samples)
        {
            var root = JsonNode.Parse(File.ReadAllText(sample)) as JsonObject;
            string? schema = root?["$schema"]?.GetValue<string>();

            Assert.NotNull(schema);
            Assert.StartsWith(SchemaHost, schema, StringComparison.Ordinal);
            Assert.EndsWith(schemaFile, schema, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<string> RepositoryFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string sub in Directory.EnumerateDirectories(directory))
            {
                if (!ExcludedDirectories.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
                {
                    pending.Push(sub);
                }
            }

            foreach (string file in Directory.EnumerateFiles(directory))
            {
                yield return file;
            }
        }
    }

    private static string RelativePath(string root, string path)
    {
        return Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
    }
}
