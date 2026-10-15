using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using EArm.Core.Diagnostics;

namespace EArm.Core.Json;

/// <summary>
/// One property name that appears more than once in the same JSON object; the position is that of the repeated (later) key.
/// <see cref="FirstKey"/> is the spelling of the earlier occurrence, which differs from <see cref="Key"/> when the two differ only in letter case.
/// </summary>
public readonly record struct DuplicateKey(JsonPath Path, SourceLocation Location, string Key, string? FirstKey = null)
{
    /// <summary>True when the repeated key is spelled differently from the first one (<c>state</c> and <c>State</c>).</summary>
    public bool DiffersInCase => FirstKey is not null && !string.Equals(FirstKey, Key, StringComparison.Ordinal);

    /// <summary>The text of the <c>DuplicateJsonKey</c> diagnostic.</summary>
    public string Describe()
    {
        if (DiffersInCase)
        {
            return $"The object defines the properties {SafeText.Quote(FirstKey!)} and {SafeText.Quote(Key)}, which differ only in letter case; JSON readers and property lookups disagree about which one wins, so both are rejected. Remove or rename one of them.";
        }

        return $"The object defines the property {SafeText.Quote(Key)} more than once; JSON readers disagree about which value wins, so a repeated key is rejected. Remove or rename one of them.";
    }
}

/// <summary>What <see cref="DuplicateKeys.Scan"/> found in one pass over a document.</summary>
public sealed record DuplicateKeyScan(IReadOnlyList<DuplicateKey> Duplicates, long Nodes, bool TooManyNodes);

/// <summary>
/// Finds repeated property names. System.Text.Json accepts <c>{"a":1,"a":2}</c> and builds a <see cref="System.Text.Json.Nodes.JsonObject"/> that
/// throws <see cref="ArgumentException"/> the first time it is read, far from the input; other JSON tools keep the first value, the last,
/// or fail. A template whose meaning depends on which one wins (a conditional access policy with two <c>state</c> keys) is rejected at load
/// time instead, with the path and position of the repeated key. Property names are compared ignoring letter case, because EARM itself looks
/// properties up that way (the first match wins), so <c>{"state": "enabledForReportingButNotEnforced", "State": "enabled"}</c> would otherwise
/// be judged on one key and sent with both. The only exception is the map of names a template declares (<c>parameters</c>, <c>variables</c>,
/// <c>outputs</c>), whose case variants are reported as <c>DuplicateDefinition</c> by the loader.
/// </summary>
public static class DuplicateKeys
{
    /// <summary>The most JSON values (objects, arrays and leaves) a document may hold; checked in the same pass.</summary>
    public const long MaxNodes = 2_000_000;

    /// <summary>The top-level properties of a template whose direct children are names (compared by the loader, not here).</summary>
    public static IReadOnlySet<string> TemplateNameMaps { get; } = new HashSet<string>(StringComparer.Ordinal) { "parameters", "variables", "outputs" };

    /// <summary>
    /// The top-level properties of a parameter file whose direct children are parameter names; the empty string stands for the root itself (the bare
    /// <c>{ name: { value } }</c> shape, whose names the parameter loader compares ignoring case and reports as <c>InvalidParameterFile</c>).
    /// </summary>
    public static IReadOnlySet<string> ParameterFileNameMaps { get; } = new HashSet<string>(StringComparer.Ordinal) { "parameters", string.Empty };

    /// <summary>
    /// The repeated keys of the document in <paramref name="utf8"/> (a leading byte-order mark is skipped), in document order, at most
    /// <paramref name="limit"/> of them. Text that is not valid JSON ends the scan early (the real parse reports the syntax error).
    /// <paramref name="nameMaps"/> names top-level properties whose direct children are compared case-sensitively only.
    /// </summary>
    public static IReadOnlyList<DuplicateKey> Find(ReadOnlyMemory<byte> utf8, int limit = 50, IReadOnlySet<string>? nameMaps = null)
    {
        return Scan(utf8, limit, nameMaps, long.MaxValue).Duplicates;
    }

