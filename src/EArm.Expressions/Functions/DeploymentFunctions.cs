using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Types;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Values;

namespace EArm.Expressions.Functions;

/// <summary>
/// Group 1: <c>parameters variables resourceId reference references copyIndex deployment tenant deployer environment</c> (<c>reference</c> and
/// <c>references</c> are implemented by <see cref="ReferenceFunctions"/>). <c>deployment()</c>,
/// <c>tenant()</c>, <c>deployer()</c> and <c>environment()</c> come from the preflight, which runs before any expression is evaluated, so they are
/// legal everywhere (parameter defaults and variables included); offline they are unknown values.
/// </summary>
internal static class DeploymentFunctions
{
    private const string Group = "deployment";

    public static void Register(FunctionRegistry registry)
    {
        registry.Register(new FunctionDescriptor { Name = "parameters", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = ParametersImplementation });
        registry.Register(new FunctionDescriptor { Name = "variables", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = VariablesImplementation });
        registry.Register(new FunctionDescriptor { Name = "resourceId", Group = Group, MinArgs = 2, HandlesUnknown = true, Implementation = ResourceId });
        registry.Register(new FunctionDescriptor { Name = "reference", Group = Group, MinArgs = 1, MaxArgs = 3, HandlesUnknown = true, Implementation = ReferenceFunctions.Reference });
        registry.Register(new FunctionDescriptor { Name = "references", Group = Group, MinArgs = 1, MaxArgs = 2, Implementation = ReferenceFunctions.References });
        registry.Register(new FunctionDescriptor { Name = "copyIndex", Group = Group, MinArgs = 0, MaxArgs = 2, Implementation = CopyIndex });
        registry.Register(new FunctionDescriptor { Name = "deployment", Group = Group, MinArgs = 0, MaxArgs = 0, Implementation = call => call.Context.Deployment.ToDeploymentObject() });
        registry.Register(new FunctionDescriptor { Name = "tenant", Group = Group, MinArgs = 0, MaxArgs = 0, Implementation = call => call.Context.Deployment.TenantValue() });
        registry.Register(new FunctionDescriptor { Name = "deployer", Group = Group, MinArgs = 0, MaxArgs = 0, Implementation = call => call.Context.Deployment.DeployerValue() });
        registry.Register(new FunctionDescriptor { Name = "environment", Group = Group, MinArgs = 0, MaxArgs = 0, Implementation = call => call.Context.Deployment.EnvironmentValue() });
    }

    /// <summary>
    /// <c>parameters(name)</c>: a template parameter, or inside a user-defined function one of the function's own
    /// parameters (the evaluator binds them as the frame's <see cref="EvaluationContext.Parameters"/>).
    /// </summary>
    private static JsonNode? Parameters(FunctionCall call)
    {
        string name = call.String(0);
        return JsonValues.Clone(ReadParameter(call.Context, name, call.Offset, call.Redact(0, name)));
    }

    /// <summary>The implementation of <c>parameters()</c>; the evaluator recognises it to read <c>parameters('x')[...]</c> without copying the whole value.</summary>
    internal static readonly FunctionImplementation ParametersImplementation = Parameters;

    /// <summary>
    /// The value of parameter <paramref name="name"/> itself, not a copy (the memoised value of <see cref="LazyParameters"/> or
    /// <see cref="NestedParameters"/>, or the stored value of a plain dictionary such as a user function's frame): the caller only reads it.
    /// <paramref name="shownName"/> is the name as a message may show it (redacted when computed from a secure value).
    /// </summary>
    internal static JsonNode? ReadParameter(EvaluationContext context, string name, int offset, string shownName)
    {
        var parameters = context.Parameters;
        if (!parameters.ContainsKey(name))
        {
            string message = context.Scope == EvaluationScope.UserFunction
                ? $"'{shownName}' is not a parameter of user-defined function {context.UserFunction ?? "(unknown)"}; inside a function, parameters() names only the function's own parameters ({DescribeNames(parameters.Keys)})."
                : $"Parameter '{shownName}' is not defined.";
            throw new ExpressionEvaluationException(DiagnosticCodes.UndefinedParameter, message, offset);
        }

        return parameters switch
        {
            LazyParameters lazy => lazy.GetShared(name),
            NestedParameters nested => nested.GetShared(name),
            _ => parameters[name],
        };
    }

    /// <summary>User-function parameter names are template source; listed for the message.</summary>
    internal static string DescribeNames(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count == 0 ? "the function declares no parameters" : string.Join(", ", list);
    }

