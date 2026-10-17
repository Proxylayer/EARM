using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Types;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Values;

namespace EArm.Expressions.Functions;

/// <summary>
/// Group 1: <c>parameters variables resourceId reference copyIndex deployment tenant deployer environment</c>. <c>deployment()</c>,
/// <c>tenant()</c>, <c>deployer()</c> and <c>environment()</c> come from the preflight, which runs before any expression is evaluated, so they are
/// legal everywhere (parameter defaults and variables included); offline they are unknown values.
/// </summary>
internal static class DeploymentFunctions
{
    private const string Group = "deployment";

    public static void Register(FunctionRegistry registry)
    {
        registry.Register(new FunctionDescriptor { Name = "parameters", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = Parameters });
        registry.Register(new FunctionDescriptor { Name = "variables", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = Variables });
        registry.Register(new FunctionDescriptor { Name = "resourceId", Group = Group, MinArgs = 2, HandlesUnknown = true, Implementation = ResourceId });
        registry.Register(new FunctionDescriptor { Name = "reference", Group = Group, MinArgs = 1, MaxArgs = 2, HandlesUnknown = true, Implementation = Reference });
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
        if (!call.Context.Parameters.TryGetValue(name, out var value))
        {
            string message = call.Context.Scope == EvaluationScope.UserFunction
                ? $"'{call.Redact(0, name)}' is not a parameter of user-defined function {call.Context.UserFunction ?? "(unknown)"}; inside a function, parameters() names only the function's own parameters ({DescribeNames(call.Context.Parameters.Keys)})."
                : $"Parameter '{call.Redact(0, name)}' is not defined.";
            throw new ExpressionEvaluationException(DiagnosticCodes.UndefinedParameter, message, call.Offset);
        }

        // LazyParameters already hands out copies; a plain dictionary does not.
        return call.Context.Parameters is LazyParameters ? value : JsonValues.Clone(value);
    }

    /// <summary>User-function parameter names are template source; listed for the message.</summary>
    internal static string DescribeNames(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count == 0 ? "the function declares no parameters" : string.Join(", ", list);
    }

