using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using EArm.Cli;
using EArm.Core.Diagnostics;
using EArm.Engine;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// Review 4L: the command-line surface added by the review rounds is pinned BY NAME, both in the command tree and in the documents. The generic parity
/// tests (<see cref="Review2F_DocsCliParityTests"/>, <see cref="DocsConsistencyTests"/>) compare the two sides with each other, so removing an option together
/// with its documentation (or renaming both) keeps them green; these tests fail when one of the flags or codes below disappears from either side.
/// </summary>
public sealed class Review4L_NewSurfaceDocsTests
{
    private static readonly Regex CodeSpan = new("`([^`]+)`", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    private static string Doc(string name)
    {
        return File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "docs", name));
    }

    private static Command? Resolve(string path)
    {
        Command current = Program.BuildRootCommand();
        foreach (string word in path.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = current.Subcommands.FirstOrDefault(c => c.Name.Equals(word, StringComparison.Ordinal));
            if (next is null)
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    /// <summary>(command path, flag): an empty path is the root command (the global options).</summary>
    private static readonly (string Path, string Flag)[] SurfaceRows =
    [
        ("deployment create", "--yes"),
        ("deployment create", "--secure-outputs-file"),
        ("deployment what-if", "--exit-code-on-changes"),
        ("deployment delete", "--parameters"),
        ("deployment delete", "--name"),
        ("deployment delete", "--template-file"),
        ("deployment delete", "--yes"),
        ("deployment delete", "--continue-on-error"),
        ("auth login", "--device-code"),
        (string.Empty, "--evaluation-timeout"),
    ];

    public static TheoryData<string, string> Surface()
    {
        var data = new TheoryData<string, string>();
        foreach (var (path, flag) in SurfaceRows)
        {
            data.Add(path, flag);
        }

        return data;
    }

    [Fact]
    public void TheSurfaceTable_IsNotEmpty_AndCoversEveryCommandItNames()
    {
        Assert.True(SurfaceRows.Length >= 10, "the pinned surface shrank to " + SurfaceRows.Length);
        Assert.All(SurfaceRows.Select(r => r.Path).Where(p => p.Length > 0).Distinct(), path => Assert.NotNull(Resolve(path)));
    }
    [Theory]
    [MemberData(nameof(Surface))]
    public void TheFlag_IsDefinedOnItsCommand(string path, string flag)
    {
        var command = Resolve(path);

        Assert.NotNull(command);
        Assert.Contains(command.Options, o => o.Name.Equals(flag, StringComparison.Ordinal) || o.Aliases.Contains(flag));
    }

    [Theory]
    [MemberData(nameof(Surface))]
    public void TheFlag_IsNamedInACodeSpanOfTheCliDocument(string path, string flag)
    {
        _ = path;
        var lines = Doc("cli.md").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        // Per line: the document has fenced blocks, and a backtick pairing across the whole text would pair the wrong ones.
        bool inCodeSpan = lines.SelectMany(l => CodeSpan.Matches(l)).Any(m => Regex.IsMatch(m.Groups[1].Value, @"(?<![\w\-])" + Regex.Escape(flag) + @"(?![\w\-])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)));

        Assert.True(inCodeSpan, $"docs/cli.md does not name {flag} in a code span");
    }

    // ---- diagnostic codes of the review rounds ----

    public static TheoryData<string> ReviewCodes()
    {
        return new TheoryData<string>
        {
            nameof(EngineDiagnosticCodes.RecreateNotConfirmed),
            nameof(EngineDiagnosticCodes.OrphanReportIncomplete),
            nameof(EngineDiagnosticCodes.AssignmentRetained),
            nameof(DiagnosticCodes.InputTooLarge),
            nameof(DiagnosticCodes.VariableChainTooDeep),
        };
    }

    private static string? ValueOf(string name)
    {
        foreach (var type in new[] { typeof(DiagnosticCodes), typeof(EngineDiagnosticCodes) })
        {
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.Static);
            if (field is not null && field.IsLiteral)
            {
                return (string?)field.GetRawConstantValue();
            }
        }

        return null;
    }

    [Theory]
    [MemberData(nameof(ReviewCodes))]
    public void TheReviewCode_IsAConstantNamedAfterItsValue_AndHasADocumentedRow(string name)
    {
        Assert.Equal(name, ValueOf(name));

        var row = Doc("diagnostics.md").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .FirstOrDefault(l => l.TrimStart().StartsWith("| `" + name + "` |", StringComparison.Ordinal));

        Assert.True(row is not null, $"docs/diagnostics.md has no table row for `{name}`");
        string[] cells = row.Trim().Trim('|').Split('|');
        Assert.True(cells.Length >= 3, $"the row of {name} lacks a severity or a description");
        Assert.True(cells[^1].Trim().Length >= 20, $"the description of {name} is missing or a stub");
    }

    [Fact]
    public void TheBatchCodes_AreNotConstants_SoTheirDocumentationIsPinnedHere()
    {
        // BatchNotSent and BatchEnvelopeFailed are code strings on GraphException items (EArm.Graph.Tests pins their behaviour in
        // Review4L_BatchErrorCodeTests); neither is an entry of DiagnosticCodes / EngineDiagnosticCodes. If one becomes a constant, the generic
        // docs test covers it and this pin can go.
        Assert.Null(ValueOf("BatchNotSent"));
        Assert.Null(ValueOf("BatchEnvelopeFailed"));
        Assert.Contains("BatchNotSent", Doc("engine.md"), StringComparison.Ordinal);
    }
}
