using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core;

namespace EArm.Expressions.Static;

/// <summary>How far a resource's <c>name</c> could be evaluated offline.</summary>
public enum ResolvedNameKind
{
    /// <summary>The name is a literal or evaluates from parameters/variables; <see cref="ResolvedResource.Id"/> is concrete.</summary>
    Static,

    /// <summary>
    /// The name depends on a deploy-time value (<c>reference()</c>, <c>tenant()</c>, an Entra lookup, an offline
    /// parameter); <see cref="ResolvedResource.Id"/> is symbolic: its name segment is the
    /// <see cref="Values.UnknownValue.Symbol"/>, so other expressions that compute the same value match it.
    /// The engine must re-evaluate the name once its inputs are known.
    /// </summary>
    Symbolic,

    /// <summary>The name depends on <c>copyIndex()</c>; the engine expands the loop (WI-05). No id.</summary>
    CopyDependent,

    /// <summary>The name could not be evaluated (a diagnostic was reported). No id.</summary>
    Invalid,
}

public enum DependencySource
{
    /// <summary>An entry of the resource's <c>dependsOn</c>.</summary>
    Explicit,

    /// <summary>The parent dependency the loader inferred for a nested child.</summary>
    Inferred,
}

/// <summary>
/// One evaluated <c>dependsOn</c> entry. Exactly one of <see cref="Target"/> (a resource), <see cref="CopyName"/>
/// (every instance of a loop, ARM idiom) or <see cref="Deferred"/> (depends on <c>copyIndex()</c>, resolved per
/// iteration by the engine; or, offline only, solely on a parameter without a value) is set when the entry is valid.
/// </summary>
public sealed record ResolvedDependency(string Text, DependencySource Source, ResourceIdentifier? Target, string? CopyName, bool Deferred)
{
    public bool IsResolved => Target is not null || CopyName is not null;
}

/// <summary>A resource after static evaluation of its keys (plan section 1.5, phase 4).</summary>
public sealed record ResolvedResource
{
    public required ResourceDeclaration Declaration { get; init; }

    public required ResolvedNameKind NameKind { get; init; }

    /// <summary>Concrete for <see cref="ResolvedNameKind.Static"/>, symbolic for <see cref="ResolvedNameKind.Symbolic"/>, otherwise <c>null</c>.</summary>
    public ResourceIdentifier? Id { get; init; }

    /// <summary>The evaluated name text when static.</summary>
    public string? Name { get; init; }

    /// <summary>The raw evaluated name (an unknown leaf for symbolic/copy-dependent names).</summary>
    public JsonNode? NameValue { get; init; }

    /// <summary>The static value of <c>condition</c>; <c>null</c> when absent or only known at deploy time.</summary>
    public bool? Condition { get; init; }

    /// <summary>True when the resource has a <c>condition</c> whose value is only known at deploy time.</summary>
    public bool ConditionIsDeployTime { get; init; }

    public string? CopyName { get; init; }

    /// <summary>The static <c>copy.count</c>; <c>null</c> when there is no copy or the count is only known at deploy time.</summary>
    public long? CopyCount { get; init; }

    public IReadOnlyList<ResolvedDependency> DependsOn { get; init; } = [];

    /// <summary>
    /// <c>reference()</c>/<c>resourceId()</c> occurrences inside <c>properties</c> (statically resolved). The engine
    /// turns same-template <c>reference()</c> occurrences outside the edge properties of plan section 1.4 into
    /// implicit dependencies.
    /// </summary>
    public IReadOnlyList<ReferenceOccurrence> References { get; init; } = [];

    public bool HasCopy => Declaration.Copy is not null;

    public bool IsStatic => NameKind == ResolvedNameKind.Static;
}

/// <summary>An output after static evaluation; <see cref="Value"/> may contain unknown leaves.</summary>
public sealed record ResolvedOutput(OutputDefinition Definition, JsonNode? Value, bool? Condition, bool IsTainted);
