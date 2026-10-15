using System;
using System.Text;

namespace EArm.Core;

/// <summary>
/// The lexical rules of the template expression language that the loader and validator need without evaluating
/// anything (evaluation lives in EArm.Expressions):
/// a string that starts with <c>[</c> and ends with <c>]</c> is an expression, except that a leading <c>[[</c>
/// escapes to a literal single <c>[</c>; inside expressions <c>'</c> is escaped by doubling.
/// </summary>
public static class ExpressionSyntax
{
    /// <summary>True when the string is an expression (<c>[...]</c>) rather than a literal.</summary>
    public static bool IsExpression(string? text)
    {
        return text is not null
            && text.Length >= 2
            && text[0] == '['
            && text[^1] == ']'
            && !text.StartsWith("[[", StringComparison.Ordinal);
    }

    /// <summary>True when the string starts with the <c>[[</c> escape.</summary>
    public static bool IsEscapedLiteral(string? text)
    {
        return text is not null && text.StartsWith("[[", StringComparison.Ordinal);
    }

    /// <summary>True for a plain string value (a literal or an escaped literal), i.e. not an expression.</summary>
    public static bool IsLiteral(string? text)
    {
        return text is not null && !IsExpression(text);
    }

    /// <summary>
    /// The literal value a non-expression string stands for: <c>[[x</c> becomes <c>[x</c>, anything else is unchanged.
    /// </summary>
    public static string LiteralValue(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return IsEscapedLiteral(text) ? text[1..] : text;
    }

    /// <summary>The expression body without the outer brackets.</summary>
    public static string Body(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (!IsExpression(expression))
        {
            throw new ArgumentException($"'{expression}' is not an expression.", nameof(expression));
        }

        return expression[1..^1];
    }

    /// <summary>Quotes a literal for use inside an expression (<c>'</c> doubled).</summary>
    public static string QuoteLiteral(string literal)
    {
        ArgumentNullException.ThrowIfNull(literal);
        return "'" + literal.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    /// <summary>
    /// Renders a name-or-expression string as an expression argument: an expression contributes its body,
    /// a literal is quoted.
    /// </summary>
    public static string AsArgument(string nameOrExpression)
    {
        ArgumentNullException.ThrowIfNull(nameOrExpression);
        return IsExpression(nameOrExpression) ? Body(nameOrExpression) : QuoteLiteral(LiteralValue(nameOrExpression));
    }

    /// <summary>
    /// Composes the ARM-style <c>parent/child</c> name of a nested child resource. Two literals join with
    /// <c>/</c>; if either side is an expression the result is <c>[format('{0}/{1}', parent, child)]</c>.
    /// This is the exact text a top-level declaration must use to be equivalent to the nested form.
    /// </summary>
    public static string ComposeName(string parentName, string childName)
    {
        ArgumentNullException.ThrowIfNull(parentName);
        ArgumentNullException.ThrowIfNull(childName);

        if (!IsExpression(parentName) && !IsExpression(childName))
        {
            return LiteralValue(parentName) + "/" + LiteralValue(childName);
        }

        var sb = new StringBuilder();
        sb.Append("[format('{0}/{1}', ").Append(AsArgument(parentName)).Append(", ").Append(AsArgument(childName)).Append(")]");
        return sb.ToString();
    }

    /// <summary>
    /// Builds the <c>[resourceId('type', name...)]</c> expression for a resource whose name may itself be an
    /// expression. A literal <c>parent/child</c> name is passed as separate arguments, as ARM expects.
    /// </summary>
    public static string ResourceIdExpression(string type, string nameOrExpression)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(nameOrExpression);

        var sb = new StringBuilder();
        sb.Append("[resourceId(").Append(QuoteLiteral(type));
        if (IsExpression(nameOrExpression))
        {
            sb.Append(", ").Append(Body(nameOrExpression));
        }
        else
        {
            foreach (var segment in LiteralValue(nameOrExpression).Split('/'))
            {
                sb.Append(", ").Append(QuoteLiteral(segment));
            }
        }

        sb.Append(")]");
        return sb.ToString();
    }
}
