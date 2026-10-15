using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Types;
using EArm.Core.Validation;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Functions;
using EArm.Expressions.Static;
using EArm.Expressions.Syntax;

namespace EArm.Expressions.Validation;

/// <summary>
/// The expression-dependent validation the core <see cref="TemplateValidator"/> defers (its remarks list them):
/// <list type="bullet">
/// <item>every expression string parses (<c>ExpressionSyntaxError</c>/<c>ExpressionTooLong</c> with offsets);</item>
/// <item>every call names a known function with the right arity; rejected ARM functions, non-deterministic functions
/// outside parameter defaults, <c>reference()</c>/<c>variables()</c>/<c>copyIndex()</c> in the wrong place, the
/// tenant-dependent Entra functions (<c>appRole permissionScope roleTemplate tenant</c>) in parameter defaults,
/// variables or user-defined functions;</item>
/// <item>lambdas: a <c>lambda()</c> only where a lambda-taking function expects one, with the parameter count that
/// slot allows; <c>lambdaVariables()</c> only inside a lambda body and naming one of its parameters;</item>
/// <item>user-defined functions: declared namespace and member, arity, no call from inside another user-defined
/// function, <c>parameters()</c> inside a body naming one of the function's own parameters;</item>
/// <item>static evaluation (<see cref="StaticEvaluator"/>): names, apiVersion, condition, copy, dependsOn targets,
/// duplicate ids after name evaluation, secure values in resource keys or plain outputs, reference() targets
/// that are not statically resolvable.</item>
/// </list>
/// Runs offline; <see cref="Analyze"/> also returns the <see cref="StaticContext"/> for callers that need the
/// evaluated model.
/// </summary>
public sealed class ExpressionValidator : ITemplateValidator
{
    private readonly ResourceTypeCatalog _catalog;
    private readonly FunctionRegistry _functions;
    private readonly StaticEvaluator _evaluator;

    public ExpressionValidator()
        : this(ResourceTypeCatalog.Default, FunctionRegistry.Default)
    {
    }

    public ExpressionValidator(ResourceTypeCatalog catalog)
        : this(catalog, FunctionRegistry.Default)
    {
    }

    public ExpressionValidator(ResourceTypeCatalog catalog, FunctionRegistry functions)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(functions);
        _catalog = catalog;
        _functions = functions;
        _evaluator = new StaticEvaluator(ExpressionEvaluator.Default, functions, catalog, null, null);
    }

    public IReadOnlyList<Diagnostic> Validate(TemplateDocument document)
    {
        return Analyze(document).Diagnostics;
    }

    /// <summary>Validates and returns the static model alongside the diagnostics.</summary>
    public ExpressionAnalysis Analyze(TemplateDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var diagnostics = new List<Diagnostic>();
        var walker = new Walker(this, document, diagnostics);
        walker.Run();

        var staticContext = _evaluator.Evaluate(document);
        var seen = new HashSet<(string Code, JsonPath? Path)>(diagnostics.Select(d => (d.Code, d.Path)));
        foreach (var diagnostic in staticContext.Diagnostics)
        {
            if (seen.Add((diagnostic.Code, diagnostic.Path)))
            {
                diagnostics.Add(diagnostic);
            }
        }

        diagnostics.Sort(DiagnosticOrder.Instance);

        // Defence in depth, not the fix: diagnostics are impossible to build from secure text by construction (every
        // message renders argument-derived strings through FunctionCall.Redact / Render, and the static evaluator has
        // already masked its own list). This last pass covers the merged list, whatever its source, so no diagnostic
        // leaves the validator carrying a secure parameter value.
        SecureTextMask.Apply(staticContext.Parameters, diagnostics);
        return new ExpressionAnalysis(staticContext, diagnostics);
    }

    /// <summary>Walks every expression of the document and checks syntax and function placement without evaluating.</summary>
    private sealed class Walker
    {
        private readonly ExpressionValidator _owner;
        private readonly TemplateDocument _document;
        private readonly List<Diagnostic> _diagnostics;

        public Walker(ExpressionValidator owner, TemplateDocument document, List<Diagnostic> diagnostics)
        {
            _owner = owner;
            _document = document;
            _diagnostics = diagnostics;
        }

        /// <summary>Where an expression sits: its path and scope, whether a copy loop encloses it, the lambda parameters in scope and the user-defined function whose body it is.</summary>
        private sealed record Frame(JsonPath Path, EvaluationScope Scope, bool InCopy, ImmutableList<string> LambdaParameters, UserFunctionDefinition? Function)
        {
            public static Frame At(JsonPath path, EvaluationScope scope, bool inCopy, UserFunctionDefinition? function = null)
            {
                return new Frame(path, scope, inCopy, ImmutableList<string>.Empty, function);
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
                WalkTree(output.Value, Frame.At(output.Path.Append("value"), EvaluationScope.Output, inCopy: false));
            }
        }

        private void WalkTree(JsonNode? node, Frame frame)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var property in obj)
                    {
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
            catch (ExpressionEvaluationException ex) when (ex.Code == DiagnosticCodes.ExpressionTooDeep)
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

            if (!_owner._functions.TryGet(call.Name, out var descriptor))
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

            if (call.Is("reference"))
            {
                CheckReferencePlacement(call, path, scope);
            }
            else if (call.Is("variables") && scope is EvaluationScope.ParameterDefault or EvaluationScope.UserFunction)
            {
                string where = scope == EvaluationScope.ParameterDefault ? "a parameter default value" : "a user-defined function";
                Error(DiagnosticCodes.FunctionNotAllowedHere, $"variables() cannot be used in {where}. (at offset {call.Offset})", path);
            }
            else if (call.Is("copyIndex") && (scope != EvaluationScope.Resource || !frame.InCopy))
            {
                string where = scope == EvaluationScope.Resource ? "this resource has no copy loop" : "copyIndex() is only valid inside a resource with a copy loop";
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

        private void CheckReferencePlacement(CallExpression call, JsonPath path, EvaluationScope scope)
        {
            switch (scope)
            {
                case EvaluationScope.ParameterDefault:
                    Error(DiagnosticCodes.ReferenceNotAllowedHere, $"reference() cannot be used in a parameter default value. (at offset {call.Offset})", path);
                    break;
                case EvaluationScope.UserFunction:
                    Error(DiagnosticCodes.ReferenceNotAllowedHere, $"reference() cannot be used inside a user-defined function. (at offset {call.Offset})", path);
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
}

/// <summary>The outcome of <see cref="ExpressionValidator.Analyze"/>.</summary>
public sealed record ExpressionAnalysis(StaticContext Static, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(d => d.IsError);
}
