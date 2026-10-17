using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Secrets;
using EArm.Expressions.Values;

namespace EArm.Expressions.Evaluation;

/// <summary>
/// The parameters of an inner-scope nested template (<c>expressionEvaluationOptions.scope: inner</c>, or a linked template), as its resources see
/// them while the engine plans and applies: a value passed in the deployment's <c>properties.parameters</c> is the parent's expression,
/// evaluated in the parent's context (with the deployment's copy iteration) each time it is read, so a value that depends on a resource the parent
/// deploys (<c>reference(...)</c>, a nested deployment's outputs) resolves once that resource is live. A concrete value is kept; an unknown one is
/// evaluated again on the next read. Every other parameter (a value from <c>parametersLink</c>, a default, a missing value) comes from the nested
/// template's own <see cref="LazyParameters"/>. Secure parameters stay tainted.
/// </summary>
public sealed class NestedParameters : IReadOnlyDictionary<string, JsonNode?>
{
    private readonly LazyParameters _own;
    private readonly Dictionary<string, JsonNode?> _passed;
    private readonly EvaluationContext _parent;
    private readonly Dictionary<string, JsonNode?> _memo = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private IExpressionEvaluator? _evaluator;

    /// <param name="bound">The nested template's parameters bound to their defaults and file values.</param>
    /// <param name="passed">The <c>value</c> of each entry of the deployment's <c>properties.parameters</c>, unevaluated, by parameter name.</param>
    /// <param name="parent">The context of the deployment resource in the parent template (resource scope, with its copy iteration).</param>
    public NestedParameters(IReadOnlyDictionary<string, BoundParameter> bound, IReadOnlyDictionary<string, JsonNode?> passed, EvaluationContext parent)
    {
        ArgumentNullException.ThrowIfNull(bound);
        ArgumentNullException.ThrowIfNull(passed);
        ArgumentNullException.ThrowIfNull(parent);
        _own = new LazyParameters(bound);
        _passed = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in passed)
        {
            if (bound.ContainsKey(name))
            {
                _passed[name] = value;
            }
        }

        _parent = parent.ForScope(EvaluationScope.Resource);
    }

    /// <summary>The nested template's own parameter store (defaults, file values).</summary>
    public LazyParameters Own => _own;

    public IEnumerable<string> Keys => _own.Keys;

    public IEnumerable<JsonNode?> Values => Keys.Select(Get);

    public int Count => _own.Count;

    public JsonNode? this[string key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException($"Parameter '{key}' is not defined.");

    public bool ContainsKey(string key)
    {
        return _own.ContainsKey(key);
    }

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!_own.ContainsKey(key))
        {
            value = null;
            return false;
        }

        value = Get(key);
        return true;
    }

    internal void Bind(IExpressionEvaluator evaluator, EvaluationContext context)
    {
        lock (_gate)
        {
            _evaluator = evaluator;
        }

        _own.Bind(evaluator, context);
    }

    /// <summary>The value of <paramref name="name"/>: the parent's expression evaluated now, or the nested template's own value.</summary>
    public JsonNode? Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!_passed.ContainsKey(name))
        {
            return _own.Get(name);
        }

        lock (_gate)
        {
            if (_memo.TryGetValue(name, out var memoised))
            {
                return JsonValues.Clone(memoised);
            }
        }

        return Evaluate(name);
    }

    /// <summary>
    /// The value of <paramref name="name"/> without a copy of a memoised value: the caller must only read it (never mutate it or attach it to
    /// another tree). A value evaluated now is the caller's own.
    /// </summary>
    internal JsonNode? GetShared(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!_passed.ContainsKey(name))
        {
            return _own.GetShared(name);
        }

        lock (_gate)
        {
            if (_memo.TryGetValue(name, out var memoised))
            {
                return memoised;
            }
        }

        return Evaluate(name);
    }

    /// <summary>Evaluates the parent's expression for <paramref name="name"/> and memoises a concrete result; returns a value the caller owns.</summary>
    private JsonNode? Evaluate(string name)
    {
        var node = _passed[name];
        IExpressionEvaluator evaluator;
        lock (_gate)
        {
            evaluator = _evaluator ?? throw new InvalidOperationException("NestedParameters must be bound to a context before it is read (EvaluationContext.Bind).");
        }

        var value = evaluator.EvaluateTree(node, _parent);
        if (_own.TryGetBound(name, out var bound) && bound.IsSecure)
        {
            value = SecretJson.Taint(value);
        }

        if (!UnknownJson.ContainsUnknown(value))
        {
            lock (_gate)
            {
                _memo[name] = JsonValues.Clone(value);
            }
        }

        return value;
    }

    public IEnumerator<KeyValuePair<string, JsonNode?>> GetEnumerator()
    {
        foreach (var name in Keys)
        {
            yield return new KeyValuePair<string, JsonNode?>(name, Get(name));
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
