using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace EArm.Core.Json;

/// <summary>
/// Raised when a value tree is nested too deeply to be walked safely: either it exceeds <see cref="ValueGuard.MaxDepth"/>
/// or a recursive walker ran out of stack. Callers turn it into a <c>ValueTooDeep</c> diagnostic; it must never reach the
/// top level as an unhandled exception (a real stack overflow cannot be caught and aborts the process).
/// </summary>
public sealed class ValueTooDeepException : InvalidOperationException
{
    public ValueTooDeepException()
        : base(ValueGuard.TooDeepMessage)
    {
    }

    public ValueTooDeepException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Guards the recursive walkers over <see cref="JsonNode"/> value trees (cloning, taint propagation, canonical JSON,
/// equality, visiting). Values are capped at <see cref="MaxDepth"/> levels of nesting where they are created (the
/// evaluator checks every function result), which matches the nesting the JSON reader and writer accept; the walkers
/// additionally ask <see cref="EnsureStack"/> before they recurse, so even a tree built by hand fails with a catchable
/// <see cref="ValueTooDeepException"/> instead of overflowing the stack.
/// </summary>
public static class ValueGuard
{
    /// <summary>The deepest nesting a value may have (containers inside containers), the limit of the JSON reader and writer.</summary>
    public const int MaxDepth = 64;

    /// <summary>The text of the <c>ValueTooDeep</c> diagnostic.</summary>
    public const string TooDeepMessage = "A value is nested more than 64 levels deep (the limit of the JSON representation) or too deeply to be processed.";

    /// <summary>Throws <see cref="ValueTooDeepException"/> when the remaining stack is too small to recurse once more.</summary>
    public static void EnsureStack()
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw new ValueTooDeepException();
        }
    }

    /// <summary>
    /// True when <paramref name="node"/> nests containers more than <paramref name="maxDepth"/> levels deep (a scalar has depth 0,
    /// an array of scalars 1). Iterative, so it is safe on a tree of any depth; stops at the first level that is too deep.
    /// </summary>
    public static bool ExceedsDepth(JsonNode? node, int maxDepth = MaxDepth)
    {
        if (node is not JsonObject and not JsonArray)
        {
            return false;
        }

        var stack = new Stack<(JsonNode Node, int Depth)>();
        stack.Push((node, 1));
        while (stack.Count > 0)
        {
            var (current, depth) = stack.Pop();
            if (depth > maxDepth)
            {
                return true;
            }

            switch (current)
            {
                case JsonObject obj:
                    foreach (var property in obj)
                    {
                        if (property.Value is JsonObject or JsonArray)
                        {
                            stack.Push((property.Value, depth + 1));
                        }
                    }

                    break;
                case JsonArray array:
                    foreach (var item in array)
                    {
                        if (item is JsonObject or JsonArray)
                        {
                            stack.Push((item, depth + 1));
                        }
                    }

                    break;
            }
        }

        return false;
    }
}
