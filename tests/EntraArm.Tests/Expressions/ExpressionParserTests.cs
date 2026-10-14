using System;
using System.Linq;
using EntraArm.Core.Diagnostics;
using EntraArm.Expressions;
using EntraArm.Expressions.Syntax;
using Xunit;

namespace EntraArm.Tests;

public sealed class ExpressionParserTests
{
    // ---- syntax errors with offsets (0-based, counting the opening '[') ----

    [Theory]
    [InlineData("[]", 1)]
    [InlineData("[   ]", 1)]
    [InlineData("[true]", 5)]
    [InlineData("[concat('a']", 11)]
    [InlineData("[concat('a') x]", 13)]
    [InlineData("[concat('abc)]", 8)]
    [InlineData("[add(1.5,1)]", 5)]
    [InlineData("[a.b.c()]", 4)]
    [InlineData("[9223372036854775808]", 1)]
    [InlineData("[-9223372036854775809]", 1)]
    [InlineData("[a + b]", 3)]
    [InlineData("[concat('a',)]", 12)]
    [InlineData("[concat(]", 8)]
    [InlineData("[.x]", 1)]
    [InlineData("[createArray(1)[0]", 17)]
    [InlineData("[createObject('a', 1).]", 22)]
    [InlineData("[map(createArray(1), lambda())]", 28)]
    [InlineData("[lambda('', add(1, 1))]", 8)]
    [InlineData("[concat('a'))]", 12)]
    [InlineData("[1 2]", 3)]
    public void Parse_ReportsSyntaxErrorsWithOffsets(string text, int expectedOffset)
    {
        var exception = Expr.ParseFails(text, expectedOffset);

        Assert.EndsWith($"(at offset {expectedOffset})", exception.MessageWithOffset);
    }

    [Theory]
    [InlineData("literal")]
    [InlineData("[[escaped]")]
    [InlineData(" [x]")]
    [InlineData("[x")]
    [InlineData("")]
    public void Parse_RejectsNonExpressionsAtOffsetZero(string text)
    {
        Expr.ParseFails(text, 0);
    }

    [Fact]
    public void Parse_EnforcesTheArmLengthLimit()
    {
        const string prefix = "[concat('";
        const string suffix = "')]";
        string ok = prefix + new string('a', ExpressionParser.MaxExpressionLength - prefix.Length - suffix.Length) + suffix;
        string tooLong = prefix + new string('a', ExpressionParser.MaxExpressionLength + 1 - prefix.Length - suffix.Length) + suffix;

        Assert.Equal(24576, ExpressionParser.MaxExpressionLength);
        Assert.Equal(24576, ok.Length);
        Assert.Equal(24577, tooLong.Length);
        Assert.IsType<CallExpression>(ExpressionParser.Parse(ok));
        Expr.ParseFails(tooLong, 0, DiagnosticCodes.ExpressionTooLong);
        Assert.Equal(ok.Length - prefix.Length - suffix.Length, Expr.AssertString(Expr.Eval(ok)).Length);
    }

