using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using EArm.Core.Json;

namespace EArm.Core.Validation;

/// <summary>
/// The languageVersion 2.0 aggregate type constraints checked against a value (https://learn.microsoft.com/azure/azure-resource-manager/templates/definitions,
/// https://learn.microsoft.com/azure/azure-resource-manager/templates/parameters): <c>properties</c> (a property is required unless its type is
/// <c>nullable</c>), <c>additionalProperties</c> (<c>false</c>, or a type for the undeclared properties; Bicep's <c>sealed</c> is accepted as
/// <c>false</c>), <c>discriminator</c> (<c>propertyName</c> plus a <c>mapping</c> from its value to an object type), <c>prefixItems</c> and
/// <c>items</c> (a type, or <c>false</c> for no further elements), and inside them the scalar constraints <c>type</c>, <c>allowedValues</c>,
/// <c>minValue</c>/<c>maxValue</c>, <c>minLength</c>/<c>maxLength</c>. Nested types may use <c>$ref</c>. Messages name the location in the value
/// (<c>.a.b[0]</c>), never a value, so they are safe for secure parameters.
/// </summary>
public static class TypeConstraints
{
    private const int MaxDepth = 64;

    /// <summary>The first violation of the aggregate constraints of <paramref name="schema"/> by <paramref name="value"/>, or <c>null</c>.</summary>
    public static string? AggregateViolation(JsonObject? schema, JsonNode? value, IReadOnlyDictionary<string, JsonObject> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        return schema is null ? null : Aggregate(schema, value, definitions, string.Empty, 0);
    }

    private static string? Aggregate(JsonObject schema, JsonNode? value, IReadOnlyDictionary<string, JsonObject> definitions, string at, int depth)
    {
        if (depth > MaxDepth)
        {
            return null;
        }

        switch (value)
        {
            case JsonObject obj:
                return ObjectViolation(schema, obj, definitions, at, depth);
            case JsonArray array:
                return ArrayViolation(schema, array, definitions, at, depth);
            default:
                return null;
        }
    }

    /// <summary>Checks a nested value against a nested type node: nullability, type, scalar constraints, then its own aggregate constraints.</summary>
    private static string? Nested(JsonNode? typeNode, JsonNode? value, bool present, IReadOnlyDictionary<string, JsonObject> definitions, string at, int depth)
    {
        if (typeNode is not JsonObject raw)
        {
            return null;
        }

        var schema = TypeReferences.Resolve(raw, definitions, out _);
        if (schema is null)
        {
            return null;
        }

        if (!present || value is null)
        {
            return TypeReferences.IsNullable(schema) ? null : (present ? $"{Where(at)} must not be null" : $"the required property {Where(at)} is missing");
        }

        if (TemplateParameterTypes.TryParse(TemplateJson.AsString(schema["type"]), out var type))
        {
            if (!TemplateParameterTypes.Matches(value, type))
            {
                return $"{Where(at)} is not of type '{TemplateParameterTypes.CanonicalName(type)}'";
            }

            if (ScalarViolation(schema, type, value, at) is { } scalar)
            {
                return scalar;
            }
        }

        return Aggregate(schema, value, definitions, at, depth + 1);
    }

    private static string? ScalarViolation(JsonObject schema, TemplateParameterType type, JsonNode value, string at)
    {
        if (schema["allowedValues"] is JsonArray allowed)
        {
            bool ok = type == TemplateParameterType.Array && value is JsonArray elements ? AllAllowed(elements, allowed) : IsAllowed(value, allowed);
            if (!ok)
            {
                return $"{Where(at)} does not match its allowedValues";
            }
        }

        long? length = value switch
        {
            JsonArray a => a.Count,
            JsonValue when TemplateJson.AsString(value) is { } text => text.Length,
            _ => null,
        };
        if (length is { } n)
        {
            if (TemplateJson.AsInt64(schema["minLength"]) is { } minLength && n < minLength)
            {
                return $"{Where(at)} is shorter than minLength ({minLength.ToString(CultureInfo.InvariantCulture)})";
            }

            if (TemplateJson.AsInt64(schema["maxLength"]) is { } maxLength && n > maxLength)
            {
                return $"{Where(at)} is longer than maxLength ({maxLength.ToString(CultureInfo.InvariantCulture)})";
            }
        }

        if (type == TemplateParameterType.Int && TemplateJson.AsInt64(value) is { } number)
        {
            if (TemplateJson.AsInt64(schema["minValue"]) is { } min && number < min)
            {
                return $"{Where(at)} is below minValue ({min.ToString(CultureInfo.InvariantCulture)})";
            }

            if (TemplateJson.AsInt64(schema["maxValue"]) is { } max && number > max)
            {
                return $"{Where(at)} is above maxValue ({max.ToString(CultureInfo.InvariantCulture)})";
            }
        }

        return null;
    }

