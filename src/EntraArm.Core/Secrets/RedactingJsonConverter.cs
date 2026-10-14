using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EntraArm.Core.Secrets;

/// <summary>
/// Default converter for <see cref="SecretValue"/>: always writes <see cref="SecretValue.Mask"/>.
/// Reading accepts a JSON string and wraps it, so a redacted document round-trips as a (masked) secret
/// rather than failing.
/// </summary>
public sealed class RedactingJsonConverter : JsonConverter<SecretValue>
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
        writer.WriteStringValue(SecretValue.Mask);
    }
}
