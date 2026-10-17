using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Functions;
using EArm.Expressions.Syntax;

namespace EArm.Expressions.Validation;

/// <summary>Walks every expression of the document and checks syntax and function placement without evaluating.</summary>
internal sealed class ExpressionPlacementWalker
{
    private readonly FunctionRegistry _functions;
    private readonly TemplateDocument _document;
    private readonly List<Diagnostic> _diagnostics;

    public ExpressionPlacementWalker(FunctionRegistry functions, TemplateDocument document, List<Diagnostic> diagnostics)
    {
        _functions = functions;
        _document = document;
        _diagnostics = diagnostics;
    }

    /// <summary>Where an expression sits: its path and scope, whether a copy loop encloses it, the lambda parameters in scope and the user-defined function whose body it is.</summary>
    /// <remarks><see cref="InCopy"/>: a resource or output loop encloses it (<c>copyIndex()</c> without a name is legal); <see cref="PropertyLoops"/>: the enclosing property iterations, addressed only by name.</remarks>
    private sealed record Frame(JsonPath Path, EvaluationScope Scope, bool InCopy, ImmutableList<string> LambdaParameters, UserFunctionDefinition? Function, ImmutableList<string> PropertyLoops)
    {
        public static Frame At(JsonPath path, EvaluationScope scope, bool inCopy, UserFunctionDefinition? function = null)
        {
            return new Frame(path, scope, inCopy, ImmutableList<string>.Empty, function, ImmutableList<string>.Empty);
        }

        public bool InLambda => LambdaParameters.Count > 0;

        public bool HasLambdaParameter(string name)
        {
            return LambdaParameters.Any(p => p.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Run()
    {
        foreach (var parameter in _document.Parameters.Values)
        {
            if (parameter.HasDefaultValue)
            {
                WalkTree(parameter.DefaultValue, Frame.At(parameter.Path.Append("defaultValue"), EvaluationScope.ParameterDefault, inCopy: false));
            }
        }

        foreach (var variable in _document.Variables)
        {
            if (_document.VariableIterations.TryGetValue(variable.Key, out var iteration))
            {
                // Variable iteration: copyIndex('name') is legal in the input of the entry named 'name', as in property iteration.
                var frame = Frame.At(iteration.Path, EvaluationScope.Variable, inCopy: false);
                WalkTree(iteration.Count, frame with { Path = iteration.Path.Append("count") });
                WalkTree(iteration.Input, frame with { Path = iteration.Path.Append("input"), PropertyLoops = frame.PropertyLoops.Add(iteration.Name) });
                continue;
            }

            WalkTree(variable.Value, Frame.At(JsonPath.Of("variables", variable.Key), EvaluationScope.Variable, inCopy: false));
        }

        foreach (var ns in _document.Functions)
        {
            foreach (var member in ns.Members.Values)
            {
                WalkTree(member.OutputValue, Frame.At(member.Path.Append("output").Append("value"), EvaluationScope.UserFunction, inCopy: false, member));
            }
        }

        foreach (var resource in _document.Resources)
        {
            bool inCopy = resource.Copy is not null;
            if (resource.Name is not null)
            {
                WalkString(resource.Name, Frame.At(resource.Path.Append("name"), EvaluationScope.Resource, inCopy));
            }

            WalkTree(resource.Condition, Frame.At(resource.Path.Append("condition"), EvaluationScope.Resource, inCopy));
            if (resource.Copy is { } copy)
            {
                // copyIndex() inside the copy block itself (count) is not legal.
                WalkTree(copy.Count, Frame.At(copy.Path.Append("count"), EvaluationScope.Resource, inCopy: false));
                WalkTree(copy.Name, Frame.At(copy.Path.Append("name"), EvaluationScope.Resource, inCopy: false));
            }

            for (int i = 0; i < resource.DependsOn.Count; i++)
            {
                WalkString(resource.DependsOn[i], Frame.At(resource.Path.Append("dependsOn").Append(i), EvaluationScope.Resource, inCopy));
            }

            foreach (var inferred in resource.InferredDependsOn)
            {
                WalkString(inferred, Frame.At(resource.Path, EvaluationScope.Resource, inCopy));
            }

            WalkTree(resource.Properties, Frame.At(resource.Path.Append("properties"), EvaluationScope.Resource, inCopy));
        }

        foreach (var output in _document.Outputs.Values)
        {
            WalkTree(output.Condition, Frame.At(output.Path.Append("condition"), EvaluationScope.Output, inCopy: false));
            if (output.Copy is { } copy)
            {
                WalkTree(copy.Count, Frame.At(copy.Path.Append("count"), EvaluationScope.Output, inCopy: false));
                WalkTree(copy.Input, Frame.At(copy.Path.Append("input"), EvaluationScope.Output, inCopy: true));
            }
            else
            {
                WalkTree(output.Value, Frame.At(output.Path.Append("value"), EvaluationScope.Output, inCopy: false));
            }
        }
    }

    private void WalkTree(JsonNode? node, Frame frame)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj)
                {
                    if (frame.Scope is EvaluationScope.Resource or EvaluationScope.Variable && PropertyCopyBlock.IsBlock(property.Key, property.Value))
                    {
                        // Property iteration: copyIndex('name') is legal in the input of the entry named 'name'.
                        foreach (var entry in PropertyCopyBlock.Entries(obj))
                        {
                            var entryPath = frame.Path.Append(property.Key).Append(entry.Index);
                            WalkTree(entry.Count, frame with { Path = entryPath.Append("count") });
                            var loops = entry.Name is null ? frame.PropertyLoops : frame.PropertyLoops.Add(entry.Name);
                            WalkTree(entry.Input, frame with { Path = entryPath.Append("input"), PropertyLoops = loops });
                        }

                        continue;
                    }

                    WalkTree(property.Value, frame with { Path = frame.Path.Append(property.Key) });
                }

                break;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    WalkTree(array[i], frame with { Path = frame.Path.Append(i) });
                }

