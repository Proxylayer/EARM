using System.Text.Json.Nodes;
using EArm.Core.Json;
using EArm.Core.Secrets;
using EArm.Expressions.Values;

namespace EArm.Engine.State;

/// <summary>
/// <c>contentHash</c> of a state entry (plan section 1.7): SHA-256 of the canonical JSON (sorted keys, no whitespace,
/// UTF-8) of the evaluated properties with every secret and deploy-time-unknown leaf removed, so the hash never
/// depends on a secret and never changes because a placeholder was rendered differently.
/// </summary>
public static class StateHashing
{
    public const string Prefix = "sha256:";

    public static string ContentHash(JsonNode? properties)
    {
        return Prefix + CanonicalJson.Hash(Strip(properties));
    }

    /// <summary>A deep copy without secret or unknown leaves (object members and array elements alike).</summary>
    public static JsonNode? Strip(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                {
                    var copy = new JsonObject();
                    foreach (var property in obj)
                    {
                        if (IsHidden(property.Value))
                        {
                            continue;
                        }

                        copy[property.Key] = Strip(property.Value);
                    }

                    return copy;
                }

            case JsonArray array:
                {
                    var copy = new JsonArray();
                    foreach (var item in array)
                    {
                        if (IsHidden(item))
                        {
                            continue;
                        }

                        copy.Add(Strip(item));
                    }

                    return copy;
                }

            default:
                return node?.DeepClone();
        }
    }

    private static bool IsHidden(JsonNode? node)
    {
        return node is JsonValue && (SecretJson.IsSecret(node) || UnknownJson.IsUnknown(node));
    }
}
