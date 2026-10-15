using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Json;

namespace EArm.Core;

/// <summary>One entry of the template's <c>outputs</c> object, unevaluated.</summary>
public sealed record OutputDefinition
{
    public required string Name { get; init; }

    public string? Type { get; init; }

    public JsonNode? Value { get; init; }

    public JsonNode? Condition { get; init; }

    public JsonObject? Metadata { get; init; }

    public required JsonObject Source { get; init; }

    public required JsonPath Path { get; init; }

    public SourceLocation? Location { get; init; }

    public TemplateParameterType? ParsedType => TemplateParameterTypes.TryParse(Type, out var type) ? type : null;

    public bool IsSecure => ParsedType is { } type && TemplateParameterTypes.IsSecure(type);
}
