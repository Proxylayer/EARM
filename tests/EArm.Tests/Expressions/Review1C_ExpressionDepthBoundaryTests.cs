using System;
using System.Linq;
using EArm.Core.Diagnostics;
using EArm.Expressions;
using EArm.Expressions.Syntax;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Review 1C, mutation survivor L03: the expression nesting limit (<see cref="ExpressionParser.MaxNestingDepth"/>, 100 levels) had no test at the
/// exact boundary, so an off-by-one in either comparison went unnoticed. The expression itself is level 1 and every nested call argument, index or
/// lambda body adds one: 99 calls around a leaf call make exactly 100 levels (accepted), 100 make 101 (rejected).
/// </summary>
public sealed class Review1C_ExpressionDepthBoundaryTests
{
    private const int Limit = ExpressionParser.MaxNestingDepth;

    /// <summary><paramref name="calls"/> nested <c>or(</c> calls around <paramref name="leaf"/>: <c>calls + 1</c> levels.</summary>
    private static string Calls(int calls, string leaf = "true()")
    {
        return "[" + string.Concat(Enumerable.Repeat("or(", calls)) + leaf + new string(')', calls) + "]";
    }

    private static void AssertAccepted(string expression)
    {
        Assert.NotNull(ExpressionParser.Parse(expression));
    }

    private static void AssertTooDeep(string expression)
    {
        var error = Assert.Throws<ExpressionParseException>(() => ExpressionParser.Parse(expression));
        Assert.Equal(DiagnosticCodes.ExpressionTooDeep, error.Code);
        Assert.Contains("maximum depth of 100", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLimit_IsOneHundred()
    {
        Assert.Equal(100, Limit);
    }

    [Theory]
    [InlineData("true()")]
    [InlineData("1")]
    [InlineData("'x'")]
    public void NinetyNineNestedCalls_AroundAnyLeaf_AreExactlyAtTheLimit_AndAHundredAreOver(string leaf)
    {
        AssertAccepted(Calls(Limit - 2, leaf));
        AssertAccepted(Calls(Limit - 1, leaf));
        AssertTooDeep(Calls(Limit, leaf));
        AssertTooDeep(Calls(Limit + 1, leaf));
    }

    [Fact]
    public void AChainOfIndexers_IsLimitedByItsLinks()
    {
        // x[0][0]...: each link is one level on top of the expression's own; 99 links make 100 levels.
        string Chain(int links)
        {
            return "[variables('a')" + string.Concat(Enumerable.Repeat("[0]", links)) + "]";
        }

        AssertAccepted(Chain(Limit - 2));
        AssertAccepted(Chain(Limit - 1));
        AssertTooDeep(Chain(Limit));
        AssertTooDeep(Chain(Limit + 1));
    }

    [Fact]
    public void AChainOfPropertyAccesses_IsLimitedByItsLinks()
    {
        string Chain(int links)
        {
            return "[variables('a')" + string.Concat(Enumerable.Repeat(".p", links)) + "]";
        }

        AssertAccepted(Chain(Limit - 2));
        AssertAccepted(Chain(Limit - 1));
        AssertTooDeep(Chain(Limit));
        AssertTooDeep(Chain(Limit + 1));
    }

    [Fact]
    public void ALinkChain_InsideACall_CountsTheCallToo()
    {
        // or(variables('a')[0]...): the call is level 1, its argument level 2, then one level per link.
        string Chain(int links)
        {
            return "[or(variables('a')" + string.Concat(Enumerable.Repeat("[0]", links)) + ")]";
        }

        AssertAccepted(Chain(Limit - 3));
        AssertAccepted(Chain(Limit - 2));
        AssertTooDeep(Chain(Limit - 1));
        AssertTooDeep(Chain(Limit));
    }

    [Fact]
    public void LambdaBodies_AndIndexExpressions_CountLikeCalls()
    {
        string Lambdas(int depth)
        {
            // map(x, lambda('e', map(x, lambda('e', ... 1 ...)))): each map call and each lambda body adds a level.
            string body = "1";
            for (int i = 0; i < depth; i++)
            {
                body = "map(createArray(1), lambda('e', " + body + "))";
            }

            return "[" + body + "]";
        }

        AssertAccepted(Lambdas(10));
        AssertTooDeep(Lambdas(Limit));

        // The exact edge for lambdas: find it by search, then pin both sides of it.
        int accepted = Enumerable.Range(1, Limit).Last(d => TryParse(Lambdas(d)));
        Assert.True(accepted is > 20 and < Limit, "the largest accepted lambda nesting is " + accepted);
        AssertAccepted(Lambdas(accepted));
        AssertTooDeep(Lambdas(accepted + 1));
    }

    private static bool TryParse(string expression)
    {
        try
        {
            ExpressionParser.Parse(expression);
            return true;
        }
        catch (ExpressionParseException)
        {
            return false;
        }
    }

    [Fact]
    public void AnExpressionAtTheLimit_Evaluates_AndOneBeyondIsADiagnosticFromTheValidator()
    {
        Assert.Equal("true", Expr.Eval(Calls(Limit - 1))!.ToJsonString());

        string over = Review1C.Template("\"variables\": { \"deep\": " + System.Text.Json.JsonSerializer.Serialize(Calls(Limit)) + " }, \"resources\": []");
        string at = Review1C.Template("\"variables\": { \"deep\": " + System.Text.Json.JsonSerializer.Serialize(Calls(Limit - 1)) + " }, \"resources\": []");

        Assert.Contains(Review1C.Validate(over), d => d.Code == DiagnosticCodes.ExpressionTooDeep && d.Path!.ToString() == "variables.deep");
        Assert.DoesNotContain(Review1C.Validate(at), d => d.Code == DiagnosticCodes.ExpressionTooDeep);
    }
}
