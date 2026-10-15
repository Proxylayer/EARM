using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading;
using EArm.Core.Diagnostics;
using EArm.Core.Json;

namespace EArm.Expressions.Evaluation;

/// <summary>
/// The limits one run of the evaluator works within. The size limits are fixed (they are part of the template format and
/// documented in <c>docs/format.md</c>); the work limits can be tuned: the wall-clock budget through <c>--evaluation-timeout</c> or
/// <c>EARM_EVALUATION_TIMEOUT</c> (seconds), the others only through code (tests).
/// </summary>
public sealed record EvaluationLimits
{
    /// <summary>The environment variable that sets <see cref="Timeout"/> (seconds; <c>0</c> disables the wall-clock budget).</summary>
    public const string TimeoutVariable = "EARM_EVALUATION_TIMEOUT";

    /// <summary>The largest <c>copy.count</c> of one resource (the ARM limit).</summary>
    public const int MaxCopyCount = 800;

    /// <summary>The most resource instances a template may expand to, all <c>copy</c> loops together.</summary>
    public const int MaxInstances = 5_000;

    /// <summary>The longest string a function may produce, in characters (1 MiB).</summary>
    public const int MaxStringLength = 1_048_576;

    /// <summary>The most elements one array or object may hold.</summary>
    public const int MaxCollectionCount = 100_000;

    /// <summary>The most nodes (containers and leaves) one value may hold.</summary>
    public const int MaxValueNodes = 1_000_000;

    /// <summary>The deepest nesting a value may have (see <see cref="ValueGuard.MaxDepth"/>).</summary>
    public const int MaxValueDepth = ValueGuard.MaxDepth;

    /// <summary>The largest wall-clock budget a flag or the environment may set, in seconds (24 hours).</summary>
    public const int MaxTimeoutSeconds = 86_400;

    /// <summary>The default wall-clock budget, in seconds.</summary>
    public const int DefaultTimeoutSeconds = 60;

    /// <summary>Function invocations plus lambda iterations a run may perform.</summary>
    public long MaxSteps { get; init; } = 50_000_000;

    /// <summary>Value nodes a run may produce in total (every function result is walked once).</summary>
    public long MaxNodes { get; init; } = 5_000_000;

    /// <summary>Characters of string results a run may produce in total (128 Mi characters, 256 MiB of text).</summary>
    public long MaxStringChars { get; init; } = 128L * 1024 * 1024;

    /// <summary>Bytes of evaluated values a run may keep alive (the memoised variables), estimated from string lengths and node counts (256 MiB).</summary>
    public long MaxRetainedBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Wall-clock time one evaluation (a variable, the properties of a resource, an output) may take, measured from its start; <see cref="TimeSpan.Zero"/> disables the limit. The step and node budgets bound the total work of a run.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(DefaultTimeoutSeconds);

    public static EvaluationLimits Default { get; } = new();

    /// <summary>The default limits with the timeout of <see cref="TimeoutVariable"/> when it holds a valid number of seconds.</summary>
    public static EvaluationLimits FromEnvironment()
    {
        string? text = Environment.GetEnvironmentVariable(TimeoutVariable);
        return TryParseTimeout(text, out var timeout) ? new EvaluationLimits { Timeout = timeout } : Default;
    }

    /// <summary>Parses a number of seconds (<c>0</c> to <see cref="MaxTimeoutSeconds"/>, fractions allowed); false for anything else (non-numbers, NaN, infinities, negatives, too large).</summary>
    public static bool TryParseTimeout(string? text, out TimeSpan timeout)
    {
        timeout = default;
        if (string.IsNullOrWhiteSpace(text)
            || !double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            || !double.IsFinite(seconds)
            || seconds < 0
            || seconds > MaxTimeoutSeconds)
        {
            return false;
        }

        timeout = TimeSpan.FromSeconds(seconds);
        return true;
    }
}

/// <summary>
/// The work and size budget of one run (a command invocation): counts function invocations, lambda iterations and produced
/// value nodes and string characters, watches the wall clock of each evaluation, and checks every function result against the size limits of
/// <see cref="EvaluationLimits"/> (string length, elements per array or object, nodes per value, nesting depth). When a limit is hit it
/// throws an <see cref="ExpressionEvaluationException"/> (<c>EvaluationBudgetExceeded</c>, or <c>ValueTooDeep</c> for nesting) and stays
/// tripped, so the rest of the run fails fast instead of continuing to burn time and memory.
/// <para>
/// A run owns one instance, installed with <see cref="BeginRun"/>; every <see cref="EvaluationContext"/> created while it is active
/// shares it. Contexts created outside a run get a private budget with the default limits. Thread-safe.
/// </para>
/// </summary>
public sealed class EvaluationBudget
{
    private static readonly AsyncLocal<EvaluationBudget?> Ambient = new();

