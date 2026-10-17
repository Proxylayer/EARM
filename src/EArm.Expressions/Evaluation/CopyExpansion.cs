using System;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Expressions.Values;

namespace EArm.Expressions.Evaluation;

/// <summary>
/// The ARM iterations that build a value rather than resources: property iteration (<c>"copy": [ { name, count, input } ]</c>
/// inside resource properties and variable values), variable iteration (the same array at the top of <c>variables</c>) and output iteration
/// (<c>"copy": { count, input }</c> on an output). Each evaluates
/// <c>input</c> once per position with a concrete <see cref="CopyFrame"/> and collects the results in an array.
/// </summary>
public static class CopyExpansion
{
    /// <summary>
    /// Evaluates a loop count. Returns the count, or <c>null</c> with <paramref name="unknown"/> set when it is only known at
    /// deployment (or depends on an unresolved <c>copyIndex()</c> of an enclosing loop). Throws when the count is invalid:
    /// not a non-negative integer, above the ARM limit of 800, or computed from a secure value.
    /// </summary>
    public static long? EvaluateCount(IExpressionEvaluator evaluator, JsonNode? count, EvaluationContext context, string what, out UnknownValue? unknown)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(what);
        unknown = null;
        if (count is null)
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.InvalidCopy, $"{what}: 'count' is required.");
        }

        var value = evaluator.EvaluateTree(count, context);
        if (UnknownJson.ContainsTaint(value))
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.SecureValueInResourceKey, $"{what}: a secure value flows into 'count'; the loop count is logged and cannot be secret.");
        }

        if (UnknownJson.IsUnknown(value, out var deployTime))
        {
            unknown = deployTime;
            return null;
        }

        if (!JsonValues.TryGetWholeNumber(value, out long result) || result < 0)
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.InvalidCopy, $"{what}: 'count' must evaluate to a non-negative integer, got {JsonValues.Render(value)}.");
        }

        if (result > EvaluationLimits.MaxCopyCount)
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.ExpansionLimitExceeded, $"{what}: 'count' is {result}, more than the limit of {EvaluationLimits.MaxCopyCount} iterations (the ARM limit).");
        }

        return result;
    }

    /// <summary>
    /// Builds the array of a loop named <paramref name="name"/>: <paramref name="input"/> evaluated once per position. A count
    /// only known at deployment yields an unknown value with the count's origins. <paramref name="requiresName"/> marks a
    /// property iteration (only <c>copyIndex('name')</c> addresses it).
    /// </summary>
    public static JsonNode? Expand(IExpressionEvaluator evaluator, string name, JsonNode? count, JsonNode? input, EvaluationContext context, bool requiresName, string what)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(context);
        long? n = EvaluateCount(evaluator, count, context, what, out var unknown);
        if (n is null)
        {
            return UnknownJson.Create($"copy('{name}')", unknown!.Origins);
        }

        var result = new JsonArray();
        for (long i = 0; i < n.Value; i++)
        {
            var element = evaluator.EvaluateTree(input, context.PushCopy(new CopyFrame(name, i, n.Value, requiresName)));
            result.Add(element?.Parent is null ? element : JsonValues.Clone(element));
        }

        return result;
    }

    /// <summary>Evaluates the property iteration block entries of <paramref name="source"/> into <paramref name="result"/>, one property per entry.</summary>
    internal static void ExpandProperties(ExpressionEvaluator evaluator, JsonObject source, JsonObject result, EvaluationContext context)
    {
        foreach (var entry in PropertyCopyBlock.Entries(source))
        {
            if (entry.Name is null)
            {
                throw new ExpressionEvaluationException(DiagnosticCodes.InvalidCopy, $"Property copy entry {entry.Index} needs a non-empty literal 'name'.");
            }

            result[entry.Name] = Expand(evaluator, entry.Name, entry.Count, entry.Input, context, requiresName: true, $"property copy '{entry.Name}'");
        }
    }

    /// <summary>Evaluates an output: its <c>copy</c> loop when it has one, otherwise its <c>value</c>.</summary>
    public static JsonNode? EvaluateOutput(ExpressionEvaluator evaluator, Core.OutputDefinition output, EvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(context);
        return output.Copy is { } copy
            ? Expand(evaluator, output.Name, copy.Count, copy.Input, context, requiresName: false, $"output '{output.Name}' copy")
            : evaluator.EvaluateTree(output.Value, context);
    }
}
