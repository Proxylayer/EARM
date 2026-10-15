using EArm.Core.Diagnostics;
using Xunit;

namespace EArm.Tests;

public sealed class LogicNumericFunctionTests
{
    [Theory]
    [InlineData("[if(true(), 'a', div(1, 0))]", "\"a\"")]
    [InlineData("[if(false(), div(1, 0), 'b')]", "\"b\"")]
    [InlineData("[if(equals(1, 1), createArray(1), createObject())]", "[1]")]
    [InlineData("[and(true(), false())]", "false")]
    [InlineData("[and(true(), true())]", "true")]
    [InlineData("[and(true())]", "true")]
    [InlineData("[or(false(), true())]", "true")]
    [InlineData("[or(false())]", "false")]
    [InlineData("[not(false())]", "true")]
    [InlineData("[not(true())]", "false")]
    [InlineData("[equals(1, json('1.0'))]", "true")]
    [InlineData("[equals(json('1.5'), json('1.50'))]", "true")]
    [InlineData("[equals('a', 'A')]", "false")]
    [InlineData("[equals('a', 'a')]", "true")]
    [InlineData("[equals(createObject('a', 1), createObject('A', 1))]", "false")]
    [InlineData("[equals(createObject('a', 1, 'b', 2), createObject('b', 2, 'a', 1))]", "true")]
    [InlineData("[equals(createArray(1, 2), createArray(2, 1))]", "false")]
    [InlineData("[equals(createArray(1, 2), createArray(1, 2))]", "true")]
    [InlineData("[equals(null(), null())]", "true")]
    [InlineData("[equals(1, '1')]", "false")]
    [InlineData("[equals(true(), true())]", "true")]
    [InlineData("[equals(true(), 1)]", "false")]
    [InlineData("[less(1, 2)]", "true")]
    [InlineData("[less(2, 1)]", "false")]
    [InlineData("[less('a', 'b')]", "true")]
    [InlineData("[less('B', 'a')]", "true")]
    [InlineData("[less(json('1.5'), 2)]", "true")]
    [InlineData("[lessOrEquals(2, 2)]", "true")]
    [InlineData("[greater(json('1.5'), 1)]", "true")]
    [InlineData("[greater(1, 2)]", "false")]
    [InlineData("[greaterOrEquals('b', 'a')]", "true")]
    [InlineData("[greaterOrEquals(1, 2)]", "false")]
    [InlineData("[coalesce(null(), null())]", "null")]
    [InlineData("[coalesce(null(), 'x')]", "\"x\"")]
    [InlineData("[coalesce('a', 'b')]", "\"a\"")]
    [InlineData("[coalesce(null(), createArray())]", "[]")]
    [InlineData("[bool('TRUE')]", "true")]
    [InlineData("[bool(' false ')]", "false")]
    [InlineData("[bool(2)]", "true")]
    [InlineData("[bool(0)]", "false")]
    [InlineData("[bool(-1)]", "true")]
    [InlineData("[bool(false())]", "false")]
    [InlineData("[int('42 ')]", "42")]
    [InlineData("[int('1.0')]", "1")]
    [InlineData("[int('-3')]", "-3")]
    [InlineData("[int(7)]", "7")]
    [InlineData("[float('1e3')]", "1000")]
    [InlineData("[float('2.5')]", "2.5")]
    [InlineData("[float(2)]", "2")]
    [InlineData("[add(1, 2)]", "3")]
    [InlineData("[add(json('1.5'), 1)]", "2.5")]
    [InlineData("[sub(5, 7)]", "-2")]
    [InlineData("[sub(json('0.5'), json('0.25'))]", "0.25")]
    [InlineData("[mul(json('1.5'), 2)]", "3")]
    [InlineData("[mul(3, 4)]", "12")]
    [InlineData("[div(7, 2)]", "3")]
    [InlineData("[div(-7, 2)]", "-3")]
    [InlineData("[div(json('7.0'), 2)]", "3.5")]
    [InlineData("[mod(-7, 3)]", "-1")]
    [InlineData("[mod(7, 3)]", "1")]
    [InlineData("[mod(json('7.5'), 2)]", "1.5")]
    [InlineData("[min(1, 5, -2)]", "-2")]
    [InlineData("[min(4)]", "4")]
    [InlineData("[max(createArray(1, 9, 3))]", "9")]
    [InlineData("[max(json('1.5'), 1)]", "1.5")]
    [InlineData("[max(createArray(json('0.5'), json('0.25')))]", "0.5")]
    [InlineData("[min(createArray(3, json('2.5')))]", "2.5")]
    [InlineData("[true()]", "true")]
    [InlineData("[false()]", "false")]
    [InlineData("[null()]", "null")]
    [InlineData("[add(mul(2, 3), div(10, 5))]", "8")]
    public void Evaluates(string expression, string expectedJson)
    {
        Expr.AssertJson(expectedJson, Expr.Eval(expression));
    }

    [Theory]
    [InlineData("[if('x', 1, 2)]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[if(true(), 1)]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[and(true(), 'x')]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[or(1)]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[not(1)]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[less(1, 'a')]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[less(createArray(), createArray())]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[greater(true(), false())]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[bool('yes')]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[bool(createArray())]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[int('1.5')]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[int('abc')]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[int(json('1.5'))]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[int(true())]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[float('x')]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[float(createArray())]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[div(1, 0)]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[div(json('1.0'), 0)]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[mod(7, 0)]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[add(9223372036854775807, 1)]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[mul(9223372036854775807, 2)]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[sub(-9223372036854775807, 2)]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[div(-9223372036854775808, -1)]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[mul('a', 2)]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[add(1)]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[add(1, 2, 3)]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[min(createArray())]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[min('a')]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[max(1, 'a')]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[coalesce()]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[true(1)]", DiagnosticCodes.InvalidFunctionArguments)]
    public void Fails(string expression, string code)
    {
        var exception = Expr.Fails(expression, code);

        Assert.NotNull(exception.Offset);
    }

    [Fact]
    public void If_EvaluatesOnlyTheSelectedBranch()
    {
        // The unselected branch would throw (unknown function) if it were evaluated.
        Assert.Equal("a", Expr.AssertString(Expr.Eval("[if(true(), 'a', nope())]")));
        Assert.Equal("b", Expr.AssertString(Expr.Eval("[if(false(), nope(), 'b')]")));
        Expr.Fails("[if(true(), nope(), 'b')]", DiagnosticCodes.UnknownFunction);
    }

    [Fact]
    public void Mod_OfMinValueByMinusOne_IsZero()
    {
        Expr.AssertJson("0", Expr.Eval("[mod(-9223372036854775808, -1)]"));
    }
}
