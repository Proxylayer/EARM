using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Json;

namespace EArm.Core;

/// <summary>
/// One entry of the template's <c>parameters</c> object, as authored (values are not evaluated).
/// </summary>
public sealed record ParameterDefinition
{
    public required string Name { get; init; }

    /// <summary>The declared type text (<c>string</c>, <c>securestring</c>, ...), or <c>null</c> when missing; for a <c>$ref</c>, the referenced type's.</summary>
    public string? Type { get; init; }

    /// <summary>
    /// The effective type node (languageVersion 2.0): the definition itself, or with a <c>$ref</c> the referenced user-defined type with the local
    /// keys laid over it. Carries the aggregate constraints (<c>properties</c>, <c>items</c>, <c>prefixItems</c>, <c>additionalProperties</c>,
    /// <c>discriminator</c>, <c>nullable</c>). <c>null</c> when a <c>$ref</c> cannot be resolved (<see cref="TypeProblem"/>).
    /// </summary>
    public JsonObject? TypeSchema { get; init; }

    /// <summary>Why the <c>$ref</c> of the definition could not be resolved, or <c>null</c>.</summary>
    public string? TypeProblem { get; init; }

    /// <summary>The template's <c>definitions</c>, against which nested <c>$ref</c>s of <see cref="TypeSchema"/> resolve.</summary>
    public IReadOnlyDictionary<string, JsonObject> TypeDefinitions { get; init; } = new Dictionary<string, JsonObject>();

    /// <summary>True for <c>"nullable": true</c> (languageVersion 2.0): the value may be null or omitted.</summary>
    public bool IsNullable => TypeReferences.IsNullable(TypeSchema);

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
