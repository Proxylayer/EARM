using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Expressions;
using EArm.Expressions.Evaluation;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Review 3J, findings 2 and 3: <c>format('{0:D999999999}', 5)</c> peaked at 5.7 GB / 6 s (three of them 11.5 GB / 24 s) because the precision of a
/// specifier was handed to <c>string.Format</c>, and <c>lastIndexOf</c> over 200,000 characters took 12 s (a megabyte more than two minutes) with the
/// wall clock unable to interrupt it. <c>format</c> is now evaluated by EARM itself with every item checked first, and the searches are linear.
/// </summary>
public sealed class Review3J_StringBudgetTests
{
    private static EvaluationContext Context(EvaluationLimits? limits = null, Dictionary<string, JsonNode?>? variables = null)
    {
        return Expr.Context(variables: variables) with { Budget = new EvaluationBudget(limits) };
    }

    private static ExpressionEvaluationException Fails(string expression, string? code = null, EvaluationContext? context = null)
    {
        var error = Assert.Throws<ExpressionEvaluationException>(() => Expr.Eval(expression, context ?? Context()));
        if (code is not null)
        {
            Assert.Equal(code, error.Code);
        }

        return error;
    }

    // ---- format ----

    [Theory]
    [InlineData("[format('{0:D999999999}', 5)]")]
    [InlineData("[format('{0:N999999999}', 5)]")]
    [InlineData("[format('{0:F101}', 5)]")]
    [InlineData("[format('{0:X1000}', 5)]")]
    [InlineData("[format('{0:D999999999}{0:D999999999}{0:D999999999}', 5)]")]
    [InlineData("[format('{0:E2147483647}', json('1.5'))]")]
    public void ALargePrecision_IsRefused_BeforeAnythingIsAllocated(string expression)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();

        var error = Fails(expression);

