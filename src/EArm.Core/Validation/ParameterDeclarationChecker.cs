using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Json;

namespace EArm.Core.Validation;

/// <summary>
/// The parameter checks of <see cref="SemanticChecker"/>: each declaration (type, allowed values, ranges, default value) and each supplied
/// value against its declaration. Appends to the diagnostics list it is given.
/// </summary>
internal sealed class ParameterDeclarationChecker
{
    private readonly TemplateDocument _document;
    private readonly List<Diagnostic> _diagnostics;

    public ParameterDeclarationChecker(TemplateDocument document, List<Diagnostic> diagnostics)
    {
        _document = document;
        _diagnostics = diagnostics;
    }

    public void Run()
    {
        CheckParameters();
        CheckParameterValues();
    }

    private void CheckParameters()
    {
        foreach (var definition in _document.Parameters.Values)
        {
            var typePath = definition.Path.Append("type");
            if (definition.TypeProblem is not null)
            {
                // An unresolvable '$ref' is reported by the type definition checker.
                continue;
            }

            if (definition.Type is null)
            {
                Error(DiagnosticCodes.InvalidParameterType, $"Parameter '{definition.Name}' is missing the required 'type' property.", typePath);
                continue;
            }

            if (definition.ParsedType is not { } type)
            {
                Error(DiagnosticCodes.InvalidParameterType, $"Parameter '{definition.Name}' has unknown type '{definition.Type}'; expected one of: {string.Join(", ", TemplateParameterTypes.CanonicalNames)}.", typePath);
                continue;
            }

            // For an array parameter the allowed values are the values its elements may take (ARM/Bicep), so they are not arrays themselves.
            if (definition.AllowedValues is { } allowed && type != TemplateParameterType.Array)
            {
                for (int i = 0; i < allowed.Count; i++)
                {
                    if (!TemplateParameterTypes.MatchesTemplateText(allowed[i], type))
                    {
                        Error(DiagnosticCodes.AllowedValueTypeMismatch, $"Parameter '{definition.Name}': allowed value at index {i} does not match the declared type '{definition.Type}'.", definition.Path.Append("allowedValues").Append(i));
                    }
                }
            }

            CheckRange(definition, type, "minValue", "maxValue", definition.MinValue, definition.MaxValue, TemplateParameterType.Int);
            CheckRange(definition, type, "minLength", "maxLength", definition.MinLength, definition.MaxLength, TemplateParameterType.String, TemplateParameterType.SecureString, TemplateParameterType.Array);

            if (!definition.HasDefaultValue)
            {
                continue;
            }

            var defaultPath = definition.Path.Append("defaultValue");
            if (definition.IsSecure)
            {
                Warning(DiagnosticCodes.SecureParameterHasDefault, $"Parameter '{definition.Name}' is secure but has a default value; secrets should not be stored in the template.", defaultPath);
            }

            if (!(definition.DefaultValue is null && definition.IsNullable) && !TemplateParameterTypes.MatchesTemplateText(definition.DefaultValue, type))
            {
                Error(DiagnosticCodes.DefaultValueTypeMismatch, $"Parameter '{definition.Name}': the default value does not match the declared type '{definition.Type}'.", defaultPath);
            }
            else if (ParameterConstraints.Violation(definition, definition.DefaultValue, isDefault: true) is { } defaultViolation)
            {
                Error(DiagnosticCodes.DefaultValueNotAllowed, $"Parameter '{definition.Name}': the default value is not accepted: {defaultViolation}.", defaultPath);
            }
        }
    }

