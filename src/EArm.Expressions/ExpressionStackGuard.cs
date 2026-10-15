using System.Runtime.CompilerServices;
using EArm.Core.Diagnostics;

namespace EArm.Expressions;

/// <summary>
/// Guards the recursive walkers over parsed expressions (evaluator, validator, reference scanner, text rendering) against trees
/// that are deeper than the stack can take, however they were built. The parser itself caps nesting at
/// <see cref="Syntax.ExpressionParser.MaxNestingDepth"/>, so only hand-built trees can get here; the walker then fails with a
/// diagnostic instead of aborting the process (a stack overflow cannot be caught).
/// </summary>
internal static class ExpressionStackGuard
{
    internal const string TooDeepMessage = "The expression is nested too deeply to be processed (the stack limit was reached).";

    /// <summary>Throws <see cref="ExpressionEvaluationException"/> (<see cref="DiagnosticCodes.ExpressionTooDeep"/>) when the remaining stack is low.</summary>
    public static void Ensure(int? offset = null)
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.ExpressionTooDeep, TooDeepMessage, offset);
        }
    }
}
