using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Cli.Output;
using EArm.Core.Diagnostics;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// Review 3J, finding 5: the sanitizer only looked at the start of a line, so a name printed by the table writer (indented by two) gave
/// <c>  ##vso[task.complete result=Succeeded]: "x"</c> and a JSON property value gave <c>{"k":"##vso[task.setvariable ...</c>, both of which an Azure
/// Pipelines agent runs. <c>##vso[</c> and <c>##[</c> are now broken wherever they appear (the second <c>#</c> is written as the visible escape
/// <c>#</c>), and a <c>::</c> that is the first thing on a line after any whitespace gets an apostrophe in front. Also the mutant-driven tests
/// of <see cref="ConsoleSanitizer"/> (CS10, CS14).
/// </summary>
public sealed class Review3J_ConsoleSanitizerTests
{
    private static string Sanitize(string text)
    {
        using var inner = new StringWriter { NewLine = "\n" };
        using (var sanitizer = new ConsoleSanitizer(inner))
        {
            sanitizer.Write(text);
        }

        return inner.ToString();
    }

    private static string SanitizeInChunks(string text, Random random)
    {
        using var inner = new StringWriter { NewLine = "\n" };
        using (var sanitizer = new ConsoleSanitizer(inner))
        {
            int at = 0;
            while (at < text.Length)
            {
                int length = Math.Min(random.Next(1, 6), text.Length - at);
                if (random.Next(3) == 0)
                {
                    sanitizer.Write(text[at]);
                    length = 1;
                }
                else
                {
                    sanitizer.Write(text.Substring(at, length));
                }

                at += length;
            }
        }

        return inner.ToString();
    }

    // ---- the reviewer's cases ----

    [Theory]
    [InlineData("  ##vso[task.complete result=Succeeded]: \"x\"\n", "  #\\u0023vso[task.complete result=Succeeded]: \"x\"\n")]
    [InlineData("\t##vso[x]\n", "\t#\\u0023vso[x]\n")]
    [InlineData("\u00a0\u3000##vso[x]\n", "\u00a0\u3000#\\u0023vso[x]\n")]
    [InlineData("o2: {\"k\":\"##vso[task.setvariable variable=EVIL]1\"}\n", "o2: {\"k\":\"#\\u0023vso[task.setvariable variable=EVIL]1\"}\n")]
    [InlineData("a ##[error]boom\n", "a #\\u0023[error]boom\n")]
    [InlineData("##VSO[x] and ##Vso[y]\n", "#\\u0023VSO[x] and #\\u0023Vso[y]\n")]
    [InlineData("###vso[x]\n", "##\\u0023vso[x]\n")]
    [InlineData("##vso[a]##vso[b]\n", "#\\u0023vso[a]#\\u0023vso[b]\n")]
    [InlineData("#\n##\n##v\n##vso\n", "#\n##\n##v\n##vso\n")]
    public void ALoggingCommandToken_IsBroken_WhereverItIs(string text, string expected)
    {
        Assert.Equal(expected, Sanitize(text));
    }

    [Theory]
    [InlineData("  ::set-output name=a::b\n", "  '::set-output name=a::b\n")]
    [InlineData("\t\t::error::x\n", "\t\t'::error::x\n")]
    [InlineData("::error::x\n", "'::error::x\n")]
    [InlineData("\u00a0::x\n", "\u00a0'::x\n")]
    public void AWorkflowCommand_AfterWhitespace_GetsAnApostropheInFrontOfIt(string text, string expected)
    {
        Assert.Equal(expected, Sanitize(text));
    }

    [Theory]
    [InlineData("text with ::inside\n")]
    [InlineData("x ::y\n")]
    [InlineData("C++ std::vector\n")]
    [InlineData("a:b::c\n")]
    [InlineData(":single\n")]
    [InlineData("#hash and ##x and # #\n")]
    public void OrdinaryText_IsNotTouched(string text)
    {
        Assert.Equal(text, Sanitize(text));
    }

    [Fact]
    public void ATokenSplitAcrossWrites_AndAcrossAColourCode_IsStillBroken()
    {
        using var inner = new StringWriter { NewLine = "\n" };
        using (var sanitizer = new ConsoleSanitizer(inner))
        {
            sanitizer.Write("name: #");
            sanitizer.Write("#v");
            sanitizer.Write("so");
            sanitizer.Write('[');
            sanitizer.Write("x]\n");
            sanitizer.Write("  :");
            sanitizer.Write(":set\n");
        }

        Assert.Equal("name: #\\u0023vso[x]\n  '::set\n", inner.ToString());
    }