    private void CheckRange(ParameterDefinition definition, TemplateParameterType type, string minKey, string maxKey, JsonNode? min, JsonNode? max, params TemplateParameterType[] applicableTypes)
    {
        if (min is null && max is null)
        {
            return;
        }

        if (Array.IndexOf(applicableTypes, type) < 0)
        {
            string names = string.Join("/", Array.ConvertAll(applicableTypes, TemplateParameterTypes.CanonicalName));
            Error(DiagnosticCodes.InvalidParameterConstraint, $"Parameter '{definition.Name}': '{minKey}'/'{maxKey}' only apply to {names} parameters, not '{definition.Type}'.", definition.Path.Append(min is not null ? minKey : maxKey));
            return;
        }

        long? minValue = LiteralInt(definition, minKey, min);
        long? maxValue = LiteralInt(definition, maxKey, max);
        if (minKey == "minLength")
        {
            if (minValue is < 0)
            {
                Error(DiagnosticCodes.InvalidParameterConstraint, $"Parameter '{definition.Name}': '{minKey}' must not be negative.", definition.Path.Append(minKey));
            }

            if (maxValue is < 0)
            {
                Error(DiagnosticCodes.InvalidParameterConstraint, $"Parameter '{definition.Name}': '{maxKey}' must not be negative.", definition.Path.Append(maxKey));
            }
        }

        if (minValue is { } lo && maxValue is { } hi && lo > hi)
        {
            Error(DiagnosticCodes.InvalidParameterConstraint, $"Parameter '{definition.Name}': '{minKey}' ({lo}) is greater than '{maxKey}' ({hi}).", definition.Path.Append(minKey));
        }
    }

    private long? LiteralInt(ParameterDefinition definition, string key, JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (TemplateJson.IsExpression(node))
        {
            // ARM: "Expressions aren't allowed with other parameter properties" (only defaultValue may be an expression).
            Error(DiagnosticCodes.InvalidParameterConstraint, $"Parameter '{definition.Name}': '{key}' must be a literal integer; expressions are only allowed in 'defaultValue'.", definition.Path.Append(key));
            return null;
        }

        long? value = TemplateJson.AsInt64(node);
        if (value is null)
        {
            Error(DiagnosticCodes.InvalidParameterConstraint, $"Parameter '{definition.Name}': '{key}' must be an integer.", definition.Path.Append(key));
        }

        return value;
    }

    private void CheckParameterValues()
    {
        string? parameterSource = _document.SuppliedParameters.SourcePath;
        foreach (var bound in _document.ParameterValues.Values)
        {
            var definition = bound.Definition;
            if (bound.IsMissing)
            {
                // A warning offline: deployment turns a missing value into an error.
                Warning(DiagnosticCodes.MissingParameterValue, $"Parameter '{definition.Name}' has no value and no default value; a value is required to deploy.", definition.Path);
                continue;
            }

            if (bound.Source == ParameterValueSource.Default || bound.PendingReference is not null || definition.ParsedType is not { } type)
            {
                continue;
            }

            if (bound.ConversionFailed || bound.IsDeployTimeValue)
            {
                // The loader already reported ParameterValueTypeMismatch for this inline value (with its location).
                continue;
            }

            JsonPath? valuePath = bound.Source == ParameterValueSource.File ? JsonPath.Of("parameters", definition.Name, "value") : null;
            string? valueSource = bound.Source == ParameterValueSource.File ? parameterSource : null;

            if (!ParameterConstraints.TypeMatches(definition, type, bound.Value))
            {
                _diagnostics.Add(Diagnostic.Error(DiagnosticCodes.ParameterValueTypeMismatch, $"The value supplied for parameter '{definition.Name}' does not match the declared type '{definition.Type}'.", valuePath, null, valueSource));
            }
            else if (ParameterConstraints.Violation(definition, bound.Value) is { } violation)
            {
                // Secure parameters are checked too, on the revealed value; the message names the constraint, never the value.
                _diagnostics.Add(Diagnostic.Error(DiagnosticCodes.ParameterValueNotAllowed, $"The value supplied for parameter '{definition.Name}' is not accepted: {violation}.", valuePath, null, valueSource));
            }
        }
    }

    // ---- helpers ----

    private void Error(string code, string message, JsonPath path)
    {
        _diagnostics.Add(Diagnostic.Error(code, message, path, _document.LocationOf(path), _document.SourcePath));
    }

    private void Warning(string code, string message, JsonPath path)
    {
        _diagnostics.Add(Diagnostic.Warning(code, message, path, _document.LocationOf(path), _document.SourcePath));
    }
}
