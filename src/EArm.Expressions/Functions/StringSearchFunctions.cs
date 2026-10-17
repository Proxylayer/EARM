using System;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Expressions.Values;

namespace EArm.Expressions.Functions;

/// <summary>
/// The searching string functions (registered with the string group by <see cref="StringFunctions"/>): <c>contains</c>, <c>indexOf</c> and
/// <c>lastIndexOf</c>, which also accept arrays (and, for <c>contains</c>, objects), and the linear-time text search behind them.
/// </summary>
internal static class StringSearchFunctions
{
    internal static JsonNode? Contains(FunctionCall call)
    {
        var container = call.Arg(0);
        var item = call.Arg(1);
        switch (container)
        {
            case JsonArray array:
                call.Context.Budget.Charge(array.Count, call.Offset);
                return JsonValue.Create(array.Any(element => JsonValues.AreEqual(element, item)));
            case JsonObject obj:
                {
                    string key = call.String(1);
                    return JsonValue.Create(obj.Any(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)));
                }

            default:
                {
                    string text = call.String(0);
                    string needle = StringFunctions.Stringify(call, item);
                    return JsonValue.Create(Search(call, text, needle, last: false, ignoreCase: false) >= 0);
                }
        }
    }

    internal static JsonNode? IndexOf(FunctionCall call, bool last)
    {
        if (call.Arg(0) is JsonArray array)
        {
            var item = call.Arg(1);
            call.Context.Budget.Charge(array.Count, call.Offset);
            int found = -1;
            for (int i = 0; i < array.Count; i++)
            {
                if (JsonValues.AreEqual(array[i], item))
                {
                    found = i;
                    if (!last)
                    {
                        break;
                    }
                }
            }

            return JsonValue.Create((long)found);
        }

        string text = call.String(0);
        string needle = call.String(1);
        return JsonValue.Create((long)Search(call, text, needle, last, ignoreCase: true));
    }

    /// <summary>
    /// The index of the first (or last) occurrence of <paramref name="needle"/> in <paramref name="text"/>, or -1, compared case-insensitively
    /// when <paramref name="ignoreCase"/> is set (both are then folded to upper case once) and ordinally otherwise. Searched with Knuth-Morris-Pratt, so the time is linear in their lengths however the text and the needle repeat (the
    /// framework's case-insensitive <c>LastIndexOf</c> takes 12 seconds for 200,000 characters and minutes for 1 MiB). The work is charged to the
    /// run's budget first. An empty needle is found at 0 (first) or at the end (last), as in .NET.
    /// </summary>
    internal static int Search(FunctionCall call, string text, string needle, bool last, bool ignoreCase)
    {
        call.Context.Budget.ChargeText((long)text.Length + needle.Length, call.Offset);
        if (needle.Length == 0)
        {
            return last ? text.Length : 0;
        }

        if (needle.Length > text.Length)
        {
            return -1;
        }

        string haystack = ignoreCase ? text.ToUpperInvariant() : text;
        string pattern = ignoreCase ? needle.ToUpperInvariant() : needle;
        int m = pattern.Length;

        // The failure table of the pattern, read forward for a first match and backward (from the end) for a last one.
        var table = new int[m];
        for (int i = 1, k = 0; i < m; i++)
        {
            char pi = pattern[last ? m - 1 - i : i];
            while (k > 0 && pi != pattern[last ? m - 1 - k : k])
            {
                k = table[k - 1];
            }

            if (pi == pattern[last ? m - 1 - k : k])
            {
                k++;
            }

            table[i] = k;
        }

        int matched = 0;
        for (int i = 0; i < haystack.Length; i++)
        {
            int at = last ? haystack.Length - 1 - i : i;
            char c = haystack[at];
            while (matched > 0 && c != pattern[last ? m - 1 - matched : matched])
            {
                matched = table[matched - 1];
            }

            if (c == pattern[last ? m - 1 - matched : matched])
            {
                matched++;
            }

            if (matched == m)
            {
                return last ? at : at - m + 1;
            }
        }

        return -1;
    }
}
