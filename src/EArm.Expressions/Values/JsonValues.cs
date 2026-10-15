using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Core.Secrets;

namespace EArm.Expressions.Values;

/// <summary>
/// <see cref="JsonNode"/> helpers the evaluator needs: cloning that preserves secret and unknown leaves
/// (<see cref="JsonNode.DeepClone"/> loses both), numeric access across element- and CLR-backed values, and
/// value equality with numeric normalisation.
/// </summary>
public static class JsonValues
{
    /// <summary>Deep-copies a tree preserving secret and unknown leaves. Use instead of <see cref="JsonNode.DeepClone"/>.</summary>
    public static JsonNode? Clone(JsonNode? node)
    {
        return UnknownJson.Transform(node, CloneLeaf);
    }

    internal static JsonNode? CloneLeaf(JsonNode? leaf)
    {
        if (leaf is null)
        {
            return null;
        }

        if (SecretJson.IsSecret(leaf, out var secret))
        {
            return SecretJson.Create(secret);
        }

        if (UnknownJson.IsUnknown(leaf, out var unknown))
        {
            return UnknownJson.Create(unknown);
        }

        return leaf.DeepClone();
    }

    /// <summary>A short kind name for messages: string, integer, number, boolean, object, array, null, secret, unknown.</summary>
    public static string Describe(JsonNode? node)
    {
        if (node is null)
        {
            return "null";
        }

        if (SecretJson.IsSecret(node))
        {
            return "secure value";
        }

        if (UnknownJson.IsUnknown(node))
        {
            return "deploy-time value";
        }

        return node switch
        {
            JsonObject => "object",
            JsonArray => "array",
            JsonValue value => value.GetValueKind() switch
            {
                JsonValueKind.String => "string",
                JsonValueKind.Number => TryGetInt64(value, out _) ? "integer" : "number",
                JsonValueKind.True or JsonValueKind.False => "boolean",
                JsonValueKind.Null => "null",
                _ => "value",
            },
            _ => "value",
        };
    }

    public static bool IsNumber(JsonNode? node)
    {
        return node is JsonValue value && !SecretJson.IsSecret(value) && !UnknownJson.IsUnknown(value) && value.GetValueKind() == JsonValueKind.Number;
    }

