using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Cli;
using EArm.Cli.Output;
using EArm.Core.Diagnostics;
using EArm.Tests.Engine;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// Review 1C, finding S-03: names, property keys and messages from a template were echoed raw, so a resource name holding
/// <c>\n##vso[task.setvariable variable=EVIL]1</c> or <c>\n::set-output ...</c> produced a line that a CI log parser runs as a command, and
/// escape sequences (ESC, OSC, BEL) or bidirectional overrides could rewrite or reorder what the terminal shows. Diagnostics are escaped where they
/// are created, and the writers of the run are wrapped in <see cref="ConsoleSanitizer"/>, which escapes control characters and neutralises
/// lines that start like a log command.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class Review1C_ConsoleSafetyTests
{
    private static readonly string Esc = ((char)0x1b).ToString();
    private static readonly string Bell = ((char)0x07).ToString();
    private static readonly string Del = ((char)0x7f).ToString();
    private static readonly string NextLine = ((char)0x85).ToString();
    private static readonly string RightToLeftOverride = ((char)0x202E).ToString();
    private static readonly string LeftToRightIsolate = ((char)0x2066).ToString();

    private static string Sanitize(string text)
    {
        using var inner = new StringWriter { NewLine = "\n" };
        using (var sanitizer = new ConsoleSanitizer(inner))
        {
            sanitizer.Write(text);
        }

        return inner.ToString();
    }

    private static string SanitizeLines(params string[] lines)
    {
        using var inner = new StringWriter { NewLine = "\n" };
        using (var sanitizer = new ConsoleSanitizer(inner))
        {
            foreach (string line in lines)
            {
                sanitizer.WriteLine(line);
            }
        }

        return inner.ToString();
    }

    private static void AssertSafe(string text)
    {
        foreach (char c in text)
        {
            Assert.True(c is '\n' or '\r' or '\t' || !SafeText.NeedsEscape(c), $"U+{(int)c:X4} was written raw");
        }

        foreach (string line in text.Split('\n'))
        {
            Assert.False(SafeText.StartsWithCommandPrefix(line.TrimEnd('\r')), "a line starts like a log command: " + line);
        }
    }

    // ---- the writer ----

    [Fact]
    public void ControlCharacters_AreWrittenAsVisibleEscapes()
    {
        string text = "a" + Esc + "]0;title" + Bell + "b" + Del + "c" + NextLine + "d" + RightToLeftOverride + "e" + LeftToRightIsolate + "f";

        string result = Sanitize(text);

        Assert.Equal("a\\u001b]0;title\\u0007b\\u007fc\\u0085d\\u202ee\\u2066f", result);
    }

    [Fact]
    public void LineBreaks_AndTabs_PassUnchanged()
    {
        Assert.Equal("one\ntwo\r\nthree\tx\n", Sanitize("one\ntwo\r\nthree\tx\n"));
    }

    [Fact]
    public void ALoneCarriageReturn_IsEscaped_BecauseItOverwritesWhatWasShown()
    {
        Assert.Equal("fake\\rreal\n", Sanitize("fake\rreal\n"));
        Assert.Equal("end\\r", Sanitize("end\r"));
    }

    [Theory]
    [InlineData("::set-output name=EVIL::1")]
    [InlineData("::error file=x::y")]
    public void ALineThatStartsLikeAWorkflowCommand_GetsALeadingApostrophe(string line)
    {
        Assert.Equal("'" + line + "\n", SanitizeLines(line));
    }

    [Theory]
    [InlineData("##vso[task.setvariable variable=EVIL]1", "#\\u0023vso[task.setvariable variable=EVIL]1")]
    [InlineData("##[error]boom", "#\\u0023[error]boom")]
    public void ALineThatStartsLikeALoggingCommand_HasItsTokenBroken(string line, string expected)
    {
        // Review 3J: the apostrophe in front is not enough, an Azure Pipelines agent also finds the token inside a line; the token itself is broken.
        Assert.Equal(expected + "\n", SanitizeLines(line));
    }

    [Fact]
    public void ABreakSmuggledInsideAValue_DoesNotLetTheNextLineBeACommand()
    {
        string result = SanitizeLines("error InvalidResourceName: bad name evil\n##vso[task.setvariable variable=EVIL]1\n::warning::x");

        Assert.Equal("error InvalidResourceName: bad name evil\n#\\u0023vso[task.setvariable variable=EVIL]1\n'::warning::x\n", result);
        AssertSafe(result);
    }

    [Fact]
    public void ACommandPrefixSplitAcrossWrites_IsStillCaught()
    {
        using var inner = new StringWriter { NewLine = "\n" };
        using (var sanitizer = new ConsoleSanitizer(inner))
        {
            sanitizer.Write("#");
            sanitizer.Write("#v");
            sanitizer.Write("so[x]");
            sanitizer.Write('\n');
            sanitizer.Write(":");
            sanitizer.Write(":set");
            sanitizer.WriteLine();
        }

        Assert.Equal("#\\u0023vso[x]\n'::set\n", inner.ToString());
    }

    [Theory]
    [InlineData("text with ::inside\n")]
    [InlineData("a:b\n")]
    [InlineData("#hash\n")]
    [InlineData("##x\n")]
    [InlineData(":single\n")]
    public void OtherLines_AreNotTouched(string text)
    {
        Assert.Equal(text, Sanitize(text));
    }

    [Fact]
    public void APartialLineWithAPossiblePrefix_IsReleasedByFlush_ForPrompts()
    {
        using var inner = new StringWriter { NewLine = "\n" };
        var sanitizer = new ConsoleSanitizer(inner);

        sanitizer.Write("Continue (y/n): ");
        sanitizer.Flush();
        Assert.Equal("Continue (y/n): ", inner.ToString());

        sanitizer.Write("::");
        sanitizer.Flush();
        Assert.EndsWith("Continue (y/n): ::", inner.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheColourCodesOfTheWhatIfText_PassThrough_AllOtherSequencesDoNot()
    {
        string coloured = Esc + "[32m+ create" + Esc + "[0m\n" + Esc + "[1;31m! boom" + Esc + "[0m\n";
        Assert.Equal(coloured, Sanitize(coloured));

        Assert.Equal("\\u001b[8mhidden" + Esc + "[0m\n", Sanitize(Esc + "[8mhidden" + Esc + "[0m\n"));
        Assert.Equal("\\u001b]0;pwned\\u0007\n", Sanitize(Esc + "]0;pwned" + Bell + "\n"));
        Assert.Equal("\\u001b[2J\n", Sanitize(Esc + "[2J\n"));
        Assert.Equal("end\\u001b", Sanitize("end" + Esc));
    }

    [Fact]
    public void ASequenceThatLooksLikeAColour_AtTheStartOfALine_DoesNotHideACommand()
    {
        string result = SanitizeLines(Esc + "[32m##vso[task.setvariable variable=EVIL]1");

        Assert.Equal(Esc + "[32m#\\u0023vso[task.setvariable variable=EVIL]1\n", result);
    }

    [Fact]
    public void Unwrap_FindsTheRealWriter()
    {
        using var inner = new StringWriter();
        using var outer = new ConsoleSanitizer(new ConsoleSanitizer(inner));

        Assert.Same(inner, ConsoleSanitizer.Unwrap(outer));
        Assert.Same(inner, ConsoleSanitizer.Unwrap(inner));
    }

    [Fact]
    public void ConcurrentWriters_NeverInterleaveInsideALine()
    {
        using var inner = new StringWriter { NewLine = "\n" };
        var sanitizer = new ConsoleSanitizer(inner);

        Parallel.For(0, 8, worker =>
        {
            for (int i = 0; i < 500; i++)
            {
                sanitizer.WriteLine("worker-" + worker + "-line-" + i + "-end");
            }
        });
        sanitizer.Flush();

        string[] lines = inner.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(8 * 500, lines.Length);
        Assert.All(lines, line => Assert.Matches("^worker-[0-7]-line-[0-9]+-end$", line));
    }

    // ---- diagnostics are single printable lines by construction ----

    [Fact]
    public void ADiagnosticMessage_IsEscapedWhereItIsCreated_EvenForAPositionalConstruction()
    {
        string hostile = "bad\n##vso[task.setvariable variable=EVIL]1" + Esc + "[2J" + RightToLeftOverride;

        var viaFactory = Diagnostic.Error("X", hostile);
        var viaConstructor = new Diagnostic(DiagnosticSeverity.Error, "X", hostile);
        var viaWith = viaFactory with { Message = hostile };

        foreach (var diagnostic in new[] { viaFactory, viaConstructor, viaWith })
        {
            Assert.DoesNotContain('\n', diagnostic.Message);
            Assert.Equal("bad\\n##vso[task.setvariable variable=EVIL]1\\u001b[2J\\u202e", diagnostic.Message);
            AssertSafe(diagnostic.ToString());
        }
    }

    [Fact]
    public void SafeText_Quote_EscapesQuotesBackslashesAndControls()
    {
        Assert.Equal("\"a\\\"b\\\\c\\nd\"", SafeText.Quote("a\"b\\c\nd"));
        Assert.Equal("\"\"", SafeText.Quote(null));
        Assert.Same("plain", SafeText.Escape("plain"));
    }

    // ---- end to end through the command line ----

    private static string HostileTemplate(string nameJson, string extraVariables = "")
    {
        return Review1C.Template(extraVariables + "\"resources\": [ " + Review1C.Group(nameJson, "{ \"displayName\": \"g\", \"mailEnabled\": false, \"securityEnabled\": true }") + " ]");
    }

    private static async Task<CliResult> ValidateTextAsync(string testName, string template, params string[] extra)
    {
        using var directory = new TestDirectory(testName);
        string path = directory.File("hostile.entra.json");
        await File.WriteAllTextAsync(path, template, new UTF8Encoding(false), TestContext.Current.CancellationToken);
        return await CliRunner.RunAsync(["deployment", "validate", "-f", path, .. extra]);
    }

    private static readonly string[] HostileNames =
    [
        "evil\n##vso[task.setvariable variable=EVIL]1",
        "evil\r\n::set-output name=EVIL::1",
        "evil" + Esc + "]0;pwned" + Bell,
        "evil" + Esc + "[2J" + Esc + "[H",
        "evil" + RightToLeftOverride + "txt.exe",
    ];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task AHostileLiteralName_NeverProducesACommandLineOrAControlCharacter_InTheTableOutput(int index)
    {
        string template = HostileTemplate(JsonSerializer.Serialize(HostileNames[index]));

        var result = await ValidateTextAsync(nameof(AHostileLiteralName_NeverProducesACommandLineOrAControlCharacter_InTheTableOutput), template);

        Assert.Equal(2, result.ExitCode);
        AssertSafe(result.StandardOutput);
        AssertSafe(result.StandardError);
        Assert.Contains("InvalidResourceName", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task AHostileLiteralName_IsValidJson_AndEscapedInTheMessage_InTheJsonOutput(int index)
    {
        string template = HostileTemplate(JsonSerializer.Serialize(HostileNames[index]));

        var result = await ValidateTextAsync(nameof(AHostileLiteralName_IsValidJson_AndEscapedInTheMessage_InTheJsonOutput), template, "-o", "json");

        Assert.Equal(2, result.ExitCode);
        var report = JsonNode.Parse(result.StandardOutput)!.AsObject();
        var diagnostic = Assert.Single(report["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "InvalidResourceName");
        string message = diagnostic!["message"]!.GetValue<string>();
        Assert.All(message, c => Assert.False(SafeText.NeedsEscape(c), "U+" + ((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture)));
        AssertSafe(result.StandardOutput);
    }

    [Fact]
    public async Task AHostileVariableName_InAnExpression_AndAHostilePropertyKey_AreEscaped()
    {
        string hostile = "x\n##vso[task.setvariable variable=EVIL]1";
        string expression = "[variables(" + "'" + hostile + "')]";
        string template = Review1C.Template("\"resources\": [ " + Review1C.Group(JsonSerializer.Serialize(expression), "{ \"displayName\": \"g\", \"mailEnabled\": false, \"securityEnabled\": true, " + JsonSerializer.Serialize("key\n::set-output name=a::b") + ": 1 }") + " ]");

        var result = await ValidateTextAsync(nameof(AHostileVariableName_InAnExpression_AndAHostilePropertyKey_AreEscaped), template);

        Assert.Equal(2, result.ExitCode);
        AssertSafe(result.StandardOutput);
        AssertSafe(result.StandardError);
        Assert.Contains("x\\n#\\u0023vso[task.setvariable variable=EVIL]1", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RawTextWrittenByACommand_IsSanitizedOnTheWayOut_OnBothStreams()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var configuration = new System.CommandLine.InvocationConfiguration { Output = stdout, Error = stderr };

        int exit = await Program.InvokeAsync(
            ["emit"],
            configuration,
            TestContext.Current.CancellationToken,
            () =>
            {
                var root = new RootCommand("test");
                var emit = new Command("emit");
                emit.SetAction(parseResult =>
                {
                    parseResult.InvocationConfiguration.Output.WriteLine("ok\n##vso[task.setvariable variable=OUT]1");
                    parseResult.InvocationConfiguration.Error.WriteLine("bad" + Esc + "]0;t" + Bell + "\n::set-output name=ERR::1");
                    return 0;
                });
                root.Subcommands.Add(emit);
                return root;
            });

        Assert.Equal(0, exit);
        Assert.Equal("ok\n#\\u0023vso[task.setvariable variable=OUT]1\n", stdout.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
        AssertSafe(stdout.ToString());
        AssertSafe(stderr.ToString());
        Assert.Contains("bad\\u001b]0;t\\u0007", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("'::set-output name=ERR::1", stderr.ToString(), StringComparison.Ordinal);
    }

    // ---- the logger ----

    [Fact]
    public void TheLogLine_IsOneEscapedLine()
    {
        var formatter = new SanitizingConsoleFormatter();
        using var writer = new StringWriter { NewLine = "\n" };
        var entry = new LogEntry<string>(LogLevel.Information, "EArm.Test", new EventId(7), "state", new InvalidOperationException("inner\n::error::x"), (s, e) => "created evil\n##vso[task.setvariable variable=EVIL]1" + Esc + "[2J");

        formatter.Write(in entry, null, writer);

        string line = writer.ToString();
        Assert.Single(line.TrimEnd('\n').Split('\n'));
        Assert.Contains(" info: EArm.Test[7] created evil\\n#\\u0023vso[task.setvariable variable=EVIL]1\\u001b[2J InvalidOperationException: inner\\n::error::x", line, StringComparison.Ordinal);
        AssertSafe(line);
    }
}
