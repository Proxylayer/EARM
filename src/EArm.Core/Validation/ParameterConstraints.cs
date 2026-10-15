using System;
using System.Text.Json.Nodes;
using EArm.Core.Json;
using EArm.Core.Secrets;

namespace EArm.Core.Validation;

/// <summary>
/// The constraints of a template parameter (<c>allowedValues</c>, <c>minValue</c>/<c>maxValue</c>, <c>minLength</c>/<c>maxLength</c>) checked against a
/// value. Secure parameters are checked too, on the revealed value; the messages never contain the value (nor an allowed value), only the name of the
/// parameter and the constraint. A constraint that is an expression is ignored here (the semantic checker rejects it: ARM allows
/// expressions only in <c>defaultValue</c>). For an array parameter, <c>allowedValues</c> lists the values its elements may take. Used for supplied values, for
/// literal defaults, and for values resolved from Key Vault at deployment time.
/// </summary>
public static class ParameterConstraints
{
    /// <summary>
    /// A description of the first constraint <paramref name="value"/> violates, or <c>null</c> when it satisfies them all. <paramref name="value"/> may hold
    /// secure (tainted) leaves; they are compared as plain values. A supplied value is data, never an expression; a default that is an expression
    /// (<paramref name="isDefault"/>), or holds one at any depth, is not known offline and is not checked here: it is checked once evaluated.
    /// </summary>
    public static string? Violation(ParameterDefinition definition, JsonNode? value, bool isDefault = false)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.ParsedType is not { } type)
        {
            return null;
        }

        var plain = SecretJson.ContainsSecret(value) ? SecretJson.Reveal(value) : value;
        if (isDefault && TemplateJson.ContainsExpression(plain))
        {
            return null;
        }

        if (definition.AllowedValues is { } allowed && !(type == TemplateParameterType.Array && plain is JsonArray elements ? AreAllAllowed(elements, allowed) : IsAllowed(plain, allowed)))
        {
            return $"the value does not match allowedValues of parameter '{definition.Name}'";
        }

        switch (type)
        {
            case TemplateParameterType.Int:
                if (TemplateJson.AsInt64(plain) is { } number)
                {
                    if (Literal(definition.MinValue) is { } min && number < min)
                    {
                        return $"the value is below minValue ({min}) of parameter '{definition.Name}'";
                    }

                    if (Literal(definition.MaxValue) is { } max && number > max)
                    {
                        return $"the value is above maxValue ({max}) of parameter '{definition.Name}'";
                    }
                }

                break;
            case TemplateParameterType.String:
            case TemplateParameterType.SecureString:
                if (TemplateJson.AsString(plain) is { } text)
                {
                    return LengthViolation(definition, text.Length);
                }

                break;
            case TemplateParameterType.Array:
                if (plain is JsonArray array)
                {
                    return LengthViolation(definition, array.Count);
                }

                break;
        }

        return null;
    }

    private static string? LengthViolation(ParameterDefinition definition, int length)
    {
        if (Literal(definition.MinLength) is { } min && length < min)
        {
            return $"the value is shorter than minLength ({min}) of parameter '{definition.Name}'";
        }

        if (Literal(definition.MaxLength) is { } max && length > max)
        {
            return $"the value is longer than maxLength ({max}) of parameter '{definition.Name}'";
        }

        return null;
    }

    private static long? Literal(JsonNode? node)
    {
        return node is null || TemplateJson.IsExpression(node) ? null : TemplateJson.AsInt64(node);
    }

    /// <summary>ARM/Bicep rule for an array parameter: every element must be one of the allowed values (the value is a subset of them).</summary>
    private static bool AreAllAllowed(JsonArray elements, JsonArray allowed)
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
