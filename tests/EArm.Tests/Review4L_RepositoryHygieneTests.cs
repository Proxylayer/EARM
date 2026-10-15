using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Review 4L: repository hygiene that is cheap to break and expensive to notice. <c>.gitignore</c> keeps the secrets files, the rotated and lock copies
/// the secure-outputs writer leaves, local settings and debug traces out of commits (the patterns are asserted textually, and, when git is installed and
/// the checkout is a repository, through <c>git check-ignore</c>, which is the real matcher); the CI workflow keeps its safety settings; wall-clock
/// assertions stay inside the <c>Category=Timing</c> group that <c>test.ps1</c> runs serially.
/// </summary>
public sealed class Review4L_RepositoryHygieneTests
{
    private static string Root => TestPaths.RepoRoot;

    private static string[] GitIgnoreLines => File.ReadAllLines(Path.Combine(Root, ".gitignore")).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToArray();

    // ---- .gitignore: the patterns exist ----

    public static TheoryData<string> RequiredPatterns()
    {
        return new TheoryData<string>
        {
            // secrets and credentials
            "secrets.json", "secure-outputs*.json", "*secure*output*.json", "*.secrets.json", "*.pfx", "*.pem", "*.key", ".env", ".env.*",
            // what the secure-outputs writer leaves next to the file
            "secure-outputs*.json.previous", "secure-outputs*.json.lock", "secrets.json.previous", "secrets.json.lock", "*.previous",
            // state and locks
            ".earm/", "**/state/*.lock", "**/state/*.previous", "*.entrastate.json",
            // local settings and debug output
            "appsettings.local.json", "appsettings*.local.json", "trace*.txt",
            // build output
            "bin/", "obj/", "artifacts/", "*.nupkg",
        };
    }

    [Theory]
    [MemberData(nameof(RequiredPatterns))]
    public void TheGitIgnore_ListsThePattern(string pattern)
    {
        Assert.Contains(pattern, GitIgnoreLines);
    }

    [Fact]
    public void TheGitIgnore_DoesNotIgnoreTheProjectItself_ByTextualPattern()
    {
        // A catch-all would hide real files; these are the shapes that did or could do that.
        string[] forbidden = ["*", "*.cs", "*.json", "*.md", "*.ps1", "src/", "tests/", "docs/", "samples/", "schemas/", ".github/", "*.csproj", "*.props"];

        Assert.Empty(GitIgnoreLines.Intersect(forbidden, StringComparer.Ordinal));
    }

    // ---- .gitignore: git agrees (the real matcher) ----