    [Fact]
    public void ThePrefixInAnIndentedTableRow_OfARealRun_IsBroken()
    {
        var text = new StringWriter { NewLine = "\n" };
        using (var sanitizer = new ConsoleSanitizer(text))
        {
            // What the table writers do: indent a name and print a value inside braces.
            sanitizer.WriteLine("  " + "##vso[task.complete result=Succeeded]" + ": \"x\"");
            sanitizer.WriteLine("o2: " + new JsonObject { ["k"] = "##vso[task.setvariable variable=EVIL]1" }.ToJsonString());
        }

        Assert.All(text.ToString().Split('\n'), line => Assert.False(SafeText.ContainsCommandToken(line), line));
    }

    [Fact]
    public void InJsonOutput_TheBrokenToken_DecodesBackToTheOriginalText()
    {
        string original = "##vso[task.setvariable variable=EVIL]1";
        string json = new JsonObject { ["k"] = original, ["list"] = new JsonArray(original, "::x") }.ToJsonString();

        string written = Sanitize(json + "\n");
        var parsed = JsonNode.Parse(written)!;

        Assert.DoesNotContain("##vso[", written, StringComparison.Ordinal);
        Assert.Equal(original, parsed["k"]!.GetValue<string>());
        Assert.Equal("::x", parsed["list"]![1]!.GetValue<string>());
    }

    [Fact]
    public void ThePreIndentedAndPrettyPrintedJson_OfAFailureDocument_IsStillValidJson()
    {
        string json = FailureDocument.Create("X", "line one ##vso[a]\nline two ::b").ToJsonString(FailureDocument.Options);

        var parsed = JsonNode.Parse(Sanitize(json + "\n"))!;

        Assert.Equal("line one ##vso[a]\nline two ::b", parsed["diagnostics"]![0]!["message"]!.GetValue<string>());
        Assert.Equal(2, parsed["diagnostics"]![0]!["lines"]!.AsArray().Count);
    }

    // ---- NeutralizeCommandPrefix (the logger) agrees with the writer ----

    [Theory]
    [InlineData("  ##vso[x] y ##[z]")]
    [InlineData("  ::x")]
    [InlineData("a ::x ##vso[")]
    [InlineData("plain")]
    public void TheLoggerSideNeutralisation_AgreesWithTheWriter(string line)
    {
        Assert.Equal(Sanitize(line + "\n"), SafeText.NeutralizeCommandPrefix(line) + "\n");
    }

    // ---- a seeded fuzz: any chunking, any mix of fragments ----

    [Fact]
    public void FuzzedFragments_NeverLeaveACommandToken_AControlCharacter_OrADependenceOnHowTheTextWasChunked()
    {
        string[] fragments = ["#", "##", "vso[", "[", ":", "::", " ", "  ", "\t", "\u00a0", "\u3000", "\n", "\r\n", "x", "ab", "\u001b[0m", "\u001b[2J", "\u2028", "\u0085", "\u202e", "VSO[", "set-output", "{\"k\":\""];
        var random = new Random(31_415);
        for (int round = 0; round < 4_000; round++)
        {
            string text = string.Concat(Enumerable.Range(0, random.Next(1, 14)).Select(_ => fragments[random.Next(fragments.Length)]));

            string whole = Sanitize(text);
            string chunked = SanitizeInChunks(text, random);

            Assert.Equal(whole, chunked);
            foreach (string line in whole.Split('\n'))
            {
                Assert.False(SafeText.ContainsCommandToken(line.TrimEnd('\r').Replace("\u001b[0m", string.Empty, StringComparison.Ordinal)), $"round {round}: {line}");
            }

            foreach (char c in whole)
            {
                Assert.True(c is '\n' or '\r' or '\t' or (char)0x1b || !SafeText.NeedsEscape(c), $"round {round}: U+{(int)c:X4}");
            }
        }
    }

    // ---- ConsoleSanitizer overloads (CS10) and a lone CR between split writes (CS14) ----