    /// <summary>How deeply nested the evaluations of the current thread are (an evaluation calls variables, which evaluate in turn); only the outermost one starts the clock.</summary>
    [ThreadStatic]
    private static int unitDepth;

    /// <summary>When the outermost evaluation of the current thread started.</summary>
    [ThreadStatic]
    private static long unitStart;

    private readonly EvaluationLimits _limits;
    private readonly long _timeoutTicks;
    private long _steps;
    private long _nodes;
    private long _stringChars;
    private long _retainedBytes;
    private volatile string? _exceeded;

    public EvaluationBudget(EvaluationLimits? limits = null)
    {
        _limits = limits ?? EvaluationLimits.Default;
        _timeoutTicks = _limits.Timeout <= TimeSpan.Zero ? 0 : (long)(_limits.Timeout.TotalSeconds * Stopwatch.Frequency);
    }

    /// <summary>The budget of the active run, or a private one with the default limits (and the environment's timeout) when no run is active.</summary>
    public static EvaluationBudget Current => Ambient.Value ?? new EvaluationBudget(EvaluationLimits.FromEnvironment());

    /// <summary>The budget installed by <see cref="BeginRun"/> for the current flow of execution, or <c>null</c>.</summary>
    public static EvaluationBudget? Active => Ambient.Value;

    public EvaluationLimits Limits => _limits;

    /// <summary>Function invocations and lambda iterations so far.</summary>
    public long Steps => Interlocked.Read(ref _steps);

    /// <summary>Value nodes produced so far.</summary>
    public long Nodes => Interlocked.Read(ref _nodes);

    /// <summary>True once a limit has been hit.</summary>
    public bool IsExceeded => _exceeded is not null;

    /// <summary>Starts a run: the new budget becomes the one every context created in this flow of execution shares, until the scope is disposed.</summary>
    public static RunScope BeginRun(EvaluationLimits? limits = null)
    {
        var previous = Ambient.Value;
        var budget = new EvaluationBudget(limits ?? EvaluationLimits.FromEnvironment());
        Ambient.Value = budget;
        return new RunScope(budget, previous);
    }

    /// <summary>
    /// Marks the start of an evaluation on the current thread (an expression, a variable, a tree): the wall-clock limit applies to the outermost
    /// one, measured from its start. Dispose the result when the evaluation ends.
    /// </summary>
    public static EvaluationUnit BeginUnit()
    {
        if (unitDepth++ == 0)
        {
            unitStart = Stopwatch.GetTimestamp();
        }

        return default;
    }

    /// <summary>The scope returned by <see cref="BeginUnit"/>.</summary>
    public readonly struct EvaluationUnit : IDisposable
    {
        public void Dispose()
        {
            if (unitDepth > 0)
            {
                unitDepth--;
            }
        }
    }

    /// <summary>Counts one function invocation or lambda iteration.</summary>
    public void Step(int? offset = null)
    {
        Advance(1, offset);
    }

    /// <summary>Counts <paramref name="units"/> of work (for example the elements an operation touches).</summary>
    public void Charge(long units, int? offset = null)
    {
        if (units > 0)
        {
            Advance(units, offset);
        }
    }

    /// <summary>
    /// Charges the work of scanning or copying <paramref name="characters"/> characters (one step per 1,024), so a long string operation counts
    /// against the step budget and the wall clock is looked at while a template makes many of them.
    /// </summary>
    public void ChargeText(long characters, int? offset = null)
    {
        if (characters > 0)
        {
            Advance(((characters - 1) >> 10) + 1, offset);
        }
    }

    /// <summary>
    /// Counts the bytes a value that stays alive for the rest of the run holds (a memoised variable): string characters at two bytes each plus
    /// a fixed cost per node. Throws <c>EvaluationBudgetExceeded</c> (and trips the run) when the total passes <see cref="EvaluationLimits.MaxRetainedBytes"/>.
    /// </summary>
    public void Retain(JsonNode? value, int? offset = null)
    {
        if (value is null)
        {
            return;
        }

        long bytes = 0;
        var stack = new Stack<JsonNode>();
        stack.Push(value);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            bytes += 48;
            switch (current)
            {
                case JsonObject obj:
                    foreach (var property in obj)
                    {
                        bytes += 2L * property.Key.Length;
                        if (property.Value is not null)
                        {
                            stack.Push(property.Value);
                        }
                    }

                    break;
                case JsonArray array:
                    foreach (var item in array)
                    {
                        if (item is not null)
                        {
                            stack.Push(item);
                        }
                    }

                    break;
                case JsonValue leaf when leaf.TryGetValue<string>(out var text):
                    bytes += 2L * text.Length;
                    break;
            }
        }

