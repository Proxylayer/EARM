using System;
using System.IO;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// docs/engine.md describes a false condition the way the what-if reports it: the instance is listed as skipped, not as an
/// <c>Ignore</c> row.
/// </summary>
public sealed class EngineDocsConditionTests
{
    [Fact]
    public void Expand_step_describes_a_false_condition_as_skipped_not_as_an_ignore_row()
    {
        string doc = string.Join(' ', File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "docs", "engine.md"))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        Assert.DoesNotContain("keeps the instance as an `Ignore` row", doc, StringComparison.Ordinal);
        Assert.Contains("`false` leaves the instance out of the deployment", doc, StringComparison.Ordinal);
        Assert.Contains("what-if lists it only as skipped (`skipped[]`", doc, StringComparison.Ordinal);
    }
}
