using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Types;
using EArm.Engine.Providers;
using EArm.Expressions;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Static;
using EArm.Expressions.Values;

namespace EArm.Engine.Planning;

/// <summary>
/// Expansion: turns every declaration of a <see cref="StaticContext"/> into
/// <see cref="ResourceInstance"/>s. A <c>copy</c> loop yields one instance per index, each with a concrete
/// <see cref="CopyFrame"/> and its name re-evaluated; <c>condition</c> is evaluated per instance (false → the
/// instance is kept as an <c>Ignore</c> row); resource ids must be unique after expansion; child instances are
/// linked to their parent instance when the parent is declared in the template.
/// </summary>
public sealed class ResourceExpander
{
    private readonly ExpressionEvaluator _evaluator;
    private readonly ResourceTypeCatalog _catalog;

    public ResourceExpander(ExpressionEvaluator evaluator, ResourceTypeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(catalog);
        _evaluator = evaluator;
        _catalog = catalog;
    }

    /// <summary>Expands the template; problems are appended to <paramref name="diagnostics"/>.</summary>
    public IReadOnlyList<ResourceInstance> Expand(StaticContext staticContext, EvaluationContext deployContext, List<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(staticContext);
        ArgumentNullException.ThrowIfNull(deployContext);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var document = staticContext.Document;
        var instances = new List<ResourceInstance>();
        var resourceContext = deployContext.ForScope(EvaluationScope.Resource);

        // A name may reference an instance of another declaration (<c>reference(format('capp-{0}', copyIndex())).appId</c> over a copy loop of applications): every
        // instance is added to the shared index as soon as it exists, and a declaration whose names reference something not expanded YET is retried after the others,
        // so the order of declaration in the template does not matter (review round 2, S2-15). Only when nothing more can be expanded are its errors reported.
        var pending = Enumerable.Range(0, staticContext.Resources.Count).ToList();
        while (pending.Count > 0)
        {
            var retry = new List<int>();
            foreach (int index in pending)
            {
                int instanceMark = instances.Count;
                int diagnosticMark = diagnostics.Count;
                ExpandDeclaration(staticContext, index, resourceContext, instances, diagnostics);
                bool unresolvedReference = diagnostics.Skip(diagnosticMark).Any(d => d.IsError && d.Code == DiagnosticCodes.ReferenceTargetNotFound);
                if (unresolvedReference && pending.Count > 1)
                {
                    instances.RemoveRange(instanceMark, instances.Count - instanceMark);
                    diagnostics.RemoveRange(diagnosticMark, diagnostics.Count - diagnosticMark);
                    retry.Add(index);
                    continue;
                }

                for (int i = instanceMark; i < instances.Count; i++)
                {
                    resourceContext.Resources.Add(new TemplateResourceIndex.Entry(instances[i].PlannedId, instances[i].IsSymbolic, instances[i].Declaration, instances[i].Copy?.Name, instances[i].Copy?.Index));
                }
            }

            if (retry.Count == pending.Count)
            {
                // No progress: the declarations left are expanded once more and their errors stand.
                foreach (int index in retry)
                {
                    ExpandDeclaration(staticContext, index, resourceContext, instances, diagnostics);
                }

                break;
            }

            pending = retry;
        }

        // Declaration order, then iteration order: what the retries changed is put back.
        var ordered = instances.Select((instance, position) => (instance, position)).OrderBy(pair => pair.instance.DeclarationIndex).ThenBy(pair => pair.position).Select(pair => pair.instance).ToList();
        instances.Clear();
        instances.AddRange(ordered);

        RemoveDuplicates(document, instances, diagnostics);
        LinkParents(instances);
        return instances;
    }

    /// <summary>Expands one declaration of the template (one instance, or the iterations of its copy loop) into <paramref name="instances"/>.</summary>
    private void ExpandDeclaration(StaticContext staticContext, int index, EvaluationContext resourceContext, List<ResourceInstance> instances, List<Diagnostic> diagnostics)
    {
        var document = staticContext.Document;
        var resolved = staticContext.Resources[index];
        var declaration = resolved.Declaration;
        var definition = _catalog.Find(declaration.Type);
        if (definition is null || declaration.ApiVersion is null || declaration.Name is null)
        {
            // Reported by validation; nothing to expand.
            return;
        }

        if (declaration.Copy is { } copy)
        {
            ExpandCopy(document, resolved, declaration, definition, copy, index, resourceContext, instances, diagnostics);
        }
        else if (instances.Count >= EvaluationLimits.MaxInstances)
        {
            Error(document, diagnostics, DiagnosticCodes.ExpansionLimitExceeded, $"The template expands to more than {EvaluationLimits.MaxInstances} resource instances; this resource is not expanded.", declaration.Path);
        }
        else if (BuildInstance(document, resolved, declaration, definition, index, resourceContext, null, diagnostics) is { } instance)
        {
            instances.Add(instance);
        }
    }

