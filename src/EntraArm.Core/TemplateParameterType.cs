using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using EntraArm.Core.Secrets;

namespace EntraArm.Core;

/// <summary>The ARM parameter and output types.</summary>
public enum TemplateParameterType
{
    String,
    SecureString,
    Int,
    Bool,
    Object,
    SecureObject,
    Array,
}

public static class TemplateParameterTypes
{
    /// <summary>Canonical spellings, as written in templates.</summary>
    public static readonly string[] CanonicalNames = ["string", "securestring", "int", "bool", "object", "secureObject", "array"];

    /// <summary>Parses a type name case-insensitively.</summary>
    public static bool TryParse(string? text, out TemplateParameterType type)
    {
        type = TemplateParameterType.String;
        if (text is null)
        {
            return false;
        }

        switch (text.Trim().ToLowerInvariant())
        {
            case "string":
                type = TemplateParameterType.String;
                return true;
            case "securestring":
                type = TemplateParameterType.SecureString;
                return true;
            case "int":
                type = TemplateParameterType.Int;
                return true;
            case "bool":
                type = TemplateParameterType.Bool;
                return true;
            case "object":
                type = TemplateParameterType.Object;
                return true;
            case "secureobject":
                type = TemplateParameterType.SecureObject;
                return true;
            case "array":
                type = TemplateParameterType.Array;
                return true;
            default:
                return false;
        }
    }

    public static string CanonicalName(TemplateParameterType type)
    {
        return type switch
        {
            TemplateParameterType.String => "string",
            TemplateParameterType.SecureString => "securestring",
            TemplateParameterType.Int => "int",
            TemplateParameterType.Bool => "bool",
            TemplateParameterType.Object => "object",
            TemplateParameterType.SecureObject => "secureObject",
            TemplateParameterType.Array => "array",
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
    }

    public static bool IsSecure(TemplateParameterType type)
    {
        return type is TemplateParameterType.SecureString or TemplateParameterType.SecureObject;
    }

    /// <summary>
    /// True when a literal JSON value (not an expression) matches the declared type. Expressions always pass:
    /// their result type is only known after evaluation.
    /// </summary>
    public static bool Matches(JsonNode? value, TemplateParameterType type)
    {
        if (SecretJson.IsSecret(value, out var secret))
        {
            return KindMatches(secret.Kind, type);
        }

        if (value is JsonValue leaf && leaf.TryGetValue<string>(out var text) && ExpressionSyntax.IsExpression(text))
        {
            return true;
        }

        return value switch
        {
            null => false,
            JsonObject => type is TemplateParameterType.Object or TemplateParameterType.SecureObject,
            JsonArray => type == TemplateParameterType.Array,
            JsonValue v => KindMatches(v.GetValueKind(), type) && (type != TemplateParameterType.Int || IsIntegral(v)),
            _ => false,
        };
    }

    private static bool KindMatches(JsonValueKind kind, TemplateParameterType type)
    {
        return type switch
        {
            TemplateParameterType.String or TemplateParameterType.SecureString => kind == JsonValueKind.String,
            TemplateParameterType.Int => kind == JsonValueKind.Number,
            TemplateParameterType.Bool => kind is JsonValueKind.True or JsonValueKind.False,
            TemplateParameterType.Object or TemplateParameterType.SecureObject => kind == JsonValueKind.Object,
            TemplateParameterType.Array => kind == JsonValueKind.Array,
            _ => false,
        };
    }

    private static bool IsIntegral(JsonValue value)
    {
        if (value.TryGetValue<long>(out _) || value.TryGetValue<int>(out _))
        {
            return true;
        }

        if (value.TryGetValue<JsonElement>(out var element))
        {
            return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out _);
        }

        if (value.TryGetValue<double>(out var d))
        {
            return Math.Floor(d) == d;
        }

        if (value.TryGetValue<decimal>(out var m))
        {
            return decimal.Truncate(m) == m;
        }

        return false;
    }
}
