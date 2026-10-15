using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Expressions.Values;

namespace EArm.Expressions.Evaluation;

/// <summary>
/// The template's <c>variables</c>, evaluated on first use and memoised, with a cycle guard. Each variable is
/// evaluated in the bound context's <see cref="EvaluationContext.Base"/> with <see cref="EvaluationScope.Variable"/>,
/// whoever asks for it. <see cref="Get"/> returns a fresh copy every time so callers can attach the result to a tree.
/// </summary>
public sealed class LazyVariables
{
    private readonly Dictionary<string, JsonNode?> _definitions;
    private readonly Dictionary<string, JsonNode?> _memo = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _inProgress = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private IExpressionEvaluator? _evaluator;
    private EvaluationContext? _context;

    public LazyVariables(IReadOnlyDictionary<string, JsonNode?> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        _definitions = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in definitions)
        {
            _definitions[pair.Key] = pair.Value;
        }
    }

    public static LazyVariables Empty => new(new Dictionary<string, JsonNode?>());

    public IEnumerable<string> Names => _definitions.Keys;

    public int Count => _definitions.Count;

    public bool Contains(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _definitions.ContainsKey(name);
    }

    /// <summary>The unevaluated definition.</summary>
    public bool TryGetDefinition(string name, out JsonNode? definition)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _definitions.TryGetValue(name, out definition);
    }

    internal void Bind(IExpressionEvaluator evaluator, EvaluationContext context)
    {
        lock (_gate)
        {
            _evaluator = evaluator;
            _context = context.Base.ForScope(EvaluationScope.Variable);
        }
    }

    public bool IsBound => _evaluator is not null;

    /// <summary>Evaluates (once) and returns a copy of the variable; throws <c>UndefinedVariable</c> / <c>VariableCycle</c>.</summary>
    public JsonNode? Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_gate)
        {
            if (_memo.TryGetValue(name, out var memoised))
            {
                return JsonValues.Clone(memoised);
            }

            if (!_definitions.TryGetValue(name, out var definition))
            {
                throw new ExpressionEvaluationException(DiagnosticCodes.UndefinedVariable, $"Variable '{name}' is not defined.");
            }

            if (_evaluator is null || _context is null)
            {
                throw new InvalidOperationException("LazyVariables must be bound to a context before variables are evaluated (EvaluationContext.Bind).");
            }

            if (!_inProgress.Add(name))
            {
                throw new ExpressionEvaluationException(DiagnosticCodes.VariableCycle, $"Variable '{name}' refers to itself (directly or through other variables).");
            }

            try
            {
                var value = _evaluator.EvaluateTree(definition, _context);
                _memo[name] = value;
                return JsonValues.Clone(value);
            }
            finally
            {
                _inProgress.Remove(name);
            }
        }
    }

    /// <summary>The memoised value when the variable has already been evaluated.</summary>
    public bool TryGetEvaluated(string name, out JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_gate)
        {
            if (_memo.TryGetValue(name, out var memoised))
            {
                value = JsonValues.Clone(memoised);
                return true;
            }
        }

        value = null;
        return false;
    }
}