    public static bool IsString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out _);
    }

    public static bool TryGetString(JsonNode? node, out string text)
    {
        text = string.Empty;
        if (node is JsonValue value && value.TryGetValue<string>(out var found))
        {
            text = found;
            return true;
        }

        return false;
    }

    public static bool TryGetBool(JsonNode? node, out bool result)
    {
        result = false;
        if (node is not JsonValue value || SecretJson.IsSecret(value) || UnknownJson.IsUnknown(value))
        {
            return false;
        }

        switch (value.GetValueKind())
        {
            case JsonValueKind.True:
                result = true;
                return true;
            case JsonValueKind.False:
                return true;
            default:
                return false;
        }
    }

    /// <summary>Reads an integral number (element-backed or CLR-backed); a double with no fraction counts.</summary>
    public static bool TryGetInt64(JsonNode? node, out long result)
    {
        result = 0;
        if (node is not JsonValue value || SecretJson.IsSecret(value) || UnknownJson.IsUnknown(value))
        {
            return false;
        }

        if (value.TryGetValue<JsonElement>(out var element))
        {
            return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out result);
        }

        if (value.TryGetValue<long>(out result))
        {
            return true;
        }

        if (value.TryGetValue<int>(out int i))
        {
            result = i;
            return true;
        }

        if (value.TryGetValue<double>(out double d))
        {
            if (Math.Floor(d) == d && Math.Abs(d) < 9007199254740992d)
            {
                result = (long)d;
                return true;
            }

            return false;
        }

        if (value.TryGetValue<decimal>(out decimal m))
        {
            if (decimal.Truncate(m) == m && m >= long.MinValue && m <= long.MaxValue)
            {
                result = (long)m;
                return true;
            }

            return false;
        }

        if (value.TryGetValue<short>(out short s))
        {
            result = s;
            return true;
        }

        if (value.TryGetValue<byte>(out byte b))
        {
            result = b;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Reads a whole number by its exact value, however it was written or created (<c>3</c>, <c>3.0</c>, <c>1.5e3</c>, a CLR double):
    /// the rule the template checker and the parameter type check use (<see cref="EArm.Core.Json.TemplateJson.AsInt64"/>). Used for
    /// loop sizes (<c>copy.count</c>, <c>copy.batchSize</c>), so a count computed from an <c>int</c> parameter supplied as <c>3.0</c> expands
    /// like the literal <c>3</c>. Arithmetic keeps using <see cref="TryGetInt64"/>, where a fractional-syntax number stays a float
    /// (<c>div(7.0, 2)</c> is <c>3.5</c>).
    /// </summary>
    public static bool TryGetWholeNumber(JsonNode? node, out long result)
    {
        result = 0;
        if (node is not JsonValue value || SecretJson.IsSecret(value) || UnknownJson.IsUnknown(value))
        {
            return false;
        }

        long? whole = EArm.Core.Json.TemplateJson.AsInt64(value);
        result = whole.GetValueOrDefault();
        return whole is not null;
    }

    /// <summary>Reads any number as a double.</summary>
    public static bool TryGetDouble(JsonNode? node, out double result)
    {
        result = 0;
        if (node is not JsonValue value || SecretJson.IsSecret(value) || UnknownJson.IsUnknown(value))
        {
            return false;
        }

        if (value.TryGetValue<JsonElement>(out var element))
        {
            return element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out result);
        }

        if (value.TryGetValue<double>(out result))
        {
            return true;
        }

        if (TryGetInt64(value, out long l))
        {
            result = l;
            return true;
        }

        if (value.TryGetValue<float>(out float f))
        {
            result = f;
            return true;
        }

        if (value.TryGetValue<decimal>(out decimal m))
        {
            result = (double)m;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Structural equality: numbers compare by value (<c>1</c> equals <c>1.0</c>), strings ordinally, objects by
    /// property set (order-insensitive), arrays element-wise. Secret leaves compare by revealed value; unknown
    /// leaves compare by symbol.
    /// </summary>
    public static bool AreEqual(JsonNode? left, JsonNode? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        bool leftIsSecret = SecretJson.IsSecret(left, out var leftSecret);
        bool rightIsSecret = SecretJson.IsSecret(right, out var rightSecret);
        if (leftIsSecret || rightIsSecret)
        {
            return leftSecret is not null && rightSecret is not null && leftSecret.Kind == rightSecret.Kind && string.Equals(leftSecret.Value, rightSecret.Value, StringComparison.Ordinal);
        }

        bool leftIsUnknown = UnknownJson.IsUnknown(left, out var leftUnknown);
        bool rightIsUnknown = UnknownJson.IsUnknown(right, out var rightUnknown);
        if (leftIsUnknown || rightIsUnknown)
        {
            return leftUnknown is not null && rightUnknown is not null && string.Equals(leftUnknown.Symbol, rightUnknown.Symbol, StringComparison.Ordinal);
        }

        switch (left)
        {
            case JsonObject leftObject:
                {
                    if (right is not JsonObject rightObject || leftObject.Count != rightObject.Count)
                    {
                        return false;
                    }

                    foreach (var property in leftObject)
                    {
                        if (!rightObject.TryGetPropertyValue(property.Key, out var other) || !AreEqual(property.Value, other))
                        {
                            return false;
                        }
                    }

                    return true;
                }

            case JsonArray leftArray:
                {
                    if (right is not JsonArray rightArray || leftArray.Count != rightArray.Count)
                    {
                        return false;
                    }

                    for (int i = 0; i < leftArray.Count; i++)
                    {
                        if (!AreEqual(leftArray[i], rightArray[i]))
                        {
                            return false;
                        }
                    }

                    return true;
                }

            case JsonValue leftValue:
                {
                    if (right is not JsonValue rightValue)
                    {
                        return false;
                    }

                    var leftKind = leftValue.GetValueKind();
                    var rightKind = rightValue.GetValueKind();
                    if (leftKind == JsonValueKind.Number && rightKind == JsonValueKind.Number)
                    {
                        if (TryGetInt64(leftValue, out long l) && TryGetInt64(rightValue, out long r))
                        {
                            return l == r;
                        }

                        return TryGetDouble(leftValue, out double dl) && TryGetDouble(rightValue, out double dr) && dl == dr;
                    }

                    if (leftKind != rightKind)
                    {
                        return false;
                    }

                    return leftKind switch
                    {
                        JsonValueKind.String => string.Equals(leftValue.GetValue<string>(), rightValue.GetValue<string>(), StringComparison.Ordinal),
                        JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
                        _ => JsonNode.DeepEquals(leftValue, rightValue),
                    };
                }

            default:
                return false;
        }
    }

    /// <summary>Renders a leaf for use in canonical symbols and messages (strings quoted, numbers invariant).</summary>
    public static string Render(JsonNode? node)
    {
        if (node is null)
        {
            return "null";
        }

        if (SecretJson.IsSecret(node))
        {
            return SecretValue.Mask;
        }

        if (UnknownJson.IsUnknown(node, out var unknown))
        {
            return unknown.Symbol;
        }

        if (TryGetString(node, out var text))
        {
            return "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
        }

        if (TryGetInt64(node, out long l))
        {
            return l.ToString(CultureInfo.InvariantCulture);
        }

        if (TryGetDouble(node, out double d))
        {
            return d.ToString("R", CultureInfo.InvariantCulture);
        }

        return node.ToJsonString();
    }
}
