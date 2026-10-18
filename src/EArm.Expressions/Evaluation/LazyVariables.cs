using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Expressions.Values;

namespace EArm.Expressions.Evaluation;

/// <summary>
/// The template's <c>variables</c>, evaluated on first use and memoised, with a cycle guard. Each variable is
/// evaluated in the bound context's <see cref="EvaluationContext.Base"/> with <see cref="EvaluationScope.Variable"/>,
/// whoever asks for it. <see cref="Get"/> returns a fresh copy every time so callers can attach the result to a tree.
/// Each variable is evaluated once per instance (one evaluation context): a reader that finds another thread evaluating it (resources are
/// planned in parallel) waits for that value instead of evaluating it again.
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

    /// <summary>The variables being evaluated right now, by the managed thread id of the evaluating thread (one evaluation per variable).</summary>
    private readonly Dictionary<string, int> _pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The threads waiting for another thread's evaluation, with the variable each waits for (deadlock detection).</summary>
    private readonly Dictionary<int, string> _waiting = [];
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
        return JsonValues.Clone(GetShared(name));
    }

    /// <summary>
    /// Like <see cref="Get"/>, but returns the memoised value itself instead of a copy: the caller only reads it (never mutates it or attaches it
    /// to another tree). The evaluator uses it for <c>variables('x')[...]</c> and <c>variables('x').y</c>, which copy only the selected part, so
    /// reading one element of a large variable costs the element, not the variable (ARM evaluates a variable once and reads it from there).
    /// </summary>
    internal JsonNode? GetShared(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var path = chain ??= [];
        foreach (var (owner, active) in path)
        {
            if (ReferenceEquals(owner, this) && string.Equals(active, name, StringComparison.OrdinalIgnoreCase))
            {
                throw Cycle(name);
            }
        }

        if (path.Count >= MaxChainDepth)
        {
            // Not recorded as the variable's own failure (see Record): the same variable read from a shallower place evaluates fine.
            throw new ExpressionEvaluationException(DiagnosticCodes.VariableChainTooDeep, $"Variables read other variables more than {MaxChainDepth} levels deep (a chain of {MaxChainDepth} or more variables, each using the next); shorten the chain.");
        }

        JsonNode? definition;
        IExpressionEvaluator? evaluator;
        EvaluationContext? context;
        int thread = Environment.CurrentManagedThreadId;
        lock (_gate)
        {
            while (true)
            {
                if (_memo.TryGetValue(name, out var memoised))
                {
                    return memoised;
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
                if (evaluator is null || context is null)
                {
                    throw new InvalidOperationException("LazyVariables must be bound to a context before variables are evaluated (EvaluationContext.Bind).");
                }

                // Another thread is evaluating it (resources are planned in parallel): wait for its value instead of evaluating it a second time.
                // The owner never waits for this thread unless the variables form a cycle across the two threads, which is reported as one.
                if (_pending.TryGetValue(name, out int owner))
                {
                    if (WaitWouldDeadlock(owner, thread))
                    {
                        throw Cycle(name);
                    }

                    _waiting[thread] = name;
                    try
                    {
                        Monitor.Wait(_gate);
                    }
                    finally
                    {
                        _waiting.Remove(thread);
                    }

                    continue;
                }

                _pending[name] = thread;
                break;
            }
        }

        path.Add((this, name));        try
        {
            var value = _iterations.TryGetValue(name, out var iteration)
                ? CopyExpansion.Expand(evaluator, iteration.Name, iteration.Count, iteration.Input, context, requiresName: true, $"variable iteration '{iteration.Name}'")
                : evaluator.EvaluateTree(definition, context);
            context.Budget.Retain(value);
            lock (_gate)
            {
                _memo[name] = value;
            }

            return value;
        }
        catch (ExpressionException ex) when (Record(name, ex))
        {
            throw;
        }
        finally
        {
            path.RemoveAt(path.Count - 1);
            lock (_gate)
            {
                _pending.Remove(name);
                Monitor.PulseAll(_gate);
            }
        }
    }

    /// <summary>True when the thread evaluating the awaited variable is itself (directly or through other threads) waiting for <paramref name="thread"/>. Called under the gate.</summary>
    private bool WaitWouldDeadlock(int owner, int thread)
    {
        int current = owner;
        for (int hops = 0; hops <= _waiting.Count; hops++)
        {
            if (current == thread)
            {
                return true;
            }

            if (!_waiting.TryGetValue(current, out var awaited) || !_pending.TryGetValue(awaited, out current))
            {
                return false;
            }
        }

        return false;
    }

    private static ExpressionEvaluationException Cycle(string name)
    {
        return new ExpressionEvaluationException(DiagnosticCodes.VariableCycle, $"Variable '{name}' refers to itself (directly or through other variables).");
    }
    /// <summary>
    /// Exception filter: remembers a failure of the variable (never matches, so nothing is caught and re-thrown). Failures that depend on where the variable
    /// was read from (depth, stack) are not remembered, nor is a lookup of an existing resource that was not found: <c>create</c> without <c>-y</c> runs with
    /// the store its what-if filled, and the object may be one the run creates (<c>reference(id, apiVersion)</c> finds it once it is published).
    /// </summary>
    private bool Record(string name, ExpressionException ex)
    {
        if (ex.Code is not (DiagnosticCodes.VariableChainTooDeep or DiagnosticCodes.ExpressionTooDeep or DiagnosticCodes.EvaluationBudgetExceeded or DiagnosticCodes.ReferenceTargetNotFound))
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
