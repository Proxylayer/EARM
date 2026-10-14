using System.Text.Json.Nodes;
using EntraArm.Core.Diagnostics;
using EntraArm.Core.Json;

namespace EntraArm.Core;

/// <summary>
/// One entry of the template's <c>parameters</c> object, as authored (values are not evaluated).
/// </summary>
public sealed record ParameterDefinition
{
    public required string Name { get; init; }

    /// <summary>The declared type text (<c>string</c>, <c>securestring</c>, ...), or <c>null</c> when missing.</summary>
    public string? Type { get; init; }

    public JsonNode? DefaultValue { get; init; }

    public bool HasDefaultValue { get; init; }

    public JsonArray? AllowedValues { get; init; }

    public JsonNode? MinValue { get; init; }

    public JsonNode? MaxValue { get; init; }

    public JsonNode? MinLength { get; init; }

    public JsonNode? MaxLength { get; init; }

    public JsonObject? Metadata { get; init; }

    /// <summary>The raw definition object.</summary>
    public required JsonObject Source { get; init; }

    public required JsonPath Path { get; init; }

    public SourceLocation? Location { get; init; }

    /// <summary>The parsed type, or <c>null</c> when <see cref="Type"/> is missing or unknown.</summary>
    public TemplateParameterType? ParsedType => TemplateParameterTypes.TryParse(Type, out var type) ? type : null;

    public bool IsSecure => ParsedType is { } type && TemplateParameterTypes.IsSecure(type);
}
