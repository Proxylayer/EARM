using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Secrets;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Syntax;
using EArm.Expressions.Values;

namespace EArm.Expressions.Functions;

public enum FunctionKind
{
    /// <summary>Implemented.</summary>
    Supported,

    /// <summary>An ARM function that has no meaning for Entra; calling it is an error naming the alternative.</summary>
    Rejected,

    /// <summary>
    /// Registered but not implemented: evaluates to an unknown value and validation warns. No function of the
    /// default registry has this kind any more (lambdas, the Entra functions and user-defined functions landed with
    /// WI-16); it remains for registries that stage a function before implementing it.
    /// </summary>
    NotImplementedYet,
}

/// <summary>The body of a built-in function.</summary>
public delegate JsonNode? FunctionImplementation(FunctionCall call);

/// <summary>Registration metadata of one function. Names are case-insensitive.</summary>
public sealed record FunctionDescriptor
{
    public required string Name { get; init; }

    public FunctionKind Kind { get; init; } = FunctionKind.Supported;

    /// <summary>The group the function belongs to (deployment, string, logic, numeric, collection, lambda, entra, rejected).</summary>
    public required string Group { get; init; }

    public int MinArgs { get; init; }

    /// <summary><c>-1</c> for unbounded.</summary>
    public int MaxArgs { get; init; } = -1;

    /// <summary><c>utcNow()</c>/<c>newGuid()</c>: legal only in parameter defaults.</summary>
    public bool NonDeterministic { get; init; }

    /// <summary>The implementation receives unevaluated arguments (<c>if</c>, functions taking lambdas).</summary>
    public bool Lazy { get; init; }

    /// <summary>
    /// The implementation is called even when an argument contains an unknown leaf (default: the call yields an
    /// unknown value without running the implementation).
    /// </summary>
    public bool HandlesUnknown { get; init; }

    /// <summary>For <see cref="FunctionKind.Rejected"/>: the Entra alternative; for <see cref="FunctionKind.NotImplementedYet"/>: the work item.</summary>
    public string? Note { get; init; }

    public FunctionImplementation? Implementation { get; init; }

    public bool AcceptsArgumentCount(int count)
    {
        return count >= MinArgs && (MaxArgs < 0 || count <= MaxArgs);
    }

    public string ArityDescription => MaxArgs < 0
        ? $"at least {MinArgs}"
        : MinArgs == MaxArgs ? $"exactly {MinArgs}" : $"between {MinArgs} and {MaxArgs}";
}

/// <summary>
/// One invocation handed to a <see cref="FunctionImplementation"/>: the evaluated arguments (secrets revealed, see
/// <see cref="ExpressionEvaluator"/>), or for lazy functions the argument expressions plus
/// <see cref="EvaluateArgument"/>. The typed accessors throw <c>InvalidFunctionArguments</c> with the call offset.
/// <para>
/// Redaction rule: text that reaches a message or an unknown symbol must come from <see cref="RedactedArguments"/>
/// (through <see cref="Render(int)"/>, <see cref="RenderArguments"/> or <see cref="Redact(int, string)"/>), never
/// from <see cref="Arguments"/>. A secure value is revealed only so the implementation can compute with it; any
/// string derived from a tainted argument renders as <c>***</c> wherever it would be shown.
/// </para>
/// </summary>
public sealed class FunctionCall
{
    private readonly ExpressionEvaluator _evaluator;

    internal FunctionCall(ExpressionEvaluator evaluator, CallExpression call, FunctionDescriptor descriptor, IReadOnlyList<JsonNode?> arguments, EvaluationContext context, IReadOnlyList<JsonNode?>? redactedArguments = null)
    {
        _evaluator = evaluator;
        Call = call;
        Descriptor = descriptor;
        Arguments = arguments;
        RedactedArguments = redactedArguments ?? arguments;
        Context = context;
        IsTainted = RedactedArguments.Any(UnknownJson.ContainsTaint);
    }

    public CallExpression Call { get; }

    public FunctionDescriptor Descriptor { get; }

    public string Name => Descriptor.Name;

    public int Offset => Call.Offset;

    public EvaluationContext Context { get; }

    /// <summary>Evaluated arguments (empty for lazy functions).</summary>
    public IReadOnlyList<JsonNode?> Arguments { get; }

    /// <summary>
    /// The arguments as evaluated, before secrets were revealed (the same list as <see cref="Arguments"/> when the
    /// call is not tainted): the only source for text shown in messages and symbols.
    /// </summary>
    public IReadOnlyList<JsonNode?> RedactedArguments { get; }

    /// <summary>True when an argument contained a secure value (revealed in <see cref="Arguments"/>) or a tainted unknown.</summary>
    public bool IsTainted { get; }

    public IReadOnlyList<Expression> ArgumentExpressions => Call.Arguments;

    public int Count => Call.Arguments.Count;

