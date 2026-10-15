using System;
using System.Text.RegularExpressions;
using EArm.Core.Diagnostics;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Functions;
using Xunit;

namespace EArm.Tests;

public sealed partial class StringFunctionTests
{
    [Theory]
    [InlineData("[concat('a', 'b', 'c')]", "\"abc\"")]
    [InlineData("[concat('a', 1, true())]", "\"a1true\"")]
    [InlineData("[concat('x')]", "\"x\"")]
    [InlineData("[concat(createArray(1), createArray(2, 3))]", "[1,2,3]")]
    [InlineData("[concat(createArray(), createArray())]", "[]")]
    [InlineData("[format('{0}-{1}', 'a', 2)]", "\"a-2\"")]
    [InlineData("[format('{0:D3}', 7)]", "\"007\"")]
    [InlineData("[format('{{x}}')]", "\"{x}\"")]
    [InlineData("[format('{0}|{1}', true(), json('1.5'))]", "\"true|1.5\"")]
    [InlineData("[format('{0}', null())]", "\"\"")]
    [InlineData("[toLower('AbC')]", "\"abc\"")]
    [InlineData("[toUpper('AbC')]", "\"ABC\"")]
    [InlineData("[trim('  a ')]", "\"a\"")]
    [InlineData("[substring('abcdef', 2, 3)]", "\"cde\"")]
    [InlineData("[substring('abc', 1)]", "\"bc\"")]
    [InlineData("[substring('abc')]", "\"abc\"")]
    [InlineData("[substring('abc', 3)]", "\"\"")]
    [InlineData("[replace('aXbX', 'X', '-')]", "\"a-b-\"")]
    [InlineData("[replace('AbA', 'a', 'x')]", "\"AbA\"")]
    [InlineData("[split('a,b;c', createArray(',', ';'))]", "[\"a\",\"b\",\"c\"]")]
    [InlineData("[split('a,b', ',')]", "[\"a\",\"b\"]")]
    [InlineData("[split('a', ',')]", "[\"a\"]")]
    [InlineData("[join(createArray('a', 'b'), '-')]", "\"a-b\"")]
    [InlineData("[join(createArray(), '-')]", "\"\"")]
    [InlineData("[startsWith('Hello', 'he')]", "true")]
    [InlineData("[startsWith('Hello', 'x')]", "false")]
    [InlineData("[endsWith('Hello', 'LO')]", "true")]
    [InlineData("[contains('Hello', 'ell')]", "true")]
    [InlineData("[contains('Hello', 'ELL')]", "false")]
    [InlineData("[contains('OneTwoThree', 'e')]", "true")]
    [InlineData("[contains(createObject('One', 1), 'one')]", "true")]
    [InlineData("[indexOf('Hello', 'ELL')]", "1")]
    [InlineData("[startsWith('Hello', 'HE')]", "true")]
    [InlineData("[contains('Hello', 'xyz')]", "false")]
    [InlineData("[contains(createArray(1, 2), 2)]", "true")]
    [InlineData("[contains(createArray('a'), 'A')]", "false")]
    [InlineData("[contains(createObject('Key', 1), 'key')]", "true")]
    [InlineData("[contains(createObject('Key', 1), 'nope')]", "false")]
    [InlineData("[length('abc')]", "3")]
    [InlineData("[length('')]", "0")]
    [InlineData("[length(createArray(1, 2))]", "2")]
    [InlineData("[length(createObject())]", "0")]
    [InlineData("[empty('')]", "true")]
    [InlineData("[empty('a')]", "false")]
    [InlineData("[empty(createArray())]", "true")]
    [InlineData("[empty(createArray(0))]", "false")]
    [InlineData("[empty(createObject())]", "true")]
    [InlineData("[empty(null())]", "true")]
    [InlineData("[indexOf('Hello', 'L')]", "2")]
    [InlineData("[indexOf('abc', 'z')]", "-1")]
    [InlineData("[lastIndexOf('hello', 'L')]", "3")]
    [InlineData("[indexOf(createArray(1, 2, 1), 1)]", "0")]
    [InlineData("[lastIndexOf(createArray(1, 2, 1), 1)]", "2")]
    [InlineData("[indexOf(createArray(1), 9)]", "-1")]
    [InlineData("[padLeft('7', 3, '0')]", "\"007\"")]
    [InlineData("[padLeft(7, 3, '0')]", "\"007\"")]
    [InlineData("[padLeft('ab', 4)]", "\"  ab\"")]
    [InlineData("[padLeft('abcdef', 3)]", "\"abcdef\"")]
    [InlineData("[string(true())]", "\"true\"")]
    [InlineData("[string(42)]", "\"42\"")]
    [InlineData("[string(json('1.5'))]", "\"1.5\"")]
    [InlineData("[string('s')]", "\"s\"")]
    [InlineData("[base64('hello')]", "\"aGVsbG8=\"")]
    [InlineData("[base64ToString('aGVsbG8=')]", "\"hello\"")]
    [InlineData("[uri('http://a/b/', '../c')]", "\"http://a/c\"")]
    [InlineData("[uri('http://contoso.com/', 'path')]", "\"http://contoso.com/path\"")]
    [InlineData("[uri('http://contoso.com/a/b', 'c')]", "\"http://contoso.com/a/c\"")]
    [InlineData("[uriComponent('a b&c')]", "\"a%20b%26c\"")]
    [InlineData("[uriComponentToString('a%20b')]", "\"a b\"")]
    [InlineData("[dateTimeAdd('2024-01-31T00:00:00Z', 'P1M')]", "\"2024-02-29T00:00:00Z\"")]
    [InlineData("[dateTimeAdd('2024-03-01T00:00:00Z', '-P1D')]", "\"2024-02-29T00:00:00Z\"")]
    [InlineData("[dateTimeAdd('2024-01-01T00:00:00Z', 'PT1H30M')]", "\"2024-01-01T01:30:00Z\"")]
    [InlineData("[dateTimeAdd('2024-01-01T00:00:00Z', 'P1W')]", "\"2024-01-08T00:00:00Z\"")]
    [InlineData("[dateTimeAdd('2024-01-01T00:00:00Z', 'P1Y2M3DT4H5M6S')]", "\"2025-03-04T04:05:06Z\"")]
    [InlineData("[dateTimeAdd('2024-01-01T00:00:00Z', 'P1D', 'yyyy-MM-dd')]", "\"2024-01-02\"")]
    [InlineData("[dateTimeAdd('2024-01-01', 'P1D')]", "\"2024-01-02T00:00:00Z\"")]
    [InlineData("[dateTimeAdd('2024-01-01T12:00:00+02:00', 'PT0S')]", "\"2024-01-01T10:00:00Z\"")]
    [InlineData("[dateTimeFromEpoch(0)]", "\"1970-01-01T00:00:00Z\"")]
    [InlineData("[dateTimeFromEpoch(86400)]", "\"1970-01-02T00:00:00Z\"")]
    [InlineData("[dateTimeToEpoch('1970-01-02T00:00:00Z')]", "86400")]
    [InlineData("[dateTimeToEpoch('1970-01-01T00:00:00Z')]", "0")]
    public void Evaluates(string expression, string expectedJson)
    {
        Expr.AssertJson(expectedJson, Expr.Eval(expression));
    }

