using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Secrets;

namespace EArm.Core.Loading;

/// <summary>
/// Binds the supplied parameter values (parameter file, <c>-p name=value</c>) to the template's parameter definitions: one
/// <see cref="BoundParameter"/> per definition (the supplied value, the default, or missing), secure values tainted, inline text converted to
/// the declared type, and every supplied name the template does not declare reported.
/// </summary>
internal static class ParameterBinder
{
    public static Dictionary<string, BoundParameter> BindParameters(Dictionary<string, ParameterDefinition> definitions, ParameterSet supplied, LoadContext context)
    {
        var bound = new Dictionary<string, BoundParameter>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in definitions.Values)
        {
            if (supplied.Values.TryGetValue(definition.Name, out var value))
            {
                bound[definition.Name] = Bind(definition, value, context);
                continue;
            }

            if (definition.HasDefaultValue)
            {
                JsonNode? defaultValue = definition.DefaultValue;
                if (definition.IsSecure && !TemplateJson.IsExpression(defaultValue))
                {
                    defaultValue = SecretJson.Taint(defaultValue);
                }

                bound[definition.Name] = new BoundParameter { Name = definition.Name, Definition = definition, Value = defaultValue, Source = ParameterValueSource.Default };
                continue;
            }

            // A nullable parameter (languageVersion 2.0) may be omitted: its value is null.
            bound[definition.Name] = definition.IsNullable
                ? new BoundParameter { Name = definition.Name, Definition = definition, Value = null, Source = ParameterValueSource.Default }
                : new BoundParameter { Name = definition.Name, Definition = definition, Source = ParameterValueSource.Default, IsMissing = true };
        }

        foreach (var pair in supplied.Values)
        {
            if (definitions.ContainsKey(pair.Key))
            {
                continue;
            }

            string origin = pair.Value.IsInline ? "inline parameter (-p)" : "parameter file";
            context.Diagnostics.Add(Diagnostic.Error(
                DiagnosticCodes.UnknownParameter,
                $"The {origin} supplies a value for '{pair.Key}', but the template declares no such parameter.",
                pair.Value.IsInline ? null : JsonPath.Of("parameters", pair.Key),
                null,
                pair.Value.SourcePath));
        }

        return bound;
    }

    private static BoundParameter Bind(ParameterDefinition definition, ParameterValue supplied, LoadContext context)
    {
        if (supplied.Reference is not null)
        {
            return new BoundParameter
            {
                Name = definition.Name,
                Definition = definition,
                PendingReference = supplied.Reference,
                Source = supplied.Source,
            };
        }

        JsonNode? value;
        bool conversionFailed = false;
        if (supplied.IsInline && supplied.RawText is not null)
        {
            conversionFailed = !TryConvertInline(definition, supplied.RawText, context, out value);
        }
        else
        {
            value = supplied.Value?.DeepClone();
        }

        if (definition.IsSecure)
        {
            value = SecretJson.Taint(value);
        }

        return new BoundParameter
        {
            Name = definition.Name,
            Definition = definition,
            Value = value,
            Source = supplied.Source,
            ConversionFailed = conversionFailed,
        };
    }

    /// <summary>
    /// Converts the raw text of <c>-p name=value</c> to the declared type: strings are taken verbatim, <c>int</c> and
    /// <c>bool</c> are parsed, <c>object</c>/<c>array</c> are parsed as JSON. Unknown types keep the text.
    /// Returns <c>false</c> (with the raw text as <paramref name="value"/>) when the conversion fails; the
    /// <c>ParameterValueTypeMismatch</c> error is reported here, once, with the definition's location.
    /// </summary>
    private static bool TryConvertInline(ParameterDefinition definition, string rawText, LoadContext context, out JsonNode? value)
    {
        switch (definition.ParsedType)
        {
            case TemplateParameterType.Int:
                if (long.TryParse(rawText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                {
                    value = JsonValue.Create(l);
                    return true;
                }

                break;

            case TemplateParameterType.Bool:
                if (bool.TryParse(rawText.Trim(), out bool b))
                {
                    value = JsonValue.Create(b);
                    return true;
                }

                break;

            case TemplateParameterType.Object:
            case TemplateParameterType.SecureObject:
            case TemplateParameterType.Array:
                try
                {
                    string jsonText = LoneSurrogates.Replace(rawText);
                    var duplicates = DuplicateKeys.Find(Encoding.UTF8.GetBytes(jsonText), limit: 1);
                    if (duplicates.Count > 0)
                    {
                        context.Diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.DuplicateJsonKey,
                            $"The inline value for parameter '{definition.Name}' is not accepted: {duplicates[0].Describe()}",
                            definition.Path,
                            definition.Location,
                            context.SourcePath));
                        value = JsonValue.Create(rawText);
                        return false;
                    }

                    var parsed = JsonNode.Parse(jsonText, documentOptions: TemplateJson.DocumentOptions);
                    if (TemplateParameterTypes.Matches(parsed, definition.ParsedType.Value))
                    {
                        value = parsed;
                        return true;
                    }
                }
                catch (JsonException)
                {
                    // Reported below.
                }

                break;

            default:
                value = JsonValue.Create(rawText);
                return true;
        }

        context.Diagnostics.Add(Diagnostic.Error(
            DiagnosticCodes.ParameterValueTypeMismatch,
            $"The inline value for parameter '{definition.Name}' cannot be converted to type '{definition.Type}'.",
            definition.Path,
            definition.Location,
            context.SourcePath));
        value = JsonValue.Create(rawText);
        return false;
    }
}
