using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EntraArm.Core.Secrets;

/// <summary>
/// Writes the real value of a <see cref="SecretValue"/>. Never registered by default: add it to the
/// <see cref="JsonSerializerOptions.Converters"/> of the one writer that must see secrets (Graph request bodies,
/// <c>--reveal-secure-outputs</c>). Options-level converters take precedence over the type's
/// <see cref="RedactingJsonConverter"/> attribute.
/// </summary>
public sealed class RevealingJsonConverter : JsonConverter<SecretValue>
{
    public override SecretValue? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => new SecretValue(reader.GetString()!, JsonValueKind.String),
            _ => throw new JsonException("A secret value must be a JSON string."),
        };
    }

    public override void Write(Utf8JsonWriter writer, SecretValue value, JsonSerializerOptions options)
    {
        if (value.Kind == JsonValueKind.String)
        {
            writer.WriteStringValue(value.Value);
            return;
        }

        using var document = JsonDocument.Parse(value.Value);
        document.RootElement.WriteTo(writer);
    }
}
