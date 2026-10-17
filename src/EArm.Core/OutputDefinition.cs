using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Json;

namespace EArm.Core;

/// <summary>One entry of the template's <c>outputs</c> object, unevaluated.</summary>
public sealed record OutputDefinition
{
    public required string Name { get; init; }

    /// <summary>The declared type text, or <c>null</c> when missing; for a <c>$ref</c>, the referenced type's.</summary>
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
