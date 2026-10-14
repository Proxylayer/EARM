namespace EntraArm.Expressions.Syntax;

public enum TokenKind
{
    /// <summary>A quoted string; <see cref="Token.Text"/> holds the unescaped value.</summary>
    String,

    /// <summary>An integer literal; <see cref="Token.Text"/> holds the digits (with an optional leading minus).</summary>
    Number,

    Identifier,
    OpenParen,
    CloseParen,
    Comma,
    Dot,
    OpenBracket,
    CloseBracket,
    End,
}

/// <summary>
/// One lexical token. <see cref="Offset"/> is the 0-based offset of the token's first character inside the full
/// expression string (the opening <c>[</c> is at offset 0).
/// </summary>
public readonly record struct Token(TokenKind Kind, string Text, int Offset, int Length)
{
    public override string ToString()
    {
        return Kind switch
        {
            TokenKind.String => "'" + Text + "'",
            TokenKind.End => "end of expression",
            _ => Text,
        };
    }
}
