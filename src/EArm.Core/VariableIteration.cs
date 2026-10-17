using System.Text.Json.Nodes;
using EArm.Core.Json;

namespace EArm.Core;

/// <summary>
/// One entry of the top-level <c>"copy": [ { "name", "count", "input" } ]</c> array of <c>variables</c> (ARM variable iteration): the variable
/// <see cref="Name"/> is an array of <see cref="Count"/> elements, each <see cref="Input"/> evaluated with <c>copyIndex('name')</c> bound to its
/// position. See https://learn.microsoft.com/azure/azure-resource-manager/templates/copy-variables.
/// </summary>
public sealed record VariableIteration
{
    public required string Name { get; init; }

    public JsonNode? Count { get; init; }

    public JsonNode? Input { get; init; }

    /// <summary>The entry object as authored.</summary>
    public required JsonObject Source { get; init; }

    /// <summary>Where the entry sits, for example <c>variables.copy[0]</c>.</summary>
    public required JsonPath Path { get; init; }
}