    /// <summary>The implementation of <c>variables()</c>; the evaluator recognises it to read <c>variables('x')[...]</c> without copying the whole variable.</summary>
    internal static readonly FunctionImplementation VariablesImplementation = Variables;

    private static JsonNode? Variables(FunctionCall call)
    {
        string name = call.String(0);
        return JsonValues.Clone(ReadVariable(call.Context, name, call.Offset, call.Redact(0, name)));
    }

    /// <summary>
    /// The memoised value of variable <paramref name="name"/> itself, not a copy (see <see cref="LazyVariables.GetShared"/>): the caller only reads it.
    /// Applies the rules of <c>variables()</c> (not in a parameter default or a user-defined function; the variable must exist);
    /// <paramref name="shownName"/> is the name as a message may show it (redacted when computed from a secure value).
    /// </summary>
    internal static JsonNode? ReadVariable(EvaluationContext context, string name, int offset, string shownName)
    {
        switch (context.Scope)
        {
            case EvaluationScope.ParameterDefault:
                throw new ExpressionEvaluationException(DiagnosticCodes.FunctionNotAllowedHere, "variables() cannot be used in a parameter default value.", offset);
            case EvaluationScope.UserFunction:
                throw new ExpressionEvaluationException(DiagnosticCodes.FunctionNotAllowedHere, "variables() cannot be used inside a user-defined function.", offset);
        }

        if (!context.Variables.Contains(name))
        {
            // Reported here rather than by LazyVariables.Get so a name computed from a secure value can be redacted.
            throw new ExpressionEvaluationException(DiagnosticCodes.UndefinedVariable, $"Variable '{shownName}' is not defined.", offset);
        }

        try
        {
            return context.Variables.GetShared(name);
        }
        catch (ExpressionEvaluationException ex) when (ex.Offset is null)
        {
            throw new ExpressionEvaluationException(ex.Code, ex.Message, offset, ex);
        }
    }

