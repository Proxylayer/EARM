using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Secrets;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Syntax;
using EArm.Expressions.Values;

namespace EArm.Expressions.Functions;

/// <summary>
/// Group 5: <c>map filter reduce sort toObject groupBy lambdaVariables</c>. A lambda is a
/// <see cref="LambdaExpression"/> argument, so these functions are lazy: they evaluate the array argument, then the
/// lambda body once per element with the parameters bound in <see cref="EvaluationContext.LambdaScope"/>
/// (<see cref="FunctionCall.InvokeLambda"/>); <c>lambdaVariables('x')</c> reads a binding.
/// <para>
/// Propagation: an unknown array yields an unknown result (symbol <c>map(&lt;symbol&gt;, lambda(...))</c>); unknown
/// elements flow through the lambda like any value, except where the function must decide on the lambda's result
/// (a <c>filter</c> verdict, a <c>sort</c> comparison, a <c>toObject</c>/<c>groupBy</c> key), in which case the
/// whole result is unknown. Secret elements are handled by the body's functions; a secret verdict, comparison or key
/// taints the whole result, as <c>if()</c> does with a secret condition.
/// </para>
/// </summary>
internal static class LambdaFunctions
{
    private const string Group = "lambda";

    /// <summary>The functions that take a lambda, for messages.</summary>
    public const string TakingFunctions = "map, filter, reduce, sort, toObject, groupBy";

    /// <summary>Where a function takes a lambda and how many parameters it may declare (for the validator and the implementations).</summary>
    public sealed record LambdaSlot(int MinParameters, int MaxParameters, string Roles);

    private static readonly LambdaSlot Element = new(1, 1, "element");
    private static readonly LambdaSlot ElementAndIndex = new(1, 2, "element[, index]");
    private static readonly LambdaSlot Accumulator = new(2, 3, "current, next[, index]");
    private static readonly LambdaSlot Comparison = new(2, 2, "left, right");

