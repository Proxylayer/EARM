using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Expressions.Functions;
using EArm.Expressions.Static;
using EArm.Expressions.Syntax;
using EArm.Expressions.Values;

namespace EArm.Expressions.Evaluation;

/// <summary>
/// The default <see cref="IExpressionEvaluator"/>. Stateless: everything an evaluation observes is in the
/// <see cref="EvaluationContext"/>.
/// <para>
/// Propagation rules applied around every built-in call: an argument containing an unknown leaf makes the call
/// yield an unknown value (unless the function declares <see cref="FunctionDescriptor.HandlesUnknown"/>); an
/// argument containing a secret is revealed for the implementation and the whole result is tainted again.
/// Property and index access on an unknown value yield an unknown value with an extended symbol.
/// </para>
/// </summary>
public sealed class ExpressionEvaluator : IExpressionEvaluator
{
    public static ExpressionEvaluator Default { get; } = new();

    public Expression Parse(string text)
    {
        return ExpressionParser.Parse(text);
    }

    public JsonNode? Evaluate(Expression expression, EvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(context);
        using var unit = EvaluationBudget.BeginUnit();
        ExpressionStackGuard.Ensure(expression.Offset);

        switch (expression)
        {
            case LiteralExpression literal:
                return literal.IsString ? JsonValue.Create(literal.StringValue) : JsonValue.Create(literal.IntegerValue.GetValueOrDefault());
            case CallExpression call:
                return EvaluateCall(call, context);
            case PropertyExpression property:
                return EvaluateProperty(property, context);
            case IndexExpression index:
                return EvaluateIndex(index, context);
            case LambdaExpression lambda:
                throw new ExpressionEvaluationException(DiagnosticCodes.InvalidFunctionArguments, $"lambda() can only be passed to a function that takes a lambda ({LambdaFunctions.TakingFunctions}).", lambda.Offset);
            default:
                throw new ExpressionEvaluationException(DiagnosticCodes.ExpressionEvaluationFailed, $"Unsupported expression node {expression.GetType().Name}.", expression.Offset);
        }
    }

    /// <summary>Evaluates a full expression string (<c>[...]</c>).</summary>
    public JsonNode? Evaluate(string expression, EvaluationContext context)
    {
        return Evaluate(Parse(expression), context);
    }

    /// <summary>
    /// Evaluates a string that may be a literal, an escaped literal (<c>[[</c>) or an expression; literals come
    /// back as string leaves.
    /// </summary>
    public JsonNode? EvaluateString(string text, EvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(text);
        return ExpressionSyntax.IsExpression(text) ? Evaluate(Parse(text), context) : JsonValue.Create(ExpressionSyntax.LiteralValue(text));
    }