    private static string? ObjectViolation(JsonObject schema, JsonObject value, IReadOnlyDictionary<string, JsonObject> definitions, string at, int depth)
    {
        if (schema["discriminator"] is JsonObject discriminator && TemplateJson.AsString(discriminator["propertyName"]) is { } tagName)
        {
            string? tag = value.TryGetPropertyValue(tagName, out var tagNode) ? TemplateJson.AsString(tagNode) : null;
            if (tag is null)
            {
                return $"the discriminator property {Where(at + "." + tagName)} is missing or not a string";
            }

            if (discriminator["mapping"] is not JsonObject mapping || !mapping.TryGetPropertyValue(tag, out var variant) || variant is not JsonObject variantType)
            {
                return $"the discriminator property {Where(at + "." + tagName)} has a value that matches no variant of the type";
            }

            if (Nested(variantType, value, present: true, definitions, at, depth) is { } variantViolation)
            {
                return variantViolation;
            }
        }

        var declared = schema["properties"] as JsonObject;
        if (declared is not null)
        {
            foreach (var (name, propertyType) in declared)
            {
                bool present = value.TryGetPropertyValue(name, out var propertyValue);
                if (Nested(propertyType, propertyValue, present, definitions, at + "." + name, depth) is { } violation)
                {
                    return violation;
                }
            }
        }

        var additional = schema["additionalProperties"];
        bool sealedType = TemplateJson.AsBool(schema["sealed"]) == true || TemplateJson.AsBool(additional) == false;
        foreach (var (name, propertyValue) in value)
        {
            if (declared is not null && declared.ContainsKey(name))
            {
                continue;
            }

            if (schema["discriminator"] is JsonObject d && string.Equals(TemplateJson.AsString(d["propertyName"]), name, StringComparison.Ordinal))
            {
                continue;
            }

            if (sealedType && schema["discriminator"] is null)
            {
                return $"the property {Where(at + "." + name)} is not declared by the type (additionalProperties is false)";
            }

            if (additional is JsonObject additionalType && Nested(additionalType, propertyValue, present: true, definitions, at + "." + name, depth) is { } violation)
            {
                return violation;
            }
        }

        return null;
    }

    private static string? ArrayViolation(JsonObject schema, JsonArray value, IReadOnlyDictionary<string, JsonObject> definitions, string at, int depth)
    {
        int prefix = 0;
        if (schema["prefixItems"] is JsonArray prefixItems)
        {
            prefix = prefixItems.Count;
            for (int i = 0; i < prefixItems.Count; i++)
            {
                string element = at + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                if (i >= value.Count)
                {
                    if (prefixItems[i] is JsonObject itemType && !TypeReferences.IsNullable(TypeReferences.Resolve(itemType, definitions, out _)))
                    {
                        return $"the element {Where(element)} required by prefixItems is missing";
                    }

                    continue;
                }

                if (Nested(prefixItems[i], value[i], present: true, definitions, element, depth) is { } violation)
                {
                    return violation;
                }
            }
        }

        var items = schema["items"];
        if (TemplateJson.AsBool(items) == false && value.Count > prefix)
        {
            return $"{Where(at)} has more elements than its prefixItems allow (items is false)";
        }

        if (items is JsonObject itemsType)
        {
            for (int i = prefix; i < value.Count; i++)
            {
                if (Nested(itemsType, value[i], present: true, definitions, at + "[" + i.ToString(CultureInfo.InvariantCulture) + "]", depth) is { } violation)
                {
                    return violation;
                }
            }
        }

        return null;
    }

    private static string Where(string at)
    {
        return at.Length == 0 ? "the value" : "'" + at.TrimStart('.') + "'";
    }

    private static bool AllAllowed(JsonArray elements, JsonArray allowed)
    {
        foreach (var element in elements)
        {
            if (!IsAllowed(element, allowed))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAllowed(JsonNode? value, JsonArray allowed)
    {
        foreach (var candidate in allowed)
        {
            if (JsonNode.DeepEquals(candidate, value))
            {
                return true;
            }
        }

        return false;
    }
}