    private const string Hostile = "ok \u001b[2J ##vso[x]\n  ::y\u2028z\u0085\n";
    private const string HostileExpected = "ok \\u001b[2J #\\u0023vso[x]\n  '::y\\u2028z\\u0085\n";

    [Fact]
    public async Task EveryWriteOverload_SanitizesTheSame()
    {
        var results = new System.Collections.Generic.Dictionary<string, string>();

        string Run(Action<ConsoleSanitizer> write)
        {
            using var inner = new StringWriter { NewLine = "\n" };
            using (var sanitizer = new ConsoleSanitizer(inner))
            {
                write(sanitizer);
            }

            return inner.ToString();
        }

        results["string"] = Run(s => s.Write(Hostile));
        results["char"] = Run(s =>
        {
            foreach (char c in Hostile)
            {
                s.Write(c);
            }
        });
        results["char[]"] = Run(s => s.Write(Hostile.ToCharArray(), 0, Hostile.Length));
        results["char[] slice"] = Run(s =>
        {
            char[] padded = ("XX" + Hostile + "YY").ToCharArray();
            s.Write(padded, 2, Hostile.Length);
        });
        results["span"] = Run(s => s.Write(Hostile.AsSpan()));

        using (var inner = new StringWriter { NewLine = "\n" })
        {
            var sanitizer = new ConsoleSanitizer(inner);
            await sanitizer.WriteAsync(Hostile);
            await sanitizer.FlushAsync();
            results["WriteAsync(string)"] = inner.ToString();
        }

        using (var inner = new StringWriter { NewLine = "\n" })
        {
            var sanitizer = new ConsoleSanitizer(inner);
            await sanitizer.WriteAsync(Hostile.ToCharArray(), 0, Hostile.Length);
            await sanitizer.FlushAsync();
            results["WriteAsync(char[])"] = inner.ToString();
        }

        using (var inner = new StringWriter { NewLine = "\n" })
        {
            var sanitizer = new ConsoleSanitizer(inner);
            await sanitizer.WriteAsync(Hostile.AsMemory(), TestContext.Current.CancellationToken);
            await sanitizer.FlushAsync();
            results["WriteAsync(memory)"] = inner.ToString();
        }

        using (var inner = new StringWriter { NewLine = "\n" })
        {
            var sanitizer = new ConsoleSanitizer(inner);
            await sanitizer.WriteAsync('a');
            await sanitizer.WriteLineAsync("b ##vso[x]");
            await sanitizer.WriteLineAsync();
            await sanitizer.FlushAsync();
            Assert.Equal("ab #\\u0023vso[x]\n\n", inner.ToString());
        }

        Assert.All(results, pair => Assert.Equal(HostileExpected, pair.Value));
    }

    [Fact]
    public void WriteLine_Overloads_AreSanitizedAndEndTheLine()
    {
        using var inner = new StringWriter { NewLine = "\n" };
        using (var sanitizer = new ConsoleSanitizer(inner))
        {
            sanitizer.WriteLine("##vso[x]");
            sanitizer.WriteLine();
            sanitizer.WriteLine("::y");
        }

        Assert.Equal("#\\u0023vso[x]\n\n'::y\n", inner.ToString());
    }

    [Fact]
    public void ALoneCarriageReturn_ReleasesWhatWasHeldBack_BetweenSplitWrites()
    {
        using var inner = new StringWriter { NewLine = "\n" };
        using (var sanitizer = new ConsoleSanitizer(inner))
        {
            sanitizer.Write("##vs");
            sanitizer.Write("\r");
            sanitizer.Write("o[x]");
        }

        // The held "##vs" is written, then the carriage return (it did not belong to a line break) as the visible escape: no token, no lost text.
        Assert.Equal("##vs\\ro[x]", inner.ToString());
    }

    [Fact]
    public void ACarriageReturnThatEndsTheText_IsEscapedByFlush_AndOneBeforeALineFeedIsAnOrdinaryLineBreak()
    {
        using var inner = new StringWriter { NewLine = "\n" };
        using (var sanitizer = new ConsoleSanitizer(inner))
        {
            sanitizer.Write("##v");
            sanitizer.Write("\r");
            sanitizer.Write("\n");
            sanitizer.Write("end\r");
        }

        Assert.Equal("##v\r\nend\\r", inner.ToString());
    }
}
