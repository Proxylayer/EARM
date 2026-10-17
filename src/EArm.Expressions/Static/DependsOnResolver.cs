using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Types;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Functions;
using EArm.Expressions.Values;

namespace EArm.Expressions.Static;

/// <summary>
/// Resolves the <c>dependsOn</c> entries (explicit and inferred) of one resource against the template's resource index: a resource id,
/// <c>type/name</c>, a unique resource name, a copy loop name, or (languageVersion 2.0) a symbolic name, <c>loop</c> or <c>loop[i]</c>. Entries
/// that depend on <c>copyIndex()</c> or on a parameter without a value offline are deferred; failures are appended to the diagnostics.
/// </summary>
internal sealed class DependsOnResolver
{
    private readonly ExpressionEvaluator _evaluator;
    private readonly ResourceTypeCatalog _catalog;
    private readonly TemplateDocument _document;
    private readonly EvaluationContext _context;
    private readonly List<Diagnostic> _diagnostics;

    public DependsOnResolver(ExpressionEvaluator evaluator, ResourceTypeCatalog catalog, TemplateDocument document, EvaluationContext context, List<Diagnostic> diagnostics)
    {
        _evaluator = evaluator;
        _catalog = catalog;
        _document = document;
        _context = context;
        _diagnostics = diagnostics;
    }

    public IReadOnlyList<ResolvedDependency> Resolve(ResolvedResource resource)
    {
        var declaration = resource.Declaration;
        var context = StaticContext.ContextFor(_context, declaration);
        var dependencies = new List<ResolvedDependency>();

        for (int i = 0; i < declaration.DependsOn.Count; i++)
        {
            dependencies.Add(ResolveDependency(declaration.DependsOn[i], DependencySource.Explicit, context, declaration.Path.Append("dependsOn").Append(i)));
        }

        foreach (var inferred in declaration.InferredDependsOn)
        {
            dependencies.Add(ResolveDependency(inferred, DependencySource.Inferred, context, declaration.Path));
        }

        return dependencies;
    }