    private static JsonNode? Variables(FunctionCall call)
    {
        string name = call.String(0);
        switch (call.Context.Scope)
        {
            case EvaluationScope.ParameterDefault:
                throw new ExpressionEvaluationException(DiagnosticCodes.FunctionNotAllowedHere, "variables() cannot be used in a parameter default value.", call.Offset);
            case EvaluationScope.UserFunction:
                throw new ExpressionEvaluationException(DiagnosticCodes.FunctionNotAllowedHere, "variables() cannot be used inside a user-defined function.", call.Offset);
        }

        if (!call.Context.Variables.Contains(name))
        {
            // Reported here rather than by LazyVariables.Get so a name computed from a secure value can be redacted.
            throw new ExpressionEvaluationException(DiagnosticCodes.UndefinedVariable, $"Variable '{call.Redact(0, name)}' is not defined.", call.Offset);
        }

        try
        {
            return call.Context.Variables.Get(name);
        }
        catch (ExpressionEvaluationException ex) when (ex.Offset is null)
        {
            throw new ExpressionEvaluationException(ex.Code, ex.Message, call.Offset, ex);
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

    /// <summary><c>reference(nameOrId[, apiVersion])</c>: see <see cref="IReferenceResolver"/>.</summary>
    private static JsonNode? Reference(FunctionCall call)
    {
        var context = call.Context;
        string? apiVersion = null;
        if (call.Has(1))
        {
            if (UnknownJson.IsUnknown(call.Arg(1), out var unknownVersion))
            {
                return call.Unknown(unknownVersion.Origins | UnknownOrigins.Reference, unknownVersion.Tainted);
            }

            apiVersion = call.String(1);
        }

        switch (context.Scope)
        {
            case EvaluationScope.ParameterDefault:
                throw new ExpressionEvaluationException(DiagnosticCodes.ReferenceNotAllowedHere, "reference() cannot be used in a parameter default value.", call.Offset);
            case EvaluationScope.UserFunction:
                throw new ExpressionEvaluationException(DiagnosticCodes.ReferenceNotAllowedHere, "reference() cannot be used inside a user-defined function.", call.Offset);
            case EvaluationScope.Variable when apiVersion is null:
                throw new ExpressionEvaluationException(DiagnosticCodes.ReferenceNotAllowedHere, "A variable cannot reference a resource of this template; only existing resources may be looked up from variables: reference(resourceId(...), apiVersion).", call.Offset);
        }

        var target = call.Arg(0);
        ResourceIdentifier id;
        bool tainted = false;
        UnknownValue? unknownTarget = null;
        if (UnknownJson.IsUnknown(target, out var unknown))
        {
            unknownTarget = unknown;
            tainted = unknown.Tainted;
            if (unknown.ResourceId is { } symbolicId)
            {
                id = symbolicId;
            }
            else if (apiVersion is null && TryFindSymbolicByName(context.Resources, unknown.Symbol, out var sameComputation))
            {
                // reference(expr) where a resource's name is the same deploy-time computation: matched symbolically,
                // exactly as resourceId(type, expr) would be.
                id = sameComputation;
            }
            else
            {
                return call.Unknown(unknown.Origins | UnknownOrigins.Reference, unknown.Tainted);
            }
        }
        else
        {
            string text = call.String(0);
            try
            {
                id = ResolveTarget(call, text, apiVersion);
            }
            catch (ExpressionEvaluationException ex) when (apiVersion is null && ex.Code == DiagnosticCodes.ReferenceTargetNotFound && context.Resources.PendingCopyLoops.Count > 0)
            {
                return PendingCopyLoopTarget(call, ex);
            }
        }

        if (apiVersion is null && context.Resources.Entries.Count > 0 && !context.Resources.Contains(id))
        {
            if (unknownTarget is not null)
            {
                // A symbolic id (deploy-time name segments) that matches no resource: the target cannot be computed
                // offline. The static scanner classifies the result (an offline artefact when only a parameter value
                // is missing, otherwise ReferenceNotStaticallyResolvable); the symbol text is never reported as an id.
                return call.Unknown(unknownTarget.Origins | UnknownOrigins.Reference, tainted);
            }

            var notFound = new ExpressionEvaluationException(DiagnosticCodes.ReferenceTargetNotFound, $"reference(): '{call.Redact(0, id.Id)}' is not a resource of this template; to look up an existing resource pass an apiVersion: reference(resourceId(...), '{ResourceTypeCatalog.SchemaVersion}').", call.Offset);
            if (context.Resources.PendingCopyLoops.Count > 0)
            {
                return PendingCopyLoopTarget(call, notFound);
            }

            throw notFound;
        }

        var result = context.References.Resolve(id, apiVersion);
        switch (result)
        {
            case ReferenceResult.Live live:
                return tainted ? TaintHelper.Taint(JsonValues.Clone(live.Object)) : JsonValues.Clone(live.Object);
            case ReferenceResult.Placeholder placeholder:
                // The symbol is display text (it reaches messages and other symbols); the identity itself stays in Source.
                return UnknownJson.Create(Symbol(call.Redact(0, placeholder.Id.Id), apiVersion is null ? null : call.Redact(1, apiVersion)), UnknownOrigins.Reference, tainted, source: placeholder.Id);
            case ReferenceResult.NotFound notFound:
                throw new ExpressionEvaluationException(DiagnosticCodes.ReferenceTargetNotFound, $"reference(): existing resource '{call.Redact(0, notFound.Id.Id)}' (apiVersion {call.Redact(1, notFound.ApiVersion)}) was not found.", call.Offset);
            default:
                throw call.Failure($"the reference resolver returned an unexpected result ({result.GetType().Name}).");
        }
    }

    /// <summary>
    /// A same-template <c>reference()</c> that names no indexed resource while some copy loop could not be expanded statically
    /// (its count is not known yet): the target may be one of that loop's instances. When every such loop waits only for a
    /// parameter value that is missing offline the result is an unknown value of origin <see cref="UnknownOrigins.Parameter"/>
    /// alone (the static scanner defers it); otherwise the loop's count is only known at deployment, which ARM does not allow
    /// either, and the reference fails with that explanation.
    /// </summary>
    private static JsonNode? PendingCopyLoopTarget(FunctionCall call, ExpressionEvaluationException notFound)
    {
        var pending = call.Context.Resources.PendingCopyLoops;
        if (pending.All(p => p.Origins == UnknownOrigins.Parameter))
        {
            return UnknownJson.Create($"ref({call.Render(0)})", UnknownOrigins.Parameter);
        }

        var loops = string.Join(", ", pending.Where(p => p.Origins != UnknownOrigins.Parameter).Select(p => $"'{p.Name}' (count {p.Symbol})"));
        throw new ExpressionEvaluationException(notFound.Code, $"{notFound.Message} It may name an instance of copy loop {loops}, but that loop's count is only known at deployment, so its instances cannot be referenced by name; make the count computable from parameters and variables.", notFound.Offset);
    }

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

    /// <summary>
    /// Finds the one resource whose name is the deploy-time computation <paramref name="symbol"/>: a symbolic index
    /// entry whose single name segment is that symbol (see <see cref="UnknownValue.Symbol"/>). False when none or
    /// several match (several is a duplicate-id situation reported by the static evaluator).
    /// </summary>
    private static bool TryFindSymbolicByName(TemplateResourceIndex index, string symbol, [NotNullWhen(true)] out ResourceIdentifier? id)
    {
        id = null;
        foreach (var entry in index.Entries)
        {
            if (!entry.IsSymbolic || entry.Id.NameSegments.Count != 1 || !string.Equals(entry.Id.NameSegments[0], symbol, StringComparison.Ordinal))
            {
                continue;
            }

            if (id is not null)
            {
                id = null;
                return false;
            }

            id = entry.Id;
        }

        return id is not null;
    }

    /// <summary>
    /// Turns the string argument of <c>reference()</c> into an identity: a resource id, a bare name (unique in the
    /// template), or a <c>type/name</c> pair.
    /// </summary>
    internal static ResourceIdentifier ResolveTarget(FunctionCall call, string text, string? apiVersion)
    {
        var context = call.Context;
        if (text.StartsWith(ResourceIdentifier.ProvidersPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (!context.Catalog.TryParseResourceId(text, out var parsed) || parsed is null)
            {
                throw new ExpressionEvaluationException(DiagnosticCodes.InvalidResourceId, $"reference(): '{call.Redact(0, text)}' is not a valid resource id (expected /providers/{ResourceIdentifier.ProviderNamespace}/<type>/<name>[/<childType>/<childName>] with a known type).", call.Offset);
            }

            return parsed;
        }

        var byName = context.Resources.FindByName(text);
        if (byName.Count == 1)
        {
            return byName[0].Id;
        }

        if (byName.Count > 1)
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.AmbiguousReference, $"reference(): '{call.Redact(0, text)}' names {byName.Count} resources ({string.Join(", ", byName.Select(e => e.Id.Type))}); use resourceId() to pick one.", call.Offset);
        }

        if (TryParseTypeAndName(context.Catalog, text, out var typed))
        {
            return typed;
        }

        if (apiVersion is not null)
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.ReferenceTargetNotFound, $"reference(): '{call.Redact(0, text)}' is not a resource of this template; an existing resource must be identified by resourceId() or 'type/name' together with the apiVersion.", call.Offset);
        }

        throw new ExpressionEvaluationException(DiagnosticCodes.ReferenceTargetNotFound, $"reference(): no resource named '{call.Redact(0, text)}' is declared in this template.", call.Offset);
    }

    /// <summary><c>Microsoft.Entra/users/alice@contoso.com</c> → identity, when the longest known type prefix leaves the expected number of name segments.</summary>
    internal static bool TryParseTypeAndName(ResourceTypeCatalog catalog, string text, out ResourceIdentifier identifier)
    {
        identifier = null!;
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
