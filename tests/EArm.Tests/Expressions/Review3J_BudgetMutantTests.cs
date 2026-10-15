using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading;
using EArm.Core.Diagnostics;
using EArm.Expressions;
using EArm.Expressions.Evaluation;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Review 3J, finding 12 (mutant-driven tests, no product change): the boundaries of <see cref="EvaluationBudget"/> (EB01, EB02, EB03, EB07, EB10,
/// EB17, EB19) and of the new retained-memory and text-work counters. A limit that is reached exactly passes; one more throws.
/// </summary>
public sealed class Review3J_BudgetMutantTests
{
    private static ExpressionEvaluationException Exceeded(Action action)
    {
        var error = Assert.Throws<ExpressionEvaluationException>(action);
        Assert.Equal(DiagnosticCodes.EvaluationBudgetExceeded, error.Code);
        return error;
    }

    private static JsonArray Leaves(int count)
    {
        var array = new JsonArray();
        for (int i = 0; i < count; i++)
        {
            array.Add(JsonValue.Create(i));
        }

        return array;
    }

    // ---- EB01: steps ----

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(300)]
    public void TheStepLimit_IsReachedExactly_AndOneMoreThrows(int limit)
    {
        var budget = new EvaluationBudget(new EvaluationLimits { MaxSteps = limit });

        for (int i = 0; i < limit; i++)
        {
            budget.Step();
        }

        Assert.Equal(limit, budget.Steps);
        var error = Exceeded(() => budget.Step());
        Assert.Contains(limit.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " steps", error.Message, StringComparison.Ordinal);
        Assert.True(budget.IsExceeded);
    }

    [Fact]
    public void AChargeOfSeveralSteps_ThatLandsOnTheLimit_Passes_AndOneMoreThrows()
    {
        var budget = new EvaluationBudget(new EvaluationLimits { MaxSteps = 100 });

        budget.Charge(100);
        Exceeded(() => budget.Charge(1));
        Assert.True(budget.IsExceeded);
        Exceeded(() => budget.Step());
    }

    [Fact]
    public void ANonPositiveCharge_CostsNothing()
    {
        var budget = new EvaluationBudget(new EvaluationLimits { MaxSteps = 1 });

        budget.Charge(0);
        budget.Charge(-5);

        Assert.Equal(0, budget.Steps);
    }

    // ---- EB02: nodes (and EB17: the step cost of nodes) ----

    [Fact]
    public void TheNodeLimit_IsReachedExactly_AndOneMoreThrows()
    {
        var budget = new EvaluationBudget(new EvaluationLimits { MaxNodes = 10 });

        budget.CheckValue(Leaves(9));

        Assert.Equal(10, budget.Nodes);
        Exceeded(() => budget.CheckValue(new JsonArray()));
    }

    [Fact]
    public void AValueOfSixteenNodesOrMore_CostsOneStepPerSixteenNodes_AtLeastOne()
    {
        // 160 nodes (the array and 159 leaves) cost 10 steps; a single node costs 1.
        Assert.Equal(10, Steps(Leaves(159)));
        Assert.Equal(1, Steps(Leaves(1)));
        Assert.Equal(1, Steps(new JsonArray()));

        var tight = new EvaluationBudget(new EvaluationLimits { MaxSteps = 9 });
        Exceeded(() => tight.CheckValue(Leaves(159)));
        var exact = new EvaluationBudget(new EvaluationLimits { MaxSteps = 10 });
        exact.CheckValue(Leaves(159));

        static long Steps(JsonNode value)
        {
            var budget = new EvaluationBudget();
            budget.CheckValue(value);
            return budget.Steps;
        }
    }

    // ---- EB03: characters ----

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    public void TheTextLimit_IsReachedExactly_AndOneMoreThrows(int limit)
    {
        var budget = new EvaluationBudget(new EvaluationLimits { MaxStringChars = limit });

        budget.CheckValue(JsonValue.Create(new string('a', limit)));

        var error = Exceeded(() => budget.CheckValue(JsonValue.Create("b")));
        Assert.Contains("characters of text in total", error.Message, StringComparison.Ordinal);
        Assert.True(budget.IsExceeded);
    }

    [Fact]
    public void TheTextOfSeveralValues_AddsUp()
    {
        var budget = new EvaluationBudget(new EvaluationLimits { MaxStringChars = 10 });

        budget.CheckValue(JsonValue.Create("12345"));
        budget.CheckValue(new JsonArray(JsonValue.Create("123"), JsonValue.Create("12")));
        Exceeded(() => budget.CheckValue(JsonValue.Create("1")));
    }

    [Fact]
    public void TheDefaultTextLimit_Is128MebiCharacters_AndTheRetainedLimit256Mebibytes()
    {
        Assert.Equal(128L * 1024 * 1024, EvaluationLimits.Default.MaxStringChars);
        Assert.Equal(256L * 1024 * 1024, EvaluationLimits.Default.MaxRetainedBytes);
    }

    // ---- EB07: the limits of one value ----

    [Fact]
    public void AValueOfExactlyOneMillionNodes_Passes_AndOneMoreThrows()
    {
        // 1 + 27 x (1 + 37,036) = 1,000,000 nodes in 27 arrays of 37,036 leaves each (an array may hold 100,000 elements).
        JsonNode Build(int extra)
        {
            var outer = new JsonArray();
            for (int i = 0; i < 27; i++)
            {
                outer.Add(Leaves(37_036 + (i == 0 ? extra : 0)));
            }

            return outer;
        }

        var budget = new EvaluationBudget();
        budget.CheckValue(Build(0));
        Assert.Equal(1_000_000, budget.Nodes);

        var error = Exceeded(() => new EvaluationBudget().CheckValue(Build(1)));
        Assert.Contains("1,000,000 nodes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnArrayOfExactlyOneHundredThousandElements_Passes_AndOneMoreThrows()
    {
        new EvaluationBudget().CheckValue(Leaves(100_000));

        Exceeded(() => new EvaluationBudget().CheckValue(Leaves(100_001)));
    }

    [Fact]
    public void AStringOfExactlyOneMebibyte_Passes_AndOneMoreThrows()
    {
        new EvaluationBudget().CheckValue(JsonValue.Create(new string('a', EvaluationLimits.MaxStringLength)));

        Exceeded(() => new EvaluationBudget().CheckValue(JsonValue.Create(new string('a', EvaluationLimits.MaxStringLength + 1))));
        new EvaluationBudget().CheckStringLength(EvaluationLimits.MaxStringLength);
        Exceeded(() => new EvaluationBudget().CheckStringLength(EvaluationLimits.MaxStringLength + 1L));
        new EvaluationBudget().CheckCollectionCount(EvaluationLimits.MaxCollectionCount);
        Exceeded(() => new EvaluationBudget().CheckCollectionCount(EvaluationLimits.MaxCollectionCount + 1L));
    }

    // ---- EB10: the clock runs only inside an evaluation ----

    [Fact]
    public void TheClock_IsNotWatched_OutsideAnEvaluationUnit_AndIsInsideOne()
    {
        var budget = new EvaluationBudget(new EvaluationLimits { Timeout = TimeSpan.FromMilliseconds(1) });

        Thread.Sleep(30);
        budget.Charge(10_000);

        using (EvaluationBudget.BeginUnit())
        {
            Thread.Sleep(30);
            var error = Exceeded(() => budget.Charge(10_000));
            Assert.Contains("took longer than", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AZeroTimeout_MeansNoClock()
    {
        var budget = new EvaluationBudget(new EvaluationLimits { Timeout = TimeSpan.Zero });

        using (EvaluationBudget.BeginUnit())
        {
            Thread.Sleep(20);
            budget.Charge(10_000);
        }

        Assert.False(budget.IsExceeded);
    }

    [Fact]
    public void ANestedUnit_DoesNotRestartTheClock()
    {
        var budget = new EvaluationBudget(new EvaluationLimits { Timeout = TimeSpan.FromMilliseconds(50) });

        using (EvaluationBudget.BeginUnit())
        {
            Thread.Sleep(80);
            using (EvaluationBudget.BeginUnit())
            {
                Exceeded(() => budget.Charge(10_000));
            }
        }
    }

    // ---- the text work and the retained memory ----

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(1024, 1)]
    [InlineData(1025, 2)]
    [InlineData(1_048_576, 1024)]
    public void ChargeText_CostsOneStepPer1024Characters_RoundedUp(int characters, int steps)
    {
        var budget = new EvaluationBudget();

        budget.ChargeText(characters);

        Assert.Equal(steps, budget.Steps);
    }

    [Fact]
    public void RetainedMemory_IsCounted_AndTheLimitThrowsAndTripsTheRun()
    {
        var budget = new EvaluationBudget(new EvaluationLimits { MaxRetainedBytes = 10_000 });

        budget.Retain(JsonValue.Create(new string('a', 4_000)));
        var error = Exceeded(() => budget.Retain(JsonValue.Create(new string('b', 1_000))));

        Assert.Contains("bytes in memory", error.Message, StringComparison.Ordinal);
        Assert.True(budget.IsExceeded);
        budget.Retain(null);
    }

    [Fact]
    public void ManyBigVariables_AreStoppedByTheRetainedLimit_NotByTheRunningOutOfMemory()
    {
        var variables = new Dictionary<string, JsonNode?>();
        for (int i = 0; i < 20; i++)
        {
            variables["v" + i] = JsonValue.Create("[format('{0,500000}', 'x')]");
        }

        // The budget must be part of the context before it is bound: the variables evaluate in the context they were bound to.
        var context = new EvaluationContext
        {
            Parameters = Expr.Parameters(),
            Variables = new LazyVariables(variables),
            Budget = new EvaluationBudget(new EvaluationLimits { MaxRetainedBytes = 4_000_000 }),
        }.Bind(Expr.Evaluator);

        // Each variable holds 500,000 characters: about 1,000,048 bytes.
        Expr.Eval("[length(variables('v0'))]", context);
        Expr.Eval("[length(variables('v1'))]", context);
        Expr.Eval("[length(variables('v2'))]", context);
        var error = Assert.Throws<ExpressionEvaluationException>(() => Expr.Eval("[length(variables('v3'))]", context));
        Assert.Equal(DiagnosticCodes.EvaluationBudgetExceeded, error.Code);
    }

    // ---- EB19 ----

    [Theory]
    [InlineData("1e18", false)]
    [InlineData("1e308", false)]
    [InlineData("86400", true)]
    [InlineData("86400.0001", false)]
    [InlineData("NaN", false)]
    [InlineData("Infinity", false)]
    [InlineData("-0", true)]
    [InlineData("-1", false)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("0x10", false)]
    [InlineData("1,5", false)]
    public void TryParseTimeout_RefusesAnythingButZeroTo86400Seconds(string text, bool accepted)
    {
        Assert.Equal(accepted, EvaluationLimits.TryParseTimeout(text, out var timeout));
        if (!accepted)
        {
            Assert.Equal(default, timeout);
        }
    }

    [Fact]
    public void ABudgetWatch_WithAFiniteTimeout_ReportsWhenTheTimeIsUp_InTheCorrectUnit()
    {
        var watch = Stopwatch.StartNew();
        var budget = new EvaluationBudget(new EvaluationLimits { Timeout = TimeSpan.FromMilliseconds(20) });

        using (EvaluationBudget.BeginUnit())
        {
            while (watch.ElapsedMilliseconds < 60)
            {
                Thread.Sleep(5);
            }

            Exceeded(() => budget.Charge(300));
        }
    }
}
