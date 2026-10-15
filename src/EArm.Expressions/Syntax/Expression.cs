using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using EArm.Core;

namespace EArm.Expressions.Syntax;

/// <summary>
/// A node of a parsed template expression. <see cref="Offset"/> is the 0-based offset of the node's first
/// character inside the full expression string (the opening <c>[</c> is at offset 0).
/// </summary>
public abstract record Expression(int Offset)
{
    /// <summary>The direct children, in source order.</summary>
    public abstract IEnumerable<Expression> Children { get; }

    /// <summary>This node and every descendant, pre-order.</summary>
    public IEnumerable<Expression> DescendantsAndSelf()
    {
        var stack = new Stack<Expression>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            var children = new List<Expression>(node.Children);
            for (int i = children.Count - 1; i >= 0; i--)
            {
                stack.Push(children[i]);
            }
        }
    }

    /// <summary>Renders the node back as expression text (canonical spacing, quotes doubled).</summary>
    public string ToExpressionText()
    {
        var sb = new StringBuilder();
        Render(sb);
        return sb.ToString();
    }

    internal abstract void Render(StringBuilder sb);

    public sealed override string ToString()
    {
        return ToExpressionText();
    }
}

/// <summary>A string or integer literal.</summary>
public sealed record LiteralExpression(int Offset, string? StringValue, long? IntegerValue) : Expression(Offset)
{
    public static LiteralExpression String(int offset, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new LiteralExpression(offset, value, null);
    }

    public static LiteralExpression Integer(int offset, long value)
    {
        return new LiteralExpression(offset, null, value);
    }

    public bool IsString => StringValue is not null;

    public override IEnumerable<Expression> Children => [];

    internal override void Render(StringBuilder sb)
    {
        if (StringValue is not null)
        {
            sb.Append(ExpressionSyntax.QuoteLiteral(StringValue));
        }
        else
        {
            sb.Append(IntegerValue.GetValueOrDefault().ToString(CultureInfo.InvariantCulture));
        }
    }
}

/// <summary>A function call, optionally namespaced (<c>ns.fn(...)</c> for user-defined functions).</summary>
public sealed record CallExpression(int Offset, string? Namespace, string Name, IReadOnlyList<Expression> Arguments) : Expression(Offset)
{
    public bool IsUserFunction => Namespace is not null;

    /// <summary>The name as written, with the namespace when present.</summary>
    public string QualifiedName => Namespace is null ? Name : Namespace + "." + Name;

    /// <summary>True when this is the built-in function <paramref name="name"/> (case-insensitive, no namespace).</summary>
    public bool Is(string name)
    {
        return Namespace is null && Name.Equals(name, StringComparison.OrdinalIgnoreCase);
    }

    public override IEnumerable<Expression> Children => Arguments;

    internal override void Render(StringBuilder sb)
    {
        ExpressionStackGuard.Ensure(Offset);
        sb.Append(QualifiedName).Append('(');
        for (int i = 0; i < Arguments.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            Arguments[i].Render(sb);
        }

        sb.Append(')');
    }
}

/// <summary>A <c>lambda(p1, ..., body)</c> argument.</summary>
public sealed record LambdaExpression(int Offset, IReadOnlyList<string> Parameters, Expression Body) : Expression(Offset)
{
    public override IEnumerable<Expression> Children => [Body];

    internal override void Render(StringBuilder sb)
    {
        ExpressionStackGuard.Ensure(Offset);
        sb.Append("lambda(");
        foreach (var parameter in Parameters)
        {
            sb.Append(ExpressionSyntax.QuoteLiteral(parameter)).Append(", ");
        }

        Body.Render(sb);
        sb.Append(')');
    }
}

/// <summary>Property access: <c>target.name</c>.</summary>
public sealed record PropertyExpression(int Offset, Expression Target, string Name) : Expression(Offset)
{
    public override IEnumerable<Expression> Children => [Target];

    internal override void Render(StringBuilder sb)
    {
        ExpressionStackGuard.Ensure(Offset);
        Target.Render(sb);
        sb.Append('.').Append(Name);
    }
}

/// <summary>Indexer access: <c>target[index]</c>.</summary>
public sealed record IndexExpression(int Offset, Expression Target, Expression Index) : Expression(Offset)
{
    public override IEnumerable<Expression> Children => [Target, Index];

    internal override void Render(StringBuilder sb)
    {
        ExpressionStackGuard.Ensure(Offset);
        Target.Render(sb);
        sb.Append('[');
        Index.Render(sb);
        sb.Append(']');
    }
}