        ThrowIfExceeded(offset);
        long total = Interlocked.Add(ref _retainedBytes, bytes);
        if (total > _limits.MaxRetainedBytes)
        {
            throw Trip(string.Create(CultureInfo.InvariantCulture, $"The evaluated variables hold more than {_limits.MaxRetainedBytes:N0} bytes in memory."), offset);
        }
    }

    /// <summary>Throws when <paramref name="length"/> characters exceed the string limit.</summary>
    public void CheckStringLength(long length, int? offset = null)
    {
        if (length > EvaluationLimits.MaxStringLength)
        {
            throw Exceeded(string.Create(CultureInfo.InvariantCulture, $"A string longer than {EvaluationLimits.MaxStringLength:N0} characters (1 MiB) would be produced."), offset);
        }
    }

    /// <summary>Throws when <paramref name="count"/> elements exceed the limit of one array or object.</summary>
    public void CheckCollectionCount(long count, int? offset = null)
    {
        if (count > EvaluationLimits.MaxCollectionCount)
        {
            throw Exceeded(string.Create(CultureInfo.InvariantCulture, $"An array or object with more than {EvaluationLimits.MaxCollectionCount:N0} elements would be produced."), offset);
        }
    }

    /// <summary>
    /// Checks a function result (or a whole evaluated tree) against the size limits and counts its nodes. Iterative, so a value of any
    /// depth is rejected without recursion. A value nested deeper than <see cref="EvaluationLimits.MaxValueDepth"/> throws
    /// <c>ValueTooDeep</c>; every other limit throws <c>EvaluationBudgetExceeded</c>.
    /// </summary>
    public void CheckValue(JsonNode? value, int? offset = null)
    {
        switch (value)
        {
            case null:
                return;
            case JsonValue leaf:
                CheckLeaf(leaf, offset);
                if (leaf.TryGetValue<string>(out var text))
                {
                    ChargeStringChars(text.Length, offset);
                }

                return;
        }

        long nodes = 0;
        long characters = 0;
        var stack = new Stack<(JsonNode Node, int Depth)>();
        stack.Push((value, 1));
        while (stack.Count > 0)
        {
            var (current, depth) = stack.Pop();
            if (depth > EvaluationLimits.MaxValueDepth)
            {
                throw new ExpressionEvaluationException(DiagnosticCodes.ValueTooDeep, ValueGuard.TooDeepMessage, offset);
            }

            nodes++;
            if (nodes > EvaluationLimits.MaxValueNodes)
            {
                throw Exceeded(string.Create(CultureInfo.InvariantCulture, $"A value with more than {EvaluationLimits.MaxValueNodes:N0} nodes would be produced."), offset);
            }

            switch (current)
            {
                case JsonObject obj:
                    CheckCollectionCount(obj.Count, offset);
                    foreach (var property in obj)
                    {
                        Visit(property.Value);
                    }

                    break;
                case JsonArray array:
                    CheckCollectionCount(array.Count, offset);
                    foreach (var item in array)
                    {
                        Visit(item);
                    }

                    break;
            }

            // The leaves of the container just read count too: check now, not only when the next container is taken (a value of 1,000,000 nodes ends in leaves).
            if (nodes > EvaluationLimits.MaxValueNodes)
            {
                throw Exceeded(string.Create(CultureInfo.InvariantCulture, $"A value with more than {EvaluationLimits.MaxValueNodes:N0} nodes would be produced."), offset);
            }

            void Visit(JsonNode? child)
            {
                if (child is JsonObject or JsonArray)
                {
                    stack.Push((child, depth + 1));
                }
                else if (child is JsonValue childLeaf)
                {
                    nodes++;
                    CheckLeaf(childLeaf, offset);
                    if (childLeaf.TryGetValue<string>(out var childText))
                    {
                        characters += childText.Length;
                    }
                }
                else
                {
                    nodes++;
                }
            }
        }

        ChargeNodes(nodes, offset);
        ChargeStringChars(characters, offset);
    }

    /// <summary>
    /// Throws <c>DuplicateJsonKey</c> when an object in <paramref name="value"/> has two properties whose names differ only in letter case
    /// (<c>union</c>, <c>createObject</c>, <c>items</c> or <c>toObject</c> can build them). EARM looks properties up ignoring case and takes the
    /// first match, but the object would be sent with both, so what a guard judged and what is written would differ. Iterative; secret-derived
    /// keys are already masked, so the names shown are template text.
    /// </summary>
    public void CheckPropertyNames(JsonNode? value, int? offset = null)
    {
        if (value is not (JsonObject or JsonArray))
        {
            return;
        }

        var stack = new Stack<JsonNode>();
        stack.Push(value);
        while (stack.Count > 0)
        {
            switch (stack.Pop())
            {
                case JsonObject obj:
                    if (obj.Count > 1 && FindCaseVariant(obj) is { } pair)
                    {
                        throw new ExpressionEvaluationException(
                            DiagnosticCodes.DuplicateJsonKey,
                            $"An object has the properties {SafeText.Quote(pair.First)} and {SafeText.Quote(pair.Second)}, which differ only in letter case; property names are compared ignoring case, so which one counts would be ambiguous. Remove or rename one of them.",
                            offset);
                    }

                    foreach (var property in obj)
                    {
                        if (property.Value is JsonObject or JsonArray)
                        {
                            stack.Push(property.Value);
                        }
                    }

                    break;
                case JsonArray array:
                    foreach (var item in array)
                    {
                        if (item is JsonObject or JsonArray)
                        {
                            stack.Push(item);
                        }
                    }

                    break;
            }
        }
    }

    private static (string First, string Second)? FindCaseVariant(JsonObject obj)
    {
        if (obj.Count <= 12)
        {
            var keys = new List<string>(obj.Count);
            foreach (var property in obj)
            {
                foreach (var earlier in keys)
                {
                    if (string.Equals(earlier, property.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        return (earlier, property.Key);
                    }
                }

                keys.Add(property.Key);
            }

            return null;
        }

        var seen = new Dictionary<string, string>(obj.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var property in obj)
        {
            if (!seen.TryAdd(property.Key, property.Key))
            {
                return (seen[property.Key], property.Key);
            }
        }

        return null;
    }

    private void CheckLeaf(JsonValue leaf, int? offset)
    {
        if (leaf.TryGetValue<string>(out var text) && text.Length > EvaluationLimits.MaxStringLength)
        {
            throw Exceeded(string.Create(CultureInfo.InvariantCulture, $"A string longer than {EvaluationLimits.MaxStringLength:N0} characters (1 MiB) would be produced."), offset);
        }
    }

    private void ChargeNodes(long nodes, int? offset)
    {
        ThrowIfExceeded(offset);
        long total = Interlocked.Add(ref _nodes, nodes);
        if (total > _limits.MaxNodes)
        {
            throw Trip(string.Create(CultureInfo.InvariantCulture, $"The evaluation produced more than {_limits.MaxNodes:N0} value elements in total."), offset);
        }

        Advance(Math.Max(1, nodes / 16), offset);
    }

    private void ChargeStringChars(long characters, int? offset)
    {
        if (characters == 0)
        {
            return;
        }

        long total = Interlocked.Add(ref _stringChars, characters);
        if (total > _limits.MaxStringChars)
        {
            throw Trip(string.Create(CultureInfo.InvariantCulture, $"The evaluation produced more than {_limits.MaxStringChars:N0} characters of text in total."), offset);
        }
    }

    private void Advance(long units, int? offset)
    {
        ThrowIfExceeded(offset);
        long after = Interlocked.Add(ref _steps, units);
        if (after > _limits.MaxSteps)
        {
            throw Trip(string.Create(CultureInfo.InvariantCulture, $"The evaluation needs more than {_limits.MaxSteps:N0} steps (function calls and lambda iterations)."), offset);
        }

        if ((after >> 8) != ((after - units) >> 8))
        {
            CheckTime(offset);
        }
    }

    private void CheckTime(int? offset)
    {
        if (_timeoutTicks == 0)
        {
            return;
        }

        // The clock runs for the outermost evaluation of this thread (one variable, one resource's properties, one output): waiting for the
        // network between evaluations does not count, and the step budget bounds the total work of a run.
        if (unitDepth == 0)
        {
            return;
        }

        if (Stopwatch.GetTimestamp() - unitStart > _timeoutTicks)
        {
            throw Trip(string.Create(CultureInfo.InvariantCulture, $"The evaluation took longer than {_limits.Timeout.TotalSeconds:0.##} seconds (see --evaluation-timeout / {EvaluationLimits.TimeoutVariable})."), offset);
        }
    }

    private void ThrowIfExceeded(int? offset)
    {
        if (_exceeded is { } message)
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.EvaluationBudgetExceeded, message, offset);
        }
    }

    /// <summary>A size violation of one value: reported, but later evaluations are not affected.</summary>
    private static ExpressionEvaluationException Exceeded(string message, int? offset)
    {
        return new ExpressionEvaluationException(DiagnosticCodes.EvaluationBudgetExceeded, message, offset);
    }

    private ExpressionEvaluationException Trip(string message, int? offset)
    {
        _exceeded ??= message;
        return new ExpressionEvaluationException(DiagnosticCodes.EvaluationBudgetExceeded, message, offset);
    }

    /// <summary>Restores the previous ambient budget when disposed.</summary>
    public sealed class RunScope : IDisposable
    {
        private readonly EvaluationBudget? _previous;
        private bool _disposed;

        internal RunScope(EvaluationBudget budget, EvaluationBudget? previous)
        {
            Budget = budget;
            _previous = previous;
        }

        public EvaluationBudget Budget { get; }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Ambient.Value = _previous;
            }
        }
    }
}
