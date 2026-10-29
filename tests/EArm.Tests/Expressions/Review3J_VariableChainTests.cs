using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Diagnostics;
using EArm.Expressions;
using EArm.Expressions.Evaluation;
using EArm.TestSupport;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Review 3J, finding 1: a chain of about 400 variables, each reading the previous one, hung what-if/create (0 CPU, no diagnostic): the stack
/// guard fired around depth 300-350 and the unwind through a lock plus a catch-and-rethrow per level never finished. Variable chains are now
/// bounded by <see cref="LazyVariables.MaxChainDepth"/> with a plain counter, and a failing chain reports <c>VariableChainTooDeep</c>.
/// </summary>
public sealed class Review3J_VariableChainTests
{
    private static Dictionary<string, JsonNode?> Chain(int length)
    {
        var variables = new Dictionary<string, JsonNode?> { ["v0"] = JsonValue.Create("x") };
        for (int i = 1; i <= length; i++)
        {
            variables["v" + i.ToString(CultureInfo.InvariantCulture)] = JsonValue.Create("[concat(variables('v" + (i - 1).ToString(CultureInfo.InvariantCulture) + "'), 'a')]");
        }

        return variables;
    }

    /// <summary>Evaluates the last variable of a chain on a worker thread; the test fails (rather than hangs) when it does not finish in 120 seconds.</summary>
    private static (JsonNode? Value, ExpressionException? Error, TimeSpan Elapsed) Run(int length)
    {
        var stopwatch = Stopwatch.StartNew();
        var task = Task.Factory.StartNew(
            () =>
            {
                try
                {
                    var context = Expr.Context(variables: Chain(length));
                    return ((JsonNode?)Expr.Eval("[variables('v" + length.ToString(CultureInfo.InvariantCulture) + "')]", context), (ExpressionException?)null);
                }
                catch (ExpressionException ex)
                {
                    return (null, ex);
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        // The hang guard: generous, so a loaded machine does not trip it (the evaluation itself is a few milliseconds).
        Assert.True(task.Wait(TimeSpan.FromSeconds(120)), $"A chain of {length} variables did not finish within 120 seconds.");
        var (value, error) = task.Result;
        return (value, error, stopwatch.Elapsed);
    }

    [Theory]
    [InlineData(250)]
    [InlineData(340)]
    [InlineData(400)]
    [InlineData(1000)]
    [InlineData(2000)]
    public void ALongChain_FinishesWithADiagnostic_NeverHangs(int length)
    {
        var (value, error, _) = Run(length);

        Assert.Null(value);
        Assert.NotNull(error);
        Assert.Equal(DiagnosticCodes.VariableChainTooDeep, error.Code);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(100)]
    public void AChainWithinTheLimit_Evaluates(int length)
    {
        var (value, error, _) = Run(length);

        Assert.Null(error);
        Assert.Equal(1 + length, Expr.AssertString(value).Length);
    }

    [Fact]
    public void TheLimit_IsOneHundredAndTwentyEight_ToTheVariable()
    {
        Assert.Equal(128, LazyVariables.MaxChainDepth);

        var (okValue, okError, _) = Run(LazyVariables.MaxChainDepth - 1);
        Assert.Null(okError);
        Assert.Equal(LazyVariables.MaxChainDepth, Expr.AssertString(okValue).Length);

        var (_, error, _) = Run(LazyVariables.MaxChainDepth);
        Assert.NotNull(error);
        Assert.Equal(DiagnosticCodes.VariableChainTooDeep, error.Code);
    }

    [Fact]
    public void AFailedChain_FailsTheSameWay_ForEveryLaterReader_AtACostBoundedByTheLimit()
    {
        var context = Expr.Context(variables: Chain(500));
        var first = Assert.Throws<ExpressionEvaluationException>(() => Expr.Eval("[variables('v500')]", context));
        long afterFirst = context.Budget.Steps;
        Assert.True(afterFirst >= 100, $"the first failure walked the chain and was charged for it ({afterFirst} steps)");
        for (int i = 0; i < 50; i++)
        {
            var again = Assert.Throws<ExpressionEvaluationException>(() => Expr.Eval("[variables('v500')]", context));
            Assert.Equal(first.Code, again.Code);
        }

        // Every later read walks at most the 128 levels the limit allows before it fails (two steps per level), however long the declared chain is: the cost of a
        // read is bounded by the limit, never by the chain (500 here), and 50 reads are 50 times that bound, not an unbounded re-evaluation. (A failed intermediate variable
        // is not memoised, so a read costs about as much as the first one: bounded, not free.)
        long rereads = context.Budget.Steps - afterFirst;
        long perRead = 2L * LazyVariables.MaxChainDepth + 8;
        Assert.True(rereads <= 50 * perRead, $"50 later reads were charged {rereads} steps (the first read: {afterFirst}; bound per read {perRead})");
    }

    [Fact]
    public void ACycle_IsStillReportedAsACycle()
    {
        var variables = new Dictionary<string, JsonNode?>
        {
            ["a"] = JsonValue.Create("[variables('b')]"),
            ["b"] = JsonValue.Create("[variables('a')]"),
        };
        var context = Expr.Context(variables: variables);

        Expr.Fails("[variables('a')]", DiagnosticCodes.VariableCycle, context);
    }

    [Fact]
    public void AVariableThatIsReadByManyOthers_IsEvaluatedOnce_AndMayBeDeepestAtTheLimit()
    {
        var variables = Chain(100);
        variables["wide"] = JsonValue.Create("[concat(variables('v100'), variables('v100'), variables('v50'))]");
        var context = Expr.Context(variables: variables);

        Assert.Equal(101 + 101 + 51, Expr.AssertString(Expr.Eval("[variables('wide')]", context)).Length);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(2000)]
    public void AChainOfParameterDefaults_FinishesWithADiagnostic_NeverHangs(int length)
    {
        var builder = new System.Text.StringBuilder("\"parameters\": { \"p0\": { \"type\": \"string\", \"defaultValue\": \"x\" }");
        for (int i = 1; i <= length; i++)
        {
            builder.Append(CultureInfo.InvariantCulture, $", \"p{i}\": {{ \"type\": \"string\", \"defaultValue\": \"[concat(parameters('p{i - 1}'), 'a')]\" }}");
        }

        builder.Append(" }, \"resources\": []");
        var document = Expr.ParseBody(builder.ToString());
        var parameters = new LazyParameters(document.ParameterValues);
        var context = new EvaluationContext { Parameters = parameters }.Bind(Expr.Evaluator);

        var error = OnWorkerThread(
            () =>
            {
                try
                {
                    parameters.Get("p" + length.ToString(CultureInfo.InvariantCulture));
                    return (ExpressionException?)null;
                }
                catch (ExpressionException ex)
                {
                    return ex;
                }
            });

        Assert.NotNull(error);
        Assert.Equal(DiagnosticCodes.ExpressionTooDeep, error.Code);
    }

    /// <summary>
    /// Runs <paramref name="work"/> on its own thread with a fixed 1 MiB stack and fails the test (instead of hanging it) when it does not finish within
    /// 30 seconds. The fixed stack keeps the outcome the same on every OS: the guard trips on the remaining stack, and a Linux thread has 8 MiB.
    /// </summary>
    private static T OnWorkerThread<T>(Func<T> work)
    {
        return FixedStackThread.Run(work);
    }
}
