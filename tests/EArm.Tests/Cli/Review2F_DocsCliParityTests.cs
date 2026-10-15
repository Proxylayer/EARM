using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EArm.Cli;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// Review 2F: docs/cli.md and the System.CommandLine tree agree in BOTH directions, per command (reflection over the tree). The older
/// <see cref="DocsConsistencyTests"/> checks that what the document says exists and that every option is mentioned somewhere; this one checks that every
/// option of a command is documented FOR THAT COMMAND (an option documented only under another verb, or only in prose of a different section, fails),
/// that no <c>--flag</c> the document names is a flag nothing defines (a removed option left behind), and that the tables put each option on the verbs it belongs to.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class Review2F_DocsCliParityTests
{
    private static readonly Regex FlagToken = new(@"(?<![\w<\-])(--[A-Za-z][\w\-]*|-[A-Za-z])(?![\w\-])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex CodeSpan = new("`([^`]+)`", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    /// <summary>Options a command accepts although the document leaves them out: each is a known inconsistency (none at present).</summary>
    private static readonly HashSet<string> KnownUndocumented = new(StringComparer.Ordinal);

    /// <summary>Flags the document names on purpose although no command defines them: the removed Azure CLI transport options of the migration note, System.CommandLine's built-ins and <c>dotnet tool install -g</c>.</summary>
    private static readonly HashSet<string> NamedButNotDefined = new(StringComparer.Ordinal) { "--az-command", "--pwsh-path", "--temp-dir", "--version", "--help", "-h", "-?", "-g" };

    private static string[] DocLines => File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "docs", "cli.md")).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

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

    private static IEnumerable<string> Spellings(Option option)
    {
        yield return option.Name;
        foreach (string alias in option.Aliases)
        {
            yield return alias;
        }
    }

    private static string[] FlagsIn(string text)
    {
        return FlagToken.Matches(text).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string[] FlagsInCodeSpans(string text)
    {
        return CodeSpan.Matches(text).SelectMany(s => FlagsIn(s.Groups[1].Value)).Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>The command path an <c>earm …</c> invocation names (leading words that are commands).</summary>
    private static string[] PathOf(Command root, string invocation)
    {
        var path = new List<string>();
        Command current = root;
        foreach (string word in invocation.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var next = current.Subcommands.FirstOrDefault(c => c.Name.Equals(word, StringComparison.Ordinal));
            if (next is null)
            {
                break;
            }

            path.Add(word);
            current = next;
        }

        return [.. path];
    }

    /// <summary>The <c>earm …</c> invocations of the document (inline code spans and fenced lines), as the older test reads them.</summary>
    private static IEnumerable<string> Invocations()
    {
        foreach (string line in DocLines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("earm ", StringComparison.Ordinal))
            {
                yield return trimmed;
            }

            foreach (Match span in CodeSpan.Matches(line))
            {
                string text = span.Groups[1].Value.Trim();
                if (text.StartsWith("earm ", StringComparison.Ordinal))
                {
                    yield return text;
                }
            }
        }
    }

    private static List<(string Heading, List<string[]> Rows)> Tables()
    {
        var tables = new List<(string Heading, List<string[]> Rows)>();
        string heading = string.Empty;
        List<string[]>? rows = null;
        bool header = true;
        foreach (string line in DocLines)
        {
            if (line.StartsWith('#'))
            {
                heading = line.TrimStart('#', ' ').Replace("`", string.Empty, StringComparison.Ordinal).Trim();
                rows = null;
                continue;
            }

            if (!line.TrimStart().StartsWith('|'))
            {
                rows = null;
                continue;
            }

            if (rows is null)
            {
                rows = [];
                tables.Add((heading, rows));
                header = true;
            }

            // A pipe inside a cell is written \| in the Markdown (--mode Incremental\|Complete): it is part of the cell, not a separator.
            string[] cells = line.Trim().Trim('|').Replace("\\|", "\u0001", StringComparison.Ordinal).Split('|').Select(c => c.Trim().Replace("\u0001", "|", StringComparison.Ordinal)).ToArray();
            if (header)
            {
                header = false;
                continue;
            }

            if (cells[0].All(c => c == '-' || c == ' ' || c == ':'))
            {
                continue;
            }

            rows.Add(cells);
        }

        return tables;
    }

    /// <summary>The flags the document attaches to a command: its invocations, its option tables and the rows of the verb tables.</summary>
    private static HashSet<string> DocumentedFor(Command root, string[] path)
    {
        var documented = new HashSet<string>(StringComparer.Ordinal);
        string key = string.Join(' ', path);

        foreach (string invocation in Invocations())
        {
            if (string.Join(' ', PathOf(root, invocation)) == key)
            {
                documented.UnionWith(FlagsIn(invocation));
            }
        }

        foreach (var (heading, rows) in Tables())
        {
            if (heading.StartsWith("Shared inputs of", StringComparison.Ordinal) && path is ["deployment", "validate" or "what-if" or "create"])
            {
                documented.UnionWith(rows.SelectMany(r => FlagsInCodeSpans(r[0])));
            }

            if (heading.StartsWith("deployment what-if and deployment create", StringComparison.Ordinal) && path is ["deployment", "what-if" or "create"])
            {
                foreach (var row in rows)
                {
                    string verb = row[1].Trim('`', ' ');
                    if (verb == "both" || verb == path[1])
                    {
                        documented.UnionWith(FlagsInCodeSpans(row[0]));
                    }
                }
            }

            if (heading.StartsWith("deployment show", StringComparison.Ordinal) && path is ["deployment", ..])
            {
                AddVerbRows(documented, rows, path[1..]);
            }

            if (heading.Equals("state", StringComparison.Ordinal) && path is ["state", ..])
            {
                AddVerbRows(documented, rows, path[1..]);
            }
        }

        return documented;
    }

    private static void AddVerbRows(HashSet<string> documented, List<string[]> rows, string[] verbPath)
    {
        foreach (var row in rows)
        {
            string[] words = row[0].Replace("`", string.Empty, StringComparison.Ordinal).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.SequenceEqual(verbPath) && row.Length > 1)
            {
                documented.UnionWith(FlagsInCodeSpans(row[1]));
            }
        }
    }

    // ---- tests ----

    [Fact]
    public void EveryOptionOfEveryCommand_IsDocumentedForThatCommand()
    {
        var root = Program.BuildRootCommand();
        var missing = new List<string>();
        foreach (var (path, command) in Walk(root, []).Where(w => w.Path.Length > 0))
        {
            var documented = DocumentedFor(root, path);
            foreach (var option in command.Options.Where(o => !o.Hidden && !(o.Name is "--help" or "-?" or "-h")))
            {
                if (!Spellings(option).Any(documented.Contains) && !KnownUndocumented.Contains(string.Join(' ', path) + " " + option.Name))
                {
                    missing.Add($"earm {string.Join(' ', path)}: {option.Name}");
                }
            }
        }

        Assert.True(missing.Count == 0, "docs/cli.md does not document these options under their own command: " + string.Join("; ", missing));
    }

    [Fact]
    public void EveryRootOption_IsInTheGlobalOptionsTable()
    {
        var root = Program.BuildRootCommand();
        var global = Tables().Single(t => t.Heading.Equals("Global options", StringComparison.Ordinal)).Rows.SelectMany(r => FlagsInCodeSpans(r[0])).ToHashSet(StringComparer.Ordinal);
        var missing = root.Options
            .Where(o => !o.Hidden && !(o.Name is "--help" or "-?" or "-h" or "--version"))
            .Where(o => !Spellings(o).Any(global.Contains))
            .Select(o => o.Name)
            .ToList();

        Assert.True(missing.Count == 0, "the global options table lacks: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryFlagTheDocumentNames_IsDefinedByTheCommandTree()
    {
        var root = Program.BuildRootCommand();
        var defined = Walk(root, []).SelectMany(w => w.Command.Options).SelectMany(Spellings).ToHashSet(StringComparer.Ordinal);
        var named = DocLines.SelectMany(FlagsInCodeSpans).Distinct(StringComparer.Ordinal).ToList();

        Assert.True(named.Count >= 40, "expected the document to name many flags, found " + named.Count);
        var unknown = named.Where(f => !defined.Contains(f) && !NamedButNotDefined.Contains(f)).ToList();

        Assert.True(unknown.Count == 0, "docs/cli.md names flags that no command defines: " + string.Join(", ", unknown));
    }

    [Fact]
    public void TheCreateOnlyOptions_AreNotOnWhatIf_AndTheSharedOnesAreOnBoth()
    {
        // The what-if and create table says which verb takes which flag; the tree must say the same, option by option.
        var root = Program.BuildRootCommand();
        var whatIf = Resolve(root, ["deployment", "what-if"])!.Options.SelectMany(Spellings).ToHashSet(StringComparer.Ordinal);
        var create = Resolve(root, ["deployment", "create"])!.Options.SelectMany(Spellings).ToHashSet(StringComparer.Ordinal);
        var table = Tables().Single(t => t.Heading.StartsWith("deployment what-if and deployment create", StringComparison.Ordinal)).Rows;

        foreach (var row in table)
        {
            bool both = row[1].Trim('`', ' ') == "both";
            foreach (string flag in FlagsInCodeSpans(row[0]))
            {
                Assert.Contains(flag, create);
                Assert.Equal(both, whatIf.Contains(flag));
            }
        }

        // Nothing is on what-if that the table does not list as shared.
        var shared = table.Where(r => r[1].Trim('`', ' ') == "both").SelectMany(r => FlagsInCodeSpans(r[0])).ToHashSet(StringComparer.Ordinal);
        var common = Tables().Single(t => t.Heading.StartsWith("Shared inputs of", StringComparison.Ordinal)).Rows.SelectMany(r => FlagsInCodeSpans(r[0])).ToHashSet(StringComparer.Ordinal);
        var undocumented = Resolve(root, ["deployment", "what-if"])!.Options
            .Where(o => !o.Hidden && !(o.Name is "--help" or "-?" or "-h"))
            .Where(o => !Spellings(o).Any(shared.Contains) && !Spellings(o).Any(common.Contains))
            .Select(o => o.Name)
            .ToList();
        Assert.True(undocumented.Count == 0, "what-if has options that docs/cli.md lists for neither 'both' nor the shared inputs: " + string.Join(", ", undocumented));
    }

    [Fact]
    public void EveryDocumentedExitCode_ExistsInTheExitCodeConstants()
    {
        var table = Tables().Single(t => t.Heading.Equals("Exit codes", StringComparison.Ordinal)).Rows;
        var documented = table.Select(r => int.Parse(r[0], System.Globalization.CultureInfo.InvariantCulture)).Order().ToArray();
        var defined = typeof(ExitCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(int))
            .Select(f => (int)f.GetRawConstantValue()!)
            .Distinct()
            .Order()
            .ToArray();

        Assert.Equal(defined, documented);
    }
}
