using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Types;
using EArm.Core.Validation;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Expressions;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Validation;

namespace EArm.Engine.Permissions;

/// <summary>
/// A loaded template validated and expanded offline, with no Graph access and no token: the input of
/// <see cref="PermissionManifest"/> and <see cref="RunScopes"/>. The steps are the planner's own (validation,
/// copy and condition expansion, dependency graph, property evaluation) with the offline context, so deploy-time values
/// (reference(...), Key Vault secrets that are not resolved yet) stay unevaluated and the providers fall back to the declaration.
/// </summary>
public sealed class OfflineTemplateExpansion
{
    private OfflineTemplateExpansion(
        TemplateDocument document,
        IReadOnlyList<Diagnostic> errors,
        IReadOnlyList<ResourceInstance> instances,
        int ignoredCount)
    {
        Document = document;
        Errors = errors;
        Instances = instances;
        IgnoredCount = ignoredCount;
    }

    /// <summary>The template the expansion was computed from.</summary>
    public TemplateDocument Document { get; }

    /// <summary>
    /// Empty when the template expanded; otherwise the sorted diagnostics of the step that failed (all validation
    /// diagnostics, warnings included, when validation failed; the expansion diagnostics when expansion failed).
    /// </summary>
    public IReadOnlyList<Diagnostic> Errors { get; }

    /// <summary>True when validation and expansion produced no error.</summary>
    public bool Succeeded => Errors.Count == 0;

    /// <summary>The expanded instances whose condition is not false offline (empty when <see cref="Succeeded"/> is false).</summary>
    public IReadOnlyList<ResourceInstance> Instances { get; }

    /// <summary>The number of instances left out because their condition evaluates to false offline.</summary>
    public int IgnoredCount { get; }

    /// <summary>Every string of the template that is an expression (starts with <c>[</c>), for the lookup functions.</summary>
    public IEnumerable<string> ExpressionStrings => EnumerateExpressionStrings(Document.Root);

    /// <summary>The parameters bound to Key Vault secrets that are not resolved yet.</summary>
    public IEnumerable<(string Parameter, KeyVaultReference Reference)> KeyVaultReferences =>
        Document.ParameterValues.Values
            .Where(b => b.PendingReference is not null)
            .Select(b => (b.Name, b.PendingReference!));

    /// <summary>Validates and expands <paramref name="document"/> offline.</summary>
    public static OfflineTemplateExpansion Run(TemplateDocument document, ResourceTypeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalog);

        // Offline validation first: the permissions of a broken template are not meaningful.
        var diagnostics = new List<Diagnostic>(new TemplateValidator(catalog).Validate(document));
        var analysis = new ExpressionValidator(catalog).Analyze(document);
        diagnostics.AddRange(analysis.Diagnostics);
        if (diagnostics.Any(d => d.IsError))
        {
            diagnostics.Sort(DiagnosticOrder.Instance);
            return new OfflineTemplateExpansion(document, diagnostics, [], 0);
        }

        // Expand copy loops and conditions with the offline context, then wire the dependency graph so the providers
        // can see what each instance references (the planner does the same before it asks for permissions).
        var evaluator = ExpressionEvaluator.Default;
        var expansionDiagnostics = new List<Diagnostic>();
        var instances = new ResourceExpander(evaluator, catalog).Expand(analysis.Static, analysis.Static.Context, expansionDiagnostics);
        if (!expansionDiagnostics.Any(d => d.IsError))
        {
            DependencyGraph.Build(instances, analysis.Static, evaluator, catalog, expansionDiagnostics);
        }

        if (expansionDiagnostics.Any(d => d.IsError))
        {
            expansionDiagnostics.Sort(DiagnosticOrder.Instance);
            return new OfflineTemplateExpansion(document, expansionDiagnostics, [], 0);
        }

        int ignored = instances.Count(i => i.IsIgnored);
        var active = instances.Where(i => !i.IsIgnored).ToList();
        foreach (var instance in active)
        {
            try
            {
                InstanceEvaluator.EvaluateProperties(evaluator, instance);
            }
            catch (ExpressionException)
            {
                // Deploy-time values: the providers fall back to the declaration.
            }
        }

        return new OfflineTemplateExpansion(document, [], active, ignored);
    }

    private static IEnumerable<string> EnumerateExpressionStrings(JsonNode? root)
    {
        var stack = new Stack<JsonNode?>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            switch (stack.Pop())
            {
                case JsonObject obj:
                    foreach (var pair in obj)
                    {
                        stack.Push(pair.Value);
                    }

                    break;
                case JsonArray array:
                    foreach (var item in array)
                    {
                        stack.Push(item);
                    }

                    break;
                case JsonValue value when value.TryGetValue<string>(out string? text) && text.StartsWith('['):
                    yield return text;
                    break;
            }
        }
    }
}
