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

    /// <summary>
    /// True for the template of a nested or linked deployment (<see cref="Types.DeploymentResourceType"/>): its <c>$schema</c> may also be ARM's
    /// <c>deploymentTemplate.json</c>, so ARM snippets paste in.
    /// </summary>
    public bool IsNested { get; init; }

    public string? Schema { get; init; }

    public string? ContentVersion { get; init; }

    public JsonObject? Metadata { get; init; }

    /// <summary>The <c>languageVersion</c> text, or <c>null</c> when the template has none (language version 1).</summary>
    public string? LanguageVersion { get; init; }

    /// <summary>
    /// True when <c>resources</c> is an object keyed by symbolic name (languageVersion 2.0): <c>dependsOn</c> and <c>reference()</c> then name
    /// resources by symbolic name (see <see cref="ResourceDeclaration.SymbolicName"/>).
    /// </summary>
    public bool UsesSymbolicNames { get; init; }

    /// <summary>The languageVersion 2.0 user-defined types of <c>definitions</c>, keyed by name (case-sensitive), as authored.</summary>
    public IReadOnlyDictionary<string, JsonObject> Definitions { get; init; } = new Dictionary<string, JsonObject>();

    /// <summary>Parameter definitions keyed by name (case-insensitive).</summary>
    public IReadOnlyDictionary<string, ParameterDefinition> Parameters { get; init; } = new Dictionary<string, ParameterDefinition>();

    /// <summary>Variables keyed by name (case-insensitive), unevaluated.</summary>
    public IReadOnlyDictionary<string, JsonNode?> Variables { get; init; } = new Dictionary<string, JsonNode?>();

    /// <summary>
    /// The variables built by the top-level <c>copy</c> array of <c>variables</c> (variable iteration), keyed by name (case-insensitive). Each also
    /// appears in <see cref="Variables"/>, with its entry object as the definition; evaluators build the array from the iteration instead.
    /// </summary>
    public IReadOnlyDictionary<string, VariableIteration> VariableIterations { get; init; } = new Dictionary<string, VariableIteration>();

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

    /// <summary>The size of the template text in UTF-8 bytes, for the ARM template size limit.</summary>
    public long SourceByteLength { get; init; }

    /// <summary>The top-level keys present in the file, in document order.</summary>
    public IReadOnlyList<string> TopLevelKeys { get; init; } = [];

    /// <summary>Resolves the location of a path, falling back to the nearest ancestor that has one.</summary>
    public SourceLocation? LocationOf(JsonPath? path)
    {
        return path is null ? null : SourceMap.FindNearest(path);
    }
}
