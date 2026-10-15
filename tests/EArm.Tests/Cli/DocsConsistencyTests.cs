using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using EArm.Cli;
using EArm.Cli.Commands;
using EArm.Core.Diagnostics;
using EArm.Engine;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// Cheap consistency checks between the documentation and the code: every verb and flag docs/cli.md mentions exists in the
/// System.CommandLine tree (and answers <c>--help</c>), and every diagnostic code constant appears in docs/diagnostics.md.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class DocsConsistencyTests
{
    private static readonly Regex FlagToken = new(@"(?<![\w<\-])(--[A-Za-z][\w\-]*|-[A-Za-z])(?![\w\-])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex CodeSpan = new("`([^`]+)`", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex TreeLine = new(@"[├└]── (.+?)(?:\s{2,}|$)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    private static string CliDoc => File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "docs", "cli.md"));

    private static string[] CliDocLines => CliDoc.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    // ---- the command tree ----

    private static RootCommand Root()
    {
        return Program.BuildRootCommand();
    }

    private static Command? Resolve(Command root, IEnumerable<string> path)
    {
        Command current = root;
        foreach (string word in path)
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

    private static IEnumerable<(string[] Path, Command Command)> Walk(Command command, string[] path)
    {
        yield return (path, command);
        foreach (var sub in command.Subcommands)
        {
            foreach (var item in Walk(sub, [.. path, sub.Name]))
            {
                yield return item;
            }
        }
    }

    /// <summary>The flags valid on a command: its own options plus the recursive options of every ancestor.</summary>
    private static HashSet<string> FlagsOf(Command root, string[] path)
    {
        var flags = new HashSet<string>(StringComparer.Ordinal);
        Command current = root;
        AddOptions(flags, current, inheritedOnly: false);
        foreach (string word in path)
        {
            current = current.Subcommands.First(c => c.Name == word);
            AddOptions(flags, current, inheritedOnly: false);
        }

        return flags;
    }

    private static void AddOptions(HashSet<string> flags, Command command, bool inheritedOnly)
    {
        foreach (var option in command.Options)
        {
            if (inheritedOnly && !option.Recursive)
            {
                continue;
            }

            flags.Add(option.Name);
            foreach (string alias in option.Aliases)
            {
                flags.Add(alias);
            }
        }
    }

    // ---- extracting from docs/cli.md ----

    /// <summary>The command paths drawn in the tree block at the top of the document.</summary>
    private static List<string[]> TreePaths()
    {
        var lines = CliDocLines;
        int open = Array.FindIndex(lines, l => l.StartsWith("```", StringComparison.Ordinal));
        int close = Array.FindIndex(lines, open + 1, l => l.StartsWith("```", StringComparison.Ordinal));
        var stack = new List<string>();
        var paths = new List<string[]>();
        foreach (string line in lines[(open + 1)..close])
        {
            var match = TreeLine.Match(line);
            if (!match.Success)
            {
                continue;
            }

            int depth = line.IndexOfAny(['├', '└']) / 4;
            while (stack.Count > depth)
            {
                stack.RemoveAt(stack.Count - 1);
            }

            string[] words = match.Groups[1].Value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // A multi-word name ("operation list") is a chain of verbs on one line; each prefix is a command too.
            stack.Add(words[0]);
            paths.Add([.. stack]);
            for (int i = 1; i < words.Length; i++)
            {
                paths.Add([.. stack, .. words.Take(i + 1).Skip(1)]);
            }
        }
        return paths;
    }

    /// <summary>Every <c>earm ...</c> invocation in the document: inline code spans and lines of fenced blocks.</summary>
    private static List<string> Invocations()
    {
        var found = new List<string>();
        foreach (string line in CliDocLines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("earm ", StringComparison.Ordinal) || trimmed == "earm")
            {
                found.Add(trimmed);
            }

            foreach (Match span in CodeSpan.Matches(line))
            {
                string text = span.Groups[1].Value.Trim();
                if (text.StartsWith("earm ", StringComparison.Ordinal) || text == "earm")
                {
                    found.Add(text);
                }
            }
        }

        return found.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Splits an invocation into the command path it names and the flags it uses.</summary>
    private static (string[] Path, string[] Flags) Parse(Command root, string invocation)
    {
        var words = invocation.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
        var path = new List<string>();
        Command current = root;
        foreach (string word in words)
        {
            var next = current.Subcommands.FirstOrDefault(c => c.Name.Equals(word, StringComparison.Ordinal));
            if (next is null)
            {
                break;
            }

            path.Add(word);
            current = next;
        }

        string[] flags = FlagToken.Matches(invocation).Select(m => m.Groups[1].Value).ToArray();
        return ([.. path], flags);
    }

    private sealed record Section(string Heading, List<string> Lines);

    private static List<Section> Sections()
    {
        var sections = new List<Section>();
        Section? current = null;
        foreach (string line in CliDocLines)
        {
            if (line.StartsWith('#'))
            {
                current = new Section(line.TrimStart('#', ' ').Replace("`", string.Empty, StringComparison.Ordinal).Trim(), []);
                sections.Add(current);
            }
            else
            {
                current?.Lines.Add(line);
            }
        }

        return sections;
    }

    private static Section SectionStartingWith(string heading)
    {
        return Sections().First(s => s.Heading.Equals(heading, StringComparison.Ordinal) || (heading.Length > 5 && s.Heading.StartsWith(heading, StringComparison.Ordinal)));
    }

    private static List<string[]> TableRows(Section section)
    {
        return section.Lines
            .Where(l => l.TrimStart().StartsWith('|'))
            .Select(l => l.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray())
            .Where(cells => !cells[0].StartsWith("---", StringComparison.Ordinal) && !cells[0].All(c => c == '-' || c == ' '))
            .Skip(1) // the header row
            .ToList();
    }

    private static string[] FlagsIn(string cell)
    {
        return CodeSpan.Matches(cell).SelectMany(s => FlagToken.Matches(s.Groups[1].Value).Select(m => m.Groups[1].Value)).Distinct(StringComparer.Ordinal).ToArray();
    }

    // ---- tests ----

    [Fact]
    public void TheTreeInTheDocument_IsTheCommandTree()
    {
        var root = Root();
        var documented = TreePaths();

        Assert.Contains(documented, p => p.SequenceEqual(["deployment", "validate"]));
        foreach (var path in documented)
        {
            Assert.True(Resolve(root, path) is not null, "docs/cli.md lists '" + string.Join(' ', path) + "', which is not a command");
        }

        // The reverse: no command exists that the tree does not draw.
        var drawn = documented.Select(p => string.Join(' ', p)).ToHashSet(StringComparer.Ordinal);
        foreach (var (path, _) in Walk(root, []).Where(w => w.Path.Length > 0))
        {
            Assert.True(drawn.Contains(string.Join(' ', path)), "the command '" + string.Join(' ', path) + "' is missing from the tree in docs/cli.md");
        }
    }

    [Fact]
    public void EveryInvocationInTheDocument_NamesARealCommandAndRealFlags()
    {
        var root = Root();
        var invocations = Invocations();
        Assert.True(invocations.Count >= 10, "expected a good number of documented invocations, found " + invocations.Count);

        foreach (string invocation in invocations)
        {
            var (path, flags) = Parse(root, invocation);
            var valid = FlagsOf(root, path);
            foreach (string flag in flags)
            {
                Assert.True(valid.Contains(flag), $"docs/cli.md: '{invocation}' uses {flag}, which 'earm {string.Join(' ', path)}' does not have");
            }
        }
    }

    [Fact]
    public void TheGlobalOptionsTable_ListsRecursiveRootOptions()
    {
        var root = Root();
        var rows = TableRows(SectionStartingWith("Global options"));

        Assert.True(rows.Count >= 7);
        foreach (var row in rows)
        {
            foreach (string flag in FlagsIn(row[0]))
            {
                var option = root.Options.FirstOrDefault(o => o.Name == flag || o.Aliases.Contains(flag));
                Assert.True(option is not null, $"global option {flag} is documented but not defined on the root");
                Assert.True(option!.Recursive, $"global option {flag} must be recursive (valid on every verb)");
            }
        }
    }

    [Fact]
    public void TheSharedInputsTable_AppliesToValidateWhatIfAndCreate()
    {
        var root = Root();
        var rows = TableRows(SectionStartingWith("Shared inputs of"));

        Assert.True(rows.Count >= 3);
        foreach (string verb in new[] { "validate", "what-if", "create" })
        {
            var flags = FlagsOf(root, ["deployment", verb]);
            foreach (var row in rows)
            {
                foreach (string flag in FlagsIn(row[0]))
                {
                    Assert.True(flags.Contains(flag), $"deployment {verb} lacks the shared input {flag}");
                }
            }
        }
    }

    [Fact]
    public void TheWhatIfAndCreateTable_PutsEachOptionOnTheVerbsItNames()
    {
        var root = Root();
        var rows = TableRows(SectionStartingWith("deployment what-if and deployment create"));

        Assert.True(rows.Count >= 10);
        var whatIf = FlagsOf(root, ["deployment", "what-if"]);
        var create = FlagsOf(root, ["deployment", "create"]);
        foreach (var row in rows)
        {
            string verb = row[1].Trim('`', ' ');
            foreach (string flag in FlagsIn(row[0]))
            {
                Assert.True(create.Contains(flag), $"deployment create lacks {flag}");
                if (verb == "both")
                {
                    Assert.True(whatIf.Contains(flag), $"deployment what-if lacks {flag}, which docs/cli.md lists for both verbs");
                }
                else
                {
                    Assert.False(whatIf.Contains(flag), $"{flag} is documented as 'create' only but deployment what-if accepts it");
                }
            }
        }
    }

    [Theory]
    [InlineData("deployment show", "deployment")]
    [InlineData("state", "state")]
    public void TheVerbSyntaxTables_NameFlagsTheirVerbsHave(string heading, string parent)
    {
        var root = Root();
        var rows = TableRows(SectionStartingWith(heading));

        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            string[] path = [parent, .. row[0].Replace("`", string.Empty, StringComparison.Ordinal).Split(' ', StringSplitOptions.RemoveEmptyEntries)];
            Assert.True(Resolve(root, path) is not null, "docs/cli.md lists the verb '" + string.Join(' ', path) + "'");
            var valid = FlagsOf(root, path);
            foreach (string flag in FlagsIn(row[1]))
            {
                Assert.True(valid.Contains(flag), $"docs/cli.md: '{string.Join(' ', path)}' is documented with {flag}, which it does not have");
            }
        }
    }

    [Fact]
    public void EveryCommandOptionInTheTree_IsMentionedInTheDocument()
    {
        var root = Root();
        string doc = CliDoc;

        foreach (var (path, command) in Walk(root, []))
        {
            foreach (var option in command.Options.Where(o => !o.Hidden))
            {
                if (option.Name is "--help" or "-?" or "-h")
                {
                    continue;
                }

                Assert.True(doc.Contains(option.Name, StringComparison.Ordinal), $"'{string.Join(' ', path)}' has {option.Name}, which docs/cli.md never mentions");
            }
        }
    }

    public static TheoryData<string> CommandPaths()
    {
        var data = new TheoryData<string>();
        foreach (var (path, _) in Walk(Root(), []).Where(w => w.Path.Length > 0))
        {
            data.Add(string.Join(' ', path));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CommandPaths))]
    public async Task HelpOfEveryVerb_ExitsZero_AndListsItsOptionsAndSubcommands(string path)
    {
        var root = Root();
        string[] words = path.Split(' ');
        var command = Resolve(root, words)!;

        var result = await CliRunner.RunAsync([.. words, "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.Contains("Usage:", result.StandardOutput, StringComparison.Ordinal);
        foreach (var option in command.Options.Where(o => !o.Hidden))
        {
            Assert.Contains(option.Name, result.StandardOutput, StringComparison.Ordinal);
        }

        foreach (var sub in command.Subcommands)
        {
            Assert.Contains(sub.Name, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("-?")]
    [InlineData("-h")]
    [InlineData("--help")]
    public async Task EveryHelpSpelling_WorksOnTheRootAndOnAVerb(string spelling)
    {
        var root = await CliRunner.RunAsync(spelling);
        var verb = await CliRunner.RunAsync("deployment", "validate", spelling);

        Assert.Equal(0, root.ExitCode);
        Assert.Contains("Usage:", root.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(0, verb.ExitCode);
        Assert.Contains("--template-file", verb.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Version_PrintsTheVersion()
    {
        var result = await CliRunner.RunAsync("--version");

        Assert.Equal(0, result.ExitCode);
        Assert.Matches(@"^\d+\.\d+\.\d+", result.StandardOutput.Trim());
    }

    // ---- diagnostic codes ----

    private static IReadOnlyDictionary<string, string> CodeConstants()
    {
        var codes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in new[] { typeof(DiagnosticCodes), typeof(EngineDiagnosticCodes) })
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy).Where(f => f.IsLiteral && f.FieldType == typeof(string)))
            {
                codes[(string)field.GetRawConstantValue()!] = type.Name + "." + field.Name;
            }
        }

        return codes;
    }

    private static string DiagnosticsDoc => File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "docs", "diagnostics.md"));

    [Fact]
    public void EveryDiagnosticCode_AppearsInTheDiagnosticsDocument()
    {
        var codes = CodeConstants();
        string doc = DiagnosticsDoc;
        Assert.True(codes.Count >= 60, "expected the full set of code constants, found " + codes.Count);

        var missing = codes.Where(c => !doc.Contains('`' + c.Key + '`', StringComparison.Ordinal)).Select(c => c.Value + " = " + c.Key).ToList();

        Assert.True(missing.Count == 0, "docs/diagnostics.md lacks: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryCodeInTheDiagnosticsTables_IsAKnownCode()
    {
        var known = new HashSet<string>(CodeConstants().Keys, StringComparer.Ordinal) { ValidateCommand.InvalidArgumentCode };
        var documented = new Regex(@"^\|\s*`([A-Z][A-Za-z0-9]+)`\s*\|", RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2))
            .Matches(DiagnosticsDoc)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(documented.Count >= 60, "expected the full table, found " + documented.Count);
        var unknown = documented.Where(c => !known.Contains(c)).ToList();

        Assert.True(unknown.Count == 0, "docs/diagnostics.md documents codes no constant defines: " + string.Join(", ", unknown));
    }

    [Fact]
    public void DiagnosticCodeConstants_AreUnique_AndNamedAfterTheirValue()
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in new[] { typeof(DiagnosticCodes), typeof(EngineDiagnosticCodes) })
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral && f.FieldType == typeof(string)))
            {
                string value = (string)field.GetRawConstantValue()!;
                Assert.Equal(field.Name, value);
                Assert.False(seen.TryGetValue(value, out string? other), $"{type.Name}.{field.Name} duplicates {other}");
                seen[value] = type.Name + "." + field.Name;
            }
        }
    }
}
