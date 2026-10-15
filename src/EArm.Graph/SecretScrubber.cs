using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;

namespace EArm.Graph;

/// <summary>
/// A registry of secret VALUES that must never appear in a message, log line or exception text, whatever shape they arrive in.
/// <see cref="SecretRedactor"/> masks secret-looking fragments by their shape (property names, bearer tokens, JWTs); a value that was
/// resolved at run time (a Key Vault secret, a generated password, a client secret from a parameter) has no tell-tale shape, so whoever
/// learns such a value calls <see cref="Register(string?, bool)"/> and every message built afterwards by this assembly (Graph and transport exceptions, the
/// error parser, <see cref="SecretRedactor.RedactText"/>, therefore all logging redaction) has it replaced with <see cref="Mask"/>.
/// <para>
/// The registry is backed by an <see cref="AsyncLocal{T}"/> holder, so concurrent runs (and parallel tests) do not see each other's secrets.
/// Because async-local changes made inside an async method do not flow back to its caller, call <see cref="Begin"/> (or <see cref="Register(string?, bool)"/>)
/// from the top of the run, before the work that may later format an error is started; everything that flows from there shares the registry.
/// A nested <see cref="Begin"/> JOINS the registry of the outer scope instead of copying it: what an inner scope registers (the deployment runner
/// inside the program-level scope) is still masked by the outer catch-all after the inner scope ends.
/// </para>
/// <para>
/// Besides the raw value, the URL-encoded forms (upper and lower case hex), the JSON-escaped forms (upper and lower case <c>\uXXXX</c> hex), the HTML-encoded
/// form and the base64 forms are registered, because that is how an echoed value usually comes back. Scrubbing is a single left-to-right pass over the
/// text against a sorted pattern table, so its cost does not grow with the number of registered values; where matches overlap the union is masked.
/// </para>
/// <para>
/// A value that was NOT declared secure (a derived value found by taint tracking rather than a parameter marked <c>secure</c>) and is shorter than
/// <see cref="WordBoundaryBelow"/> characters is matched only as a whole word (neither neighbour is a letter or digit), so a short derived value such
/// as <c>pass</c> does not shred <c>passwordCredentials</c>.
/// </para>
/// </summary>
public static class SecretScrubber
{
    /// <summary>What replaces a registered value.</summary>
    public const string Mask = "***";

    /// <summary>Values shorter than this are ignored: masking a one- or two-character "secret" would shred every message.</summary>
    public const int MinimumLength = 3;

    /// <summary>A value that was not declared secure and is shorter than this is matched on word boundaries only.</summary>
    public const int WordBoundaryBelow = 8;

    private static readonly AsyncLocal<Registry?> Current = new();

    /// <summary>
    /// Starts a registry for the current async flow and returns a handle that restores the previous registry on dispose. When a registry already
    /// exists (an outer scope) it is joined, not copied: values registered inside survive the end of the inner scope, so the outer scope's
    /// catch-all still masks them. Optional: <see cref="Register(string?, bool)"/> creates a registry on demand.
    /// </summary>
    public static IDisposable Begin()
    {
        var previous = Current.Value;
        if (previous is not null)
        {
            return new Scope(previous, restore: false);
        }

        Current.Value = new Registry();
        return new Scope(null, restore: true);
    }

    /// <summary>Registers a secret declared secure (the 3-character floor, plain substring matching). See <see cref="Register(string?, bool)"/>.</summary>
    public static void Register(string? secret)
    {
        Register(secret, secureDeclared: true);
    }