    private static bool GitIsUsable()
    {
        if (!Directory.Exists(Path.Combine(Root, ".git")) && !File.Exists(Path.Combine(Root, ".git")))
        {
            return false;
        }

        try
        {
            return RunGit("--version").ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static (int ExitCode, string Output) RunGit(params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    /// <summary>
    /// Whether git ignores the path. <c>git check-ignore</c> exits 0 for a path that only matches a NEGATED pattern (<c>!.env.example</c>) as well, so the verbose
    /// form is read: the path is ignored when the last matching pattern is not a negation.
    /// </summary>
    private static bool IsIgnored(string path)
    {
        var (exitCode, output) = RunGit("check-ignore", "--no-index", "--verbose", "--", path);
        if (exitCode != 0)
        {
            return false;
        }

        string pattern = output.Split('\t')[0].Split(':', 3)[^1];
        return !pattern.StartsWith('!');
    }

    public static TheoryData<string> PathsThatMustBeIgnored()
    {
        return new TheoryData<string>
        {
            "secrets.json",
            "secrets.json.previous",
            "secure-outputs.json",
            "secure-outputs-prod.json",
            "secure-outputs.json.previous",
            "secure-outputs.json.lock",
            "deploy/secure-outputs.json.previous",
            ".earm/state/demo.json",
            ".earm/state/demo.lock",
            ".earm/state/demo.json.previous",
            "infra/state/demo.lock",
            "infra/state/demo.previous",
            "src/EArm.Cli/appsettings.local.json",
            "src/EArm.Cli/appsettings.Development.local.json",
            "trace.txt",
            "trace-2026-10-15.txt",
            "src/EArm.Cli/bin/Debug/net10.0/earm.dll",
            "tests/EArm.Tests/obj/project.assets.json",
            "artifacts/test-results/EArm.Tests.trx",
            "cert.pfx",
            ".env",
            ".env.production",
        };
    }

    [Theory]
    [MemberData(nameof(PathsThatMustBeIgnored))]
    public void Git_IgnoresThePath(string path)
    {
        Assert.SkipUnless(GitIsUsable(), "git is not installed or the checkout is not a repository");

        Assert.True(IsIgnored(path), $"git does not ignore '{path}': add a pattern to .gitignore.");
    }

    public static TheoryData<string> PathsThatMustNotBeIgnored()
    {
        return new TheoryData<string>
        {
            "README.md",
            "SECURITY.md",
            ".gitignore",
            ".editorconfig",
            "global.json",
            "Directory.Build.props",
            "Directory.Packages.props",
            "build.ps1",
            "test.ps1",
            "deploy.ps1",
            ".github/workflows/ci.yml",
            "src/EArm.Cli/EArm.Cli.csproj",
            "src/EArm.Engine/State/FileStateStore.cs",
            "docs/testing.md",
            "samples/groups.entra.json",
            "schemas/2026-10-01/deploymentTemplate.json",
            "tests/COVERAGE.md",
            "tests/fixtures/templates/valid/minimal.entra.json",
            "tests/fixtures/whatif/create-incremental.expected.txt",
            "tools/Get-CoverageReport.ps1",
            ".env.example",
        };
    }

    [Theory]
    [MemberData(nameof(PathsThatMustNotBeIgnored))]
    public void Git_DoesNotIgnoreTheProjectFile(string path)
    {
        Assert.SkipUnless(GitIsUsable(), "git is not installed or the checkout is not a repository");

        Assert.False(IsIgnored(path), $"git ignores '{path}', which the repository needs");
    }

    [Fact]
    public void NoTrackedFile_IsIgnored()
    {
        Assert.SkipUnless(GitIsUsable(), "git is not installed or the checkout is not a repository");

        var (exitCode, output) = RunGit("ls-files", "--cached", "--ignored", "--exclude-standard");

        Assert.Equal(0, exitCode);
        Assert.True(output.Trim().Length == 0, "tracked files that .gitignore matches (a pattern is too broad, or a secret was committed):\n" + output);
    }

    // ---- the CI workflow ----

    private static string Workflow => File.ReadAllText(Path.Combine(Root, ".github", "workflows", "ci.yml"));

    [Fact]
    public void TheWorkflow_RunsBothHosts_FullBuildAndTest_AndPacksAReleaseArtifact()
    {
        string workflow = Workflow;

        Assert.Contains("windows-latest", workflow, StringComparison.Ordinal);
        Assert.Contains("ubuntu-latest", workflow, StringComparison.Ordinal);
        Assert.Contains("build.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("test.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet pack", workflow, StringComparison.Ordinal);
        Assert.Contains("ContinuousIntegrationBuild=true", workflow, StringComparison.Ordinal);
        Assert.Contains("-Configuration Release", workflow, StringComparison.Ordinal);
        // The gated integration suite writes to a real tenant: the workflow never sets its gate variable (a mention in a comment is fine).
        Assert.DoesNotMatch(new Regex(@"^\s*EARM_IT_TENANT\s*:", RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)), workflow);
    }

    [Fact]
    public void TheWorkflow_EmptiesTheGoldenUpdateVariable_AndFailsWhenItIsSet()
    {
        string workflow = Workflow;

        Assert.Matches(new Regex(@"EARM_UPDATE_GOLDENS:\s*(""""|'')", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)), workflow);
        Assert.Contains("$env:EARM_UPDATE_GOLDENS", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWorkflow_PinsEveryActionToAMajorTag_NeverABranch()
    {
        var uses = Regex.Matches(Workflow, @"^\s*(?:-\s*)?uses:\s*(\S+)", RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)).Select(m => m.Groups[1].Value).ToList();

        Assert.NotEmpty(uses);
        Assert.All(uses, action => Assert.Matches(new Regex(@"^[\w\-]+/[\w\-./]+@v\d+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)), action));
    }

    [Fact]
    public void TheWorkflow_GrantsOnlyReadAccess()
    {
        string workflow = Workflow;

        Assert.Matches(new Regex(@"^permissions:\s*\n\s+contents:\s*read\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)), workflow);
        Assert.DoesNotContain("write", Regex.Match(workflow, @"^permissions:[\s\S]*?(?=^\S)", RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)).Value, StringComparison.Ordinal);
    }

    // ---- wall-clock assertions live in the Timing group ----

    private static IEnumerable<string> TestSources()
    {
        string tests = Path.Combine(Root, "tests");
        return Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !Path.GetFileName(f).StartsWith("Review4L_RepositoryHygiene", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryTestFile_ThatAssertsAWallClockBound_CarriesTheTimingTrait()
    {
        var wallClock = new Regex(@"Assert\.\w+\([^;]*\b(?:Elapsed|ElapsedMilliseconds)\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
        var sources = TestSources().ToList();
        Assert.True(sources.Count > 100, "found only " + sources.Count + " test sources: is the repository root right?");

        var offenders = new List<string>();
        foreach (string file in sources)
        {
            string text = File.ReadAllText(file);
            if (wallClock.IsMatch(text) && !text.Contains("[Trait(\"Category\", \"Timing\")]", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetRelativePath(Root, file));
            }
        }

        Assert.True(offenders.Count == 0, "these files assert an elapsed-time bound without a [Trait(\"Category\", \"Timing\")] test (test.ps1 runs that group serially; see docs/testing.md): " + string.Join(", ", offenders));
    }

    [Fact]
    public void TestScript_AndTheTestingDocument_AgreeOnTheTimingGroup()
    {
        string script = File.ReadAllText(Path.Combine(Root, "test.ps1"));
        string doc = File.ReadAllText(Path.Combine(Root, "docs", "testing.md"));

        Assert.Contains("-SkipTiming", doc, StringComparison.Ordinal);
        Assert.Contains("SkipTiming", script, StringComparison.Ordinal);
        Assert.Contains("Category=Timing", script, StringComparison.Ordinal);
        Assert.Contains("Category=Timing", doc, StringComparison.Ordinal);
        Assert.Contains("exit 3", script, StringComparison.Ordinal);
    }
}
