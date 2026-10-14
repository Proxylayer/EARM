using System.Text.Json.Serialization;

namespace EntraArm.Core.Secrets;

/// <summary>
/// Source-generated metadata for <see cref="SecretValue"/> so secrets can be embedded in a
/// <see cref="System.Text.Json.Nodes.JsonNode"/> tree without reflection-based serialization.
/// </summary>
[JsonSerializable(typeof(SecretValue))]
internal sealed partial class SecretJsonContext : JsonSerializerContext
{
}
