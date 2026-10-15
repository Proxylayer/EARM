using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace EArm.Core.Json;

/// <summary>
/// One segment of a <see cref="JsonPath"/>: either an object property name or an array index.
/// </summary>
public readonly record struct JsonPathSegment
{
    private JsonPathSegment(string? name, int index)
    {
        Name = name;
        Index = index;
    }

    /// <summary>The property name, or <c>null</c> for an array index segment.</summary>
    public string? Name { get; }

    /// <summary>The array index, or <c>-1</c> for a property segment.</summary>
    public int Index { get; }

    public bool IsIndex => Name is null;

    public static JsonPathSegment Property(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new JsonPathSegment(name, -1);
    }

    public static JsonPathSegment ArrayIndex(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return new JsonPathSegment(null, index);
    }

    public override string ToString()
    {
        return IsIndex ? "[" + Index.ToString(CultureInfo.InvariantCulture) + "]" : Name!;
    }
}

/// <summary>
/// An immutable path into a JSON document, rendered ARM-style: <c>resources[0].properties.displayName</c>.
/// Property names that are not simple identifiers are rendered in bracket-quote form: <c>properties['@odata.type']</c>.
/// Used for diagnostics, source locations and canonical-JSON exclusions.
/// </summary>
public sealed class JsonPath : IEquatable<JsonPath>
{
    /// <summary>The empty path (the document root).</summary>
    public static readonly JsonPath Root = new([]);

    private readonly JsonPathSegment[] _segments;
    private string? _text;

    private JsonPath(JsonPathSegment[] segments)
    {
        _segments = segments;
    }

    public IReadOnlyList<JsonPathSegment> Segments => _segments;

    public bool IsRoot => _segments.Length == 0;

    public int Depth => _segments.Length;

    /// <summary>The path without its last segment, or <c>null</c> for the root.</summary>
    public JsonPath? Parent => IsRoot ? null : new JsonPath(_segments[..^1]);

    /// <summary>The last segment; throws for the root.</summary>
    public JsonPathSegment Last => IsRoot ? throw new InvalidOperationException("The root path has no segments.") : _segments[^1];

    public static JsonPath Of(params string[] propertyNames)
    {
        var path = Root;
        foreach (var name in propertyNames)
        {
            path = path.Append(name);
        }

        return path;
    }

    public JsonPath Append(string propertyName)
    {
        return Append(JsonPathSegment.Property(propertyName));
    }

    public JsonPath Append(int index)
    {
        return Append(JsonPathSegment.ArrayIndex(index));
    }

    public JsonPath Append(JsonPathSegment segment)
    {
        var next = new JsonPathSegment[_segments.Length + 1];
        Array.Copy(_segments, next, _segments.Length);
        next[^1] = segment;
        return new JsonPath(next);
    }

    public JsonPath Append(JsonPath relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        if (relative.IsRoot)
        {
            return this;
        }

        var next = new JsonPathSegment[_segments.Length + relative._segments.Length];
        Array.Copy(_segments, next, _segments.Length);
        Array.Copy(relative._segments, 0, next, _segments.Length, relative._segments.Length);
        return new JsonPath(next);
    }

