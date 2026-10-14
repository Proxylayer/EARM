using System.Text.Json.Nodes;
using EntraArm.Core.Diagnostics;
using Xunit;

namespace EntraArm.Tests;

public sealed class CollectionFunctionTests
{
    [Theory]
    [InlineData("[createArray()]", "[]")]
    [InlineData("[createArray(1, 'a', createObject())]", "[1,\"a\",{}]")]
    [InlineData("[array('x')]", "[\"x\"]")]
    [InlineData("[array(createArray(1))]", "[[1]]")]
    [InlineData("[array(createObject('a', 1))]", "[{\"a\":1}]")]
    [InlineData("[createObject('a', 1)]", "{\"a\":1}")]
    [InlineData("[createObject()]", "{}")]
    [InlineData("[createObject('a', 1, 'a', 2)]", "{\"a\":2}")]
    [InlineData("[createObject('a', createArray(1), 'b', null())]", "{\"a\":[1],\"b\":null}")]
    [InlineData("[union(createObject('a', 1), createObject('b', 2, 'a', 3))]", "{\"a\":3,\"b\":2}")]
    [InlineData("[union(createArray(1, 2), createArray(2, 3))]", "[1,2,3]")]
    [InlineData("[union(createArray(1, json('1.0')), createArray())]", "[1]")]
    [InlineData("[union(createArray('a'), createArray('A'))]", "[\"a\",\"A\"]")]
    [InlineData("[union(createArray(createObject('k', 1)), createArray(createObject('k', 1)))]", "[{\"k\":1}]")]
    [InlineData("[intersection(createObject('a', 1, 'b', 2), createObject('a', 1, 'b', 3))]", "{\"a\":1}")]
    [InlineData("[intersection(createArray(1, 2, 3), createArray(3, 1))]", "[1,3]")]
    [InlineData("[intersection(createArray(1, 1, 2), createArray(1))]", "[1]")]
    [InlineData("[intersection(createArray(1), createArray(2))]", "[]")]
    [InlineData("[intersection(createArray(1, 2, 3), createArray(2, 3), createArray(3))]", "[3]")]
    [InlineData("[range(1, 3)]", "[1,2,3]")]
    [InlineData("[range(0, 0)]", "[]")]
    [InlineData("[range(-2, 2)]", "[-2,-1]")]
    [InlineData("[take(createArray(1, 2, 3), 2)]", "[1,2]")]
    [InlineData("[take(createArray(1), 5)]", "[1]")]
    [InlineData("[take(createArray(1), -1)]", "[]")]
    [InlineData("[take('abc', 2)]", "\"ab\"")]
    [InlineData("[take('abc', 10)]", "\"abc\"")]
    [InlineData("[skip(createArray(1, 2, 3), 1)]", "[2,3]")]
    [InlineData("[skip(createArray(1), 5)]", "[]")]
    [InlineData("[skip('abc', 1)]", "\"bc\"")]
    [InlineData("[skip('abc', 10)]", "\"\"")]
    [InlineData("[first(createArray(1, 2))]", "1")]
    [InlineData("[last(createArray(1, 2))]", "2")]
    [InlineData("[first(createArray())]", "null")]
    [InlineData("[last(createArray())]", "null")]
    [InlineData("[first('abc')]", "\"a\"")]
    [InlineData("[last('abc')]", "\"c\"")]
    [InlineData("[first('')]", "\"\"")]
    [InlineData("[items(createObject('b', 1, 'a', 2))]", "[{\"key\":\"a\",\"value\":2},{\"key\":\"b\",\"value\":1}]")]
    [InlineData("[items(createObject())]", "[]")]
    [InlineData("[flatten(createArray(createArray(1, 2), 3, createArray(createArray(4))))]", "[1,2,3,[4]]")]
    [InlineData("[flatten(createArray())]", "[]")]
    [InlineData("[json('null')]", "null")]
    [InlineData("[json('{\"a\": [1, 2]}')]", "{\"a\":[1,2]}")]
    [InlineData("[json('\"s\"')]", "\"s\"")]
    [InlineData("[json('1.5')]", "1.5")]
    [InlineData("[json('[1, 2,]')]", "[1,2]")]
    [InlineData("[json('true')]", "true")]
    [InlineData("[objectKeys(createObject('a', 1, 'b', 2))]", "[\"a\",\"b\"]")]
    [InlineData("[objectKeys(createObject())]", "[]")]
    [InlineData("[createArray(1, 2)[1]]", "2")]
    [InlineData("[createObject('k', 1).K]", "1")]
    [InlineData("[createObject('k', 1)['K']]", "1")]
    [InlineData("[createObject('k', createArray('a'))['k'][0]]", "\"a\"")]
    [InlineData("[json('{\"a\":{\"b\":[0,{\"c\":7}]}}').a.b[1].c]", "7")]
    [InlineData("[length(range(0, 10000))]", "10000")]
    [InlineData("[contains(items(createObject('a', 1))[0], 'KEY')]", "true")]
    public void Evaluates(string expression, string expectedJson)
    {
        Expr.AssertJson(expectedJson, Expr.Eval(expression));
    }

    [Theory]
    [InlineData("[createObject('a')]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[createObject(1, 'a')]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[union(createObject('a', 1), createArray())]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[union('a', 'b')]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[intersection(createArray(1), createObject())]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[range(0, 10001)]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[range(0, -1)]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[range('a', 1)]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[take(createArray(), 'x')]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[take(1, 1)]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[first(1)]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[items(createArray())]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[objectKeys(1)]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[flatten('a')]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[json('{bad')]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[json(1)]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[createArray(1)[5]]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[createArray(1)[-1]]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[createArray(1)['a']]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[createObject('a', 1).b]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[createObject('a', 1)[1]]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("['abc'.length]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[createArray(1).x]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[true()[0]]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[null().x]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[array()]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[null(1)]", DiagnosticCodes.InvalidFunctionArguments)]
    public void Fails(string expression, string code)
    {
        Expr.Fails(expression, code);
    }

    [Fact]
    public void Range_ThatOverflowsSixtyFourBits_IsAnEvaluationFailure()
    {
        // Every other checked arithmetic (add/mul/sub, copyIndex offsets) surfaces as ExpressionEvaluationFailed;
        // range() must not let a raw OverflowException escape past the validator's ExpressionException handling.
        Expr.Fails("[range(9223372036854775807, 2)]", DiagnosticCodes.ExpressionEvaluationFailed);
    }

    [Fact]
    public void Constructors_CopyTheirArguments()
    {
        var parameters = Expr.Parameters(("obj", new JsonObject { ["k"] = "v" }));
        var context = Expr.Context(parameters);

        var array = Assert.IsType<JsonArray>(Expr.Eval("[createArray(parameters('obj'))]", context));
        array[0]!["k"] = "changed";

        Assert.Equal("v", parameters["obj"]!["k"]!.GetValue<string>());
        Assert.Equal("v", Expr.AssertString(Expr.Eval("[parameters('obj').k]", context)));
    }

    [Fact]
    public void Json_AcceptsCommentsAndTrailingCommas()
    {
        Expr.AssertJson("{\"a\":1}", Expr.Eval("[json('{ /* c */ \"a\": 1, }')]"));
    }
}
