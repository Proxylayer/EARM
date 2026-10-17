using System;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Values;

namespace EArm.Expressions.Functions;

/// <summary>
/// ARM <c>format()</c> (registered with the string group by <see cref="StringFunctions"/>): composite formatting with every format item
/// checked against the run's budget before it is built.
/// </summary>
internal static class StringFormatFunctions
{
    /// <summary>The longest format specifier (the text after the colon of a format item) that is accepted.</summary>
    private const int MaxFormatSpecifierLength = 100;

    /// <summary>The largest precision of a standard numeric specifier (<c>D</c>, <c>N</c>, <c>F</c>, ...), as in <c>{0:D10}</c>.</summary>
    private const int MaxFormatPrecision = 100;

    private const string StandardSpecifiers = "DdNnFfXxGgPpEe";

    internal static JsonNode? Format(FunctionCall call)
    {
        string format = call.String(0);
        var args = new object?[call.Count - 1];
        for (int i = 1; i < call.Count; i++)
        {
            args[i - 1] = FormatArgument(call, call.Arg(i));
        }

        try
        {
            return JsonValue.Create(FormatItems(call, format, args));
        }
        catch (FormatException ex)
        {
            throw call.Failure(call.ArgumentIsTainted(0) ? "invalid format string." : $"invalid format string '{format}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// ARM <c>format()</c>: .NET composite formatting (<c>{index[,alignment][:specifier]}</c>, <c>{{</c> and <c>}}</c> for braces), done here instead of by
    /// <c>string.Format</c> because that one allocates what a format item asks for before anything can be checked (<c>{0:D999999999}</c> builds a
    /// 2 GB string, <c>{0,1000000}</c> repeated 2000 times 4 GB). Here every item is parsed, its specifier held to a short allow-list (standard
    /// numeric specifiers with a precision of at most 100, or a custom numeric format of at most 100 characters; a string argument ignores it, as in
    /// .NET), its alignment held to the string limit, and the running length of the result is checked before each piece is appended.
    /// </summary>
    private static string FormatItems(FunctionCall call, string format, object?[] args)
    {
        var budget = call.Context.Budget;
        budget.ChargeText(format.Length, call.Offset);
        var result = new StringBuilder();
        int position = 0;
        while (position < format.Length)
        {
            char c = format[position];
            if (c == '}')
            {
                if (position + 1 < format.Length && format[position + 1] == '}')
                {
                    result.Append('}');
                    position += 2;
                    continue;
                }

                throw new FormatException("Input string was not in a correct format (unmatched '}').");
            }

            if (c != '{')
            {
                int next = format.AsSpan(position).IndexOfAny('{', '}');
                int length = next < 0 ? format.Length - position : next;
                budget.CheckStringLength(result.Length + (long)length, call.Offset);
                result.Append(format, position, length);
                position += length;
                continue;
            }

            if (position + 1 < format.Length && format[position + 1] == '{')
            {
                result.Append('{');
                position += 2;
                continue;
            }

            position++;
            long index = ReadFormatNumber(format, ref position, "the argument index");
            SkipSpaces(format, ref position);
            long alignment = 0;
            if (position < format.Length && format[position] == ',')
            {
                position++;
                SkipSpaces(format, ref position);
                bool left = position < format.Length && format[position] == '-';
                if (left)
                {
                    position++;
                }

                alignment = ReadFormatNumber(format, ref position, "the alignment");
                if (alignment > EvaluationLimits.MaxStringLength)
                {
                    throw new ExpressionEvaluationException(EArm.Core.Diagnostics.DiagnosticCodes.EvaluationBudgetExceeded, $"{call.Name}(): a format item pads to more than {EvaluationLimits.MaxStringLength:N0} characters (1 MiB).", call.Offset);
                }

                if (left)
                {
                    alignment = -alignment;
                }

                SkipSpaces(format, ref position);
            }

            string? specifier = null;
            if (position < format.Length && format[position] == ':')
            {
                position++;
                var spec = new StringBuilder();
                while (true)
                {
                    if (position >= format.Length)
                    {
                        throw new FormatException("Input string was not in a correct format (the format item is not closed).");
                    }

                    char s = format[position];
                    if (s == '{')
                    {
                        if (position + 1 < format.Length && format[position + 1] == '{')
                        {
                            spec.Append('{');
                            position += 2;
                            continue;
                        }

                        throw new FormatException("Input string was not in a correct format (unexpected '{' in a format specifier).");
                    }

                    if (s == '}')
                    {
                        if (position + 1 < format.Length && format[position + 1] == '}')
                        {
                            spec.Append('}');
                            position += 2;
                            continue;
                        }

                        break;
                    }

                    if (spec.Length >= MaxFormatSpecifierLength)
                    {
                        throw call.Error($"a format specifier is longer than {MaxFormatSpecifierLength} characters.");
                    }

                    spec.Append(s);
                    position++;
                }

                specifier = spec.ToString();
            }

            if (position >= format.Length || format[position] != '}')
            {
                throw new FormatException("Input string was not in a correct format (the format item is not closed).");
            }

            position++;
            if (index >= args.Length)
            {
                throw new FormatException("Index (zero based) must be greater than or equal to zero and less than the size of the argument list.");
            }

            string piece = FormatPiece(call, args[(int)index], specifier);
            long width = Math.Max(piece.Length, Math.Abs(alignment));
            budget.CheckStringLength(result.Length + width, call.Offset);
            if (alignment > piece.Length)
            {
                result.Append(' ', (int)(alignment - piece.Length));
            }

            result.Append(piece);
            if (-alignment > piece.Length)
            {
                result.Append(' ', (int)(-alignment - piece.Length));
            }
        }

        return result.ToString();
    }

    private static long ReadFormatNumber(string format, ref int position, string what)
    {
        int start = position;
        long value = 0;
        while (position < format.Length && char.IsAsciiDigit(format[position]))
        {
            // Saturate: anything this large is refused by the caller's limits; the digits are still consumed.
            value = Math.Min(value * 10 + (format[position] - '0'), 1_000_000_000_000L);
            position++;
        }

        if (position == start)
        {
            throw new FormatException($"Input string was not in a correct format ({what} is missing).");
        }

        return value;
    }

    private static void SkipSpaces(string format, ref int position)
    {
        while (position < format.Length && format[position] == ' ')
        {
            position++;
        }
    }

    private static string FormatPiece(FunctionCall call, object? argument, string? specifier)
    {
        switch (argument)
        {
            case string text:
                return text;
            case long or double:
                {
                    string? checkedSpecifier = CheckSpecifier(call, specifier);
                    var formattable = (IFormattable)argument;
                    return formattable.ToString(checkedSpecifier, CultureInfo.InvariantCulture);
                }

            default:
                return Convert.ToString(argument, CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    /// <summary>
    /// A specifier for a numeric argument: empty means none; a single letter followed by digits is a standard specifier (<c>D N F X G P E</c>,
    /// either case) whose precision is at most 100; anything else is a custom numeric format, bounded by its length (the output of a custom format
    /// is no longer than the format plus the digits of the number).
    /// </summary>
    private static string? CheckSpecifier(FunctionCall call, string? specifier)
    {
        if (string.IsNullOrEmpty(specifier))
        {
            return null;
        }

        if (char.IsAsciiLetter(specifier[0]) && specifier.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0)
        {
            if (StandardSpecifiers.IndexOf(specifier[0], StringComparison.Ordinal) < 0)
            {
                throw call.Error($"the format specifier '{specifier[0]}' is not supported (use D, N, F, X, G, P or E, or a custom numeric format such as 0.00).");
            }

            if (specifier.Length > 1 && (specifier.Length > 4 || int.Parse(specifier.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture) > MaxFormatPrecision))
            {
                throw call.Error($"the precision of a format specifier must be at most {MaxFormatPrecision}.");
            }
        }

        return specifier;
    }
    private static object? FormatArgument(FunctionCall call, JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        if (JsonValues.TryGetString(node, out var text))
        {
            return text;
        }

        if (JsonValues.TryGetInt64(node, out long l))
        {
            return l;
        }

        if (JsonValues.TryGetDouble(node, out double d))
        {
            return d;
        }

        return StringFunctions.Stringify(call, node);
    }
}
