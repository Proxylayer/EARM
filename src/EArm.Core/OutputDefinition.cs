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

    /// <summary>The output iteration (<c>"copy": { "count", "input" }</c>): the value is an array of <c>count</c> elements built from <c>input</c>.</summary>
    public OutputCopy? Copy { get; init; }

    public JsonObject? Metadata { get; init; }

    public required JsonObject Source { get; init; }

    public required JsonPath Path { get; init; }

    public SourceLocation? Location { get; init; }

    public TemplateParameterType? ParsedType => TemplateParameterTypes.TryParse(Type, out var type) ? type : null;

    public bool IsSecure => ParsedType is { } type && TemplateParameterTypes.IsSecure(type);
}

/// <summary>The <c>copy</c> block of an output, unevaluated. <c>copyIndex()</c> inside <see cref="Input"/> is the element's position.</summary>
public sealed record OutputCopy
{
    public JsonNode? Count { get; init; }

    public JsonNode? Input { get; init; }

    public required JsonObject Source { get; init; }

    public required JsonPath Path { get; init; }
}
