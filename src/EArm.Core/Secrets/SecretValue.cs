using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace EArm.Core.Secrets;

/// <summary>
/// A secure (tainted) value. It renders as <c>***</c> everywhere by default: <see cref="ToString"/>, the
/// <see cref="RedactingJsonConverter"/> applied through the attribute, and canonical JSON. Only
/// <see cref="RevealingJsonConverter"/> / <see cref="SecretJson.Reveal"/> expose <see cref="Value"/>.
/// </summary>
/// <param name="Value">
/// The protected text. For <see cref="JsonValueKind.String"/> it is the raw string; for any other kind it is the
/// JSON text of the leaf (for example <c>42</c> or <c>true</c>) so that non-string leaves of a <c>secureObject</c>
/// keep their type when revealed.
/// </param>
/// <param name="Kind">The JSON kind of the revealed value.</param>
[JsonConverter(typeof(RedactingJsonConverter))]
public sealed record SecretValue(string Value, JsonValueKind Kind = JsonValueKind.String)
{
    /// <summary>The text written wherever a secret is rendered without revealing it.</summary>
    public const string Mask = "***";

    public override string ToString()
    {
        return Mask;
    }

    /// <summary>Builds the plain <see cref="JsonNode"/> this secret stands for.</summary>
    public JsonNode? ToRevealedNode()
    {
        return Kind == JsonValueKind.String ? JsonValue.Create(Value) : JsonNode.Parse(Value);
    }

    /// <summary>
    /// Wraps a JSON leaf. Strings keep their text; other kinds keep their JSON text and kind.
    /// Returns <c>null</c> for <c>null</c> (there is nothing to protect).
    /// </summary>
    public static SecretValue? FromLeaf(JsonNode? leaf)
    {
        if (leaf is null)
        {
            return null;
        }

        if (leaf is not JsonValue value)
        {
            throw new ArgumentException("Only JSON leaves (string, number, boolean) can be wrapped as a secret.", nameof(leaf));
        }

        if (SecretJson.IsSecret(value, out var existing))
        {
            return existing;
        }

        var kind = value.GetValueKind();
        if (kind == JsonValueKind.String)
        {
            return new SecretValue(value.GetValue<string>(), JsonValueKind.String);
        }

        return new SecretValue(value.ToJsonString(), kind);
    }
}