    [Fact]
    public void Parse_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => ExpressionParser.Parse(null!));
        Assert.Throws<ArgumentNullException>(() => ExpressionParser.ParseBody(null!));
    }

    // ---- literals and escapes through EvaluateString ----

    [Theory]
    [InlineData("[concat('it''s')]", "it's")]
    [InlineData("['']", "")]
    [InlineData("[[x]", "[x]")]
    [InlineData("[[", "[")]
    [InlineData("[x", "[x")]
    [InlineData(" [x]", " [x]")]
    [InlineData("x]", "x]")]
    [InlineData("plain", "plain")]
    [InlineData("[ concat( 'a' , 'b' ) ]", "ab")]
    [InlineData("[concat(\t'a',\n'b')]", "ab")]
    [InlineData("[CONCAT('a', 'b')]", "ab")]
    [InlineData("[toLower(concat('A', 'B'))]", "ab")]
    public void EvaluateString_HandlesLiteralsEscapesAndWhitespace(string text, string expected)
    {
        Assert.Equal(expected, Expr.AssertString(Expr.Eval(text)));
    }

    [Theory]
    [InlineData("[add(-5, 2)]", -3)]
    [InlineData("[-5]", -5)]
    [InlineData("[9223372036854775807]", long.MaxValue)]
    [InlineData("[-9223372036854775808]", long.MinValue)]
    [InlineData("[0]", 0)]
    public void Parse_IntegerLiteralsAreSixtyFourBit(string text, long expected)
    {
        Expr.AssertJson(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), Expr.Eval(text));
    }

    // ---- tree shape and rendering ----

    [Theory]
    [InlineData("[ concat( 'a' , 'b' ) ]", "concat('a', 'b')")]
    [InlineData("[createObject('k', 1).K]", "createObject('k', 1).K")]
    [InlineData("[createArray(1)[0]]", "createArray(1)[0]")]
    [InlineData("[reference('app').roles[0].id]", "reference('app').roles[0].id")]
    [InlineData("[map(createArray(1), lambda('x', add(lambdaVariables('x'), 1)))]", "map(createArray(1), lambda('x', add(lambdaVariables('x'), 1)))")]
    [InlineData("[map(createArray(1), lambda(x, y, add(lambdaVariables('x'), lambdaVariables('y'))))]", "map(createArray(1), lambda('x', 'y', add(lambdaVariables('x'), lambdaVariables('y'))))")]
    [InlineData("[c.slug('it''s')]", "c.slug('it''s')")]
    [InlineData("[true()]", "true()")]
    [InlineData("[-5]", "-5")]
    [InlineData("[createObject('a', createArray())['a']]", "createObject('a', createArray())['a']")]
    public void ToExpressionText_RendersCanonically(string text, string rendered)
    {
        var expression = ExpressionParser.Parse(text);

        Assert.Equal(rendered, expression.ToExpressionText());
        Assert.Equal(rendered, expression.ToString());
        Assert.Equal(rendered, ExpressionParser.ParseBody(rendered).ToExpressionText());
    }

    [Fact]
    public void Parse_BuildsTheExpectedNodesWithOffsets()
    {
        var root = Assert.IsType<CallExpression>(ExpressionParser.Parse("[concat('a', add(1, 2))]"));

        Assert.Equal(1, root.Offset);
        Assert.Equal("concat", root.Name);
        Assert.Null(root.Namespace);
        Assert.False(root.IsUserFunction);
        Assert.True(root.Is("CONCAT"));
        Assert.False(root.Is("add"));
        Assert.Equal(2, root.Arguments.Count);

        var first = Assert.IsType<LiteralExpression>(root.Arguments[0]);
        Assert.True(first.IsString);
        Assert.Equal("a", first.StringValue);
        Assert.Equal(8, first.Offset);

        var add = Assert.IsType<CallExpression>(root.Arguments[1]);
        Assert.Equal(13, add.Offset);
        var one = Assert.IsType<LiteralExpression>(add.Arguments[0]);
        Assert.False(one.IsString);
        Assert.Equal(1, one.IntegerValue);
        Assert.Equal(17, one.Offset);
        Assert.Equal(20, add.Arguments[1].Offset);

        Assert.Equal(["concat", "'a'", "add", "1", "2"], root.DescendantsAndSelf().Select(n => n is CallExpression c ? c.Name : n.ToExpressionText()).ToList());
        Assert.Equal(2, root.Children.Count());
    }

    [Fact]
    public void Parse_PostfixAccessKeepsTheTargetOffset()
    {
        var property = Assert.IsType<PropertyExpression>(ExpressionParser.Parse("[reference('app').appId]"));
        Assert.Equal(1, property.Offset);
        Assert.Equal("appId", property.Name);
        Assert.IsType<CallExpression>(property.Target);

        var index = Assert.IsType<IndexExpression>(ExpressionParser.Parse("[createArray(1, 2)[1]]"));
        Assert.Equal(1, index.Offset);
        Assert.Equal(19, index.Index.Offset);
        Assert.Equal(2, index.Children.Count());
    }

    [Fact]
    public void Parse_UserFunctionsAndLambdas()
    {
        var user = Assert.IsType<CallExpression>(ExpressionParser.Parse("[c.slug('x')]"));
        Assert.True(user.IsUserFunction);
        Assert.Equal("c", user.Namespace);
        Assert.Equal("slug", user.Name);
        Assert.Equal("c.slug", user.QualifiedName);
        Assert.False(user.Is("slug"));

        var map = Assert.IsType<CallExpression>(ExpressionParser.Parse("[map(createArray(1), lambda('x', lambdaVariables('x')))]"));
        var lambda = Assert.IsType<LambdaExpression>(map.Arguments[1]);
        Assert.Equal(["x"], lambda.Parameters);
        Assert.Equal(21, lambda.Offset);
        Assert.IsType<CallExpression>(Assert.Single(lambda.Children));

        var bare = Assert.IsType<LambdaExpression>(ExpressionParser.Parse("[lambda(a, b, add(lambdaVariables('a'), lambdaVariables('b')))]"));
        Assert.Equal(["a", "b"], bare.Parameters);
        Assert.Equal("add(lambdaVariables('a'), lambdaVariables('b'))", bare.Body.ToExpressionText());
    }

    [Fact]
    public void ParseBody_ShiftsOffsetsByTheBase()
    {
        var expression = ExpressionParser.ParseBody("concat('a')", 10);

        Assert.Equal(10, expression.Offset);
        Assert.Equal(17, Assert.IsType<CallExpression>(expression).Arguments[0].Offset);
        Assert.Throws<ExpressionParseException>(() => ExpressionParser.ParseBody(string.Empty));
    }

    [Fact]
    public void Exceptions_CarryCodeAndOffset()
    {
        var parse = new ExpressionParseException(DiagnosticCodes.ExpressionSyntaxError, "bad", 3);
        Assert.Equal("bad (at offset 3)", parse.MessageWithOffset);
        Assert.Equal(3, parse.Offset);

        var evaluation = new ExpressionEvaluationException(DiagnosticCodes.ExpressionEvaluationFailed, "boom");
        Assert.Equal("boom", evaluation.MessageWithOffset);
        Assert.Null(evaluation.Offset);
        Assert.Throws<ArgumentException>(() => new ExpressionEvaluationException(" ", "m"));
    }

    [Fact]
    public void Token_RendersForMessages()
    {
        Assert.Equal("'a'", new Token(TokenKind.String, "a", 0, 3).ToString());
        Assert.Equal("end of expression", new Token(TokenKind.End, string.Empty, 0, 0).ToString());
        Assert.Equal("concat", new Token(TokenKind.Identifier, "concat", 0, 6).ToString());
    }
}
