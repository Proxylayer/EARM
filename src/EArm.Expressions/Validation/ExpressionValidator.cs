using System;
using System.Collections.Generic;
using System.Linq;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Types;
using EArm.Core.Validation;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Functions;
using EArm.Expressions.Static;

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
/// Runs offline; <see cref="Analyze(TemplateDocument)"/> also returns the <see cref="StaticContext"/> for callers that need the
/// evaluated model.
/// </summary>
public sealed class ExpressionValidator : ITemplateValidator
{
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
        : this(catalog, functions, null)
    {
    }

    /// <param name="catalog">The type catalog.</param>
    /// <param name="functions">The function registry.</param>
    /// <param name="deployment">
    /// What <c>deployment()</c> returns during static evaluation; <c>null</c> is <see cref="DeploymentInfo.Offline"/>. The template of an inner-scope
    /// nested deployment passes its own (its name is the nested deployment's).
    /// </param>
    public ExpressionValidator(ResourceTypeCatalog catalog, FunctionRegistry functions, DeploymentInfo? deployment)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(functions);
        _functions = functions;
        _evaluator = new StaticEvaluator(ExpressionEvaluator.Default, functions, catalog, deployment, null);
    }

    public IReadOnlyList<Diagnostic> Validate(TemplateDocument document)
    {
        return Analyze(document).Diagnostics;
    }

    /// <summary>Validates and returns the static model alongside the diagnostics.</summary>
    public ExpressionAnalysis Analyze(TemplateDocument document)
    {
        return Analyze(document, null);
    }

    /// <summary>
    /// Validates the template of a nested deployment evaluated in the outer scope (<paramref name="outer"/>: the deployment's copy iteration and the
    /// parent's resources), or a template of its own when <paramref name="outer"/> is <c>null</c>.
    /// </summary>
    public ExpressionAnalysis Analyze(TemplateDocument document, OuterTemplateScope? outer)
    {
        ArgumentNullException.ThrowIfNull(document);

        var diagnostics = new List<Diagnostic>();
        var walker = new ExpressionPlacementWalker(_functions, document, diagnostics, outer);
        walker.Run();

        var staticContext = _evaluator.Evaluate(document, outer);
        var seen = new HashSet<(string Code, JsonPath? Path)>(diagnostics.Select(d => (d.Code, d.Path)));
        foreach (var diagnostic in staticContext.Diagnostics)
        {
            if (seen.Add((diagnostic.Code, diagnostic.Path)))
            {
                diagnostics.Add(diagnostic);
            }
        }

        PropertyExpressionChecks.CheckPropertyReferences(document, staticContext, diagnostics);
        PropertyExpressionChecks.CheckPropertyTypes(document, staticContext, diagnostics);
        diagnostics.Sort(DiagnosticOrder.Instance);

        // Defence in depth, not the fix: diagnostics are impossible to build from secure text by construction (every
        // message renders argument-derived strings through FunctionCall.Redact / Render, and the static evaluator has
        // already masked its own list). This last pass covers the merged list, whatever its source, so no diagnostic
        // leaves the validator carrying a secure parameter value.
        SecureTextMask.Apply(staticContext.Parameters, diagnostics);
        return new ExpressionAnalysis(staticContext, diagnostics);
    }

}

/// <summary>The outcome of <see cref="ExpressionValidator.Analyze(TemplateDocument)"/>.</summary>
public sealed record ExpressionAnalysis(StaticContext Static, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(d => d.IsError);
}
