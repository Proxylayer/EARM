using System;
using System.Collections.Generic;
using EntraArm.Core;
using EntraArm.Core.Diagnostics;
using EntraArm.Expressions.Evaluation;

namespace EntraArm.Expressions.Static;

/// <summary>
/// The result of <see cref="StaticEvaluator"/>: a template with its parameters bound, variables evaluated lazily,
/// resource keys evaluated as far as possible offline, and the <see cref="EvaluationContext"/> to evaluate further
/// expressions against (placeholder references, no copy iteration). Used by validation (WI-03) and as the
/// starting point of expansion and dependency-graph construction (WI-05).
/// </summary>
public sealed class StaticContext
{
    internal StaticContext(
        TemplateDocument document,
        EvaluationContext context,
        LazyParameters parameters,
        LazyVariables variables,
        TemplateResourceIndex index,
        IReadOnlyList<ResolvedResource> resources,
        IReadOnlyDictionary<string, ResolvedOutput> outputs,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        Document = document;
        Context = context;
        Parameters = parameters;
        Variables = variables;
        Index = index;
        Resources = resources;
        Outputs = outputs;
        Diagnostics = diagnostics;
    }

    public TemplateDocument Document { get; }

    /// <summary>The base context (resource scope, no copy loop) with the final <see cref="Index"/>.</summary>
    public EvaluationContext Context { get; }

    public LazyParameters Parameters { get; }

    public LazyVariables Variables { get; }

    /// <summary>Every resource with a static or symbolic id.</summary>
    public TemplateResourceIndex Index { get; }

    /// <summary>One entry per declaration, in document order.</summary>
    public IReadOnlyList<ResolvedResource> Resources { get; }

    /// <summary>Outputs keyed by name (case-insensitive).</summary>
    public IReadOnlyDictionary<string, ResolvedOutput> Outputs { get; }

    /// <summary>Findings made while evaluating (syntax and function errors inside evaluated expressions included).</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>The context to evaluate a resource's own expressions in: an unresolved copy frame when it has a <c>copy</c>.</summary>
    public EvaluationContext ContextFor(ResourceDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        return ContextFor(Context, declaration);
    }

    internal static EvaluationContext ContextFor(EvaluationContext context, ResourceDeclaration declaration)
    {
        var scoped = context.ForScope(EvaluationScope.Resource);
        if (declaration.Copy is { } copy)
        {
            string? name = Core.Json.TemplateJson.LiteralString(copy.Name);
            scoped = scoped.PushCopy(CopyFrame.Unresolved(name ?? "copy"));
        }

        return scoped;
    }

    public ResolvedResource? Find(ResourceDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        foreach (var resource in Resources)
        {
            if (ReferenceEquals(resource.Declaration, declaration))
            {
                return resource;
            }
        }

        return null;
    }
}