    /// <summary>
    /// One pass over the document: the repeated keys (at most <paramref name="limit"/>) and the number of JSON values, stopping as soon as the
    /// number passes <paramref name="maxNodes"/> (<see cref="DuplicateKeyScan.TooManyNodes"/>), before a parser has built anything.
    /// </summary>
    public static DuplicateKeyScan Scan(ReadOnlyMemory<byte> utf8, int limit, IReadOnlySet<string>? nameMaps, long maxNodes)
    {
        var bytes = TemplateJson.StripBom(utf8);
        var found = new List<DuplicateKey>();
        var frames = new Stack<Frame>();
        string? pendingKey = null;
        long[]? lineStarts = null;
        long nodes = 0;
        try
        {
            var reader = new Utf8JsonReader(bytes.Span, TemplateJson.ReaderOptions);
            while (reader.Read())
            {
                if (found.Count >= limit)
                {
                    break;
                }

                switch (reader.TokenType)
                {
                    case JsonTokenType.Comment:
                        break;

                    case JsonTokenType.PropertyName:
                        {
                            var frame = frames.Peek();
                            string key = reader.GetString()!;
                            pendingKey = key;
                            frame.Keys ??= new Dictionary<string, string>(frame.ExactNames ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
                            if (!frame.Keys.TryAdd(key, key))
                            {
                                lineStarts ??= ComputeLineStarts(bytes.Span);
                                found.Add(new DuplicateKey(PathOf(frame).Append(key), Locate(bytes.Span, lineStarts, reader.TokenStartIndex), key, frame.Keys[key]));
                            }

                            break;
                        }

                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        {
                            if (++nodes > maxNodes)
                            {
                                return new DuplicateKeyScan(found, nodes, TooManyNodes: true);
                            }

                            var parent = frames.Count == 0 ? null : frames.Peek();
                            bool nameMap = reader.TokenType == JsonTokenType.StartObject && nameMaps is not null
                                && (parent is null ? nameMaps.Contains(string.Empty) : parent is { Parent: null, IsArray: false } && pendingKey is not null && nameMaps.Contains(pendingKey));
                            frames.Push(new Frame(parent, ValueKey(parent, ref pendingKey), reader.TokenType == JsonTokenType.StartArray)
                            {
                                ExactNames = nameMap,
                            });
                            break;
                        }

                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        frames.Pop();
                        break;

                    default:
                        if (++nodes > maxNodes)
                        {
                            return new DuplicateKeyScan(found, nodes, TooManyNodes: true);
                        }

                        ValueKey(frames.Count == 0 ? null : frames.Peek(), ref pendingKey);
                        break;
                }
            }
        }
        catch (JsonException)
        {
            // Not valid JSON: reported by the real parse.
        }

        return new DuplicateKeyScan(found, nodes, TooManyNodes: false);
    }

    /// <summary>The segment under which the next value hangs: the pending property name, or the next index of an array.</summary>
    private static PathStep ValueKey(Frame? parent, ref string? pendingKey)
    {
        if (parent is null)
        {
            return default;
        }

        if (parent.IsArray)
        {
            return new PathStep(null, parent.NextIndex++);
        }

        var step = new PathStep(pendingKey, 0);
        pendingKey = null;
        return step;
    }

    private static JsonPath PathOf(Frame frame)
    {
        var steps = new List<PathStep>();
        for (var current = frame; current is { Parent: not null }; current = current.Parent)
        {
            steps.Add(current.Step);
        }

        var path = JsonPath.Root;
        for (int i = steps.Count - 1; i >= 0; i--)
        {
            path = steps[i].Name is { } name ? path.Append(name) : path.Append(steps[i].Index);
        }

        return path;
    }

    private static long[] ComputeLineStarts(ReadOnlySpan<byte> bytes)
    {
        var starts = new List<long> { 0 };
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                starts.Add(i + 1);
            }
        }

        return [.. starts];
    }

    private static SourceLocation Locate(ReadOnlySpan<byte> bytes, long[] lineStarts, long offset)
    {
        int line = Array.BinarySearch(lineStarts, offset);
        if (line < 0)
        {
            line = ~line - 1;
        }

        int lineStart = (int)lineStarts[line];
        int column = Encoding.UTF8.GetCharCount(bytes.Slice(lineStart, (int)offset - lineStart)) + 1;
        return new SourceLocation(line + 1, column);
    }

    private readonly record struct PathStep(string? Name, int Index);

    private sealed class Frame
    {
        public Frame(Frame? parent, PathStep step, bool isArray)
        {
            Parent = parent;
            Step = step;
            IsArray = isArray;
        }

        public Frame? Parent { get; }

        public PathStep Step { get; }

        public bool IsArray { get; }

        public int NextIndex { get; set; }

        /// <summary>The object is one of the declared name maps: its keys are compared exactly (case variants of a declared name are the loader's business).</summary>
        public bool ExactNames { get; init; }

        public Dictionary<string, string>? Keys { get; set; }
    }
}