        Assert.Equal(DiagnosticCodes.InvalidFunctionArguments, error.Code);
        Assert.Contains("at most 100", error.Message, StringComparison.Ordinal);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 10_000_000);
    }

    [Fact]
    public void ACustomSpecifierLongerThanOneHundredCharacters_IsRefused()
    {
        Fails("[format('{0:" + new string('0', 101) + "}', 5)]", DiagnosticCodes.InvalidFunctionArguments);
        Assert.Equal(new string('0', 99) + "5", Expr.AssertString(Expr.Eval("[format('{0:" + new string('0', 100) + "}', 5)]", Context())));
    }

    [Theory]
    [InlineData("[format('{0:C}', 5)]")]
    [InlineData("[format('{0:y}', 5)]")]
    public void AStandardSpecifierOutsideTheAllowList_IsRefused(string expression)
    {
        var error = Fails(expression, DiagnosticCodes.InvalidFunctionArguments);

        Assert.Contains("not supported", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[format('{0:D3}', 7)]", "007")]
    [InlineData("[format('{0:x4}', 255)]", "00ff")]
    [InlineData("[format('{0:X}', 255)]", "FF")]
    [InlineData("[format('{0:N2}', 1234567)]", "1,234,567.00")]
    [InlineData("[format('{0:F1}', json('2.26'))]", "2.3")]
    [InlineData("[format('{0:0.00}', json('2.5'))]", "2.50")]
    [InlineData("[format('{0:#,##0}', 1234567)]", "1,234,567")]
    [InlineData("[format('{0,5}|{0,-5}|', 42)]", "   42|42   |")]
    [InlineData("[format('{0 , 5}', 'ab')]", "   ab")]
    [InlineData("[format('{{{0}}}', 'x')]", "{x}")]
    [InlineData("[format('{1}{0}{1}', 'a', 'b')]", "bab")]
    [InlineData("[format('{0:yyyy-MM-dd}', 'text')]", "text")]
    [InlineData("[format('a}}b{{c')]", "a}b{c")]
    public void FormatItems_KeepTheirNetSemantics(string expression, string expected)
    {
        Assert.Equal(expected, Expr.AssertString(Expr.Eval(expression, Context())));
    }

    [Fact]
    public void ThePrecisionLimit_IsOneHundred_ToTheDigit()
    {
        Assert.Equal(100, Expr.AssertString(Expr.Eval("[format('{0:D100}', 7)]", Context())).Length);
        Fails("[format('{0:D101}', 7)]", DiagnosticCodes.InvalidFunctionArguments);
    }

    [Theory]
    [InlineData("[format('{0')]")]
    [InlineData("[format('{')]")]
    [InlineData("[format('}')]")]
    [InlineData("[format('{a}', 1)]")]
    [InlineData("[format('{0,}', 1)]")]
    [InlineData("[format('{0:D3', 1)]")]
    [InlineData("[format('{1}', 'x')]")]
    [InlineData("[format('{0:D}', json('1.5'))]")]
    public void MalformedFormatStrings_AreAnEvaluationFailure(string expression)
    {
        var error = Fails(expression);

        Assert.Equal(DiagnosticCodes.ExpressionEvaluationFailed, error.Code);
        Assert.Contains("invalid format string", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ManyPaddedItems_AreHeldToTheStringLimit_ByTheirSum()
    {
        string items = string.Concat(Enumerable.Repeat("{0,1000000}", 2000));

        var error = Fails("[format('" + items + "', 'x')]", DiagnosticCodes.EvaluationBudgetExceeded);

        Assert.Contains("1 MiB", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ManyCopiesOfABigArgument_AreHeldToTheStringLimit()
    {
        var variables = new Dictionary<string, JsonNode?> { ["big"] = JsonValue.Create(new string('a', 400_000)) };
        string items = string.Concat(Enumerable.Repeat("{0}", 2000));

        Fails("[format('" + items + "', variables('big'))]", DiagnosticCodes.EvaluationBudgetExceeded, Context(variables: variables));

        Assert.Equal(800_000, Expr.AssertString(Expr.Eval("[format('{0}{0}', variables('big'))]", Context(variables: variables))).Length);
    }

    [Fact]
    [Trait("Category", "Timing")]
    public void AMaximumLengthFormatString_IsEvaluatedQuickly()
    {
        var stopwatch = Stopwatch.StartNew();
        string items = string.Concat(Enumerable.Repeat("{0}", 300_000));
        var variables = new Dictionary<string, JsonNode?> { ["f"] = JsonValue.Create(items) };

        Assert.Equal(300_000, Expr.AssertString(Expr.Eval("[format(variables('f'), 'x')]", Context(variables: variables))).Length);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), $"took {stopwatch.Elapsed}");
    }

    // ---- lastIndexOf / indexOf / contains ----

    [Theory]
    [InlineData("abcabc", "bc", 1, 4)]
    [InlineData("ABCabc", "BC", 1, 4)]
    [InlineData("aaaa", "aa", 0, 2)]
    [InlineData("abc", "", 0, 3)]
    [InlineData("abc", "x", -1, -1)]
    [InlineData("abc", "abcd", -1, -1)]
    [InlineData("ab", "AB", 0, 0)]
    [InlineData("mississippi", "ISSI", 1, 4)]
    [InlineData("aabaabaaab", "aab", 0, 7)]
    public void TheSearches_AreCaseInsensitive_AndFindTheFirstAndLastOccurrence(string text, string needle, int first, int last)
    {
        var variables = new Dictionary<string, JsonNode?> { ["t"] = JsonValue.Create(text), ["n"] = JsonValue.Create(needle) };
        var context = Context(variables: variables);

        Assert.Equal(first, (int)Expr.Eval("[indexOf(variables('t'), variables('n'))]", context)!.GetValue<long>());
        Assert.Equal(last, (int)Expr.Eval("[lastIndexOf(variables('t'), variables('n'))]", context)!.GetValue<long>());
        Assert.Equal(first >= 0, Expr.Eval("[contains(variables('t'), variables('n'))]", context)!.GetValue<bool>());
    }

    [Fact]
    public void TheSearches_AgreeWithTheFrameworkOnRandomText()
    {
        var random = new Random(12345);
        const string alphabet = "abAB";
        for (int round = 0; round < 300; round++)
        {
            string text = new(Enumerable.Range(0, random.Next(0, 40)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            string needle = new(Enumerable.Range(0, random.Next(0, 5)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            var variables = new Dictionary<string, JsonNode?> { ["t"] = JsonValue.Create(text), ["n"] = JsonValue.Create(needle) };
            var context = Context(variables: variables);

            Assert.Equal(text.IndexOf(needle, StringComparison.OrdinalIgnoreCase), (int)Expr.Eval("[indexOf(variables('t'), variables('n'))]", context)!.GetValue<long>());
            Assert.Equal(text.LastIndexOf(needle, StringComparison.OrdinalIgnoreCase), (int)Expr.Eval("[lastIndexOf(variables('t'), variables('n'))]", context)!.GetValue<long>());
        }
    }

    [Fact]
    [Trait("Category", "Timing")]
    public void LastIndexOf_AdversarialRepeats_AreLinear()
    {
        // The needle matches its first and last characters at every position of the text and differs in the middle: the naive search compares half
        // the needle at each of 500,000 positions.
        string text = new('a', 1_000_000);
        string needle = new string('a', 250_000) + "b" + new string('a', 250_000);
        var variables = new Dictionary<string, JsonNode?> { ["t"] = JsonValue.Create(text), ["n"] = JsonValue.Create(needle) };
        var context = Context(variables: variables);
        var stopwatch = Stopwatch.StartNew();

        Assert.Equal(-1L, Expr.Eval("[lastIndexOf(variables('t'), variables('n'))]", context)!.GetValue<long>());
        Assert.Equal(-1L, Expr.Eval("[indexOf(variables('t'), variables('n'))]", context)!.GetValue<long>());
        Assert.False(Expr.Eval("[contains(variables('t'), variables('n'))]", context)!.GetValue<bool>());

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public void ManyLongSearches_AreCharged_AgainstTheStepBudget()
    {
        var variables = new Dictionary<string, JsonNode?> { ["t"] = JsonValue.Create(new string('a', 1_000_000)) };
        var context = Context(new EvaluationLimits { MaxSteps = 2_500 }, variables);

        // Each scan of 1,000,000 characters costs about 1,000 steps.
        Fails("[concat(string(lastIndexOf(variables('t'), 'b')), string(lastIndexOf(variables('t'), 'b')), string(lastIndexOf(variables('t'), 'b')))]", DiagnosticCodes.EvaluationBudgetExceeded, context);
    }

    [Fact]
    [Trait("Category", "Timing")]
    public void ALongSplit_ByManyDelimiters_IsRefusedByTheBudgetBeforeItRuns()
    {
        var delimiters = new JsonArray();
        for (int i = 0; i < 100_000; i++)
        {
            delimiters.Add(JsonValue.Create("d" + i));
        }

        var variables = new Dictionary<string, JsonNode?> { ["t"] = JsonValue.Create(new string('a', 1_000_000)), ["d"] = delimiters };
        var context = Context(variables: variables);
        var stopwatch = Stopwatch.StartNew();

        Fails("[length(split(variables('t'), variables('d')))]", DiagnosticCodes.EvaluationBudgetExceeded, context);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public void Base64_OfAnAlmostMaximumString_IsRefused_NotAllocated()
    {
        var variables = new Dictionary<string, JsonNode?> { ["t"] = JsonValue.Create(new string('a', 900_000)) };

        Fails("[base64(variables('t'))]", DiagnosticCodes.EvaluationBudgetExceeded, Context(variables: variables));
        Assert.Equal(4 * ((100_000 + 2) / 3), Expr.AssertString(Expr.Eval("[base64(substring(variables('t'), 0, 100000))]", Context(variables: variables))).Length);
    }

    [Fact]
    public void FlattenOfManyBigArrays_IsRefusedBeforeCopying()
    {
        var inner = new JsonArray();
        for (int i = 0; i < 90_000; i++)
        {
            inner.Add(JsonValue.Create(i));
        }

        var outer = new JsonArray();
        for (int i = 0; i < 11; i++)
        {
            outer.Add(inner.DeepClone());
        }

        var variables = new Dictionary<string, JsonNode?> { ["a"] = outer };

        Fails("[length(flatten(variables('a')))]", DiagnosticCodes.EvaluationBudgetExceeded, Context(variables: variables));
    }

    [Fact]
    public void StringOfAHugeValue_IsRefusedBeforeItIsSerialised()
    {
        var items = new JsonArray();
        for (int i = 0; i < 20_000; i++)
        {
            items.Add(JsonValue.Create(new string('x', 100)));
        }

        var variables = new Dictionary<string, JsonNode?> { ["a"] = items };

        Fails("[string(variables('a'))]", DiagnosticCodes.EvaluationBudgetExceeded, Context(variables: variables));
    }
}