    private void ExpandCopy(TemplateDocument document, ResolvedResource resolved, ResourceDeclaration declaration, ResourceTypeDefinition definition, CopyDeclaration copy, int index, EvaluationContext resourceContext, List<ResourceInstance> instances, List<Diagnostic> diagnostics)
    {
        string? copyName = resolved.CopyName ?? EvaluateCopyString(document, copy.Name, copy.Path.Append("name"), resourceContext, diagnostics);
        if (copyName is null)
        {
            Error(document, diagnostics, DiagnosticCodes.InvalidCopy, "copy.name must be a literal string.", copy.Path.Append("name"));
            return;
        }

        long? count = resolved.CopyCount ?? EvaluateCopyCount(document, copy, resourceContext, diagnostics);
        if (count is null)
        {
            return;
        }

        // The budgets are checked before a single iteration is built: a huge count must cost nothing but this message.
        if (count.Value > EvaluationLimits.MaxCopyCount)
        {
            Error(document, diagnostics, DiagnosticCodes.ExpansionLimitExceeded, $"copy.count is {count.Value}, more than the limit of {EvaluationLimits.MaxCopyCount} iterations per resource (the ARM limit); nothing is expanded.", copy.Path.Append("count"));
            return;
        }

        if (instances.Count + count.Value > EvaluationLimits.MaxInstances)
        {
            Error(document, diagnostics, DiagnosticCodes.ExpansionLimitExceeded, $"The template expands to more than {EvaluationLimits.MaxInstances} resource instances (all copy loops together); this copy loop of {count.Value} iterations would pass the limit and is not expanded.", copy.Path.Append("count"));
            return;
        }

        var mode = CopyMode.Parallel;
        string? modeText = EvaluateCopyString(document, copy.Mode, copy.Path.Append("mode"), resourceContext, diagnostics);
        if (modeText is not null)
        {
            if (modeText.Equals("serial", StringComparison.OrdinalIgnoreCase))
            {
                mode = CopyMode.Serial;
            }
            else if (!modeText.Equals("parallel", StringComparison.OrdinalIgnoreCase))
            {
                Error(document, diagnostics, DiagnosticCodes.InvalidCopy, $"copy.mode must be 'serial' or 'parallel', got '{modeText}'.", copy.Path.Append("mode"));
                return;
            }
        }

        int batchSize = mode == CopyMode.Serial ? 1 : (int)Math.Min(count.Value, int.MaxValue);
        if (copy.BatchSize is not null)
        {
            JsonNode? batchValue;
            try
            {
                batchValue = _evaluator.EvaluateTree(copy.BatchSize, resourceContext);
            }
            catch (ExpressionException ex)
            {
                Error(document, diagnostics, ex.Code, ex.MessageWithOffset, copy.Path.Append("batchSize"));
                return;
            }

            if (!JsonValues.TryGetWholeNumber(batchValue, out long size) || size < 1)
            {
                Error(document, diagnostics, DiagnosticCodes.InvalidCopy, $"copy.batchSize must be a positive integer, got {JsonValues.Render(batchValue)}.", copy.Path.Append("batchSize"));
                return;
            }

            batchSize = (int)Math.Min(size, int.MaxValue);
        }

        for (long i = 0; i < count.Value; i++)
        {
            var iteration = new CopyIteration(copyName, i, count.Value, mode, batchSize);
            var context = resourceContext.PushCopy(new CopyFrame(copyName, i, count.Value));
            if (BuildInstance(document, resolved, declaration, definition, index, context, iteration, diagnostics) is { } instance)
            {
                instances.Add(instance);
            }
        }
    }

    private long? EvaluateCopyCount(TemplateDocument document, CopyDeclaration copy, EvaluationContext context, List<Diagnostic> diagnostics)
    {
        var path = copy.Path.Append("count");
        if (copy.Count is null)
        {
            Error(document, diagnostics, DiagnosticCodes.InvalidCopy, "copy.count is required.", path);
            return null;
        }

        JsonNode? value;
        try
        {
            value = _evaluator.EvaluateTree(copy.Count, context);
        }
        catch (ExpressionException ex)
        {
            Error(document, diagnostics, ex.Code, ex.MessageWithOffset, path);
            return null;
        }

        if (UnknownJson.IsUnknown(value, out var unknown))
        {
            Error(document, diagnostics, EngineDiagnosticCodes.CopyExpansionFailed, $"copy.count depends on a value that is not known before deployment ({unknown.Symbol}); the loop count must be computable from parameters and variables.", path);
            return null;
        }

        if (!JsonValues.TryGetWholeNumber(value, out long count) || count < 0)
        {
            Error(document, diagnostics, DiagnosticCodes.InvalidCopy, $"copy.count must evaluate to a non-negative integer, got {JsonValues.Render(value)}.", path);
            return null;
        }

        return count;
    }

