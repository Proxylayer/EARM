using System;
using System.Text;
using EArm.Core.Diagnostics;

namespace EArm.Expressions.Syntax;

/// <summary>
/// Tokenizer for the body of a template expression. Offsets reported in tokens and errors are relative to the
/// full expression string: the lexer receives the body (without the outer brackets) plus the offset the body
/// starts at (1 for <c>[...]</c>).
/// </summary>
internal sealed class Lexer
{
    private readonly string _body;
    private readonly int _baseOffset;
    private int _position;

    public Lexer(string body, int baseOffset)
    {
        ArgumentNullException.ThrowIfNull(body);
        _body = body;
        _baseOffset = baseOffset;
    }

    public Token Next()
    {
        SkipWhitespace();
        if (_position >= _body.Length)
        {
            return new Token(TokenKind.End, string.Empty, _baseOffset + _position, 0);
        }

        char c = _body[_position];
        int start = _position;
        switch (c)
        {
            case '(':
                _position++;
                return Single(TokenKind.OpenParen, "(", start);
            case ')':
                _position++;
                return Single(TokenKind.CloseParen, ")", start);
            case ',':
                _position++;
                return Single(TokenKind.Comma, ",", start);
            case '.':
                _position++;
                return Single(TokenKind.Dot, ".", start);
            case '[':
                _position++;
                return Single(TokenKind.OpenBracket, "[", start);
            case ']':
                _position++;
                return Single(TokenKind.CloseBracket, "]", start);
            case '\'':
                return ReadString(start);
        }

        if (char.IsAsciiDigit(c) || (c == '-' && _position + 1 < _body.Length && char.IsAsciiDigit(_body[_position + 1])))
        {
            return ReadNumber(start);
        }

        if (IsIdentifierStart(c))
        {
            while (_position < _body.Length && IsIdentifierPart(_body[_position]))
            {
                _position++;
            }

            return new Token(TokenKind.Identifier, _body[start.._position], _baseOffset + start, _position - start);
        }

        throw new ExpressionParseException(DiagnosticCodes.ExpressionSyntaxError, $"Unexpected character '{c}'.", _baseOffset + start);
    }

    private Token Single(TokenKind kind, string text, int start)
    {
        return new Token(kind, text, _baseOffset + start, 1);
    }

    private Token ReadString(int start)
    {
        // Opening quote already at _position. A quote is escaped by doubling it.
        _position++;
        var sb = new StringBuilder();
        while (true)
        {
            if (_position >= _body.Length)
            {
                throw new ExpressionParseException(DiagnosticCodes.ExpressionSyntaxError, "Unterminated string literal.", _baseOffset + start);
            }

            char c = _body[_position];
            if (c == '\'')
            {
                if (_position + 1 < _body.Length && _body[_position + 1] == '\'')
                {
                    sb.Append('\'');
                    _position += 2;
                    continue;
                }

                _position++;
                break;
            }

            sb.Append(c);
            _position++;
        }

        return new Token(TokenKind.String, sb.ToString(), _baseOffset + start, _position - start);
    }

    private Token ReadNumber(int start)
    {
        if (_body[_position] == '-')
        {
            _position++;
        }

        while (_position < _body.Length && char.IsAsciiDigit(_body[_position]))
        {
            _position++;
        }

        if (_position < _body.Length && _body[_position] == '.' && _position + 1 < _body.Length && char.IsAsciiDigit(_body[_position + 1]))
        {
            throw new ExpressionParseException(DiagnosticCodes.ExpressionSyntaxError, "Floating-point literals are not supported; use json('1.5') or float('1.5').", _baseOffset + start);
        }

        return new Token(TokenKind.Number, _body[start.._position], _baseOffset + start, _position - start);
    }

    private void SkipWhitespace()
    {
        while (_position < _body.Length && char.IsWhiteSpace(_body[_position]))
        {
            _position++;
        }
    }

    private static bool IsIdentifierStart(char c)
    {
        return char.IsAsciiLetter(c) || c == '_';
    }

    private static bool IsIdentifierPart(char c)
    {
        return char.IsAsciiLetterOrDigit(c) || c == '_';
    }
}