    /// <summary>Evaluates argument <paramref name="index"/> now (lazy functions).</summary>
    public JsonNode? EvaluateArgument(int index)
    {
        return _evaluator.Evaluate(Call.Arguments[index], Context);
    }

    public JsonNode? Arg(int index)
    {
        return index < Arguments.Count ? Arguments[index] : null;
    }

    public bool Has(int index)
    {
        return index < Arguments.Count;
    }

    public string String(int index)
    {
        if (JsonValues.TryGetString(Arg(index), out var text))
        {
            return text;
        }

        throw Error($"argument {index + 1} must be a string, got {JsonValues.Describe(Arg(index))}.");
    }

    public long Int(int index)
    {
        if (JsonValues.TryGetInt64(Arg(index), out long value))
        {
            return value;
        }

        throw Error($"argument {index + 1} must be an integer, got {JsonValues.Describe(Arg(index))}.");
    }

    public int Int32(int index)
    {
        long value = Int(index);
        if (value is < int.MinValue or > int.MaxValue)
        {
            throw Error($"argument {index + 1} is out of range ({Render(index)}).");
        }

        return (int)value;
    }

    public bool Bool(int index)
    {
        if (JsonValues.TryGetBool(Arg(index), out bool value))
        {
            return value;
        }

        throw Error($"argument {index + 1} must be a boolean, got {JsonValues.Describe(Arg(index))}.");
    }

    public JsonArray Array(int index)
    {
        if (Arg(index) is JsonArray array)
        {
            return array;
        }

        throw Error($"argument {index + 1} must be an array, got {JsonValues.Describe(Arg(index))}.");
    }

    public JsonObject Object(int index)
    {
        if (Arg(index) is JsonObject obj)
        {
            return obj;
        }

        throw Error($"argument {index + 1} must be an object, got {JsonValues.Describe(Arg(index))}.");
    }

    /// <summary>An <c>InvalidFunctionArguments</c> error for this call.</summary>
    public ExpressionEvaluationException Error(string detail)
    {
        return new ExpressionEvaluationException(DiagnosticCodes.InvalidFunctionArguments, $"{Name}(): {detail}", Offset);
    }

    /// <summary>An <c>ExpressionEvaluationFailed</c> error for this call (runtime failure with valid argument types).</summary>
    public ExpressionEvaluationException Failure(string detail, Exception? inner = null)
    {
        return new ExpressionEvaluationException(DiagnosticCodes.ExpressionEvaluationFailed, $"{Name}(): {detail}", Offset, inner);
    }

    /// <summary>An unknown value standing for this call's result (canonical symbol from the evaluated arguments).</summary>
    public JsonValue Unknown(UnknownOrigins origins, bool tainted = false)
    {
        return UnknownJson.Create($"{Name}({RenderArguments()})", origins, tainted);
    }

    /// <summary>
    /// An unknown result whose origins and taint are the union of every unknown leaf in the arguments (for
    /// implementations that handle unknown inputs but hit an argument they must inspect).
    /// </summary>
    public JsonValue UnknownFromArguments()
    {
        var origins = UnknownOrigins.None;
        bool tainted = false;
        foreach (var argument in Arguments)
        {
            origins |= UnknownJson.OriginsOf(argument);
            tainted |= UnknownJson.ContainsTaint(argument);
        }

        return Unknown(origins == UnknownOrigins.None ? UnknownOrigins.Reference : origins, tainted);
    }

    /// <summary>True when argument <paramref name="index"/> is itself an unknown leaf.</summary>
    public bool IsUnknown(int index)
    {
        return UnknownJson.IsUnknown(Arg(index));
    }

    /// <summary>True when argument <paramref name="index"/> contains a secure value or a tainted unknown.</summary>
    public bool ArgumentIsTainted(int index)
    {
        return index >= 0 && index < RedactedArguments.Count && UnknownJson.ContainsTaint(RedactedArguments[index]);
    }

    /// <summary>
    /// Argument <paramref name="index"/> rendered for a message or symbol: secure values as <c>***</c>, unknowns by
    /// symbol, strings quoted, numbers invariant (<see cref="JsonValues.Render"/> of the redacted argument).
    /// </summary>
    public string Render(int index)
    {
        return JsonValues.Render(index >= 0 && index < RedactedArguments.Count ? RedactedArguments[index] : null);
    }

    /// <summary>Every argument rendered as by <see cref="Render(int)"/>, comma-separated: the body of this call's canonical symbol.</summary>
    public string RenderArguments()
    {
        return string.Join(", ", RedactedArguments.Select(JsonValues.Render));
    }

    /// <summary>
    /// The display form of <paramref name="derived"/>, a string computed from argument <paramref name="index"/>:
    /// <c>***</c> when that argument is tainted, otherwise the string itself.
    /// </summary>
    public string Redact(int index, string derived)
    {
        return ArgumentIsTainted(index) ? SecretValue.Mask : derived;
    }

