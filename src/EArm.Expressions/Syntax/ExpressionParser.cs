using System;
using System.Collections.Generic;
using System.Globalization;
using EArm.Core;
using EArm.Core.Diagnostics;

namespace EArm.Expressions.Syntax;

/// <summary>
/// Hand-written recursive-descent parser for template expressions:
/// <code>
/// expr    := primary postfix*
/// primary := string | number | call
/// call    := IDENT ('.' IDENT)? '(' [args] ')'
/// args    := (expr | lambda) (',' (expr | lambda))*
/// lambda  := 'lambda' '(' param (',' param)* ',' expr ')'      param := IDENT | string
/// postfix := '.' IDENT | '[' expr ']'
/// </code>
/// Lambda parameters accept both the identifier form of the plan grammar and the quoted form ARM JSON uses
/// (<c>lambda('x', ...)</c>). Errors carry the offset inside the full expression string.
/// </summary>
public sealed class ExpressionParser
{
    /// <summary>The ARM limit on the length of one expression string.</summary>
    public const int MaxExpressionLength = 24576;

    /// <summary>
    /// The maximum nesting depth of one expression: the expression itself is level 1; nested calls, indexers, lambdas and chained property/index accesses are
    /// counted together (each <c>f(</c>, <c>[</c>, <c>lambda(</c> body and each <c>.name</c> / <c>[..]</c> on an existing value adds one level).
    /// The parser and every later walker over the tree are recursive, so this keeps a hostile template from overflowing the stack.
    /// </summary>
    public const int MaxNestingDepth = 100;

    private readonly List<Token> _tokens;
    private int _index;
    private int _depth;

    private ExpressionParser(string body, int baseOffset)
    {
        var lexer = new Lexer(body, baseOffset);
        _tokens = [];
        Token token;
        do
        {
            token = lexer.Next();
            _tokens.Add(token);
        }
        while (token.Kind != TokenKind.End);
    }

    private Token Current => _tokens[_index];

    private Token Peek => _index + 1 < _tokens.Count ? _tokens[_index + 1] : _tokens[^1];

    /// <summary>
    /// Parses a full expression string (<c>[...]</c>). Throws <see cref="ExpressionParseException"/> when the text
    /// is not an expression, exceeds <see cref="MaxExpressionLength"/>, or does not match the grammar.
    /// </summary>
    public static Expression Parse(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (expression.Length > MaxExpressionLength)
        {
            throw new ExpressionParseException(DiagnosticCodes.ExpressionTooLong, $"The expression is {expression.Length} characters long; the limit is {MaxExpressionLength}.", 0);
        }

        if (!ExpressionSyntax.IsExpression(expression))
        {
            throw new ExpressionParseException(DiagnosticCodes.ExpressionSyntaxError, "Not an expression: expected '[...]' (a leading '[[' is an escaped literal).", 0);
        }

        return ParseBody(ExpressionSyntax.Body(expression), 1);
    }

    /// <summary>Parses an expression body (no outer brackets); offsets are shifted by <paramref name="baseOffset"/>.</summary>
    public static Expression ParseBody(string body, int baseOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(body);
        var parser = new ExpressionParser(body, baseOffset);
        if (parser.Current.Kind == TokenKind.End)
        {
            throw new ExpressionParseException(DiagnosticCodes.ExpressionSyntaxError, "The expression is empty.", baseOffset);
        }

        var expression = parser.ParseExpression();
        if (parser.Current.Kind != TokenKind.End)
        {
            throw parser.Unexpected("end of expression");
        }

        return expression;
    }

    private Expression ParseExpression()
    {
        EnterNesting(Current.Offset);
        try
        {
            var expression = ParsePrimary();
            int chain = 0;
            while (true)
            {
                if (Current.Kind == TokenKind.Dot)
                {
                    CheckChain(++chain);
                    Advance();
                    var name = Expect(TokenKind.Identifier, "a property name");
                    expression = new PropertyExpression(expression.Offset, expression, name.Text);
                    continue;
                }

                if (Current.Kind == TokenKind.OpenBracket)
                {
                    CheckChain(++chain);
                    Advance();
                    var index = ParseExpression();
                    Expect(TokenKind.CloseBracket, "']'");
                    expression = new IndexExpression(expression.Offset, expression, index);
                    continue;
                }

                return expression;
            }
        }
        finally
        {
            _depth--;
        }
    }

    /// <summary>Enters one nesting level; fails past <see cref="MaxNestingDepth"/> (or when the stack runs low) with a position.</summary>
    private void EnterNesting(int offset)
    {
        _depth++;
        if (_depth > MaxNestingDepth || !System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw TooDeep(offset);
        }
    }

