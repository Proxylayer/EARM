using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace EArm.TestSupport;

/// <summary>The route model of <see cref="FakeDirectory"/>: the parsed request path and its entity selectors.</summary>
public sealed partial class FakeDirectory
{
    // ---- routing ----

    /// <summary>An entity selector: an id/UPN segment or an alternate key <c>(property='value')</c>.</summary>
    /// <summary>An authentication method configuration's state before a PATCH, still served by the next <see cref="ReadsLeft"/> GETs.</summary>
    private sealed record StaleRead(JsonObject Before)
    {
        public int ReadsLeft { get; set; }
    }

    private sealed record Selector(string? Property, string Value)
    {
        public bool IsAlternateKey => Property is not null;
    }

    private sealed class Route
    {
        public required string Path { get; init; }

        /// <summary>The collection root without leading slash, canonical casing (<c>applications</c>, <c>directory/administrativeUnits</c>).</summary>
        public required string Root { get; init; }

        public Selector? Selector { get; init; }

        public string? Nav { get; init; }

        public Selector? ChildSelector { get; init; }

        public bool IsRef { get; init; }

        public static Route? Parse(string path, string[] knownRoots)
        {
            string trimmed = path.Trim();
            if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                int apiIndex = trimmed.IndexOf("/v1.0/", StringComparison.OrdinalIgnoreCase);
                if (apiIndex < 0)
                {
                    apiIndex = trimmed.IndexOf("/beta/", StringComparison.OrdinalIgnoreCase);
                }

                if (apiIndex >= 0)
                {
                    trimmed = trimmed[(apiIndex + 6)..];
                }
            }

            var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
            {
                return null;
            }

            foreach (var root in knownRoots.OrderByDescending(r => r.Length))
            {
                var rootSegments = root.Split('/');
                if (segments.Length < rootSegments.Length)
                {
                    continue;
                }

                bool match = true;
                string? rootKey = null;
                for (int i = 0; i < rootSegments.Length; i++)
                {
                    var (name, key) = SplitKey(segments[i]);
                    if (!name.Equals(rootSegments[i], StringComparison.OrdinalIgnoreCase) || (key is not null && i != rootSegments.Length - 1))
                    {
                        match = false;
                        break;
                    }

                    rootKey = key;
                }

                if (!match)
                {
                    continue;
                }

                int next = rootSegments.Length;
                Selector? selector = null;
                if (rootKey is not null)
                {
                    selector = ParseKey(rootKey);
                }
                else if (next < segments.Length)
                {
                    selector = new Selector(null, Decode(segments[next]));
                    next++;
                }

                string? nav = null;
                Selector? childSelector = null;
                bool isRef = false;
                if (next < segments.Length)
                {
                    var (navName, navKey) = SplitKey(segments[next]);
                    nav = navName;
                    next++;
                    if (navKey is not null)
                    {
                        childSelector = ParseKey(navKey);
                    }

                    if (next < segments.Length && segments[next] == "$ref")
                    {
                        isRef = true;
                        next++;
                    }
                    else if (next < segments.Length)
                    {
                        childSelector ??= new Selector(null, Decode(segments[next]));
                        next++;
                        if (next < segments.Length && segments[next] == "$ref")
                        {
                            isRef = true;
                            next++;
                        }
                    }
                }

                if (next < segments.Length)
                {
                    return null;
                }

                return new Route { Path = path, Root = root, Selector = selector, Nav = nav, ChildSelector = childSelector, IsRef = isRef };
            }

            return null;
        }

        private static (string Name, string? Key) SplitKey(string segment)
        {
            int open = segment.IndexOf('(');
            if (open > 0 && segment.EndsWith(')'))
            {
                return (segment[..open], segment[(open + 1)..^1]);
            }

            return (segment, null);
        }

        private static Selector ParseKey(string key)
        {
            int eq = key.IndexOf('=');
            if (eq <= 0)
            {
                return new Selector(null, Decode(key.Trim('\'')));
            }

            string property = key[..eq].Trim();
            string value = key[(eq + 1)..].Trim();
            if (value.StartsWith('\'') && value.EndsWith('\'') && value.Length >= 2)
            {
                value = value[1..^1];
            }

            return new Selector(property, Decode(value).Replace("''", "'", StringComparison.Ordinal));
        }

        private static string Decode(string segment)
        {
            return Uri.UnescapeDataString(segment);
        }
    }
}
