using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core.Json;

namespace EArm.Core;

/// <summary>One entry of the template's <c>functions</c> array: a namespace with its user-defined functions.</summary>
public sealed record UserFunctionNamespace
{
    public string? Namespace { get; init; }

    /// <summary>Functions keyed by name (case-insensitive).</summary>
    public required IReadOnlyDictionary<string, UserFunctionDefinition> Members { get; init; }

    public required JsonObject Source { get; init; }

    public required JsonPath Path { get; init; }
}

/// <summary>A user-defined function, unevaluated.</summary>
public sealed record UserFunctionDefinition
{
    public required string Name { get; init; }

    public IReadOnlyList<UserFunctionParameter> Parameters { get; init; } = [];

    public string? OutputType { get; init; }

    public JsonNode? OutputValue { get; init; }

    public required JsonObject Source { get; init; }

    public required JsonPath Path { get; init; }
}

public sealed record UserFunctionParameter(string? Name, string? Type);
