using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using EntraArm.Expressions.Values;

namespace EntraArm.Expressions.Functions;

/// <summary>
/// Group 4a: <c>add sub mul div mod min max int float</c>. Integer arithmetic is 64-bit and checked (overflow is
/// an error); when either operand is a non-integral number the operation is performed in double precision.
/// <c>div</c> and <c>mod</c> on integers are integer operations (ARM rule).
/// </summary>
internal static class NumericFunctions
{
    private const string Group = "numeric";

    public static void Register(FunctionRegistry registry)
    {
        registry.Register(new FunctionDescriptor { Name = "add", Group = Group, MinArgs = 2, MaxArgs = 2, Implementation = call => Binary(call, static (a, b) => checked(a + b), static (a, b) => a + b) });
        registry.Register(new FunctionDescriptor { Name = "sub", Group = Group, MinArgs = 2, MaxArgs = 2, Implementation = call => Binary(call, static (a, b) => checked(a - b), static (a, b) => a - b) });
        registry.Register(new FunctionDescriptor { Name = "mul", Group = Group, MinArgs = 2, MaxArgs = 2, Implementation = call => Binary(call, static (a, b) => checked(a * b), static (a, b) => a * b) });
        registry.Register(new FunctionDescriptor { Name = "div", Group = Group, MinArgs = 2, MaxArgs = 2, Implementation = Div });
        registry.Register(new FunctionDescriptor { Name = "mod", Group = Group, MinArgs = 2, MaxArgs = 2, Implementation = Mod });
        registry.Register(new FunctionDescriptor { Name = "min", Group = Group, MinArgs = 1, Implementation = call => MinMax(call, min: true) });
        registry.Register(new FunctionDescriptor { Name = "max", Group = Group, MinArgs = 1, Implementation = call => MinMax(call, min: false) });
        registry.Register(new FunctionDescriptor { Name = "int", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = Int });
        registry.Register(new FunctionDescriptor { Name = "float", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = Float });
    }

    private static JsonNode? Binary(FunctionCall call, Func<long, long, long> integers, Func<double, double, double> doubles)
    {
        var left = call.Arg(0);
        var right = call.Arg(1);
        if (JsonValues.TryGetInt64(left, out long l) && JsonValues.TryGetInt64(right, out long r))
        {
            try
            {
                return JsonValue.Create(integers(l, r));
            }
            catch (OverflowException ex)
            {
                throw call.Failure($"integer overflow ({call.Render(0)}, {call.Render(1)}).", ex);
            }
        }

        return JsonValue.Create(doubles(Double(call, 0), Double(call, 1)));
    }

    private static JsonNode? Div(FunctionCall call)
    {
        var left = call.Arg(0);
        var right = call.Arg(1);
        if (JsonValues.TryGetInt64(left, out long l) && JsonValues.TryGetInt64(right, out long r))
        {
            if (r == 0)
            {
                throw call.Failure("division by zero.");
            }

            if (l == long.MinValue && r == -1)
            {
                throw call.Failure("integer overflow.");
            }

            return JsonValue.Create(l / r);
        }

        double divisor = Double(call, 1);
        if (divisor == 0)
        {
            throw call.Failure("division by zero.");
        }

        return JsonValue.Create(Double(call, 0) / divisor);
    }

    private static JsonNode? Mod(FunctionCall call)
    {
        var left = call.Arg(0);
        var right = call.Arg(1);
        if (JsonValues.TryGetInt64(left, out long l) && JsonValues.TryGetInt64(right, out long r))
        {
            if (r == 0)
            {
                throw call.Failure("division by zero.");
            }

            if (l == long.MinValue && r == -1)
            {
                return JsonValue.Create(0L);
            }

            return JsonValue.Create(l % r);
        }

        double divisor = Double(call, 1);
        if (divisor == 0)
        {
            throw call.Failure("division by zero.");
        }

        return JsonValue.Create(Double(call, 0) % divisor);
    }

    private static JsonNode? MinMax(FunctionCall call, bool min)
    {
        var values = new List<JsonNode?>();
        if (call.Count == 1 && call.Arg(0) is JsonArray array)
        {
            if (array.Count == 0)
            {
                throw call.Error("the array must not be empty.");
            }

            foreach (var item in array)
            {
                values.Add(item);
            }
        }
        else
        {
            for (int i = 0; i < call.Count; i++)
            {
                values.Add(call.Arg(i));
            }
        }

        bool allIntegers = true;
        var longs = new List<long>();
        var doubles = new List<double>();
        for (int i = 0; i < values.Count; i++)
        {
            if (JsonValues.TryGetInt64(values[i], out long l))
            {
                longs.Add(l);
                doubles.Add(l);
                continue;
            }

            if (JsonValues.TryGetDouble(values[i], out double d))
            {
                allIntegers = false;
                doubles.Add(d);
                continue;
            }

            throw call.Error($"value {i + 1} must be a number, got {JsonValues.Describe(values[i])}.");
        }

        if (allIntegers)
        {
            long result = longs[0];
            foreach (long l in longs)
            {
                result = min ? Math.Min(result, l) : Math.Max(result, l);
            }

            return JsonValue.Create(result);
        }

        double best = doubles[0];
        foreach (double d in doubles)
        {
            best = min ? Math.Min(best, d) : Math.Max(best, d);
        }

        return JsonValue.Create(best);
    }

    private static JsonNode? Int(FunctionCall call)
    {
        var node = call.Arg(0);
        if (JsonValues.TryGetInt64(node, out long l))
        {
            return JsonValue.Create(l);
        }

        if (JsonValues.TryGetString(node, out var text))
        {
            string trimmed = text.Trim();
            if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out l))
            {
                return JsonValue.Create(l);
            }

            if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && Math.Floor(parsed) == parsed && Math.Abs(parsed) < 9007199254740992d)
            {
                return JsonValue.Create((long)parsed);
            }

            throw call.Failure($"'{call.Redact(0, text)}' is not an integer.");
        }

        if (JsonValues.TryGetDouble(node, out double d))
        {
            throw call.Failure($"{call.Redact(0, d.ToString("R", CultureInfo.InvariantCulture))} is not an integer.");
        }

        throw call.Error($"cannot convert {JsonValues.Describe(node)} to an integer.");
    }

    private static JsonNode? Float(FunctionCall call)
    {
        var node = call.Arg(0);
        if (JsonValues.TryGetDouble(node, out double d))
        {
            return JsonValue.Create(d);
        }

        if (JsonValues.TryGetString(node, out var text))
        {
            if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
            {
                return JsonValue.Create(d);
            }

            throw call.Failure($"'{call.Redact(0, text)}' is not a number.");
        }

        throw call.Error($"cannot convert {JsonValues.Describe(node)} to a number.");
    }

    private static double Double(FunctionCall call, int index)
    {
        if (JsonValues.TryGetDouble(call.Arg(index), out double d))
        {
            return d;
        }

        throw call.Error($"argument {index + 1} must be a number, got {JsonValues.Describe(call.Arg(index))}.");
    }
}
