using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Expressions;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Values;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Review 1C, findings I-04 and S-04: nothing bounded the work or the size of what an expression produced, so a few lines of template could
/// take 8-13 GB (<c>concat(v, v)</c> doubling, nested <c>map(range(...))</c>, a huge <c>format</c> alignment). The run now has an
/// <see cref="EvaluationBudget"/>: size limits per value (1 MiB strings, 100,000 elements per array or object, 1,000,000 nodes per value),
/// a step budget, a node budget and a wall clock, reported as <c>EvaluationBudgetExceeded</c>.
/// </summary>
public sealed class Review1C_BudgetTests
{
    private static EvaluationContext ContextWith(EvaluationLimits? limits = null)
    {
        return Expr.Context() with { Budget = new EvaluationBudget(limits) };
    }

    private static ExpressionEvaluationException Fails(string expression, EvaluationContext? context = null)
    {
        return Assert.Throws<ExpressionEvaluationException>(() => Expr.Eval(expression, context ?? ContextWith()));
    }

    // ---- sizes ----

    [Fact]
    public void AStringBeyondOneMebibyte_IsRefused_ByConcat()
    {
        // 2^20 characters is the limit: the sixteenth doubling of 16 characters reaches it, the seventeenth passes it.
        var variables = Enumerable.Range(1, 20).ToDictionary(i => "v" + i, i => (JsonNode?)JsonValue.Create("[concat(variables('v" + (i - 1) + "'), variables('v" + (i - 1) + "'))]"));
        variables["v0"] = JsonValue.Create(new string('a', 16));
        var withVariables = Expr.Context(variables: variables);

        Assert.Equal(1 << 20, ((string?)Expr.Eval("[variables('v16')]", withVariables))!.Length);
        var error = Fails("[variables('v17')]", withVariables);
        Assert.Equal(DiagnosticCodes.EvaluationBudgetExceeded, error.Code);
        Assert.Contains("1 MiB", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHugeFormatAlignment_IsRefusedBeforeAnythingIsAllocated()
    {
        var error = Fails("[format('{0,1000000000}', 'x')]");

        Assert.Equal(DiagnosticCodes.EvaluationBudgetExceeded, error.Code);
        Assert.Contains("1 MiB", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[format('{0,1048576}', 'x')]", true)]
    [InlineData("[format('{0,1048577}', 'x')]", false)]
    [InlineData("[format('{0,-1048576}', 'x')]", true)]
    [InlineData("[format('{{{0,5}}}', 'x')]", true)]
    public void FormatAlignments_AreHeldToTheStringLimit_ToTheCharacter(string expression, bool allowed)
    {
        if (allowed)
        {
            Assert.NotNull(Expr.Eval(expression, ContextWith()));
        }
        else
        {
            Assert.Equal(DiagnosticCodes.EvaluationBudgetExceeded, Fails(expression).Code);
        }
    }

    [Fact]
    public void Replace_ThatWouldGrowPastTheLimit_IsRefused_AndOneThatShrinksIsNot()
    {
        var variables = new System.Collections.Generic.Dictionary<string, JsonNode?>
        {
            ["big"] = JsonValue.Create(new string('a', 600_000)),
        };
        var context = Expr.Context(variables: variables);

        Assert.Equal(DiagnosticCodes.EvaluationBudgetExceeded, Fails("[replace(variables('big'), 'a', 'bb')]", context).Code);
        Assert.Equal(600_000, ((string?)Expr.Eval("[replace(variables('big'), 'a', 'b')]", context))!.Length);
        Assert.Equal(300_000, ((string?)Expr.Eval("[replace(variables('big'), 'aa', 'b')]", context))!.Length);
    }

    [Fact]
    public void Join_AndArrayConcat_AreHeldToTheirLimits()
    {
        var variables = new System.Collections.Generic.Dictionary<string, JsonNode?>
        {
            ["words"] = new JsonArray(Enumerable.Range(0, 10).Select(_ => (JsonNode?)JsonValue.Create(new string('w', 150_000))).ToArray()),
            ["hundredK"] = new JsonArray(Enumerable.Range(0, 100_000).Select(i => (JsonNode?)JsonValue.Create((long)i)).ToArray()),
        };
        var context = Expr.Context(variables: variables);

        Assert.Equal(DiagnosticCodes.EvaluationBudgetExceeded, Fails("[join(variables('words'), ',')]", context).Code);
        Assert.Equal(100_000, ((JsonArray)Expr.Eval("[concat(variables('hundredK'))]", context)!).Count);
        Assert.Equal(DiagnosticCodes.EvaluationBudgetExceeded, Fails("[concat(variables('hundredK'), createArray(1))]", context).Code);
        Assert.Equal(DiagnosticCodes.EvaluationBudgetExceeded, Fails("[union(variables('hundredK'), range(100000, 10))]", context).Code);
    }

    [Fact]
    public void AValueWithMoreThanAMillionNodes_IsRefused()
    {
        // 1,000 arrays of 1,000 numbers: one million leaves plus the containers is past the node limit of a single value.
        var context = Expr.Context();
        var error = Fails("[map(range(0, 1000), lambda('i', range(0, 1000)))]", context);

        Assert.Equal(DiagnosticCodes.EvaluationBudgetExceeded, error.Code);
        Assert.Contains("nodes", error.Message, StringComparison.Ordinal);
    }

    // ---- work ----

    [Fact]
    public void TheStepBudget_StopsAnEndlessLoopOfLambdaIterations_AndStaysTripped()
    {
        var context = ContextWith(new EvaluationLimits { MaxSteps = 2_000 });

        var error = Fails("[map(range(0, 1000), lambda('i', map(range(0, 1000), lambda('j', add(lambdaVariables('i'), lambdaVariables('j'))))))]", context);

        Assert.Equal(DiagnosticCodes.EvaluationBudgetExceeded, error.Code);
        Assert.Contains("steps", error.Message, StringComparison.Ordinal);
        Assert.True(context.Budget.IsExceeded);

        // A trivial expression fails fast too: the run is over.
        Assert.Equal(DiagnosticCodes.EvaluationBudgetExceeded, Fails("[add(1, 2)]", context).Code);
    }

    [Fact]
    public void TheNodeBudget_BoundsTheTotalNumberOfProducedElements()
    {
        var context = ContextWith(new EvaluationLimits { MaxNodes = 50_000 });

        var error = Fails("[map(range(0, 100), lambda('i', range(0, 1000)))]", context);

        Assert.Equal(DiagnosticCodes.EvaluationBudgetExceeded, error.Code);
        Assert.Contains("in total", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Timing")]
    public void TheWallClockBudget_StopsALongEvaluation()
    {
        var context = ContextWith(new EvaluationLimits { Timeout = TimeSpan.FromMilliseconds(150), MaxSteps = long.MaxValue, MaxNodes = long.MaxValue });
        var watch = Stopwatch.StartNew();

        var error = Fails("[reduce(range(0, 10000), 0, lambda('acc', 'x', reduce(range(0, 10000), 0, lambda('a', 'b', add(lambdaVariables('a'), 1)))))]", context);

        Assert.Equal(DiagnosticCodes.EvaluationBudgetExceeded, error.Code);
        Assert.Contains("seconds", error.Message, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(120), "stopped after " + watch.Elapsed);
    }

    [Fact]
    public void ATimeoutOfZero_DisablesTheClock_AndTheStepLimitStillApplies()
    {
        var context = ContextWith(new EvaluationLimits { Timeout = TimeSpan.Zero, MaxSteps = 500 });

        Assert.Equal(DiagnosticCodes.EvaluationBudgetExceeded, Fails("[map(range(0, 1000), lambda('i', add(lambdaVariables('i'), 1)))]", context).Code);
    }

    [Theory]
    [InlineData("60", 60.0, true)]
    [InlineData("0", 0.0, true)]
    [InlineData(" 2.5 ", 2.5, true)]
    [InlineData("-1", 0.0, false)]
    [InlineData("abc", 0.0, false)]
    [InlineData("", 0.0, false)]
    [InlineData("NaN", 0.0, false)]
    public void TheTimeoutText_OfTheOptionAndTheEnvironmentVariable_IsParsedStrictly(string text, double seconds, bool valid)
    {
        bool parsed = EvaluationLimits.TryParseTimeout(text, out var timeout);

        Assert.Equal(valid, parsed);
        if (valid)
        {
            Assert.Equal(TimeSpan.FromSeconds(seconds), timeout);
        }
    }

    [Fact]
    public void ARun_SharesOneBudget_WithEveryContextCreatedInsideIt_AndRestoresTheOutsideOnDispose()
    {
        var outside = EvaluationBudget.Current;
        Assert.Null(EvaluationBudget.Active);

        using (var run = EvaluationBudget.BeginRun(new EvaluationLimits { MaxSteps = 7 }))
        {
            Assert.Same(run.Budget, EvaluationBudget.Active);
            var first = Expr.Context();
            var second = Expr.Context();

            Assert.Same(run.Budget, first.Budget);
            Assert.Same(run.Budget, second.Budget);
            Assert.Same(run.Budget, (first with { Scope = EvaluationScope.Output }).Budget);
        }

        Assert.Null(EvaluationBudget.Active);
        Assert.NotSame(outside, EvaluationBudget.Current);
    }

    // ---- union / intersection are linear ----

    [Fact]
    [Trait("Category", "Timing")]
    public void Union_OfTwoLargeArrays_IsLinear_AndKeepsFirstOccurrenceOrder()
    {
        var left = new JsonArray(Enumerable.Range(0, 60_000).Select(i => (JsonNode?)JsonValue.Create((long)i)).ToArray());
        var right = new JsonArray(Enumerable.Range(30_000, 60_000).Select(i => (JsonNode?)JsonValue.Create((long)i)).ToArray());
        var context = Expr.Context(variables: new System.Collections.Generic.Dictionary<string, JsonNode?> { ["l"] = left, ["r"] = right });
        var watch = Stopwatch.StartNew();

        var union = (JsonArray)Expr.Eval("[union(variables('l'), variables('r'))]", context)!;

        Assert.Equal(90_000, union.Count);
        Assert.Equal(0L, union[0]!.GetValue<long>());
        Assert.Equal(89_999L, union[^1]!.GetValue<long>());
        // The charge is linear in the elements (one step each); the wall-clock bound only has to separate "linear" from "quadratic" (minutes), so it is generous.
        Assert.InRange(context.Budget.Steps, 120_000, 120_000 * 4);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(120), "union took " + watch.Elapsed);
    }

    [Fact]
    [Trait("Category", "Timing")]
    public void Intersection_OfLargeArrays_IsLinear()
    {
        var left = new JsonArray(Enumerable.Range(0, 80_000).Select(i => (JsonNode?)JsonValue.Create((long)i)).ToArray());
        var right = new JsonArray(Enumerable.Range(40_000, 80_000).Select(i => (JsonNode?)JsonValue.Create((long)i)).ToArray());
        var context = Expr.Context(variables: new System.Collections.Generic.Dictionary<string, JsonNode?> { ["l"] = left, ["r"] = right });
        var watch = Stopwatch.StartNew();

        var common = (JsonArray)Expr.Eval("[intersection(variables('l'), variables('r'))]", context)!;

        Assert.Equal(40_000, common.Count);
        Assert.Equal(40_000L, common[0]!.GetValue<long>());
        Assert.InRange(context.Budget.Steps, 160_000, 160_000 * 4);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(120), "intersection took " + watch.Elapsed);
    }

    [Theory]
    [InlineData("[union(createArray(1, 2), createArray(json('1.0'), 3, 2))]", "[1,2,3]")]
    [InlineData("[union(createArray(createObject('a', 1, 'b', 2)), createArray(createObject('b', 2, 'a', json('1.0'))))]", "[{\"a\":1,\"b\":2}]")]
    [InlineData("[union(createArray('a', 'A'), createArray('a'))]", "[\"a\",\"A\"]")]
    [InlineData("[union(createArray(createArray(1, 2)), createArray(createArray(2, 1)), createArray(createArray(1, 2)))]", "[[1,2],[2,1]]")]
    [InlineData("[union(createArray(true(), 1), createArray(true(), 'true'))]", "[true,1,\"true\"]")]
    [InlineData("[intersection(createArray(1, 1, 2, 3), createArray(3, json('1.0')), createArray(1, 3, 4))]", "[1,3]")]
    [InlineData("[intersection(createArray(createObject('k', createArray(1, 2))), createArray(createObject('k', createArray(1, 2))))]", "[{\"k\":[1,2]}]")]
    [InlineData("[intersection(createArray('x'), createArray('X'))]", "[]")]
    public void SetFunctions_KeepTheirSemantics(string expression, string expected)
    {
        Assert.Equal(expected, Expr.Eval(expression, ContextWith())!.ToJsonString());
    }

    // ---- unknown symbols cannot grow without bound ----

    [Fact]
    public void AnUnknownSymbol_IsCappedInLength_AndStaysDistinguishable()
    {
        string longSymbol = "concat(" + new string('x', 5_000) + ")";
        string otherSymbol = "concat(" + new string('x', 5_000) + "y)";

        var first = UnknownJson.Create(longSymbol, UnknownOrigins.Parameter);
        var second = UnknownJson.Create(otherSymbol, UnknownOrigins.Parameter);
        var again = UnknownJson.Create(longSymbol, UnknownOrigins.Parameter);

        Assert.True(UnknownJson.IsUnknown(first, out var firstValue));
        Assert.True(UnknownJson.IsUnknown(second, out var secondValue));
        Assert.True(UnknownJson.IsUnknown(again, out var againValue));
        Assert.True(firstValue.Symbol.Length <= UnknownJson.MaxSymbolLength);
        Assert.NotEqual(firstValue.Symbol, secondValue.Symbol);
        Assert.Equal(firstValue.Symbol, againValue.Symbol);
        Assert.Equal("short", UnknownJson.IsUnknown(UnknownJson.Create("short", UnknownOrigins.Parameter), out var shortValue) ? shortValue.Symbol : null);
    }

    [Fact]
    [Trait("Category", "Timing")]
    public void DoublingAnUnknownThroughVariables_DoesNotExplode()
    {
        // Without a value for p, parameters('p') is unknown; concat(x, x) over it doubled the symbol at every level (2^40 characters here).
        var sb = new System.Text.StringBuilder("\"parameters\": { \"p\": { \"type\": \"string\" } }, \"variables\": { \"v0\": \"[parameters('p')]\"");
        for (int i = 1; i <= 40; i++)
        {
            sb.Append(", \"v").Append(i).Append("\": \"[concat(variables('v").Append(i - 1).Append("'), variables('v").Append(i - 1).Append("'))]\"");
        }

        sb.Append(" }, \"resources\": []");
        var watch = Stopwatch.StartNew();

        var diagnostics = Review1C.Validate(Review1C.Template(sb.ToString()));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(120), "took " + watch.Elapsed);
        Assert.DoesNotContain(diagnostics, d => d.Code is DiagnosticCodes.EvaluationBudgetExceeded or DiagnosticCodes.ExpressionEvaluationFailed);
    }

    // ---- a failed variable is not evaluated again for every reader ----

    [Fact]
    [Trait("Category", "Timing")]
    public void AChainOfFailingVariables_IsLinear_AndEveryOneReportsItsError()
    {
        var sb = new System.Text.StringBuilder("\"variables\": { \"v0\": \"[notAFunction()]\"");
        for (int i = 1; i <= 3_000; i++)
        {
            sb.Append(", \"v").Append(i).Append("\": \"[variables('v").Append(i - 1).Append("')]\"");
        }

        sb.Append(" }, \"resources\": []");
        var watch = Stopwatch.StartNew();

        var diagnostics = Review1C.Validate(Review1C.Template(sb.ToString()));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(120), "took " + watch.Elapsed);
        Assert.Equal(3_001, diagnostics.Count(d => d.Code == DiagnosticCodes.UnknownFunction && d.Path!.ToString().StartsWith("variables.", StringComparison.Ordinal)));
    }
}