    private static readonly Dictionary<string, LambdaSlot?[]> Slots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["map"] = [null, ElementAndIndex],
        ["filter"] = [null, ElementAndIndex],
        ["reduce"] = [null, null, Accumulator],
        ["sort"] = [null, Comparison],
        ["toObject"] = [null, Element, Element],
        ["groupBy"] = [null, Element],
    };

    /// <summary>True when argument <paramref name="argumentIndex"/> of built-in <paramref name="function"/> must be a lambda.</summary>
    public static bool TryGetLambdaSlot(string function, int argumentIndex, [NotNullWhen(true)] out LambdaSlot? slot)
    {
        slot = null;
        if (Slots.TryGetValue(function, out var slots) && argumentIndex >= 0 && argumentIndex < slots.Length && slots[argumentIndex] is { } found)
        {
            slot = found;
            return true;
        }

        return false;
    }

    public static void Register(FunctionRegistry registry)
    {
        registry.Register(new FunctionDescriptor { Name = "map", Group = Group, MinArgs = 2, MaxArgs = 2, Lazy = true, Implementation = Map });
        registry.Register(new FunctionDescriptor { Name = "filter", Group = Group, MinArgs = 2, MaxArgs = 2, Lazy = true, Implementation = Filter });
        registry.Register(new FunctionDescriptor { Name = "reduce", Group = Group, MinArgs = 3, MaxArgs = 3, Lazy = true, Implementation = Reduce });
        registry.Register(new FunctionDescriptor { Name = "sort", Group = Group, MinArgs = 2, MaxArgs = 2, Lazy = true, Implementation = Sort });
        registry.Register(new FunctionDescriptor { Name = "toObject", Group = Group, MinArgs = 2, MaxArgs = 3, Lazy = true, Implementation = ToObject });
        registry.Register(new FunctionDescriptor { Name = "groupBy", Group = Group, MinArgs = 2, MaxArgs = 2, Lazy = true, Implementation = GroupBy });
        registry.Register(new FunctionDescriptor { Name = "lambdaVariables", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = LambdaVariables });
    }

    private static JsonNode? LambdaVariables(FunctionCall call)
    {
        string name = call.String(0);
        var scope = call.Context.LambdaScope;
        if (scope.Count == 0)
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.FunctionNotAllowedHere, $"lambdaVariables() can only be used inside the body of a lambda passed to {TakingFunctions}.", call.Offset);
        }

        if (!scope.TryGetValue(name, out var value))
        {
            // Lambda parameter names are template source, safe to list; the requested name may be computed.
            throw call.Error($"'{call.Redact(0, name)}' is not a parameter of an enclosing lambda (in scope: {string.Join(", ", scope.Keys)}).");
        }

        return JsonValues.Clone(value);
    }

    private static JsonNode? Map(FunctionCall call)
    {
        var lambda = Lambda(call, 1);
        if (!TryGetArray(call, lambda, out var array, out var unknown))
        {
            return unknown;
        }

        var result = new JsonArray();
        for (int i = 0; i < array.Count; i++)
        {
            result.Add(Own(call.InvokeLambda(lambda, array[i], JsonValue.Create((long)i))));
        }

        return result;
    }

    private static JsonNode? Filter(FunctionCall call)
    {
        var lambda = Lambda(call, 1);
        if (!TryGetArray(call, lambda, out var array, out var unknown))
        {
            return unknown;
        }

        var result = new JsonArray();
        var origins = UnknownOrigins.None;
        bool anyUnknown = false;
        bool tainted = false;
        for (int i = 0; i < array.Count; i++)
        {
            var verdict = call.InvokeLambda(lambda, array[i], JsonValue.Create((long)i));
            if (UnknownJson.IsUnknown(verdict, out var unknownVerdict))
            {
                anyUnknown = true;
                origins |= unknownVerdict.Origins;
                tainted |= unknownVerdict.Tainted;
                continue;
            }

            if (!TryGetBool(verdict, ref tainted, out bool keep))
            {
                throw call.Error($"the lambda must return a boolean for every element, got {JsonValues.Describe(verdict)} for element {i}.");
            }

            if (keep)
            {
                result.Add(JsonValues.Clone(array[i]));
            }
        }

        if (anyUnknown)
        {
            return UnknownResult(call, lambda, origins, tainted);
        }

        return tainted ? TaintHelper.Taint(result) : result;
    }

    private static JsonNode? Reduce(FunctionCall call)
    {
        var lambda = Lambda(call, 2);
        var source = call.EvaluateArgument(0);
        var initial = call.EvaluateArgument(1);
        if (UnknownJson.IsUnknown(source, out var unknownSource))
        {
            return UnknownJson.Create($"{call.Name}({unknownSource.Symbol}, {JsonValues.Render(initial)}, {lambda.ToExpressionText()})", unknownSource.Origins | UnknownJson.OriginsOf(initial), unknownSource.Tainted || UnknownJson.ContainsTaint(initial));
        }

        var array = RequireArray(call, source);
        bool sourceTainted = array.Count == 0 && SecretJson.IsTaintedEmpty(array);
        var current = Own(initial);
        for (int i = 0; i < array.Count; i++)
        {
            current = Own(call.InvokeLambda(lambda, current, array[i], JsonValue.Create((long)i)));
        }

        // A source that is empty because of a secret leaves the initial value as the result: tainted, or it would reveal the emptiness.
        return sourceTainted ? TaintHelper.Taint(current) : current;
    }

    /// <summary>
    /// Stable merge sort driven by the lambda's "left sorts before right" verdict: an element moves ahead of an
    /// earlier one only when the lambda says so, so equal elements keep their order and each merge step asks once.
    /// </summary>
    private static JsonNode? Sort(FunctionCall call)
    {
        var lambda = Lambda(call, 1);
        if (!TryGetArray(call, lambda, out var array, out var unknown))
        {
            return unknown;
        }

        var state = new SortState(call, lambda);
        var sorted = MergeSort(array.ToList(), state);
        if (state.AnyUnknown)
        {
            return UnknownResult(call, lambda, state.Origins, state.Tainted);
        }

        var result = new JsonArray();
        foreach (var item in sorted)
        {
            result.Add(JsonValues.Clone(item));
        }

        return state.Tainted ? TaintHelper.Taint(result) : result;
    }

    private sealed class SortState(FunctionCall call, LambdaExpression lambda)
    {
        public bool AnyUnknown { get; private set; }

        public bool Tainted { get; private set; }

        public UnknownOrigins Origins { get; private set; }

        /// <summary>True when <paramref name="left"/> sorts before <paramref name="right"/>; false once a verdict is unknown (the order no longer matters).</summary>
        public bool Before(JsonNode? left, JsonNode? right)
        {
            if (AnyUnknown)
            {
                return false;
            }

            var verdict = call.InvokeLambda(lambda, left, right);
            if (UnknownJson.IsUnknown(verdict, out var unknownVerdict))
            {
                AnyUnknown = true;
                Origins |= unknownVerdict.Origins;
                Tainted |= unknownVerdict.Tainted;
                return false;
            }

            bool tainted = Tainted;
            if (!TryGetBool(verdict, ref tainted, out bool before))
            {
                throw call.Error($"the lambda must return a boolean (true when the first element sorts before the second), got {JsonValues.Describe(verdict)}.");
            }

            Tainted = tainted;
            return before;
        }
    }

    private static List<JsonNode?> MergeSort(List<JsonNode?> items, SortState state)
    {
        if (items.Count < 2)
        {
            return items;
        }

        int middle = items.Count / 2;
        var left = MergeSort(items.GetRange(0, middle), state);
        var right = MergeSort(items.GetRange(middle, items.Count - middle), state);
        var merged = new List<JsonNode?>(items.Count);
        int l = 0;
        int r = 0;
        while (l < left.Count && r < right.Count)
        {
            if (state.Before(right[r], left[l]))
            {
                merged.Add(right[r++]);
            }
            else
            {
                merged.Add(left[l++]);
            }
        }

        merged.AddRange(left.Skip(l));
        merged.AddRange(right.Skip(r));
        return merged;
    }

    private static JsonNode? ToObject(FunctionCall call)
    {
        var keyLambda = Lambda(call, 1);
        var valueLambda = call.Count > 2 ? Lambda(call, 2) : null;
        if (!TryGetArray(call, keyLambda, out var array, out var unknown))
        {
            return unknown;
        }

        var result = new MaskedKeyObject();
        var origins = UnknownOrigins.None;
        bool anyUnknown = false;
        bool tainted = false;
        for (int i = 0; i < array.Count; i++)
        {
            if (!TryGetKey(call, keyLambda, array[i], i, ref tainted, ref origins, out var key, out bool secretKey))
            {
                anyUnknown = true;
                continue;
            }

            if (result.ContainsKey(key))
            {
                throw call.Failure($"the key lambda produced the duplicate key {(secretKey || result.IsSecretKey(key) ? SecretValue.Mask : "'" + key + "'")} (element {i}); keys must be unique.");
            }

            result.Set(key, secretKey, valueLambda is null ? JsonValues.Clone(array[i]) : Own(call.InvokeLambda(valueLambda, array[i])));
        }

        if (anyUnknown)
        {
            return UnknownResult(call, keyLambda, origins, tainted);
        }

        // Keys derived from secure values are masked by Build (object keys carry no taint of their own).
        var built = result.Build();
        return tainted ? TaintHelper.Taint(built) : built;
    }

    private static JsonNode? GroupBy(FunctionCall call)
    {
        var lambda = Lambda(call, 1);
        if (!TryGetArray(call, lambda, out var array, out var unknown))
        {
            return unknown;
        }

        var result = new MaskedKeyObject();
        var origins = UnknownOrigins.None;
        bool anyUnknown = false;
        bool tainted = false;
        for (int i = 0; i < array.Count; i++)
        {
            if (!TryGetKey(call, lambda, array[i], i, ref tainted, ref origins, out var key, out bool secretKey))
            {
                anyUnknown = true;
                continue;
            }

            if (!result.TryGetValue(key, out var existing) || existing is not JsonArray group)
            {
                group = new JsonArray();
            }

            result.Set(key, secretKey, group);
            group.Add(JsonValues.Clone(array[i]));
        }

        if (anyUnknown)
        {
            return UnknownResult(call, lambda, origins, tainted);
        }

        // Keys derived from secure values are masked by Build (object keys carry no taint of their own).
        var built = result.Build();
        return tainted ? TaintHelper.Taint(built) : built;
    }

    // ---- helpers ----

    private static LambdaExpression Lambda(FunctionCall call, int index)
    {
        if (!TryGetLambdaSlot(call.Name, index, out var slot))
        {
            throw new InvalidOperationException($"{call.Name}() has no lambda slot at argument {index + 1}.");
        }

        return call.Lambda(index, slot.MinParameters, slot.MaxParameters, slot.Roles);
    }

    /// <summary>Evaluates the array argument; false with the unknown result when it is a deploy-time unknown.</summary>
    private static bool TryGetArray(FunctionCall call, LambdaExpression lambda, out JsonArray array, out JsonNode? unknown)
    {
        var source = call.EvaluateArgument(0);
        if (UnknownJson.IsUnknown(source, out var unknownSource))
        {
            array = null!;
            unknown = UnknownJson.Create($"{call.Name}({unknownSource.Symbol}, {LambdaArguments(call, lambda)})", unknownSource.Origins, unknownSource.Tainted);
            return false;
        }

        array = RequireArray(call, source);
        unknown = null;
        if (array.Count == 0 && SecretJson.IsTaintedEmpty(array))
        {
            // The source is empty BECAUSE of a secret: there is nothing to run the lambda on, but the result must still say so (an untainted empty result would be an oracle).
            unknown = MarkedEmptyResult(call);
            return false;
        }

        return true;
    }

    /// <summary>The result of a lambda function over a source that is empty because of a secret: an empty object for the functions that build one, else an empty array; tainted either way.</summary>
    private static JsonNode MarkedEmptyResult(FunctionCall call)
    {
        bool objectResult = call.Name.Equals("toObject", StringComparison.OrdinalIgnoreCase) || call.Name.Equals("groupBy", StringComparison.OrdinalIgnoreCase);
        return SecretJson.MarkTaintedEmpty(objectResult ? new JsonObject() : new JsonArray());
    }

    private static JsonArray RequireArray(FunctionCall call, JsonNode? source)
    {
        return source as JsonArray ?? throw call.Error($"argument 1 must be an array, got {JsonValues.Describe(source)}.");
    }

    /// <summary>The key lambda's result as a string; false (with origins/taint accumulated) when it is unknown. <paramref name="secretKey"/> is true when the key was derived from a secure value (it must then be masked, never written as a property name).</summary>
    private static bool TryGetKey(FunctionCall call, LambdaExpression lambda, JsonNode? element, int index, ref bool tainted, ref UnknownOrigins origins, out string key, out bool secretKey)
    {
        key = string.Empty;
        secretKey = false;
        var value = call.InvokeLambda(lambda, element);
        if (UnknownJson.IsUnknown(value, out var unknownKey))
        {
            origins |= unknownKey.Origins;
            tainted |= unknownKey.Tainted;
            return false;
        }

        if (SecretJson.IsSecret(value))
        {
            tainted = true;
            secretKey = true;
            value = TaintHelper.RevealSecrets(value);
        }

        if (!JsonValues.TryGetString(value, out key))
        {
            throw call.Error($"the key lambda must return a string for every element, got {JsonValues.Describe(value)} for element {index}.");
        }

        return true;
    }

    private static bool TryGetBool(JsonNode? value, ref bool tainted, out bool result)
    {
        if (SecretJson.IsSecret(value))
        {
            tainted = true;
            value = TaintHelper.RevealSecrets(value);
        }

        return JsonValues.TryGetBool(value, out result);
    }

    /// <summary>
    /// The unknown result of a call whose elements were known but whose verdicts or keys were not: the symbol is the
    /// call as written (template source), since rendering the evaluated array would not be canonical.
    /// </summary>
    private static JsonNode UnknownResult(FunctionCall call, LambdaExpression lambda, UnknownOrigins origins, bool tainted)
    {
        return UnknownJson.Create($"{call.Name}({call.ArgumentExpressions[0].ToExpressionText()}, {LambdaArguments(call, lambda)})", origins == UnknownOrigins.None ? UnknownOrigins.Reference : origins, tainted);
    }

    /// <summary>The lambda argument(s) of the call as text (the value lambda of <c>toObject</c> included), for symbols.</summary>
    private static string LambdaArguments(FunctionCall call, LambdaExpression first)
    {
        var parts = new List<string>();
        for (int i = 1; i < call.ArgumentExpressions.Count; i++)
        {
            parts.Add(call.ArgumentExpressions[i] is LambdaExpression ? call.ArgumentExpressions[i].ToExpressionText() : JsonValues.Render(call.EvaluateArgument(i)));
        }

        return parts.Count == 0 ? first.ToExpressionText() : string.Join(", ", parts);
    }

    /// <summary>A lambda result as a node that can be attached to a new tree (results are fresh, but a clone is taken when one still has a parent).</summary>
    private static JsonNode? Own(JsonNode? value)
    {
        return value?.Parent is null ? value : JsonValues.Clone(value);
    }
}
