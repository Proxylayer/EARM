using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Json;

namespace EArm.Core;

/// <summary>
/// A loaded template: the raw tree plus the structured, unevaluated view of its envelope, with nested child
/// resources already flattened into <see cref="Resources"/>. Instances are immutable; the <see cref="JsonNode"/>
/// members are shared with <see cref="Root"/> and must not be mutated by consumers (clone before editing).
/// </summary>
public sealed record TemplateDocument
{
    /// <summary>The parsed template root (node tree).</summary>
    public required JsonObject Root { get; init; }

    /// <summary>The parsed template root as a standalone <see cref="JsonElement"/>, for JSON Schema evaluation.</summary>
    public required JsonElement RootElement { get; init; }

    /// <summary>The template file path, or <c>null</c> when parsed from text.</summary>
    public string? SourcePath { get; init; }

    public string? Schema { get; init; }

    public string? ContentVersion { get; init; }

    public JsonObject? Metadata { get; init; }

    /// <summary>Parameter definitions keyed by name (case-insensitive).</summary>
    public IReadOnlyDictionary<string, ParameterDefinition> Parameters { get; init; } = new Dictionary<string, ParameterDefinition>();

    /// <summary>Variables keyed by name (case-insensitive), unevaluated.</summary>
    public IReadOnlyDictionary<string, JsonNode?> Variables { get; init; } = new Dictionary<string, JsonNode?>();

    public IReadOnlyList<UserFunctionNamespace> Functions { get; init; } = [];

    /// <summary>All resources, flattened, in document order (a parent precedes its nested children).</summary>
    public IReadOnlyList<ResourceDeclaration> Resources { get; init; } = [];

    /// <summary>Output definitions keyed by name (case-insensitive).</summary>
    public IReadOnlyDictionary<string, OutputDefinition> Outputs { get; init; } = new Dictionary<string, OutputDefinition>();

    /// <summary>The supplied parameter values bound to <see cref="Parameters"/> (one entry per definition).</summary>
    public IReadOnlyDictionary<string, BoundParameter> ParameterValues { get; init; } = new Dictionary<string, BoundParameter>();

    /// <summary>The raw parameter set the values were bound from.</summary>
    public ParameterSet SuppliedParameters { get; init; } = ParameterSet.Empty;

    /// <summary>Non-fatal findings the loader made (nesting depth, duplicate names, unknown parameters...).</summary>
    public IReadOnlyList<Diagnostic> LoadDiagnostics { get; init; } = [];

    /// <summary>Line/column lookup for paths inside the template file.</summary>
    public SourceMap SourceMap { get; init; } = SourceMap.Empty;

    /// <summary>The top-level keys present in the file, in document order.</summary>
    public IReadOnlyList<string> TopLevelKeys { get; init; } = [];

    /// <summary>Resolves the location of a path, falling back to the nearest ancestor that has one.</summary>
    public SourceLocation? LocationOf(JsonPath? path)
    {
        return path is null ? null : SourceMap.FindNearest(path);
    }
}
