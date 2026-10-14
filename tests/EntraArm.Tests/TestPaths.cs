using System;
using System.IO;

namespace EntraArm.Tests;

/// <summary>
/// Locates repository files (samples, fixtures) from the test assembly's output directory by walking up to the
/// directory that contains <c>EntraArm.slnx</c>.
/// </summary>
internal static class TestPaths
{
    private static readonly Lazy<string> RepoRootLazy = new(FindRepoRoot);

    public static string RepoRoot => RepoRootLazy.Value;

    public static string SamplesDirectory => Path.Combine(RepoRoot, "samples");

    public static string FixturesDirectory => Path.Combine(RepoRoot, "tests", "fixtures", "templates");

    public static string InvalidFixtureDirectory => Path.Combine(FixturesDirectory, "invalid");

    public static string ValidFixtureDirectory => Path.Combine(FixturesDirectory, "valid");

    public static string Sample(string fileName)
    {
        return Path.Combine(SamplesDirectory, fileName);
    }

    public static string InvalidFixture(string fileName)
    {
        return Path.Combine(InvalidFixtureDirectory, fileName);
    }

    public static string ValidFixture(string fileName)
    {
        return Path.Combine(ValidFixtureDirectory, fileName);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EntraArm.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("EntraArm.slnx was not found in any directory above " + AppContext.BaseDirectory);
    }
}
