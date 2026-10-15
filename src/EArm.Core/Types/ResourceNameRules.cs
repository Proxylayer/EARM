using System;
using System.Text.RegularExpressions;
using EArm.Core.Diagnostics;

namespace EArm.Core.Types;

/// <summary>
/// The rules a resource name must satisfy for its type, shared by the checks of literal names (the semantic checker, offline) and of names
/// computed by expressions (when the resource is expanded): segment shape, the catalog's <c>nameKind</c> and <c>namePattern</c>.
/// <para>
/// Beyond the catalog: a segment may not hold control characters (a trailing line feed passed a <c>$</c>-anchored pattern), may not start or
/// end with whitespace, and may not consist of dots only (<c>.</c> and <c>..</c> are path components, never names). Patterns are matched against
/// the whole segment (<c>^</c> and <c>$</c> are read as the start and the very end of the text, not of a line).
/// </para>
/// </summary>
public static class ResourceNameRules
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The problem with a complete name (all its segments) for <paramref name="definition"/>, or <c>null</c> when it is acceptable. The message
    /// is a single line (text taken from the name is shown escaped) and is reported as <see cref="DiagnosticCodes.InvalidResourceName"/>.
    /// </summary>
    public static string? Validate(ResourceTypeDefinition definition, string name)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(name);

        var segments = name.Split('/');
        if (segments.Length != definition.Depth)
        {
            string expected = definition.Depth == 1 ? "a single segment without '/'" : $"{definition.Depth} segments ('parent/child')";
            return $"Name '{SafeText.Escape(name)}' (evaluated) is not valid for type '{definition.Type}': expected {expected}.";
        }

        foreach (var segment in segments)
        {
            if (SegmentProblem(name, segment) is { } problem)
            {
                return problem;
            }
        }

        return LeafProblem(definition, segments[^1]);
    }

    /// <summary>The problem with one segment of <paramref name="fullName"/>: empty, a control character, outer whitespace, or only dots; <c>null</c> when it is fine.</summary>
    public static string? SegmentProblem(string fullName, string segment)
    {
        ArgumentNullException.ThrowIfNull(fullName);
        ArgumentNullException.ThrowIfNull(segment);

        string shown = SafeText.Escape(fullName);
        if (string.IsNullOrWhiteSpace(segment))
        {
            return $"Name '{shown}' contains an empty segment.";
        }

        foreach (char c in segment)
        {
            if (char.IsControl(c) || SafeText.NeedsEscape(c))
            {
                return $"Name '{shown}' contains a control character (U+{(int)c:X4}); names are single-line printable text.";
            }
        }

        if (char.IsWhiteSpace(segment[0]) || char.IsWhiteSpace(segment[^1]))
        {
            return $"Name '{shown}' has leading or trailing whitespace.";
        }

        if (segment.AsSpan().IndexOfAnyExcept('.') < 0)
        {
            return $"Name '{shown}' is not valid: '.' and '..' (a segment of dots only) cannot be resource names.";
        }

        return null;
    }

    /// <summary>The problem with the last segment of a name for the type's <c>nameKind</c> and <c>namePattern</c>; <c>null</c> when it is fine.</summary>
    public static string? LeafProblem(ResourceTypeDefinition definition, string leaf)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(leaf);

        string shown = SafeText.Escape(leaf);
        switch (definition.NameKind)
        {
            case ResourceNameKind.Guid:
                if (!Guid.TryParse(leaf, out _))
                {
                    return $"Name '{shown}' must be a GUID for type '{definition.Type}' (use the guid() function for a deterministic name).";
                }

                break;

            case ResourceNameKind.AppId:
                if (!Guid.TryParse(leaf, out _))
                {
                    return $"Name '{shown}' is not an application (client) id; a '{definition.Type}' is named by the appId (a GUID) of the application it is created for, for example [reference('my-app').appId].";
                }

                break;

            case ResourceNameKind.UserPrincipalName:
                if (!leaf.Contains('@', StringComparison.Ordinal))
                {
                    return $"Name '{shown}' must be a user principal name (user@domain) for type '{definition.Type}'.";
                }

                break;
        }

        if (definition.NamePattern is { } pattern && !MatchesPattern(pattern, leaf))
        {
            return $"Name '{shown}' is not valid for type '{definition.Type}'; it must match '{pattern}'.";
        }

        return null;
    }

    /// <summary>
    /// True when the whole of <paramref name="text"/> matches <paramref name="pattern"/>: a leading <c>^</c> is read as the start of the text and a
    /// trailing <c>$</c> as its very end (the .NET <c>$</c> also matches before a final line feed, which let <c>"abc\n"</c> through).
    /// </summary>
    public static bool MatchesPattern(string pattern, string text)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(text);

        string strict = StrictPattern(pattern);
        try
        {
            return Regex.IsMatch(text, strict, RegexOptions.CultureInvariant, PatternTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>The pattern with its outer anchors replaced by <c>\A</c> and <c>\z</c>.</summary>
    internal static string StrictPattern(string pattern)
    {
        string result = pattern;
        if (result.StartsWith('^'))
        {
            result = @"\A" + result[1..];
        }

        if (result.EndsWith('$') && !EndsWithEscapedDollar(result))
        {
            result = result[..^1] + @"\z";
        }

        return result;
    }

    private static bool EndsWithEscapedDollar(string pattern)
    {
        int backslashes = 0;
        for (int i = pattern.Length - 2; i >= 0 && pattern[i] == '\\'; i--)
        {
            backslashes++;
        }

        return backslashes % 2 == 1;
    }
}