    /// <summary>
    /// <c>resourceId(type, name[, childName...])</c>: one argument per name segment. A child type also accepts the
    /// single-argument <c>parent/child</c> form with exactly its segment count; any other name argument containing
    /// <c>/</c> is rejected, since the rendered id could not be parsed back (<see cref="ResourceTypeCatalog.TryParseResourceId"/>).
    /// </summary>
    private static JsonNode? ResourceId(FunctionCall call)
    {
        var typeNode = call.Arg(0);
        if (UnknownJson.IsUnknown(typeNode, out var unknownType))
        {
            return call.Unknown(unknownType.Origins, unknownType.Tainted);
        }

        string type = call.String(0);
        if (!call.Context.Catalog.TryGet(type, out var definition))
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.UnknownResourceType, $"resourceId(): '{call.Redact(0, type)}' is not a known resource type; known types: {string.Join(", ", call.Context.Catalog.Types.Select(t => t.Type))}.", call.Offset);
        }

        var segments = new List<NameSegment>();
        var origins = UnknownOrigins.None;
        bool tainted = false;
        bool symbolic = false;
        for (int i = 1; i < call.Count; i++)
        {
            var node = call.Arg(i);
            if (UnknownJson.IsUnknown(node, out var unknown))
            {
                segments.Add(new NameSegment(unknown.Symbol, i, Symbolic: true));
                origins |= unknown.Origins;
                tainted |= unknown.Tainted;
                symbolic = true;
                continue;
            }

            if (JsonValues.TryGetString(node, out var text))
            {
                segments.Add(new NameSegment(text, i, Symbolic: false));
                continue;
            }

            throw call.Error($"name argument {i} must be a string, got {JsonValues.Describe(node)}.");
        }

        if (segments.Count == 1 && !symbolic && definition.Depth > 1 && segments[0].Text.Contains('/'))
        {
            // The single-argument 'parent/child' form of a child type's name.
            var parts = segments[0].Text.Split('/');
            if (parts.Length != definition.Depth)
            {
                throw call.Error($"name {call.Render(1)} has {parts.Length} segment(s) but type '{definition.Type}' takes {definition.Depth} ('parent/child' form).");
            }

            segments = [.. parts.Select(part => new NameSegment(part, 1, Symbolic: false))];
        }
        else
        {
            foreach (var segment in segments)
            {
                if (!segment.Symbolic && segment.Text.Contains('/'))
                {
                    string hint = definition.Depth == 1
                        ? $"a name of type '{definition.Type}' is a single segment"
                        : "pass one argument per segment, resourceId(type, parent, child), or a single 'parent/child' name";
                    throw call.Error($"name argument {segment.ArgumentIndex} must not contain '/' (got {call.Render(segment.ArgumentIndex)}); {hint}.");
                }
            }

            if (segments.Count != definition.Depth)
            {
                throw call.Error($"type '{definition.Type}' takes {definition.Depth} name argument(s) but {segments.Count} were supplied.");
            }
        }

        if (segments.Any(segment => string.IsNullOrEmpty(segment.Text)))
        {
            throw call.Error("name segments must not be empty.");
        }

        var identifier = new ResourceIdentifier(definition.Type, [.. segments.Select(segment => segment.Text)]);
        if (symbolic)
        {
            return UnknownJson.Create($"resourceId({call.RenderArguments()})", origins, tainted, resourceId: identifier);
        }

        return JsonValue.Create(identifier.Id);
    }

    /// <summary>One name segment of <c>resourceId()</c>: its text (a symbol when <paramref name="Symbolic"/>) and the argument it came from.</summary>
    private readonly record struct NameSegment(string Text, int ArgumentIndex, bool Symbolic);

    /// <summary>The canonical symbol of a placeholder reference: <c>ref(/providers/...)</c> or <c>ref(/providers/..., 'apiVersion')</c>.</summary>
    internal static string Symbol(ResourceIdentifier id, string? apiVersion)
    {
        return Symbol(id.Id, apiVersion);
    }

    /// <summary>The symbol from already-redacted id text (<c>***</c> when the target was computed from a secure value).</summary>
    internal static string Symbol(string idText, string? apiVersion)
    {
        return apiVersion is null ? $"ref({idText})" : $"ref({idText}, '{apiVersion}')";
    }

    /// <summary><c>Microsoft.Entra/users/alice@contoso.com</c> → identity, when the longest known type prefix leaves the expected number of name segments.</summary>
    internal static bool TryParseTypeAndName(ResourceTypeCatalog catalog, string text, out ResourceIdentifier identifier)
    {
        identifier = null!;
        string deploymentPrefix = DeploymentResourceType.Type + "/";
        if (text.StartsWith(deploymentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // A nested deployment: Microsoft.Resources/deployments/<name>.
            string name = text[deploymentPrefix.Length..];
            if (name.Length == 0 || name.Contains('/'))
            {
                return false;
            }

            identifier = new ResourceIdentifier(DeploymentResourceType.Type, [name]);
            return true;
        }

        if (!text.StartsWith(ResourceIdentifier.ProviderNamespace + "/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var definition in catalog.Types.OrderByDescending(d => d.Type.Length))
        {
            string prefix = definition.Type + "/";
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var segments = text[prefix.Length..].Split('/');
            if (segments.Length == definition.Depth && !segments.Any(string.IsNullOrEmpty))
            {
                identifier = new ResourceIdentifier(definition.Type, segments);
                return true;
            }
        }

        return false;
    }

    private static JsonNode? CopyIndex(FunctionCall call)
    {
        string? loopName = null;
        long offset = 0;
        int offsetIndex = -1;
        if (call.Count == 1)
        {
            if (JsonValues.TryGetString(call.Arg(0), out var name))
            {
                loopName = name;
            }
            else
            {
                offset = call.Int(0);
                offsetIndex = 0;
            }
        }
        else if (call.Count == 2)
        {
            loopName = call.String(0);
            offset = call.Int(1);
            offsetIndex = 1;
        }

        CopyFrame? frame = null;
        if (loopName is null)
        {
            frame = call.Context.CurrentCopy;
        }
        else
        {
            foreach (var candidate in call.Context.CopyStack)
            {
                if (candidate.Name.Equals(loopName, StringComparison.OrdinalIgnoreCase))
                {
                    frame = candidate;
                    break;
                }
            }
        }

        if (frame is null)
        {
            string detail = loopName is null ? "there is no enclosing copy loop" : $"there is no enclosing copy loop named '{call.Redact(0, loopName)}'";
            throw new ExpressionEvaluationException(DiagnosticCodes.CopyIndexOutsideCopy, $"copyIndex(): {detail}.", call.Offset);
        }

        if (!frame.IsConcrete)
        {
            // A secure offset is always shown (as ***): omitting it would reveal that it is zero.
            bool showOffset = offset != 0 || call.ArgumentIsTainted(offsetIndex);
            string symbol = showOffset ? $"copyIndex('{frame.Name}', {call.Render(offsetIndex)})" : $"copyIndex('{frame.Name}')";
            return UnknownJson.Create(symbol, UnknownOrigins.Copy);
        }

        try
        {
            return JsonValue.Create(checked(frame.Index + offset));
        }
        catch (OverflowException ex)
        {
            throw call.Failure($"index {frame.Index} plus offset {call.Render(offsetIndex)} exceeds the 64-bit integer range.", ex);
        }
    }
}