    private string? EvaluateCopyString(TemplateDocument document, JsonNode? node, JsonPath path, EvaluationContext context, List<Diagnostic> diagnostics)
    {
        if (node is null)
        {
            return null;
        }

        try
        {
            var value = _evaluator.EvaluateTree(node, context);
            if (JsonValues.TryGetString(value, out var text))
            {
                return text;
            }

            Error(document, diagnostics, DiagnosticCodes.InvalidCopy, $"{path.Last} must evaluate to a string, got {JsonValues.Describe(value)}.", path);
            return null;
        }
        catch (ExpressionException ex)
        {
            Error(document, diagnostics, ex.Code, ex.MessageWithOffset, path);
            return null;
        }
    }

    private ResourceInstance? BuildInstance(TemplateDocument document, ResolvedResource resolved, ResourceDeclaration declaration, ResourceTypeDefinition definition, int index, EvaluationContext context, CopyIteration? copy, List<Diagnostic> diagnostics)
    {
        ResourceIdentifier id;
        bool symbolic;
        JsonNode? nameValue;
        if (copy is null && resolved.NameKind == ResolvedNameKind.Static && resolved.Id is { } staticId)
        {
            id = staticId;
            symbolic = false;
            nameValue = resolved.NameValue;
        }
        else
        {
            var evaluation = InstanceEvaluator.EvaluateName(_evaluator, declaration, definition, context);
            if (evaluation.Id is null)
            {
                if (evaluation.Error is not null && (copy is not null || resolved.NameKind != ResolvedNameKind.Invalid))
                {
                    // Static evaluation already reported invalid names; per-iteration failures are new.
                    Error(document, diagnostics, evaluation.ErrorCode ?? DiagnosticCodes.InvalidResourceName, CopyPrefix(copy) + evaluation.Error, declaration.Path.Append("name"));
                }

                return null;
            }

            id = evaluation.Id;
            symbolic = evaluation.IsSymbolic;
            nameValue = evaluation.Value;
        }

        var instance = new ResourceInstance(declaration, resolved, definition, index, id, symbolic, nameValue, context, copy);

        if (declaration.Condition is null)
        {
            instance.Condition = true;
        }
        else if (copy is null && resolved.Condition is { } staticCondition)
        {
            instance.Condition = staticCondition;
        }
        else
        {
            instance.Condition = InstanceEvaluator.EvaluateCondition(_evaluator, declaration, context, out var error);
            if (error is not null)
            {
                Error(document, diagnostics, DiagnosticCodes.ConditionNotBoolean, CopyPrefix(copy) + error, declaration.Path.Append("condition"));
                return null;
            }
        }

        return instance;
    }

    private static void RemoveDuplicates(TemplateDocument document, List<ResourceInstance> instances, List<Diagnostic> diagnostics)
    {
        // The first declaration (document order) wins; later duplicates are reported and dropped.
        var seen = new Dictionary<ResourceIdentifier, ResourceInstance>();
        var kept = new List<ResourceInstance>();
        foreach (var instance in instances)
        {
            if (seen.TryGetValue(instance.PlannedId, out var first))
            {
                string rendered = instance.IsSymbolic ? $"{instance.Type} with deploy-time name {instance.PlannedId.Name}" : $"'{instance.PlannedId.Id}'";
                string caseNote = string.Equals(first.PlannedId.Id, instance.PlannedId.Id, StringComparison.Ordinal) ? string.Empty : " Names are compared case-insensitively because Graph matches its keys that way: ids that differ only by case address the same object.";
                string where = first.Copy is null ? first.Declaration.Path.ToString() : $"{first.Declaration.Path} iteration {first.Copy.Index}";
                Error(document, diagnostics, DiagnosticCodes.DuplicateResourceId, $"Resource id {rendered} is produced more than once after expansion (also by {where}).{caseNote}", instance.Declaration.Path.Append("name"));
                continue;
            }

            seen[instance.PlannedId] = instance;
            kept.Add(instance);
        }

        instances.Clear();
        instances.AddRange(kept);
    }

    private static void LinkParents(List<ResourceInstance> instances)
    {
        var byId = new Dictionary<ResourceIdentifier, ResourceInstance>();
        foreach (var instance in instances)
        {
            byId[instance.PlannedId] = instance;
        }

        foreach (var instance in instances)
        {
            if (instance.ParentId is { } parentId && byId.TryGetValue(parentId, out var parent))
            {
                instance.Parent = parent;
            }
        }
    }

    private static string CopyPrefix(CopyIteration? copy)
    {
        return copy is null ? string.Empty : $"copy '{copy.Name}' iteration {copy.Index}: ";
    }

    private static void Error(TemplateDocument document, List<Diagnostic> diagnostics, string code, string message, JsonPath path)
    {
        diagnostics.Add(Diagnostic.Error(code, message, path, document.LocationOf(path), document.SourcePath));
    }
}
