using System.Text.Json.Nodes;
using EntraArm.Expressions.Values;

namespace EntraArm.Expressions.Functions;

/// <summary>
/// Group 3: <c>if and or not equals less lessOrEquals greater greaterOrEquals coalesce bool true false</c>.
/// <c>if</c> evaluates only the selected branch (ARM rule); <c>and</c>/<c>or</c> evaluate every argument.
/// <c>equals</c> is case-sensitive for strings (ARM: Bicep <c>==</c>).
/// </summary>
internal static class LogicFunctions
{
    private const string Group = "logic";

    public static void Register(FunctionRegistry registry)
    {
        registry.Register(new FunctionDescriptor { Name = "if", Group = Group, MinArgs = 3, MaxArgs = 3, Lazy = true, Implementation = If });
        registry.Register(new FunctionDescriptor { Name = "and", Group = Group, MinArgs = 1, Implementation = call => AllOrAny(call, all: true) });
        registry.Register(new FunctionDescriptor { Name = "or", Group = Group, MinArgs = 1, Implementation = call => AllOrAny(call, all: false) });
        registry.Register(new FunctionDescriptor { Name = "not", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = call => JsonValue.Create(!call.Bool(0)) });
        registry.Register(new FunctionDescriptor { Name = "equals", Group = Group, MinArgs = 2, MaxArgs = 2, Implementation = call => JsonValue.Create(JsonValues.AreEqual(call.Arg(0), call.Arg(1))) });
        registry.Register(new FunctionDescriptor { Name = "less", Group = Group, MinArgs = 2, MaxArgs = 2, Implementation = call => JsonValue.Create(Compare(call) < 0) });
        registry.Register(new FunctionDescriptor { Name = "lessOrEquals", Group = Group, MinArgs = 2, MaxArgs = 2, Implementation = call => JsonValue.Create(Compare(call) <= 0) });
        registry.Register(new FunctionDescriptor { Name = "greater", Group = Group, MinArgs = 2, MaxArgs = 2, Implementation = call => JsonValue.Create(Compare(call) > 0) });
        registry.Register(new FunctionDescriptor { Name = "greaterOrEquals", Group = Group, MinArgs = 2, MaxArgs = 2, Implementation = call => JsonValue.Create(Compare(call) >= 0) });
        registry.Register(new FunctionDescriptor { Name = "coalesce", Group = Group, MinArgs = 1, HandlesUnknown = true, Implementation = Coalesce });
        registry.Register(new FunctionDescriptor { Name = "bool", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = Bool });
        registry.Register(new FunctionDescriptor { Name = "true", Group = Group, MinArgs = 0, MaxArgs = 0, Implementation = _ => JsonValue.Create(true) });
        registry.Register(new FunctionDescriptor { Name = "false", Group = Group, MinArgs = 0, MaxArgs = 0, Implementation = _ => JsonValue.Create(false) });
    }

    private static JsonNode? If(FunctionCall call)
    {
        var condition = call.EvaluateArgument(0);
        if (UnknownJson.IsUnknown(condition, out var unknown))
        {
            return UnknownJson.Create($"if({unknown.Symbol}, {call.ArgumentExpressions[1].ToExpressionText()}, {call.ArgumentExpressions[2].ToExpressionText()})", unknown.Origins, unknown.Tainted);
        }

        bool tainted = UnknownJson.ContainsTaint(condition);
        var plain = tainted ? TaintHelper.RevealSecrets(condition) : condition;
        if (!JsonValues.TryGetBool(plain, out bool value))
        {
            throw call.Error($"the condition must be a boolean, got {JsonValues.Describe(condition)}.");
        }

        var result = call.EvaluateArgument(value ? 1 : 2);
        return tainted ? TaintHelper.Taint(result) : result;
    }

    private static JsonNode? AllOrAny(FunctionCall call, bool all)
    {
        bool result = all;
        for (int i = 0; i < call.Count; i++)
        {
            bool value = call.Bool(i);
            result = all ? result && value : result || value;
        }

        return JsonValue.Create(result);
    }

    private static int Compare(FunctionCall call)
    {
        var left = call.Arg(0);
        var right = call.Arg(1);
        if (JsonValues.TryGetInt64(left, out long l) && JsonValues.TryGetInt64(right, out long r))
        {
            return l.CompareTo(r);
        }

        if (JsonValues.TryGetDouble(left, out double dl) && JsonValues.TryGetDouble(right, out double dr))
        {
            return dl.CompareTo(dr);
        }

        if (JsonValues.TryGetString(left, out var sl) && JsonValues.TryGetString(right, out var sr))
        {
            return string.CompareOrdinal(sl, sr);
        }

        throw call.Error($"both arguments must be numbers or both strings, got {JsonValues.Describe(left)} and {JsonValues.Describe(right)}.");
    }

    private static JsonNode? Coalesce(FunctionCall call)
    {
        for (int i = 0; i < call.Count; i++)
        {
            var node = call.Arg(i);
            if (node is not null)
            {
                return JsonValues.Clone(node);
            }
        }

        return null;
    }

    private static JsonNode? Bool(FunctionCall call)
    {
        var node = call.Arg(0);
        if (JsonValues.TryGetBool(node, out bool b))
        {
            return JsonValue.Create(b);
        }

        if (JsonValues.TryGetInt64(node, out long l))
        {
            return JsonValue.Create(l != 0);
        }

        bool isString = JsonValues.TryGetString(node, out var text);
        if (isString && bool.TryParse(text.Trim(), out bool parsed))
        {
            return JsonValue.Create(parsed);
        }

        string shown = isString ? $"'{call.Redact(0, text)}'" : JsonValues.Describe(node);
        throw call.Error($"cannot convert {shown} to a boolean; expected 'true', 'false' or an integer.");
    }
}
