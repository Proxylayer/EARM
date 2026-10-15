using System;

namespace EArm.Expressions;

/// <summary>
/// Base class for expression failures. <see cref="Code"/> is a <see cref="EArm.Core.Diagnostics.DiagnosticCodes"/>
/// constant; <see cref="Offset"/> is the 0-based character offset inside the full expression string (including
/// the opening <c>[</c>) when the failure can be pinned to one spot.
/// </summary>
public class ExpressionException : Exception
{
    public ExpressionException(string code, string message, int? offset = null, Exception? inner = null)
        : base(message, inner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
        Offset = offset;
    }

    public string Code { get; }

    public int? Offset { get; }

    /// <summary>The message followed by <c>(at offset N)</c> when an offset is known.</summary>
    public string MessageWithOffset => Offset is { } offset ? $"{Message} (at offset {offset})" : Message;
}

/// <summary>A lexical or grammatical error; always carries an offset.</summary>
public sealed class ExpressionParseException : ExpressionException
{
    public ExpressionParseException(string code, string message, int offset)
        : base(code, message, offset)
    {
    }
}

/// <summary>A failure while evaluating a parsed expression (unknown function, bad argument, cycle...).</summary>
public sealed class ExpressionEvaluationException : ExpressionException
{
    public ExpressionEvaluationException(string code, string message, int? offset = null, Exception? inner = null)
        : base(code, message, offset, inner)
    {
    }
}