                break;
            default:
                {
                    string? text = TemplateJson.AsString(node);
                    if (text is not null)
                    {
                        WalkString(text, frame);
                    }

                    break;
                }
        }
    }

    private void WalkString(string text, Frame frame)
    {
        if (!ExpressionSyntax.IsExpression(text))
        {
            return;
        }

        Expression root;
        try
        {
            root = ExpressionParser.Parse(text);
        }
        catch (ExpressionParseException ex)
        {
            Error(ex.Code, ex.MessageWithOffset, frame.Path);
            return;
        }

        try
        {
            Visit(root, frame, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
        catch (ExpressionException ex)
        {
            Error(ex.Code, ex.MessageWithOffset, frame.Path);
        }
    }

    /// <summary>Checks one node and descends; lambda bodies extend the frame's parameter scope, lambdas elsewhere are errors.</summary>
    private void Visit(Expression node, Frame frame, HashSet<string> warnedStubs)
    {
        ExpressionStackGuard.Ensure(node.Offset);
        switch (node)
        {
            case LambdaExpression lambda:
                Error(DiagnosticCodes.InvalidFunctionArguments, $"lambda() can only be passed to a function that takes a lambda ({LambdaFunctions.TakingFunctions}). (at offset {lambda.Offset})", frame.Path);
                Visit(lambda.Body, frame with { LambdaParameters = frame.LambdaParameters.AddRange(lambda.Parameters) }, warnedStubs);
                break;
            case CallExpression call:
                CheckCall(call, frame, warnedStubs);
                for (int i = 0; i < call.Arguments.Count; i++)
                {
                    VisitArgument(call, i, frame, warnedStubs);
                }

                break;
            case PropertyExpression property:
                Visit(property.Target, frame, warnedStubs);
                break;
            case IndexExpression index:
                Visit(index.Target, frame, warnedStubs);
                Visit(index.Index, frame, warnedStubs);
                break;
        }
    }

    private void VisitArgument(CallExpression call, int index, Frame frame, HashSet<string> warnedStubs)
    {
        var argument = call.Arguments[index];
        LambdaFunctions.LambdaSlot? slot = null;
        bool isSlot = !call.IsUserFunction && LambdaFunctions.TryGetLambdaSlot(call.Name, index, out slot);
        if (argument is not LambdaExpression lambda)
        {
            if (isSlot)
            {
                Error(DiagnosticCodes.InvalidFunctionArguments, $"{call.Name}(): argument {index + 1} must be a lambda (lambda('x', ...)), got {argument.ToExpressionText()}. (at offset {argument.Offset})", frame.Path);
            }

            Visit(argument, frame, warnedStubs);
            return;
        }

        if (!isSlot)
        {
            string where = call.IsUserFunction ? $"user-defined function {call.QualifiedName}() takes values, not lambdas" : $"{call.Name}() does not take a lambda as argument {index + 1}";
            Error(DiagnosticCodes.InvalidFunctionArguments, $"lambda() can only be passed to a function that takes a lambda ({LambdaFunctions.TakingFunctions}); {where}. (at offset {lambda.Offset})", frame.Path);
        }
        else
        {
            int count = lambda.Parameters.Count;
            if (count < slot!.MinParameters || count > slot.MaxParameters)
            {
                string expected = slot.MinParameters == slot.MaxParameters ? $"exactly {slot.MinParameters}" : $"between {slot.MinParameters} and {slot.MaxParameters}";
                Error(DiagnosticCodes.InvalidFunctionArguments, $"{call.Name}(): the lambda of argument {index + 1} takes {expected} parameter(s) ({slot.Roles}), got {count}. (at offset {lambda.Offset})", frame.Path);
            }
            else if (lambda.Parameters.Distinct(StringComparer.OrdinalIgnoreCase).Count() != count)
            {
                Error(DiagnosticCodes.InvalidFunctionArguments, $"{call.Name}(): the lambda of argument {index + 1} declares the same parameter name twice ({string.Join(", ", lambda.Parameters)}). (at offset {lambda.Offset})", frame.Path);
            }
        }

        Visit(lambda.Body, frame with { LambdaParameters = frame.LambdaParameters.AddRange(lambda.Parameters) }, warnedStubs);
    }

    private void CheckCall(CallExpression call, Frame frame, HashSet<string> warnedStubs)
    {
        var path = frame.Path;
        var scope = frame.Scope;
        if (call.IsUserFunction)
        {
            CheckUserFunction(call, frame);
            return;
        }

        if (!_functions.TryGet(call.Name, out var descriptor))
        {
            Error(DiagnosticCodes.UnknownFunction, $"{ExpressionEvaluator.UnknownFunctionMessage(call.Name)} (at offset {call.Offset})", path);
            return;
        }

        switch (descriptor.Kind)
        {
            case FunctionKind.Rejected:
                Error(DiagnosticCodes.UnsupportedArmFunction, $"{ExpressionEvaluator.RejectedMessage(descriptor)} (at offset {call.Offset})", path);
                return;
            case FunctionKind.NotImplementedYet:
                if (warnedStubs.Add(descriptor.Name))
                {
                    Warning(DiagnosticCodes.NotImplementedYet, $"{descriptor.Name}() is not implemented yet ({descriptor.Note}); it evaluates to a deploy-time placeholder for now. (at offset {call.Offset})", path);
                }

                break;
        }

        if (!descriptor.AcceptsArgumentCount(call.Arguments.Count))
        {
            Error(DiagnosticCodes.InvalidFunctionArguments, $"{descriptor.Name}() takes {descriptor.ArityDescription} argument(s) but {call.Arguments.Count} were supplied. (at offset {call.Offset})", path);
        }

        if (descriptor.NonDeterministic && scope != EvaluationScope.ParameterDefault)
        {
            Error(DiagnosticCodes.NonDeterministicFunctionNotAllowed, $"{ExpressionEvaluator.NonDeterministicMessage(descriptor)} (at offset {call.Offset})", path);
        }

        if (call.Is("reference") || call.Is("references"))
        {
            CheckReferencePlacement(call, path, scope);
        }
        else if (call.Is("variables") && scope is EvaluationScope.ParameterDefault or EvaluationScope.UserFunction)
        {
            string where = scope == EvaluationScope.ParameterDefault ? "a parameter default value" : "a user-defined function";
            Error(DiagnosticCodes.FunctionNotAllowedHere, $"variables() cannot be used in {where}. (at offset {call.Offset})", path);
        }
        else if (call.Is("copyIndex") && CopyIndexProblem(call, frame) is { } where)
        {
            Error(DiagnosticCodes.CopyIndexOutsideCopy, $"copyIndex() cannot be used here: {where}. (at offset {call.Offset})", path);
        }
        else if (call.Is("lambdaVariables"))
        {
            CheckLambdaVariables(call, frame);
        }
        else if (call.Is("parameters") && scope == EvaluationScope.UserFunction && frame.Function is { } function)
        {
            CheckFunctionParameter(call, function, path);
        }
        else if (EntraFunctions.IsResourceScopeOnly(call.Name) && EntraFunctions.NotAllowedMessage(descriptor.Name, scope) is { } notAllowed)
        {
            Error(DiagnosticCodes.FunctionNotAllowedHere, $"{notAllowed} (at offset {call.Offset})", path);
        }
    }

    /// <summary>
    /// Why <c>copyIndex()</c> is not legal at <paramref name="frame"/>, or <c>null</c>: without a loop name it needs a resource or output
    /// loop; with a literal name it needs any enclosing loop, and inside property iterations alone the name must be one of theirs.
    /// </summary>
    private static string? CopyIndexProblem(CallExpression call, Frame frame)
    {
        string? name = call.Arguments.Count >= 1 && call.Arguments[0] is LiteralExpression { IsString: true } literal ? literal.StringValue : null;
        if (frame.InCopy)
        {
            return null;
        }

        if (frame.PropertyLoops.Count > 0)
        {
            if (name is null)
            {
                return $"inside a property copy loop copyIndex() needs the loop name, for example copyIndex('{frame.PropertyLoops[^1]}')";
            }

            return frame.PropertyLoops.Contains(name, StringComparer.OrdinalIgnoreCase) ? null : $"there is no enclosing copy loop named '{name}' (property copy loops here: {string.Join(", ", frame.PropertyLoops)})";
        }

        return frame.Scope switch
        {
            EvaluationScope.Resource => "this resource has no copy loop",
            EvaluationScope.Output => "this output has no copy loop",
            _ => "copyIndex() is only valid inside a copy loop (a resource, property or output copy)",
        };
    }

    private void CheckReferencePlacement(CallExpression call, JsonPath path, EvaluationScope scope)
    {
        string function = call.Is("references") ? "references()" : "reference()";
        switch (scope)
        {
            case EvaluationScope.ParameterDefault:
                Error(DiagnosticCodes.ReferenceNotAllowedHere, $"{function} cannot be used in a parameter default value. (at offset {call.Offset})", path);
                break;
            case EvaluationScope.UserFunction:
                Error(DiagnosticCodes.ReferenceNotAllowedHere, $"{function} cannot be used inside a user-defined function. (at offset {call.Offset})", path);
                break;
            case EvaluationScope.Variable when call.Is("references"):
                Error(DiagnosticCodes.ReferenceNotAllowedHere, $"references() cannot be used in a variable; use it in the resource properties or the outputs. (at offset {call.Offset})", path);
                break;
            case EvaluationScope.Variable when call.Arguments.Count < 2:
                Error(DiagnosticCodes.ReferenceNotAllowedHere, $"A variable cannot reference a resource of this template; only existing resources may be looked up from variables: reference(resourceId(...), apiVersion). (at offset {call.Offset})", path);
                break;
        }
    }

    private void CheckLambdaVariables(CallExpression call, Frame frame)
    {
        if (!frame.InLambda)
        {
            Error(DiagnosticCodes.FunctionNotAllowedHere, $"lambdaVariables() can only be used inside the body of a lambda passed to {LambdaFunctions.TakingFunctions}. (at offset {call.Offset})", frame.Path);
            return;
        }

        if (call.Arguments.Count == 1 && call.Arguments[0] is LiteralExpression { IsString: true } literal && !frame.HasLambdaParameter(literal.StringValue!))
        {
            Error(DiagnosticCodes.InvalidFunctionArguments, $"lambdaVariables(): '{literal.StringValue}' is not a parameter of an enclosing lambda (in scope: {string.Join(", ", frame.LambdaParameters)}). (at offset {call.Offset})", frame.Path);
        }
    }

    private void CheckFunctionParameter(CallExpression call, UserFunctionDefinition function, JsonPath path)
    {
        if (call.Arguments.Count != 1 || call.Arguments[0] is not LiteralExpression { IsString: true } literal)
        {
            return;
        }

        var names = function.Parameters.Where(p => p.Name is not null).Select(p => p.Name!).ToList();
        if (!names.Any(n => n.Equals(literal.StringValue, StringComparison.OrdinalIgnoreCase)))
        {
            Error(DiagnosticCodes.UndefinedParameter, $"'{literal.StringValue}' is not a parameter of user-defined function {function.Name}; inside a function, parameters() names only the function's own parameters ({DeploymentFunctions.DescribeNames(names)}). (at offset {call.Offset})", path);
        }
    }

    private void CheckUserFunction(CallExpression call, Frame frame)
    {
        var path = frame.Path;
        var ns = _document.Functions.FirstOrDefault(n => n.Namespace is not null && n.Namespace.Equals(call.Namespace, StringComparison.OrdinalIgnoreCase));
        if (ns is null)
        {
            Error(DiagnosticCodes.UnknownFunction, $"Function '{call.QualifiedName}' is not defined: the template declares no 'functions' namespace '{call.Namespace}'. (at offset {call.Offset})", path);
            return;
        }

        if (!ns.Members.TryGetValue(call.Name, out var definition))
        {
            Error(DiagnosticCodes.UnknownFunction, $"Function '{call.QualifiedName}' is not defined: namespace '{ns.Namespace}' has no member '{call.Name}' (members: {string.Join(", ", ns.Members.Keys)}). (at offset {call.Offset})", path);
            return;
        }

        if (frame.Scope == EvaluationScope.UserFunction)
        {
            string? enclosing = frame.Function is null ? null : $"{ns.Namespace}.{frame.Function.Name}";
            Error(DiagnosticCodes.FunctionNotAllowedHere, $"{ExpressionEvaluator.NestedUserFunctionMessage(call.QualifiedName, enclosing)} (at offset {call.Offset})", path);
        }

        if (definition.Parameters.Count != call.Arguments.Count)
        {
            Error(DiagnosticCodes.InvalidFunctionArguments, $"{call.QualifiedName}() takes exactly {definition.Parameters.Count} argument(s) but {call.Arguments.Count} were supplied. (at offset {call.Offset})", path);
        }
    }

    private void Error(string code, string message, JsonPath path)
    {
        _diagnostics.Add(Diagnostic.Error(code, message, path, _document.LocationOf(path), _document.SourcePath));
    }

    private void Warning(string code, string message, JsonPath path)
    {
        _diagnostics.Add(Diagnostic.Warning(code, message, path, _document.LocationOf(path), _document.SourcePath));
    }
}
