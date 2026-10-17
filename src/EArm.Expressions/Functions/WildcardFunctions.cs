using System;
using System.Text.Json.Nodes;

namespace EArm.Expressions.Functions;

/// <summary>
/// <c>like(stringToCheck, pattern)</c> (registered with the string group): a case-insensitive match in which <c>*</c> stands for any sequence of
/// zero or more characters. ARM documents no other wildcard, so every other character, <c>?</c> included, matches only itself.
/// </summary>
internal static class WildcardFunctions
{
    public static void Register(FunctionRegistry registry, string group)
    {
        registry.Register(new FunctionDescriptor { Name = "like", Group = group, MinArgs = 2, MaxArgs = 2, Implementation = Like });
    }

    private static JsonNode? Like(FunctionCall call)
    {
        string text = call.String(0);
        string pattern = call.String(1);
        call.Context.Budget.ChargeText((long)text.Length + pattern.Length, call.Offset);
        return JsonValue.Create(Matches(text, pattern));
    }

    /// <summary>
    /// True when <paramref name="text"/> matches <paramref name="pattern"/>. The literal segments between the stars must appear in order: the first
    /// anchored at the start (unless the pattern starts with <c>*</c>), the last at the end (unless it ends with <c>*</c>); taking the leftmost
    /// occurrence of each middle segment is enough when <c>*</c> is the only wildcard.
    /// </summary>
    internal static bool Matches(string text, string pattern)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(pattern);
        var segments = pattern.Split('*');
        if (segments.Length == 1)
        {
            return string.Equals(text, pattern, StringComparison.OrdinalIgnoreCase);
        }

        string head = segments[0];
        string tail = segments[^1];
        if (head.Length + tail.Length > text.Length
            || !text.StartsWith(head, StringComparison.OrdinalIgnoreCase)
            || !text.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int position = head.Length;
        int end = text.Length - tail.Length;
        for (int i = 1; i < segments.Length - 1; i++)
        {
            string segment = segments[i];
            if (segment.Length == 0)
            {
                continue;
            }

            int found = text.IndexOf(segment, position, end - position, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return false;
            }

            position = found + segment.Length;
        }

        return true;
    }
}