    /// <summary>The display form of <paramref name="derived"/>, a string computed from several arguments: <c>***</c> when the call is tainted.</summary>
    public string Redact(string derived)
    {
        return IsTainted ? SecretValue.Mask : derived;
    }

    /// <summary>
    /// Argument <paramref name="index"/> as a lambda (lazy functions only): it must be a <c>lambda(...)</c> declaring
    /// between <paramref name="minParameters"/> and <paramref name="maxParameters"/> distinct parameter names.
    /// <paramref name="roles"/> names them for the message (<c>element[, index]</c>).
    /// </summary>
    public LambdaExpression Lambda(int index, int minParameters, int maxParameters, string roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        var expression = index < Call.Arguments.Count ? Call.Arguments[index] : null;
        if (expression is not LambdaExpression lambda)
        {
            // Expression text is template source, never a parameter value, so it is safe to show.
            string got = expression is null ? "nothing" : expression.ToExpressionText();
            throw Error($"argument {index + 1} must be a lambda (lambda('x', ...)), got {got}.");
        }

        int count = lambda.Parameters.Count;
        if (count < minParameters || count > maxParameters)
        {
            string expected = minParameters == maxParameters ? $"exactly {minParameters}" : $"between {minParameters} and {maxParameters}";
            throw new ExpressionEvaluationException(DiagnosticCodes.InvalidFunctionArguments, $"{Name}(): the lambda of argument {index + 1} takes {expected} parameter(s) ({roles}), got {count}.", lambda.Offset);
        }

        if (lambda.Parameters.Distinct(StringComparer.OrdinalIgnoreCase).Count() != count)
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.InvalidFunctionArguments, $"{Name}(): the lambda of argument {index + 1} declares the same parameter name twice ({string.Join(", ", lambda.Parameters)}).", lambda.Offset);
        }

        return lambda;
    }

    /// <summary>
    /// Evaluates the body of <paramref name="lambda"/> with its parameters bound positionally to
    /// <paramref name="values"/> (an optional parameter the lambda does not declare, such as the element index, is
    /// simply not bound). Bindings shadow those of enclosing lambdas; everything else of the context is shared.
    /// </summary>
    public JsonNode? InvokeLambda(LambdaExpression lambda, params JsonNode?[] values)
    {
        ArgumentNullException.ThrowIfNull(lambda);
        ArgumentNullException.ThrowIfNull(values);
        var scope = Context.LambdaScope;
        for (int i = 0; i < lambda.Parameters.Count && i < values.Length; i++)
        {
            scope = scope.SetItem(lambda.Parameters[i], values[i]);
        }

        return _evaluator.Evaluate(lambda.Body, Context with { LambdaScope = scope });
    }
}

/// <summary>
/// The functions an expression may call, keyed case-insensitively. <see cref="Default"/> holds every ARM function
/// the plan supports (groups 1-5: deployment, string, logic, numeric, collection and lambda), the Entra functions
/// (group 6: <c>wellKnownApp appRole permissionScope roleTemplate</c>) and the rejected ARM functions; user-defined
/// functions (group 7) are resolved from the template's <c>functions</c> section by the evaluator. Use
/// <see cref="CreateDefault"/> to get a private copy to extend with <see cref="Register"/>.
/// </summary>
public sealed class FunctionRegistry
{
    private readonly Dictionary<string, FunctionDescriptor> _functions = new(StringComparer.OrdinalIgnoreCase);

    public static FunctionRegistry Default { get; } = CreateDefault();

    public static FunctionRegistry CreateDefault()
    {
        var registry = new FunctionRegistry();
        DeploymentFunctions.Register(registry);
        StringFunctions.Register(registry);
        LogicFunctions.Register(registry);
        NumericFunctions.Register(registry);
        CollectionFunctions.Register(registry);
        LambdaFunctions.Register(registry);
        EntraFunctions.Register(registry);
        RejectedFunctions.Register(registry);
        return registry;
    }

    public IReadOnlyCollection<FunctionDescriptor> Functions => _functions.Values;

    public IEnumerable<string> Names => _functions.Keys;

    /// <summary>Adds or replaces a function.</summary>
    public void Register(FunctionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Kind == FunctionKind.Supported && descriptor.Implementation is null)
        {
            throw new ArgumentException($"Function '{descriptor.Name}' is marked supported but has no implementation.", nameof(descriptor));
        }

        _functions[descriptor.Name] = descriptor;
    }

    public bool TryGet(string name, out FunctionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _functions.TryGetValue(name, out descriptor!);
    }

    public FunctionDescriptor? Find(string name)
    {
        return TryGet(name, out var descriptor) ? descriptor : null;
    }

    public bool Contains(string name)
    {
        return TryGet(name, out _);
    }

    /// <summary>Names of a given kind, sorted, for messages and reports.</summary>
    public IReadOnlyList<string> NamesOfKind(FunctionKind kind)
    {
        return _functions.Values.Where(f => f.Kind == kind).Select(f => f.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
