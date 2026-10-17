using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using EArm.Core.Diagnostics;

namespace EArm.Core.Json;

/// <summary>
/// Maps JSON paths of a parsed file to line/column positions. Built by a second, node-free pass of
/// <see cref="Utf8JsonReader"/> over the same bytes the document was parsed from: object properties map to the
/// start of their key, array elements to the start of their value, the root to its first token.
/// </summary>
public sealed class SourceMap
{
    public static SourceMap Empty { get; } = new(new Dictionary<string, int>(StringComparer.Ordinal), [], []);

    private readonly Dictionary<string, int> _offsets;
    private readonly int[] _lineStarts;
    private readonly byte[] _utf8;

    private SourceMap(Dictionary<string, int> offsets, int[] lineStarts, byte[] utf8)
    {
        _offsets = offsets;
        _lineStarts = lineStarts;
        _utf8 = utf8;
    }

    /// <summary>Number of paths recorded.</summary>
    public int Count => _offsets.Count;

    /// <summary>Builds the map; returns <see cref="Empty"/> when the text cannot be tokenised.</summary>
    public static SourceMap Build(ReadOnlyMemory<byte> utf8)
    {
        byte[] bytes = TemplateJson.StripBom(utf8).ToArray();
        var offsets = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            Record(bytes, offsets);
        }
        catch (JsonException)
        {
            return Empty;
        }

        return new SourceMap(offsets, ComputeLineStarts(bytes), bytes);
    }

    /// <summary>
    /// Builds the map of text that was parsed after <see cref="MultiLineStrings.Escape"/>: paths are read from <paramref name="parsed"/> and their
    /// positions mapped back through <paramref name="insertions"/> to <paramref name="original"/>, whose lines and columns are reported. Neither
    /// text may start with a byte-order mark.
    /// </summary>
    public static SourceMap Build(ReadOnlyMemory<byte> parsed, ReadOnlyMemory<byte> original, IReadOnlyList<int> insertions)
    {
        ArgumentNullException.ThrowIfNull(insertions);
        if (insertions.Count == 0)
        {
            return Build(original);
        }

        var offsets = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            Record(parsed.ToArray(), offsets);
        }
        catch (JsonException)
        {
            return Empty;
        }

        foreach (var key in new List<string>(offsets.Keys))
        {
            offsets[key] = MultiLineStrings.ToOriginal(offsets[key], insertions);
        }

        byte[] bytes = original.ToArray();
        return new SourceMap(offsets, ComputeLineStarts(bytes), bytes);
    }

    /// <summary>The location of exactly this path, or <c>null</c> when unknown.</summary>
    public SourceLocation? Find(JsonPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return _offsets.TryGetValue(path.ToString(), out int offset) ? Resolve(offset) : null;
    }

    /// <summary>The location of this path or of its nearest recorded ancestor.</summary>
    public SourceLocation? FindNearest(JsonPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        for (JsonPath? current = path; current is not null; current = current.Parent)
        {
            if (_offsets.TryGetValue(current.ToString(), out int offset))
            {
                return Resolve(offset);
            }
        }

        return null;
    }

    private SourceLocation Resolve(int offset)
    {
        int line = Array.BinarySearch(_lineStarts, offset);
        if (line < 0)
        {
            line = ~line - 1;
        }

        int lineStart = _lineStarts[line];
        int column = Encoding.UTF8.GetCharCount(_utf8, lineStart, offset - lineStart) + 1;
        return new SourceLocation(line + 1, column);
    }

    private static int[] ComputeLineStarts(byte[] bytes)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                starts.Add(i + 1);
            }
        }

        return [.. starts];
    }

    private static void Record(byte[] bytes, Dictionary<string, int> offsets)
    {
        var reader = new Utf8JsonReader(bytes, TemplateJson.ReaderOptions);
        var frames = new Stack<Frame>();
        JsonPath? pendingProperty = null;
        bool rootSeen = false;

        while (reader.Read())
        {
            if (reader.TokenType is JsonTokenType.Comment)
            {
                continue;
            }

            if (!rootSeen)
            {
                rootSeen = true;
                offsets[JsonPath.Root.ToString()] = checked((int)reader.TokenStartIndex);
            }

            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    {
                        var frame = frames.Peek();
                        pendingProperty = frame.Path.Append(reader.GetString()!);
                        offsets[pendingProperty.ToString()] = checked((int)reader.TokenStartIndex);
                        break;
                    }

                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    {
                        var path = ValuePath(frames, ref pendingProperty, reader.TokenStartIndex, offsets);
                        frames.Push(new Frame(path, reader.TokenType == JsonTokenType.StartArray));
                        break;
                    }

                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    frames.Pop();
                    break;

                default:
                    ValuePath(frames, ref pendingProperty, reader.TokenStartIndex, offsets);
                    break;
            }
        }
    }

    private static JsonPath ValuePath(Stack<Frame> frames, ref JsonPath? pendingProperty, long tokenStart, Dictionary<string, int> offsets)
    {
        if (frames.Count == 0)
        {
            return JsonPath.Root;
        }

        var frame = frames.Peek();
        if (frame.IsArray)
        {
            var path = frame.Path.Append(frame.NextIndex++);
            offsets[path.ToString()] = checked((int)tokenStart);
            return path;
        }

        var propertyPath = pendingProperty ?? frame.Path;
        pendingProperty = null;
        return propertyPath;
    }

    private sealed class Frame
    {
        public Frame(JsonPath path, bool isArray)
        {
            Path = path;
            IsArray = isArray;
        }

        public JsonPath Path { get; }

        public bool IsArray { get; }

        public int NextIndex { get; set; }
    }
}