    /// <summary>A chain of postfix accesses builds a left-deep tree: each link counts as one nesting level on top of the current depth.</summary>
    private void CheckChain(int chain)
    {
        if (_depth + chain > MaxNestingDepth)
        {
            throw TooDeep(Current.Offset);
        }
    }

    private static ExpressionParseException TooDeep(int offset)
    {
        return new ExpressionParseException(DiagnosticCodes.ExpressionTooDeep, $"The expression nesting exceeds the maximum depth of {MaxNestingDepth}.", offset);
    }

    private Expression ParsePrimary()
    {
        switch (Current.Kind)
        {
            case TokenKind.String:
                {
                    var token = Current;
                    Advance();
                    return LiteralExpression.String(token.Offset, token.Text);
                }

            case TokenKind.Number:
                {
                    var token = Current;
                    Advance();
                    if (!long.TryParse(token.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value))
                    {
                        throw new ExpressionParseException(DiagnosticCodes.ExpressionSyntaxError, $"Integer literal '{token.Text}' is out of range (64-bit signed).", token.Offset);
                    }

                    return LiteralExpression.Integer(token.Offset, value);
                }

            case TokenKind.Identifier:
                return ParseCall();

            case TokenKind.End:
                throw new ExpressionParseException(DiagnosticCodes.ExpressionSyntaxError, "Unexpected end of expression; expected a string, a number or a function call.", Current.Offset);

            default:
                throw Unexpected("a string, a number or a function call");
        }
    }

    private Expression ParseCall()
    {
        var first = Current;
        Advance();

        string? ns = null;
        string name = first.Text;
        if (Current.Kind == TokenKind.Dot)
        {
            Advance();
            var member = Expect(TokenKind.Identifier, "a function name after the namespace");
            ns = first.Text;
            name = member.Text;
        }

        if (Current.Kind != TokenKind.OpenParen)
        {
            string qualified = ns is null ? name : ns + "." + name;
            throw new ExpressionParseException(DiagnosticCodes.ExpressionSyntaxError, $"Expected '(' after '{qualified}'; identifiers are only valid as function names (true, false and null are functions: true(), false(), null()).", Current.Offset);
        }

        Advance();

        if (ns is null && name.Equals("lambda", StringComparison.OrdinalIgnoreCase))
        {
            return ParseLambdaArguments(first.Offset);
        }

        var arguments = new List<Expression>();
        if (Current.Kind != TokenKind.CloseParen)
        {
            while (true)
            {
                arguments.Add(ParseExpression());
                if (Current.Kind == TokenKind.Comma)
                {
                    Advance();
                    continue;
                }

                break;
            }
        }

        Expect(TokenKind.CloseParen, "',' or ')'");
        return new CallExpression(first.Offset, ns, name, arguments);
    }

    /// <summary>Parses <c>p1, ..., body)</c> after <c>lambda(</c>: a parameter is an identifier or string followed by a comma.</summary>
    private Expression ParseLambdaArguments(int offset)
    {
        var parameters = new List<string>();
        while (Current.Kind is TokenKind.Identifier or TokenKind.String && Peek.Kind == TokenKind.Comma)
        {
            if (Current.Text.Length == 0)
            {
                throw new ExpressionParseException(DiagnosticCodes.ExpressionSyntaxError, "A lambda parameter name must not be empty.", Current.Offset);
            }

            parameters.Add(Current.Text);
            Advance();
            Advance();
        }

        if (parameters.Count == 0)
        {
            throw new ExpressionParseException(DiagnosticCodes.ExpressionSyntaxError, "lambda() needs at least one parameter name followed by the body expression.", Current.Offset);
        }

        var body = ParseExpression();
        Expect(TokenKind.CloseParen, "')'");
        return new LambdaExpression(offset, parameters, body);
    }

    private Token Expect(TokenKind kind, string description)
    {
        if (Current.Kind != kind)
        {
            throw Unexpected(description);
        }

        var token = Current;
        Advance();
        return token;
    }

    private ExpressionParseException Unexpected(string expected)
    {
        string found = Current.Kind == TokenKind.End ? "end of expression" : $"'{Current.Text}'";
        return new ExpressionParseException(DiagnosticCodes.ExpressionSyntaxError, $"Expected {expected} but found {found}.", Current.Offset);
    }

    private void Advance()
    {
        if (_index < _tokens.Count - 1)
        {
            _index++;
        }
    }
}