    private ResolvedDependency ResolveDependency(string text, DependencySource source, EvaluationContext context, JsonPath path)
    {
        var unresolved = new ResolvedDependency(text, source, null, null, false);
        JsonNode? value;
        try
        {
            value = _evaluator.EvaluateString(text, context);
        }
        catch (ExpressionException ex)
        {
            Report(ex, path);
            return unresolved;
        }

        if (UnknownJson.ContainsTaint(value))
        {
            Error(DiagnosticCodes.SecureValueInResourceKey, "A secure value flows into dependsOn; dependencies are logged and cannot be secret.", path);
            return unresolved;
        }

        if (UnknownJson.IsUnknown(value, out var unknown))
        {
            if (unknown.ResourceId is { } symbolic && _context.Resources.Contains(symbolic))
            {
                return unresolved with { Target = symbolic };
            }

            if ((unknown.Origins & UnknownOrigins.Copy) != 0)
            {
                return unresolved with { Deferred = true };
            }

            if (unknown.Origins == UnknownOrigins.Parameter)
            {
                // Only a parameter value is missing offline: an artefact of validating without values, deferred
                // like a copy-dependent entry rather than reported as an authoring error.
                Info(DiagnosticCodes.ReferenceDependsOnMissingParameter, $"dependsOn entry '{text}' depends on a parameter that has no value offline; it is resolved once the value is supplied. Deploy-time value: {unknown.Symbol}.", path);
                return unresolved with { Deferred = true };
            }

            Error(DiagnosticCodes.DependsOnTargetNotFound, $"dependsOn entry '{text}' does not resolve to a resource of this template (its value is only known at deployment: {unknown.Symbol}).", path);
            return unresolved;
        }

        if (!JsonValues.TryGetString(value, out var target))
        {
            Error(DiagnosticCodes.DependsOnTargetNotFound, $"dependsOn entry '{text}' must evaluate to a string, got {JsonValues.Describe(value)}.", path);
            return unresolved;
        }

        var index = _context.Resources;
        if (SymbolicReference.TryParse(target, out var symbolicName, out var copyIndex) && index.FindSymbolicDeclaration(symbolicName) is { } declaration)
        {
            return ResolveSymbolic(unresolved, target, declaration, copyIndex, path);
        }

        if (target.StartsWith(ResourceIdentifier.ProvidersPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (!_catalog.TryParseResourceId(target, out var id) || id is null)
            {
                Error(DiagnosticCodes.InvalidResourceId, $"dependsOn entry '{target}' is not a valid resource id.", path);
                return unresolved;
            }

            if (!index.Contains(id))
            {
                Error(DiagnosticCodes.DependsOnTargetNotFound, $"dependsOn entry '{target}' does not name a resource of this template.", path);
                return unresolved;
            }

            return unresolved with { Target = id };
        }

        if (DeploymentFunctions.TryParseTypeAndName(_catalog, target, out var typed))
        {
            if (!index.Contains(typed))
            {
                Error(DiagnosticCodes.DependsOnTargetNotFound, $"dependsOn entry '{target}' does not name a resource of this template.", path);
                return unresolved;
            }

            return unresolved with { Target = typed };
        }

        var byName = index.FindByName(target);
        if (byName.Count == 1)
        {
            return unresolved with { Target = byName[0].Id };
        }

        if (byName.Count > 1)
        {
            Error(DiagnosticCodes.AmbiguousDependsOn, $"dependsOn entry '{target}' names {byName.Count} resources ({string.Join(", ", byName.Select(e => e.Id.Type))}); use a resource id or 'type/name'.", path);
            return unresolved;
        }

        var byCopy = index.FindByCopyName(target);
        if (byCopy.Count >= 1)
        {
            return unresolved with { CopyName = byCopy[0].CopyName };
        }

        if (index.TryFindCopyLoop(target, out var loop))
        {
            // The loop's iterations have no ids yet (names depend on copyIndex()); the engine expands the loop
            // and adds an edge to every iteration (DependencyKind.CopyLoop).
            return unresolved with { CopyName = loop.Name };
        }

        Error(DiagnosticCodes.DependsOnTargetNotFound, $"dependsOn entry '{target}' does not name a resource of this template (expected a resource id, 'type/name', a unique resource name, or a copy loop name).", path);
        return unresolved;
    }

    /// <summary>
    /// languageVersion 2.0 (https://learn.microsoft.com/azure/azure-resource-manager/templates/resource-dependency#dependson): a symbolic name
    /// depends on that resource, or on every iteration of a copy loop; <c>loop[i]</c> on one iteration. An iteration whose id is not indexed yet
    /// (its name is computed per iteration) is resolved by the engine once the loop is expanded.
    /// </summary>
    private ResolvedDependency ResolveSymbolic(ResolvedDependency unresolved, string target, ResourceDeclaration declaration, long? copyIndex, JsonPath path)
    {
        var index = _context.Resources;
        if (declaration.Copy is not null)
        {
            string? loopName = TemplateJson.LiteralString(declaration.Copy.Name);
            if (copyIndex is null)
            {
                return loopName is null ? unresolved : unresolved with { CopyName = loopName };
            }

            var iteration = index.FindByDeclaration(declaration, copyIndex);
            return iteration.Count == 1 ? unresolved with { Target = iteration[0].Id } : unresolved with { Deferred = true };
        }

        if (copyIndex is not null)
        {
            Error(DiagnosticCodes.InvalidSymbolicName, $"dependsOn entry '{target}' indexes '{declaration.SymbolicName}', which is not a copy loop.", path);
            return unresolved;
        }

        var entries = index.FindByDeclaration(declaration);
        if (entries.Count == 1)
        {
            return unresolved with { Target = entries[0].Id };
        }

        Error(DiagnosticCodes.DependsOnTargetNotFound, $"dependsOn entry '{target}' names {declaration.Describe()}, whose name could not be computed.", path);
        return unresolved;
    }

    private void Report(ExpressionException exception, JsonPath path)
    {
        Error(exception.Code, exception.MessageWithOffset, path);
    }

    private void Error(string code, string message, JsonPath path)
    {
        _diagnostics.Add(Diagnostic.Error(code, message, path, _document.LocationOf(path), _document.SourcePath));
    }

    private void Info(string code, string message, JsonPath path)
    {
        _diagnostics.Add(Diagnostic.Info(code, message, path, _document.LocationOf(path), _document.SourcePath));
    }
}