    public bool StartsWith(JsonPath prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (prefix._segments.Length > _segments.Length)
        {
            return false;
        }

        for (int i = 0; i < prefix._segments.Length; i++)
        {
            if (!_segments[i].Equals(prefix._segments[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Parses the rendered form: <c>a.b[0].c</c>, <c>a['x.y'][2]</c>. An empty string or <c>$</c> is the root.
    /// </summary>
    public static JsonPath Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!TryParse(text, out var path))
        {
            throw new FormatException($"'{text}' is not a valid JSON path.");
        }

        return path;
    }

    public static bool TryParse(string text, out JsonPath path)
    {
        path = Root;
        if (text is null)
        {
            return false;
        }

        text = text.Trim();
        if (text.Length == 0 || text == "$")
        {
            return true;
        }

        var segments = new List<JsonPathSegment>();
        int i = 0;
        if (text[0] == '$')
        {
            i = 1;
        }

        while (i < text.Length)
        {
            char c = text[i];
            if (c == '.')
            {
                i++;
                continue;
            }

            if (c == '[')
            {
                i++;
                if (i >= text.Length)
                {
                    return false;
                }

                if (text[i] == '\'')
                {
                    i++;
                    var sb = new StringBuilder();
                    bool closed = false;
                    while (i < text.Length)
                    {
                        char q = text[i];
                        if (q == '\\' && i + 1 < text.Length)
                        {
                            sb.Append(text[i + 1]);
                            i += 2;
                            continue;
                        }

                        if (q == '\'')
                        {
                            closed = true;
                            i++;
                            break;
                        }

                        sb.Append(q);
                        i++;
                    }

                    if (!closed || i >= text.Length || text[i] != ']')
                    {
                        return false;
                    }

                    i++;
                    segments.Add(JsonPathSegment.Property(sb.ToString()));
                    continue;
                }

                int start = i;
                while (i < text.Length && char.IsAsciiDigit(text[i]))
                {
                    i++;
                }

                if (start == i || i >= text.Length || text[i] != ']')
                {
                    return false;
                }

                segments.Add(JsonPathSegment.ArrayIndex(int.Parse(text.AsSpan(start, i - start), CultureInfo.InvariantCulture)));
                i++;
                continue;
            }

            int nameStart = i;
            while (i < text.Length && text[i] != '.' && text[i] != '[')
            {
                i++;
            }

            if (nameStart == i)
            {
                return false;
            }

            segments.Add(JsonPathSegment.Property(text[nameStart..i]));
        }

        path = new JsonPath([.. segments]);
        return true;
    }

    /// <summary>
    /// Converts an RFC 6901 JSON Pointer (<c>/resources/0/name</c>) into a path. Numeric segments are ambiguous in a
    /// pointer; when <paramref name="instance"/> is supplied the segment is treated as an array index only if the
    /// node at that point is a <see cref="JsonArray"/>, otherwise as a property name.
    /// </summary>
    public static JsonPath FromJsonPointer(string pointer, JsonNode? instance = null)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        if (pointer.Length == 0 || pointer == "#")
        {
            return Root;
        }

        if (pointer[0] == '#')
        {
            pointer = Uri.UnescapeDataString(pointer[1..]);
        }

        if (pointer[0] != '/')
        {
            throw new FormatException($"'{pointer}' is not a valid JSON Pointer.");
        }

        var path = Root;
        var current = instance;
        foreach (var raw in pointer[1..].Split('/'))
        {
            string token = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            bool isIndex = current is JsonArray || (current is null && int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out _));
            if (isIndex && int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
            {
                path = path.Append(index);
                current = current is JsonArray array && index < array.Count ? array[index] : null;
            }
            else
            {
                path = path.Append(token);
                current = current is JsonObject obj && obj.TryGetPropertyValue(token, out var child) ? child : null;
            }
        }

        return path;
    }

    /// <summary>Renders the path as an RFC 6901 JSON Pointer.</summary>
    public string ToJsonPointer()
    {
        var sb = new StringBuilder();
        foreach (var segment in _segments)
        {
            sb.Append('/');
            if (segment.IsIndex)
            {
                sb.Append(segment.Index.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(segment.Name!.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal));
            }
        }

        return sb.ToString();
    }

    public override string ToString()
    {
        return _text ??= Render();
    }

    private string Render()
    {
        var sb = new StringBuilder();
        foreach (var segment in _segments)
        {
            if (segment.IsIndex)
            {
                sb.Append('[').Append(segment.Index.ToString(CultureInfo.InvariantCulture)).Append(']');
            }
            else if (IsSimpleIdentifier(segment.Name!))
            {
                if (sb.Length > 0)
                {
                    sb.Append('.');
                }

                sb.Append(segment.Name);
            }
            else
            {
                sb.Append("['").Append(segment.Name!.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)).Append("']");
            }
        }

        return sb.ToString();
    }

    private static bool IsSimpleIdentifier(string name)
    {
        if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_'))
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }

        return true;
    }

    public bool Equals(JsonPath? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (_segments.Length != other._segments.Length)
        {
            return false;
        }

        for (int i = 0; i < _segments.Length; i++)
        {
            if (!_segments[i].Equals(other._segments[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as JsonPath);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var segment in _segments)
        {
            hash.Add(segment);
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(JsonPath? left, JsonPath? right)
    {
        return left is null ? right is null : left.Equals(right);
    }

    public static bool operator !=(JsonPath? left, JsonPath? right)
    {
        return !(left == right);
    }
}