    /// <summary>
    /// Registers a secret value (ignored when null or shorter than <see cref="MinimumLength"/>); its encoded forms are registered too. Thread-safe.
    /// <paramref name="secureDeclared"/> is true for a value the template declared secure (a <c>secure</c> parameter, a Key Vault secret, a generated
    /// password): it is matched as a plain substring. False for a value that merely derives from one or was tainted by it: below
    /// <see cref="WordBoundaryBelow"/> characters it is matched on word boundaries only. A value registered both ways stays plain.
    /// </summary>
    public static void Register(string? secret, bool secureDeclared)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length < MinimumLength)
        {
            return;
        }

        var registry = Current.Value;
        if (registry is null)
        {
            registry = new Registry();
            Current.Value = registry;
        }

        bool boundaryOnly = !secureDeclared && secret.Length < WordBoundaryBelow;
        registry.Add(Forms(secret), boundaryOnly);
    }

    /// <summary>The text with every registered value replaced by <see cref="Mask"/> (overlapping matches are masked as one run, ordinal comparison); null becomes empty.</summary>
    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var registry = Current.Value;
        if (registry is null)
        {
            return text;
        }

        return registry.Snapshot().Scrub(text);
    }

    /// <summary>Forgets every value registered in the current async flow.</summary>
    public static void Clear()
    {
        Current.Value?.Clear();
    }

    /// <summary>True when at least one value is registered in the current async flow.</summary>
    internal static bool HasValues => Current.Value is { } registry && registry.Snapshot().Count > 0;

    private static IEnumerable<string> Forms(string secret)
    {
        yield return secret;

        var escaped = Uri.EscapeDataString(secret);
        yield return escaped;
        yield return LowerHex(escaped, '%', 2);
        yield return WebUtility.UrlEncode(secret);
        yield return LowerHex(WebUtility.UrlEncode(secret), '%', 2);

        var json = JsonEncodedText.Encode(secret).ToString();
        yield return json;
        yield return LowerHex(json, 'u', 4);
        var relaxed = JsonEncodedText.Encode(secret, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString();
        yield return relaxed;
        yield return LowerHex(relaxed, 'u', 4);

        var html = WebUtility.HtmlEncode(secret);
        yield return html;

        // Base64 of a very short value is only a few characters long and would match inside unrelated base64 text.
        var bytes = Encoding.UTF8.GetBytes(secret);
        var base64 = Convert.ToBase64String(bytes);
        if (base64.Length >= WordBoundaryBelow)
        {
            yield return base64;
            var trimmed = base64.TrimEnd('=');
            if (trimmed.Length != base64.Length)
            {
                yield return trimmed;
            }

            yield return base64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }
    }

    /// <summary>The text with the hex digits that follow every <paramref name="marker"/> (<c>%</c> + 2 digits, <c>\\u</c> + 4 digits) lower-cased.</summary>
    private static string LowerHex(string encoded, char marker, int digits)
    {
        char[]? buffer = null;
        for (var i = 0; i < encoded.Length; i++)
        {
            bool starts = marker == '%' ? encoded[i] == '%' : encoded[i] == '\\' && i + 1 < encoded.Length && encoded[i + 1] == 'u';
            if (!starts)
            {
                continue;
            }

            var first = marker == '%' ? i + 1 : i + 2;
            for (var d = 0; d < digits && first + d < encoded.Length; d++)
            {
                var c = encoded[first + d];
                if (c is >= 'A' and <= 'F')
                {
                    buffer ??= encoded.ToCharArray();
                    buffer[first + d] = char.ToLowerInvariant(c);
                }
            }
        }

        return buffer is null ? encoded : new string(buffer);
    }

    private sealed class Scope : IDisposable
    {
        private readonly Registry? _previous;
        private readonly bool _restore;
        private int _disposed;

        public Scope(Registry? previous, bool restore)
        {
            _previous = previous;
            _restore = restore;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && _restore)
            {
                Current.Value = _previous;
            }
        }
    }

    private sealed class Registry
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, bool> _boundaryByValue = new(StringComparer.Ordinal);
        private PatternTable _table = PatternTable.Empty;

        public PatternTable Snapshot()
        {
            return Volatile.Read(ref _table);
        }

        public void Add(IEnumerable<string> forms, bool boundaryOnly)
        {
            lock (_gate)
            {
                List<string>? added = null;
                List<string>? flipped = null;
                foreach (var value in forms)
                {
                    if (value.Length < MinimumLength || string.Equals(value, Mask, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (_boundaryByValue.TryGetValue(value, out var current))
                    {
                        // A plain registration wins over a boundary-only one.
                        if (current && !boundaryOnly)
                        {
                            _boundaryByValue[value] = false;
                            (flipped ??= []).Add(value);
                        }

                        continue;
                    }

                    _boundaryByValue[value] = boundaryOnly;
                    (added ??= []).Add(value);
                }

                if (added is not null || flipped is not null)
                {
                    Volatile.Write(ref _table, _table.With(added, flipped, _boundaryByValue));
                }
            }
        }
        public void Clear()
        {
            lock (_gate)
            {
                _boundaryByValue.Clear();
                Volatile.Write(ref _table, PatternTable.Empty);
            }
        }
    }

    /// <summary>
    /// An immutable, ordinally sorted table of the patterns. A scan narrows the sorted range one character at a time (binary search per
    /// character), so finding the matches at a position costs O(depth x log patterns), independent of how many secrets are registered.
    /// </summary>
    private sealed class PatternTable
    {
        public static readonly PatternTable Empty = new([], []);

        private readonly string[] _patterns;
        private readonly bool[] _boundaryOnly;

        private PatternTable(string[] patterns, bool[] boundaryOnly)
        {
            _patterns = patterns;
            _boundaryOnly = boundaryOnly;
        }

        public int Count => _patterns.Length;

        /// <summary>A new table with <paramref name="added"/> merged in (O(patterns), no re-sort of the existing ones) and the flags of <paramref name="flipped"/> refreshed.</summary>
        public PatternTable With(List<string>? added, List<string>? flipped, Dictionary<string, bool> flags)
        {
            var patterns = _patterns;
            var boundary = _boundaryOnly;
            if (added is not null)
            {
                added.Sort(StringComparer.Ordinal);
                patterns = new string[_patterns.Length + added.Count];
                boundary = new bool[patterns.Length];
                int i = 0, j = 0, k = 0;
                while (i < _patterns.Length || j < added.Count)
                {
                    if (j >= added.Count || (i < _patterns.Length && string.CompareOrdinal(_patterns[i], added[j]) < 0))
                    {
                        patterns[k] = _patterns[i];
                        boundary[k] = _boundaryOnly[i];
                        i++;
                    }
                    else
                    {
                        patterns[k] = added[j];
                        boundary[k] = flags[added[j]];
                        j++;
                    }

                    k++;
                }
            }
            else
            {
                boundary = (bool[])_boundaryOnly.Clone();
            }

            if (flipped is not null)
            {
                foreach (var value in flipped)
                {
                    var index = Array.BinarySearch(patterns, value, StringComparer.Ordinal);
                    if (index >= 0)
                    {
                        boundary[index] = flags[value];
                    }
                }
            }

            return new PatternTable(patterns, boundary);
        }
        public string Scrub(string text)
        {
            if (_patterns.Length == 0)
            {
                return text;
            }

            StringBuilder? result = null;
            var copiedUpTo = 0;
            var maskedUntil = -1;
            var runStart = -1;
            for (var i = 0; i < text.Length; i++)
            {
                var length = LongestMatchAt(text, i);
                if (length == 0)
                {
                    continue;
                }

                var end = i + length;
                if (i < maskedUntil)
                {
                    // Overlaps the run being masked: extend it.
                    if (end > maskedUntil)
                    {
                        maskedUntil = end;
                    }

                    continue;
                }

                if (runStart >= 0)
                {
                    result = Flush(result, text, ref copiedUpTo, runStart, maskedUntil);
                }

                runStart = i;
                maskedUntil = end;
            }

            if (runStart >= 0)
            {
                result = Flush(result, text, ref copiedUpTo, runStart, maskedUntil);
            }

            if (result is null)
            {
                return text;
            }

            result.Append(text, copiedUpTo, text.Length - copiedUpTo);
            return result.ToString();
        }

        private static StringBuilder Flush(StringBuilder? result, string text, ref int copiedUpTo, int start, int end)
        {
            result ??= new StringBuilder(text.Length);
            result.Append(text, copiedUpTo, start - copiedUpTo);
            result.Append(Mask);
            copiedUpTo = end;
            return result;
        }

        /// <summary>The length of the longest valid pattern that starts at <paramref name="start"/>, or 0.</summary>
        private int LongestMatchAt(string text, int start)
        {
            var lo = 0;
            var hi = _patterns.Length;
            var depth = 0;
            var best = 0;
            while (lo < hi)
            {
                // Patterns of exactly 'depth' characters sort first inside the range: they are the prefix itself.
                if (_patterns[lo].Length == depth)
                {
                    if (depth >= MinimumLength && IsValid(text, start, depth, _boundaryOnly[lo]))
                    {
                        best = depth;
                    }

                    lo++;
                    if (lo >= hi)
                    {
                        break;
                    }
                }

                if (start + depth >= text.Length)
                {
                    break;
                }

                var c = text[start + depth];
                lo = LowerBound(lo, hi, depth, c);
                hi = UpperBound(lo, hi, depth, c);
                depth++;
            }

            return best;
        }

        private static bool IsValid(string text, int start, int length, bool boundaryOnly)
        {
            if (!boundaryOnly)
            {
                return true;
            }

            var end = start + length;
            return (start == 0 || !char.IsLetterOrDigit(text[start - 1])) && (end >= text.Length || !char.IsLetterOrDigit(text[end]));
        }

        /// <summary>The first index in [lo, hi) whose pattern has a character at <paramref name="depth"/> that is not less than <paramref name="c"/>.</summary>
        private int LowerBound(int lo, int hi, int depth, char c)
        {
            while (lo < hi)
            {
                var mid = lo + ((hi - lo) >> 1);
                if (_patterns[mid][depth] < c)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            return lo;
        }

        /// <summary>The first index in [lo, hi) whose pattern has a character at <paramref name="depth"/> greater than <paramref name="c"/>.</summary>
        private int UpperBound(int lo, int hi, int depth, char c)
        {
            while (lo < hi)
            {
                var mid = lo + ((hi - lo) >> 1);
                if (_patterns[mid][depth] <= c)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            return lo;
        }
    }
}