    public JsonNode? EvaluateTree(JsonNode? node, EvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var unit = EvaluationBudget.BeginUnit();
        try
        {
            var result = EvaluateTreeCore(node, context);

            // The template's own nesting plus the nesting of what its expressions produced is checked once, on the whole tree.
            context.Budget.CheckValue(result);
            context.Budget.CheckPropertyNames(result);
            return result;
        }
        catch (ValueTooDeepException)
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.ValueTooDeep, ValueGuard.TooDeepMessage);
        }
    }

    private JsonNode? EvaluateTreeCore(JsonNode? node, EvaluationContext context)
    {
        ExpressionStackGuard.Ensure();
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                {
                    // Resource properties and variable values: a "copy" array is ARM property iteration and becomes the properties it declares.
                    bool expandCopy = context.Scope is EvaluationScope.Resource or EvaluationScope.Variable && PropertyCopyBlock.HasBlock(obj);
                    var result = new JsonObject();
                    foreach (var property in obj)
                    {
                        if (expandCopy && PropertyCopyBlock.IsBlock(property.Key, property.Value))
                        {
                            continue;
                        }

                        result[property.Key] = EvaluateTreeCore(property.Value, context);
                    }

                    if (expandCopy)
                    {
                        CopyExpansion.ExpandProperties(this, obj, result, context);
                    }

                    return result;
                }

            case JsonArray array:
                {
                    var result = new JsonArray();
                    foreach (var item in array)
                    {
                        result.Add(EvaluateTreeCore(item, context));
                    }

                    return result;
                }

            default:
                {
                    string? text = TemplateJson.AsString(node);
                    return text is null ? JsonValues.Clone(node) : EvaluateString(text, context);
                }
        }
    }

    public IReadOnlySet<ResourceIdentifier> FindReferences(JsonNode? node, StaticContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ReferenceScanner.Scan(this, node, context, context.Context, JsonPath.Root).SameTemplateIds;
    }

    /// <summary>
    /// Evaluates one call: counts it against the run's budget, runs it, and checks the result against the size limits (nesting
    /// depth, string length, elements per value), so no function can hand an oversized or too deeply nested value to the next.
    /// </summary>
    internal JsonNode? EvaluateCall(CallExpression call, EvaluationContext context)
    {
        using var unit = EvaluationBudget.BeginUnit();
        context.Budget.Step(call.Offset);
        try
        {
            var result = EvaluateCallCore(call, context);
            context.Budget.CheckValue(result, call.Offset);
            return result;
        }
        catch (ValueTooDeepException)
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.ValueTooDeep, ValueGuard.TooDeepMessage, call.Offset);
        }
    }

    private JsonNode? EvaluateCallCore(CallExpression call, EvaluationContext context)
    {
        if (call.IsUserFunction)
        {
            return EvaluateUserFunction(call, context);
        }

        if (!context.Functions.TryGet(call.Name, out var descriptor))
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.UnknownFunction, UnknownFunctionMessage(call.Name), call.Offset);
        }

        if (descriptor.Kind == FunctionKind.Rejected)
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.UnsupportedArmFunction, RejectedMessage(descriptor), call.Offset);
        }

        if (descriptor.NonDeterministic && !context.AllowNonDeterministic)
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.NonDeterministicFunctionNotAllowed, NonDeterministicMessage(descriptor), call.Offset);
        }

        if (!descriptor.AcceptsArgumentCount(call.Arguments.Count))
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.InvalidFunctionArguments, $"{descriptor.Name}() takes {descriptor.ArityDescription} argument(s) but {call.Arguments.Count} were supplied.", call.Offset);
        }

        var implementation = descriptor.Implementation ?? throw new ExpressionEvaluationException(DiagnosticCodes.ExpressionEvaluationFailed, $"Function '{descriptor.Name}' has no implementation.", call.Offset);

        if (descriptor.Lazy)
        {
            return Invoke(implementation, new FunctionCall(this, call, descriptor, [], context));
        }

        var arguments = new JsonNode?[call.Arguments.Count];
        bool anyTaint = false;
        bool anyUnknown = false;
        for (int i = 0; i < arguments.Length; i++)
        {
            var value = Evaluate(call.Arguments[i], context);
            arguments[i] = value;
            anyTaint |= UnknownJson.ContainsTaint(value);
            anyUnknown |= UnknownJson.ContainsUnknown(value);
        }

        if (anyUnknown && !descriptor.HandlesUnknown)
        {
            var origins = UnknownOrigins.None;
            foreach (var argument in arguments)
            {
                origins |= UnknownJson.OriginsOf(argument);
            }

            return new FunctionCall(this, call, descriptor, arguments, context).Unknown(origins, anyTaint);
        }

        // The implementation computes with secrets revealed; the FunctionCall keeps the evaluated (still tainted)
        // arguments alongside, and every message or symbol it produces is rendered from those (FunctionCall.Render /
        // Redact), so revealed text never leaves the implementation other than as the re-tainted result.
        var revealed = arguments;
        if (anyTaint)
        {
            revealed = new JsonNode?[arguments.Length];
            for (int i = 0; i < arguments.Length; i++)
            {
                revealed[i] = TaintHelper.RevealSecrets(arguments[i]);
            }
        }

        var result = Invoke(implementation, new FunctionCall(this, call, descriptor, revealed, context, arguments));
        return anyTaint ? TaintHelper.Taint(result) : result;
    }

    /// <summary>
    /// Runs a function body. Implementations report failures as <see cref="ExpressionException"/>; anything else (an
    /// unguarded overflow, a runtime argument exception) is a defect that must still surface as a diagnostic rather
    /// than crash the caller, so it becomes <c>ExpressionEvaluationFailed</c> with the original as inner exception.
    /// A runtime message may echo an argument, so it is left out when the call is tainted.
    /// </summary>
    private static JsonNode? Invoke(FunctionImplementation implementation, FunctionCall functionCall)
    {
        try
        {
            return implementation(functionCall);
        }
        catch (ValueTooDeepException)
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.ValueTooDeep, ValueGuard.TooDeepMessage, functionCall.Offset);
        }
        catch (Exception ex) when (ex is not (ExpressionException or OutOfMemoryException))
        {
            string detail = functionCall.IsTainted ? ex.GetType().Name : $"{ex.GetType().Name}: {ex.Message}";
            throw new ExpressionEvaluationException(DiagnosticCodes.ExpressionEvaluationFailed, $"{functionCall.Name}(): unexpected failure ({detail}).", functionCall.Offset, ex);
        }
    }

    /// <summary>
    /// A user-defined function call (group 7, ARM rules): the arguments are evaluated in the caller's context and
    /// bound to the declared parameters (type-checked when concrete), the body is evaluated in a frame of
    /// <see cref="EvaluationScope.UserFunction"/> whose <see cref="EvaluationContext.Parameters"/> are those
    /// bindings (no template parameters, variables, reference() or other user functions; no copy or lambda scope),
    /// and the result must match the declared output type (secure types taint it). A failure inside the body is
    /// reported at the call site with the body's offset and path in the message.
    /// </summary>
    private JsonNode? EvaluateUserFunction(CallExpression call, EvaluationContext context)
    {
        var ns = context.UserFunctions.FirstOrDefault(n => n.Namespace is not null && n.Namespace.Equals(call.Namespace, StringComparison.OrdinalIgnoreCase));
        if (ns is null)
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.UnknownFunction, $"Function '{call.QualifiedName}' is not defined: the template declares no 'functions' namespace '{call.Namespace}'.", call.Offset);
        }

        if (!ns.Members.TryGetValue(call.Name, out var definition))
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.UnknownFunction, $"Function '{call.QualifiedName}' is not defined: namespace '{ns.Namespace}' has no member '{call.Name}' (members: {string.Join(", ", ns.Members.Keys)}).", call.Offset);
        }

        if (context.Scope == EvaluationScope.UserFunction)
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.FunctionNotAllowedHere, NestedUserFunctionMessage(call.QualifiedName, context.UserFunction), call.Offset);
        }

        if (definition.Parameters.Count != call.Arguments.Count)
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.InvalidFunctionArguments, $"{call.QualifiedName}() takes exactly {definition.Parameters.Count} argument(s) but {call.Arguments.Count} were supplied.", call.Offset);
        }

        var bound = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < definition.Parameters.Count; i++)
        {
            var parameter = definition.Parameters[i];
            if (parameter.Name is null)
            {
                throw new ExpressionEvaluationException(DiagnosticCodes.ExpressionEvaluationFailed, $"{call.QualifiedName}(): parameter {i} of the function has no name (see {definition.Path.Append("parameters").Append(i)}).", call.Offset);
            }

            var value = Evaluate(call.Arguments[i], context);
            if (TemplateParameterTypes.TryParse(parameter.Type, out var type) && !UnknownJson.ContainsUnknown(value) && !TemplateParameterTypes.Matches(value, type))
            {
                throw new ExpressionEvaluationException(DiagnosticCodes.InvalidFunctionArguments, $"{call.QualifiedName}(): argument {i + 1} ('{parameter.Name}') must be of type {parameter.Type}, got {JsonValues.Describe(value)}.", call.Arguments[i].Offset);
            }

            bound[parameter.Name] = value;
        }

        var frame = context with
        {
            Parameters = bound,
            Scope = EvaluationScope.UserFunction,
            UserFunction = call.QualifiedName,
            CopyStack = [],
            LambdaScope = context.LambdaScope.Clear(),
        };

        JsonNode? result;
        try
        {
            result = EvaluateTree(definition.OutputValue, frame);
        }
        catch (ExpressionException ex)
        {
            throw new ExpressionEvaluationException(ex.Code, $"{call.QualifiedName}(): {ex.MessageWithOffset} (in {definition.Path.Append("output").Append("value")})", call.Offset, ex);
        }

        if (definition.OutputType is not null && TemplateParameterTypes.TryParse(definition.OutputType, out var outputType))
        {
            if (!UnknownJson.ContainsUnknown(result) && !TemplateParameterTypes.Matches(result, outputType))
            {
                throw new ExpressionEvaluationException(DiagnosticCodes.ExpressionEvaluationFailed, $"{call.QualifiedName}(): the output is declared as {definition.OutputType} but evaluated to {JsonValues.Describe(result)} (see {definition.Path.Append("output")}).", call.Offset);
            }

            if (TemplateParameterTypes.IsSecure(outputType))
            {
                result = TaintHelper.Taint(result);
            }
        }

        return result;
    }

    internal static string NestedUserFunctionMessage(string qualifiedName, string? enclosing)
    {
        string inside = enclosing is null ? "a user-defined function" : $"user-defined function {enclosing}";
        return $"{qualifiedName}() cannot be called inside {inside}: user-defined functions cannot call other user-defined functions (ARM rule).";
    }

    private JsonNode? EvaluateProperty(PropertyExpression property, EvaluationContext context)
    {
        return JsonValues.Clone(SelectProperty(property, context));
    }

    private JsonNode? EvaluateIndex(IndexExpression index, EvaluationContext context)
    {
        return JsonValues.Clone(SelectIndex(index, context));
    }

    /// <summary>
    /// Evaluates the target of a property or index access as a value that is only read, never mutated or attached: a chain of accesses rooted in
    /// <c>variables('x')</c>, <c>parameters('x')</c> or <c>lambdaVariables('x')</c> (a literal name) walks the stored value itself, and only the
    /// part finally selected is copied (by <see cref="EvaluateProperty"/> / <see cref="EvaluateIndex"/>). ARM evaluates a variable once; reading
    /// <c>variables('x')[copyIndex()].name</c> for every copy instance must not copy, or count against the run's value budget, the whole of
    /// <c>x</c> each time (likewise a large array parameter, or the element a lambda is bound to). Anything else is evaluated as usual.
    /// </summary>
    private JsonNode? EvaluateForRead(Expression expression, EvaluationContext context)
    {
        ExpressionStackGuard.Ensure(expression.Offset);
        switch (expression)
        {
            case CallExpression call when IsLiteralStoreRead(call, context, out var implementation, out string? name):
                {
                    using var unit = EvaluationBudget.BeginUnit();
                    context.Budget.Step(call.Offset);
                    if (ReferenceEquals(implementation, DeploymentFunctions.VariablesImplementation))
                    {
                        return DeploymentFunctions.ReadVariable(context, name, call.Offset, name);
                    }

                    return ReferenceEquals(implementation, DeploymentFunctions.ParametersImplementation)
                        ? DeploymentFunctions.ReadParameter(context, name, call.Offset, name)
                        : LambdaFunctions.ReadLambdaVariable(context, name, call.Offset, name);
                }

            case PropertyExpression property:
                return SelectProperty(property, context);
            case IndexExpression index:
                return SelectIndex(index, context);
            default:
                return Evaluate(expression, context);
        }
    }

    /// <summary>
    /// True for <c>variables('name')</c>, <c>parameters('name')</c> or <c>lambdaVariables('name')</c> with a literal string name, bound to the
    /// built-in function; <paramref name="implementation"/> says which.
    /// </summary>
    private static bool IsLiteralStoreRead(
        CallExpression call,
        EvaluationContext context,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FunctionImplementation? implementation,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? name)
    {
        implementation = null;
        name = null;
        if (call.IsUserFunction
            || call.Arguments.Count != 1
            || call.Arguments[0] is not LiteralExpression { IsString: true } literal
            || !context.Functions.TryGet(call.Name, out var descriptor)
            || descriptor.Implementation is not { } found
            || !(ReferenceEquals(found, DeploymentFunctions.VariablesImplementation)
                || ReferenceEquals(found, DeploymentFunctions.ParametersImplementation)
                || ReferenceEquals(found, LambdaFunctions.LambdaVariablesImplementation)))
        {
            return false;
        }

        implementation = found;
        name = literal.StringValue;
        return name is not null;
    }

    /// <summary>The selected property, shared with the target when the target is (part of) a memoised variable; see <see cref="EvaluateForRead"/>.</summary>
    private JsonNode? SelectProperty(PropertyExpression property, EvaluationContext context)
    {
        var target = EvaluateForRead(property.Target, context);
        if (UnknownJson.IsUnknown(target, out var unknown))
        {
            return UnknownJson.Create(unknown.Symbol + "." + property.Name, unknown.Origins, unknown.Tainted, unknown.Source);
        }

        if (target is JsonObject obj)
        {
            if (TryGetPropertyIgnoreCase(obj, property.Name, out var value))
            {
                return value;
            }

            throw new ExpressionEvaluationException(DiagnosticCodes.ExpressionEvaluationFailed, $"The object has no property '{property.Name}' ({DescribeProperties(obj)}).", property.Offset);
        }

        throw new ExpressionEvaluationException(DiagnosticCodes.ExpressionEvaluationFailed, $"Cannot read property '{property.Name}' of {JsonValues.Describe(target)}.", property.Offset);
    }

    /// <summary>The selected element, shared with the target when the target is (part of) a memoised variable; see <see cref="EvaluateForRead"/>.</summary>
    private JsonNode? SelectIndex(IndexExpression index, EvaluationContext context)
    {
        var target = EvaluateForRead(index.Target, context);
        var key = Evaluate(index.Index, context);

        if (UnknownJson.IsUnknown(target, out var unknownTarget))
        {
            var keyOrigins = UnknownJson.OriginsOf(key);
            return UnknownJson.Create($"{unknownTarget.Symbol}[{JsonValues.Render(key)}]", unknownTarget.Origins | keyOrigins, unknownTarget.Tainted || UnknownJson.ContainsTaint(key), unknownTarget.Source);
        }

        if (UnknownJson.IsUnknown(key, out var unknownKey))
        {
            return UnknownJson.Create($"{index.Target.ToExpressionText()}[{unknownKey.Symbol}]", unknownKey.Origins, unknownKey.Tainted);
        }

        switch (target)
        {
            case JsonArray array:
                {
                    if (!JsonValues.TryGetInt64(key, out long position))
                    {
                        throw new ExpressionEvaluationException(DiagnosticCodes.ExpressionEvaluationFailed, $"An array index must be an integer, got {JsonValues.Describe(key)}.", index.Index.Offset);
                    }

                    if (position < 0 || position >= array.Count)
                    {
                        throw new ExpressionEvaluationException(DiagnosticCodes.ExpressionEvaluationFailed, $"Index {position} is outside the array (length {array.Count}).", index.Index.Offset);
                    }

                    return array[(int)position];
                }

            case JsonObject obj:
                {
                    if (!JsonValues.TryGetString(key, out var name))
                    {
                        throw new ExpressionEvaluationException(DiagnosticCodes.ExpressionEvaluationFailed, $"An object key must be a string, got {JsonValues.Describe(key)}.", index.Index.Offset);
                    }

                    if (TryGetPropertyIgnoreCase(obj, name, out var value))
                    {
                        return value;
                    }

                    throw new ExpressionEvaluationException(DiagnosticCodes.ExpressionEvaluationFailed, $"The object has no property '{name}' ({DescribeProperties(obj)}).", index.Index.Offset);
                }

            default:
                throw new ExpressionEvaluationException(DiagnosticCodes.ExpressionEvaluationFailed, $"Cannot index into {JsonValues.Describe(target)}.", index.Offset);
        }
    }

    /// <summary>
    /// The property names for a "no such property" message. Names are not taint-tracked (a key may have been computed
    /// from a secure value, for example by <c>createObject</c>), so they are withheld when the object holds secure values.
    /// </summary>
    private static string DescribeProperties(JsonObject obj)
    {
        return UnknownJson.ContainsTaint(obj) ? "the object holds secure values, so its property names are not listed" : $"properties: {string.Join(", ", obj.Select(p => p.Key))}";
    }

    private static bool TryGetPropertyIgnoreCase(JsonObject obj, string name, out JsonNode? value)
    {
        if (obj.TryGetPropertyValue(name, out value))
        {
            return true;
        }

        foreach (var property in obj)
        {
            if (property.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    internal static string UnknownFunctionMessage(string name)
    {
        return $"Function '{name}' is not defined. See docs/diagnostics.md for the supported ARM functions; user-defined functions are called as namespace.function(...).";
    }

    internal static string RejectedMessage(FunctionDescriptor descriptor)
    {
        return $"The ARM function {descriptor.Name}() is not available in EARM: {descriptor.Note}";
    }

    internal static string NonDeterministicMessage(FunctionDescriptor descriptor)
    {
        return $"{descriptor.Name}() is non-deterministic and may only be used in a parameter's defaultValue (ARM rule); pass the value in through a parameter.";
    }
}
