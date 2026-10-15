using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core.Secrets;

namespace EArm.Expressions.Values;

/// <summary>
/// Builds an object whose keys may have been computed from secure values (<c>createObject</c>, <c>toObject</c>,
/// <c>groupBy</c>). Object property names are not taint-tracked, so a secret-derived key is never written out: it is
/// masked as <c>***</c> (the second distinct secret key as <c>***#2</c>, and so on, in first-appearance order, skipping
/// any name a clear key already uses). Keys are matched by their real text, so equal secret keys still group and
/// collide; a real key that was secret in any occurrence is masked in every occurrence.
/// </summary>
internal sealed class MaskedKeyObject
{
    private readonly List<string> _order = [];
    private readonly Dictionary<string, JsonNode?> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _secretKeys = new(StringComparer.Ordinal);

    public bool ContainsKey(string key)
    {
        return _values.ContainsKey(key);
    }

    /// <summary>True when <paramref name="key"/> was secret in any occurrence so far.</summary>
    public bool IsSecretKey(string key)
    {
        return _secretKeys.Contains(key);
    }

    public bool TryGetValue(string key, out JsonNode? value)
    {
        return _values.TryGetValue(key, out value);
    }

    /// <summary>Sets <paramref name="key"/> (last write wins, first position kept); <paramref name="secretKey"/> marks it secret-derived.</summary>
    public void Set(string key, bool secretKey, JsonNode? value)
    {
        if (!_values.ContainsKey(key))
        {
            _order.Add(key);
        }

        _values[key] = value;
        if (secretKey)
        {
            _secretKeys.Add(key);
        }
    }

    public JsonObject Build()
    {
        var clear = new HashSet<string>(StringComparer.Ordinal);
        foreach (string key in _order)
        {
            if (!_secretKeys.Contains(key))
            {
                clear.Add(key);
            }
        }

        var result = new JsonObject();
        int next = 0;
        foreach (string key in _order)
        {
            string name = key;
            if (_secretKeys.Contains(key))
            {
                do
                {
                    next++;
                    name = next == 1 ? SecretValue.Mask : $"{SecretValue.Mask}#{next}";
                }
                while (clear.Contains(name));
            }

            result[name] = _values[key];
        }

        return result;
    }
}
