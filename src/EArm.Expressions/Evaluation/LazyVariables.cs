using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core;
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
    /// <summary>The most variables that may be in the middle of being evaluated at once (a variable reading a variable reading a variable ...). ARM has no such limit; EARM keeps the evaluation stack bounded.</summary>
    public const int MaxChainDepth = 128;

    /// <summary>The variables being evaluated on this thread, outermost first (cycle detection and the depth count); evaluation is synchronous.</summary>
    [ThreadStatic]
    private static List<(LazyVariables Owner, string Name)>? chain;

    private readonly Dictionary<string, JsonNode?> _definitions;
    private readonly IReadOnlyDictionary<string, VariableIteration> _iterations;
    private readonly Dictionary<string, JsonNode?> _memo = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ExpressionException> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private IExpressionEvaluator? _evaluator;
    private EvaluationContext? _context;

    public LazyVariables(IReadOnlyDictionary<string, JsonNode?> definitions)
        : this(definitions, null)
    {
    }

    /// <summary>
    /// The variables of a template; <paramref name="iterations"/> are the names the top-level <c>copy</c> array declares (variable iteration),
    /// built as arrays from their entry instead of being evaluated as a value.
    /// </summary>
    public LazyVariables(IReadOnlyDictionary<string, JsonNode?> definitions, IReadOnlyDictionary<string, VariableIteration>? iterations)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        _iterations = iterations ?? new Dictionary<string, VariableIteration>();
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

    /// <summary>
    /// Evaluates (once) and returns a copy of the variable; throws <c>UndefinedVariable</c> / <c>VariableCycle</c> /
    /// <c>VariableChainTooDeep</c>. No lock is held while a definition is evaluated (it re-enters <see cref="Get"/> for the variables it reads),
    /// and a failure is recorded from an exception filter, so a long chain neither deadlocks nor re-throws once per level while unwinding.
    /// </summary>
    public JsonNode? Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        JsonNode? definition;
        IExpressionEvaluator? evaluator;
        EvaluationContext? context;
        lock (_gate)
        {
            if (_memo.TryGetValue(name, out var memoised))
            {
                return JsonValues.Clone(memoised);
            }

            // A variable that failed fails the same way every time it is read; evaluating it again for each reader would make a long
            // chain of failing variables quadratic.
            if (_failures.TryGetValue(name, out var failure))
            {
                throw failure;
            }

            if (!_definitions.TryGetValue(name, out definition))
            {
                throw new ExpressionEvaluationException(DiagnosticCodes.UndefinedVariable, $"Variable '{name}' is not defined.");
            }

            evaluator = _evaluator;
            context = _context;
        }

        if (evaluator is null || context is null)
        {
            throw new InvalidOperationException("LazyVariables must be bound to a context before variables are evaluated (EvaluationContext.Bind).");
        }

        var path = chain ??= [];
        foreach (var (owner, active) in path)
        {
            if (ReferenceEquals(owner, this) && string.Equals(active, name, StringComparison.OrdinalIgnoreCase))
            {
                throw new ExpressionEvaluationException(DiagnosticCodes.VariableCycle, $"Variable '{name}' refers to itself (directly or through other variables).");
            }
        }

        if (path.Count >= MaxChainDepth)
        {
            // Not recorded as the variable's own failure (see Record): the same variable read from a shallower place evaluates fine.
            throw new ExpressionEvaluationException(DiagnosticCodes.VariableChainTooDeep, $"Variables read other variables more than {MaxChainDepth} levels deep (a chain of {MaxChainDepth} or more variables, each using the next); shorten the chain.");
        }

        path.Add((this, name));
        try
        {
            var value = _iterations.TryGetValue(name, out var iteration)
                ? CopyExpansion.Expand(evaluator, iteration.Name, iteration.Count, iteration.Input, context, requiresName: true, $"variable iteration '{iteration.Name}'")
                : evaluator.EvaluateTree(definition, context);
            context.Budget.Retain(value);
            lock (_gate)
            {
                _memo[name] = value;
            }

            return JsonValues.Clone(value);
        }
        catch (ExpressionException ex) when (Record(name, ex))
        {
            throw;
        }
        finally
        {
            path.RemoveAt(path.Count - 1);
        }
    }

    /// <summary>Exception filter: remembers a failure of the variable (never matches, so nothing is caught and re-thrown). Failures that depend on where the variable was read from (depth, stack) are not remembered.</summary>
    private bool Record(string name, ExpressionException ex)
    {
        if (ex.Code is not (DiagnosticCodes.VariableChainTooDeep or DiagnosticCodes.ExpressionTooDeep or DiagnosticCodes.EvaluationBudgetExceeded))
        {
            lock (_gate)
            {
                _failures[name] = ex;
            }
        }

        return false;
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
