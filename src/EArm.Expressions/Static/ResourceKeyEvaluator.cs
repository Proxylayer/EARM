using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Types;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Values;

namespace EArm.Expressions.Static;

/// <summary>
/// Evaluates the keys of one resource that <see cref="StaticEvaluator"/> resolves offline: its <c>name</c> (to a static, symbolic or
/// copy-dependent id), its <c>condition</c> and its <c>copy.count</c>. In strict mode failures are appended to the diagnostics.
/// </summary>
internal sealed class ResourceKeyEvaluator
{
    private readonly ExpressionEvaluator _evaluator;
    private readonly TemplateDocument _document;
    private readonly List<Diagnostic> _diagnostics;

    public ResourceKeyEvaluator(ExpressionEvaluator evaluator, TemplateDocument document, List<Diagnostic> diagnostics)
    {
        _evaluator = evaluator;
        _document = document;
        _diagnostics = diagnostics;
    }

    public ResolvedResource EvaluateName(ResourceDeclaration declaration, ResourceTypeDefinition definition, EvaluationContext context, JsonPath namePath, bool strict, ResolvedResource result)
    {
        JsonNode? value;
        try
        {
            value = _evaluator.EvaluateString(declaration.Name!, context);
        }
        catch (ExpressionException ex)
        {
            if (strict)
            {
                Report(ex, namePath);
            }

            return result;
        }

        if (UnknownJson.ContainsTaint(value))
        {
            if (strict)
            {
                Error(DiagnosticCodes.SecureValueInResourceKey, "A secure value flows into the resource name; names are written to output and logs and cannot be secret.", namePath);
            }

            return result with { NameValue = value };
        }

        if (UnknownJson.IsUnknown(value, out var unknown))
        {
            if ((unknown.Origins & UnknownOrigins.Copy) != 0)
            {
                return result with { NameKind = ResolvedNameKind.CopyDependent, NameValue = value };
            }

            var symbolic = new ResourceIdentifier(definition.Type, [unknown.Symbol]);
            return result with { NameKind = ResolvedNameKind.Symbolic, Id = symbolic, NameValue = value };
        }

        if (!JsonValues.TryGetString(value, out var name))
        {
            if (strict)
            {
                Error(DiagnosticCodes.ResourceNameNotString, $"The resource name must evaluate to a string, got {JsonValues.Describe(value)}.", namePath);
            }

            return result with { NameValue = value };
        }

        var segments = name.Split('/');
        if (segments.Length != definition.Depth || segments.Any(string.IsNullOrEmpty))
        {
            if (strict)
            {
                string expected = definition.Depth == 1 ? "a single segment without '/'" : $"{definition.Depth} segments ('parent/child')";
                Error(DiagnosticCodes.InvalidResourceName, $"Name '{name}' (evaluated) is not valid for type '{definition.Type}': expected {expected}.", namePath);
            }

            return result with { NameValue = value };
        }

        // A literal name was checked by the core validator; a computed one is checked here, with the same rules.
        if (ExpressionSyntax.IsExpression(declaration.Name!) && ResourceNameRules.Validate(definition, name) is { } nameProblem)
        {
            if (strict)
            {
                Error(DiagnosticCodes.InvalidResourceName, nameProblem, namePath);
            }

            return result with { NameValue = value };
        }

        return result with
        {
            NameKind = ResolvedNameKind.Static,
            Id = new ResourceIdentifier(definition.Type, segments),
            Name = name,
            NameValue = value,
        };
    }

    public ResolvedResource EvaluateCondition(ResourceDeclaration declaration, EvaluationContext context, ResolvedResource result)
    {
        if (declaration.Condition is null)
        {
            return result;
        }

        var path = declaration.Path.Append("condition");
        JsonNode? value;
        try
        {
            value = _evaluator.EvaluateTree(declaration.Condition, context);
        }
        catch (ExpressionException ex)
        {
            Report(ex, path);
            return result;
        }

        if (UnknownJson.ContainsTaint(value))
        {
            Error(DiagnosticCodes.SecureValueInResourceKey, "A secure value flows into the resource condition; conditions are logged and cannot be secret.", path);
            return result;
        }

        if (UnknownJson.IsUnknown(value))
        {
            return result with { ConditionIsDeployTime = true };
        }

        if (JsonValues.TryGetBool(value, out bool condition))
        {
            return result with { Condition = condition };
        }

        Error(DiagnosticCodes.ConditionNotBoolean, $"condition must evaluate to a boolean, got {JsonValues.Describe(value)}.", path);
        return result;
    }

    public ResolvedResource EvaluateCopy(ResourceDeclaration declaration, EvaluationContext context, ResolvedResource result)
    {
        if (declaration.Copy is not { } copy)
        {
            return result;
        }

        var path = copy.Path.Append("count");
        if (copy.Count is null)
        {
            return result;
        }

        JsonNode? value;
        try
        {
            value = _evaluator.EvaluateTree(copy.Count, context);
        }
        catch (ExpressionException ex)
        {
            Report(ex, path);
            return result;
        }

        if (UnknownJson.ContainsTaint(value))
        {
            Error(DiagnosticCodes.SecureValueInResourceKey, "A secure value flows into copy.count; the loop count is logged and cannot be secret.", path);
            return result;
        }

        if (UnknownJson.IsUnknown(value, out var unknown))
        {
            if ((unknown.Origins & UnknownOrigins.Copy) != 0)
            {
                Error(DiagnosticCodes.InvalidCopy, "copy.count cannot depend on copyIndex().", path);
            }

            return result;
        }

        if (!JsonValues.TryGetWholeNumber(value, out long count) || count < 0)
        {
            Error(DiagnosticCodes.InvalidCopy, $"copy.count must evaluate to a non-negative integer, got {JsonValues.Render(value)}.", path);
            return result;
        }

        if (count > EvaluationLimits.MaxCopyCount)
        {
            Error(DiagnosticCodes.ExpansionLimitExceeded, $"copy.count is {count}, more than the limit of {EvaluationLimits.MaxCopyCount} iterations per resource (the ARM limit); nothing is expanded.", path);
            return result;
        }

        return result with { CopyCount = count };
    }

    private void Report(ExpressionException exception, JsonPath path)
    {
        Error(exception.Code, exception.MessageWithOffset, path);
    }

    private void Error(string code, string message, JsonPath path)
    {
        _diagnostics.Add(Diagnostic.Error(code, message, path, _document.LocationOf(path), _document.SourcePath));
    }
}
