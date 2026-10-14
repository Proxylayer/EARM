using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json.Nodes;
using EntraArm.Core;
using EntraArm.Core.Diagnostics;
using EntraArm.Core.Secrets;
using EntraArm.Expressions.Values;

namespace EntraArm.Expressions.Evaluation;

/// <summary>
/// Parameter values resolved on demand from the loader's <see cref="BoundParameter"/> set: supplied values are
/// returned as they are (secure ones already tainted), <c>defaultValue</c> expressions are evaluated once in
/// <see cref="EvaluationScope.ParameterDefault"/> (where <c>utcNow()</c>/<c>newGuid()</c> are legal) with a cycle
/// guard, a pending Key Vault reference becomes a tainted unknown, and a missing value becomes an unknown
/// (the loader already reports <c>MissingParameterValue</c>). Implements <see cref="IReadOnlyDictionary{TKey, TValue}"/>
/// so it can be used directly as <see cref="EvaluationContext.Parameters"/>.
/// </summary>
public sealed class LazyParameters : IReadOnlyDictionary<string, JsonNode?>
{
    private readonly Dictionary<string, BoundParameter> _bound;
    private readonly Dictionary<string, JsonNode?> _memo = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _inProgress = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private IExpressionEvaluator? _evaluator;
    private EvaluationContext? _context;

    public LazyParameters(IReadOnlyDictionary<string, BoundParameter> bound)
    {
        ArgumentNullException.ThrowIfNull(bound);
        _bound = new Dictionary<string, BoundParameter>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in bound)
        {
            _bound[pair.Key] = pair.Value;
        }
    }

    public static LazyParameters Empty => new(new Dictionary<string, BoundParameter>());

    /// <summary>Parameters that have neither a value nor a default.</summary>
    public IReadOnlyList<string> MissingNames => _bound.Values.Where(b => b.IsMissing).Select(b => b.Name).ToList();

    /// <summary>Parameters whose value is an unresolved Key Vault reference.</summary>
    public IReadOnlyList<string> PendingReferenceNames => _bound.Values.Where(b => b.PendingReference is not null).Select(b => b.Name).ToList();

    public IEnumerable<string> Keys => _bound.Keys;

    public IEnumerable<JsonNode?> Values => _bound.Keys.Select(Get);

    public int Count => _bound.Count;

    public JsonNode? this[string key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException($"Parameter '{key}' is not defined.");

    public bool ContainsKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _bound.ContainsKey(key);
    }

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!_bound.ContainsKey(key))
        {
            value = null;
            return false;
        }

        value = Get(key);
        return true;
    }

    public bool TryGetBound(string name, [NotNullWhen(true)] out BoundParameter? bound)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _bound.TryGetValue(name, out bound);
    }

    internal void Bind(IExpressionEvaluator evaluator, EvaluationContext context)
    {
        lock (_gate)
        {
            _evaluator = evaluator;
            _context = context.Base.ForScope(EvaluationScope.ParameterDefault);
        }
    }

    /// <summary>Resolves (once) and returns a copy of the parameter value.</summary>
    public JsonNode? Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_gate)
        {
            if (_memo.TryGetValue(name, out var memoised))
            {
                return JsonValues.Clone(memoised);
            }

            if (!_bound.TryGetValue(name, out var bound))
            {
                throw new ExpressionEvaluationException(DiagnosticCodes.UndefinedParameter, $"Parameter '{name}' is not defined.");
            }

            if (!_inProgress.Add(name))
            {
                throw new ExpressionEvaluationException(DiagnosticCodes.ParameterDefaultCycle, $"The default value of parameter '{name}' refers to itself (directly or through other parameters).");
            }

            try
            {
                var value = Resolve(bound);
                _memo[name] = value;
                return JsonValues.Clone(value);
            }
            finally
            {
                _inProgress.Remove(name);
            }
        }
    }

    private JsonNode? Resolve(BoundParameter bound)
    {
        if (bound.PendingReference is not null)
        {
            return UnknownJson.Create($"parameters('{bound.Name}')", UnknownOrigins.Parameter, tainted: true);
        }

        if (bound.IsMissing)
        {
            return UnknownJson.Create($"parameters('{bound.Name}')", UnknownOrigins.Parameter, tainted: bound.IsSecure);
        }

        if (bound.Source != ParameterValueSource.Default)
        {
            // Supplied values (file or -p) are data: expressions and '[[' escapes are not interpreted (ARM rule).
            return JsonValues.Clone(bound.Value);
        }

        if (_evaluator is null || _context is null)
        {
            throw new InvalidOperationException("LazyParameters must be bound to a context before defaults are evaluated (EvaluationContext.Bind).");
        }

        var evaluated = _evaluator.EvaluateTree(bound.Value, _context);
        return bound.IsSecure ? SecretJson.Taint(evaluated) : evaluated;
    }

    public IEnumerator<KeyValuePair<string, JsonNode?>> GetEnumerator()
    {
        foreach (var name in _bound.Keys)
        {
            yield return new KeyValuePair<string, JsonNode?>(name, Get(name));
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
