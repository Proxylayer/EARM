using System.Globalization;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Expressions.Evaluation;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Reading part of a parameter (<c>parameters('x')[i]</c>) or of a lambda binding (<c>lambdaVariables('x').y</c>) costs the part, not a copy of
/// the whole value, as reading part of a variable does: a large array parameter read once per copy iteration, or a large element a lambda is
/// bound to, must not use up the run's value budget. The selected part is still a copy, and the rules of the functions are kept.
/// </summary>
public sealed class ParameterAndLambdaReadTests
{
    private static JsonArray Range(int count)
    {
        var array = new JsonArray();
        for (int i = 0; i < count; i++)
        {
            array.Add(i);
        }

        return array;
    }

    [Fact]
    public void ElementReadsOfALargeParameter_ChargeTheElementNotTheParameter()
    {
        using var run = EvaluationBudget.BeginRun(EvaluationLimits.Default);
        var context = Expr.Context(parameters: Expr.Parameters(("big", Range(10_000))));

        for (int i = 0; i < 2_000; i++)
        {
            Assert.Equal(i.ToString(CultureInfo.InvariantCulture), Expr.Render(Expr.Eval($"[parameters('big')[{i}]]", context)));
        }

        // A few nodes per read; copying the parameter for every read would be 20,000,000 (and trip the 5,000,000 budget).
        Assert.True(run.Budget.Nodes < 100_000, $"{run.Budget.Nodes:N0} nodes");
        Assert.False(run.Budget.IsExceeded);
    }

    [Fact]
    public void ElementReadsOfALargeParameterDefault_ChargeTheElementNotTheParameter()
    {
        using var run = EvaluationBudget.BeginRun(EvaluationLimits.Default);
        var document = Expr.ParseBody("\"parameters\": { \"big\": { \"type\": \"array\", \"defaultValue\": \"[range(0, 10000)]\" } }, \"resources\": []");
        var context = new EvaluationContext { Parameters = new LazyParameters(document.ParameterValues) }.Bind(Expr.Evaluator);

        for (int i = 0; i < 2_000; i++)
        {
            Assert.Equal(i.ToString(CultureInfo.InvariantCulture), Expr.Render(Expr.Eval($"[parameters('big')[{i}]]", context)));
        }

        Assert.True(run.Budget.Nodes < 100_000, $"{run.Budget.Nodes:N0} nodes");
        Assert.False(run.Budget.IsExceeded);
    }

    [Fact]
    public void ElementReadsOfALargeLambdaBinding_ChargeTheElementNotTheBinding()
    {
        using var run = EvaluationBudget.BeginRun(EvaluationLimits.Default);
        var context = Expr.Context(parameters: Expr.Parameters(("big", Range(10_000))));

        var result = Expr.Eval("[map(createArray(parameters('big')), lambda('b', map(range(0, 2000), lambda('i', lambdaVariables('b')[lambdaVariables('i')]))))]", context);

        Assert.Equal(2_000, result![0]!.AsArray().Count);
        Assert.Equal("1999", Expr.Render(result[0]![1999]));
        Assert.True(run.Budget.Nodes < 200_000, $"{run.Budget.Nodes:N0} nodes");
        Assert.False(run.Budget.IsExceeded);
    }

    [Fact]
    public void PropertyChainsIntoAParameter_ReturnCopies()
    {
        var context = Expr.Context(parameters: Expr.Parameters(("obj", JsonNode.Parse("""{ "items": [ { "name": "a", "tags": [ "x" ] } ] }"""))));

        var tags = Expr.Eval("[parameters('obj').items[0].tags]", context)!.AsArray();
        tags.Add("mutated");

        Expr.AssertJson("""{ "items": [ { "name": "a", "tags": [ "x" ] } ] }""", Expr.Eval("[parameters('obj')]", context));
        Expr.AssertJson("\"a\"", Expr.Eval("[parameters('OBJ')['ITEMS'][0].Name]", context));
    }

    [Fact]
    public void PropertyChainsIntoALambdaBinding_ReturnCopies()
    {
        var context = Expr.Context(parameters: Expr.Parameters(("rows", JsonNode.Parse("""[ { "name": "a", "tags": [ "x" ] }, { "name": "b", "tags": [ "y" ] } ]"""))));

        Expr.AssertJson("""[ [ "x" ], [ "y" ] ]""", Expr.Eval("[map(parameters('rows'), lambda('r', lambdaVariables('r').tags))]", context));
        Expr.AssertJson("""[ "a", "b" ]""", Expr.Eval("[map(parameters('rows'), lambda('r', lambdaVariables('R')['NAME']))]", context));
        Expr.AssertJson("""[ { "name": "a", "tags": [ "x" ] }, { "name": "b", "tags": [ "y" ] } ]""", Expr.Eval("[parameters('rows')]", context));
    }

    [Fact]
    public void IndexedReads_KeepTheRulesOfParametersAndLambdaVariables()
    {
        var context = Expr.Context(parameters: Expr.Parameters(("p", JsonNode.Parse("[1]"))));

        Expr.Fails("[parameters('missing')[0]]", DiagnosticCodes.UndefinedParameter, context);
        Expr.Fails("[parameters('p')[1]]", DiagnosticCodes.ExpressionEvaluationFailed, context);
        Expr.Fails("[lambdaVariables('x').y]", DiagnosticCodes.FunctionNotAllowedHere, context);
        Expr.Fails("[map(parameters('p'), lambda('x', lambdaVariables('y')[0]))]", DiagnosticCodes.InvalidFunctionArguments, context);
    }
}