    [Theory]
    [InlineData("[concat('a', createArray(1))]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[concat(createArray(1), 'a')]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[concat('a', null())]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[concat('a', createObject())]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[concat()]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[format('{1}', 'a')]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[format(1)]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[substring('abc', 5)]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[substring('abc', 1, 5)]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[substring('abc', -1)]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[substring(123)]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[substring('abc', 'x')]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[replace('abc', '', 'x')]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[split('a', createArray(1))]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[join(createArray(1), '-')]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[join('a', '-')]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[length(123)]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[empty(1)]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[contains(createObject('a', 1), 1)]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[padLeft('a', 17)]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[padLeft('a', -1)]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[padLeft('a', 3, 'xy')]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[string(null())]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[base64ToString('!!')]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[uri('notabsolute', 'x')]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[dateTimeAdd('2024-01-01T00:00:00Z', 'P')]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[dateTimeAdd('2024-01-01T00:00:00Z', 'X1D')]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[dateTimeAdd('2024-01-01T00:00:00Z', 'P1H')]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[dateTimeAdd('2024-01-01T00:00:00Z', 'P1')]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[dateTimeAdd('notadate', 'P1D')]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[dateTimeFromEpoch('x')]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[dateTimeToEpoch('x')]", DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("[newGuid()]", DiagnosticCodes.NonDeterministicFunctionNotAllowed)]
    [InlineData("[utcNow()]", DiagnosticCodes.NonDeterministicFunctionNotAllowed)]
    [InlineData("[toLower()]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[toLower('a', 'b')]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[toLower(1)]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[guid()]", DiagnosticCodes.InvalidFunctionArguments)]
    [InlineData("[guid(null())]", DiagnosticCodes.InvalidFunctionArguments)]
    public void Fails(string expression, string code)
    {
        var exception = Expr.Fails(expression, code);

        Assert.NotNull(exception.Offset);
        Assert.EndsWith($"(at offset {exception.Offset})", exception.MessageWithOffset);
    }

    [GeneratedRegex("^[a-z2-7]{13}$")]
    private static partial Regex UniqueStringPattern();

    [Fact]
    public void Format_And_String_RenderObjectsAsCompactJson()
    {
        Assert.Equal("{\"a\":1}", Expr.AssertString(Expr.Eval("[format('{0}', createObject('a', 1))]")));
        Assert.Equal("[1,\"a\"]", Expr.AssertString(Expr.Eval("[string(createArray(1, 'a'))]")));
        Assert.Equal("{\"a\":[1]}", Expr.AssertString(Expr.Eval("[string(createObject('a', createArray(1)))]")));
        Assert.Equal("a{\"b\":true}", Expr.AssertString(Expr.Eval("[concat('a', string(createObject('b', true())))]")));
    }

    [Fact]
    public void Guid_JoinsArgumentsWithHyphensAndIsStable()
    {
        string joined = Expr.AssertString(Expr.Eval("[guid('a', 'b')]"));
        string single = Expr.AssertString(Expr.Eval("[guid('a-b')]"));
        string again = Expr.AssertString(Expr.Eval("[guid('a', 'b')]"));

        Assert.True(Guid.TryParseExact(joined, "D", out var parsed));
        Assert.Equal(joined, single);
        Assert.Equal(joined, again);
        Assert.NotEqual(joined, Expr.AssertString(Expr.Eval("[guid('a', 'c')]")));
        Assert.NotEqual(joined, Expr.AssertString(Expr.Eval("[guid('A', 'b')]")));
        Assert.Equal(parsed, Hashing.GuidV5(Hashing.GuidNamespace, "a-b"));
        Assert.Equal(5, (parsed.ToByteArray()[7] >> 4) & 0x0F);
        Assert.True(Guid.TryParse(Expr.AssertString(Expr.Eval("[guid(1, true(), createObject('k', 'v'))]")), out _));
    }

    [Fact]
    public void UniqueString_IsThirteenBase32CharactersAndStable()
    {
        string value = Expr.AssertString(Expr.Eval("[uniqueString('x')]"));

        Assert.Matches(UniqueStringPattern(), value);
        Assert.Equal(value, Expr.AssertString(Expr.Eval("[uniqueString('x')]")));
        Assert.Equal(value, Hashing.UniqueString("x"));
        Assert.Equal(Expr.AssertString(Expr.Eval("[uniqueString('a-b')]")), Expr.AssertString(Expr.Eval("[uniqueString('a', 'b')]")));
        Assert.NotEqual(value, Expr.AssertString(Expr.Eval("[uniqueString('y')]")));
        Assert.Matches(UniqueStringPattern(), Hashing.UniqueString(string.Empty));
    }

    [Fact]
    public void NonDeterministicFunctions_AreAllowedInParameterDefaults()
    {
        var context = Expr.Context(scope: EvaluationScope.ParameterDefault);

        Assert.True(context.AllowNonDeterministic);
        Assert.Matches("^[0-9]{8}T[0-9]{6}Z$", Expr.AssertString(Expr.Eval("[utcNow()]", context)));
        Assert.Matches("^[0-9]{4}$", Expr.AssertString(Expr.Eval("[utcNow('yyyy')]", context)));
        Assert.True(Guid.TryParseExact(Expr.AssertString(Expr.Eval("[newGuid()]", context)), "D", out _));
        Assert.NotEqual(Expr.AssertString(Expr.Eval("[newGuid()]", context)), Expr.AssertString(Expr.Eval("[newGuid()]", context)));

        Expr.Fails("[utcNow()]", DiagnosticCodes.NonDeterministicFunctionNotAllowed, Expr.Context(scope: EvaluationScope.Variable));
        Expr.Fails("[newGuid()]", DiagnosticCodes.NonDeterministicFunctionNotAllowed, Expr.Context(scope: EvaluationScope.Output));
    }

    [Fact]
    public void MurmurHash_MatchesTheReferenceVector()
    {
        // MurmurHash64A("", seed 0) is 0; the full-block and tail paths both contribute for longer inputs.
        Assert.Equal(0UL, Hashing.MurmurHash64A([], 0));
        Assert.NotEqual(Hashing.MurmurHash64A("abcdefgh"u8, 0), Hashing.MurmurHash64A("abcdefghi"u8, 0));
        Assert.Equal(Hashing.MurmurHash64A("abc"u8, 0), Hashing.MurmurHash64A("abc"u8, 0));
        Assert.NotEqual(Hashing.MurmurHash64A("abc"u8, 0), Hashing.MurmurHash64A("abc"u8, 1));
    }
}
