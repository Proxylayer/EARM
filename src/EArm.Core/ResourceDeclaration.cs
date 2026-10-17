using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Json;

namespace EArm.Core;

/// <summary>
/// A resource as declared in the template, after the loader has flattened nested children to top-level
/// declarations with full types and <c>parent/child</c> names. Nothing is evaluated: <see cref="Name"/>,
/// <see cref="Condition"/>, <see cref="Properties"/> and <see cref="DependsOn"/> may contain expressions.
/// The <see cref="JsonNode"/> members are shared with the document tree and must be treated as read-only.
/// </summary>
public sealed record ResourceDeclaration
{
    /// <summary>Full resource type (<c>Microsoft.Entra/...</c>), canonical casing when the catalog knows it; <c>null</c> when missing.</summary>
    public string? Type { get; init; }

    /// <summary>
    /// The languageVersion 2.0 symbolic name: the key of the declaration in the <c>resources</c> object (case-sensitive); <c>null</c> in a
    /// template whose <c>resources</c> is an array, and for nested children.
    /// </summary>
    public string? SymbolicName { get; init; }

    /// <summary>
    /// True for <c>"existing": true</c> (languageVersion 2.0): the resource is read, never created, updated or deleted; <c>reference()</c> reads it.
    /// </summary>
    public bool IsExisting { get; init; }

    /// <summary>The <c>type</c> text as authored (the short type for nested children).</summary>
    public string? DeclaredType { get; init; }

    public string? ApiVersion { get; init; }

    /// <summary>The ARM-style name (<c>parent/child</c> for children); literal or expression; <c>null</c> when missing.</summary>
    public string? Name { get; init; }

    /// <summary>The <c>name</c> text as authored (the child's own name for nested children).</summary>
    public string? DeclaredName { get; init; }

    /// <summary>The identity when <see cref="Name"/> is a literal with the segment count the type expects; otherwise <c>null</c>.</summary>
    public ResourceIdentifier? StaticId { get; init; }

    public JsonNode? Properties { get; init; }

    /// <summary>Explicit <c>dependsOn</c> entries as authored (resource ids, <c>type/name</c>, bare names, or expressions).</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>
    /// Dependencies the loader inferred: for a nested child, the <c>[resourceId(...)]</c> expression of its parent.
    /// A top-level <c>parent/child</c> declaration must list its parent in <see cref="DependsOn"/> itself (ARM rule).
    /// </summary>
    public IReadOnlyList<string> InferredDependsOn { get; init; } = [];

    public JsonNode? Condition { get; init; }

    public CopyDeclaration? Copy { get; init; }

    public string? Comments { get; init; }

    public JsonObject? Metadata { get; init; }

    /// <summary>The raw resource object in the document tree (including any nested <c>resources</c>).</summary>
    public required JsonObject Source { get; init; }

    /// <summary>Where the declaration sits in the file, for example <c>resources[0].resources[1]</c>.</summary>
    public required JsonPath Path { get; init; }

    public SourceLocation? Location { get; init; }

    /// <summary>1 for a top-level declaration, 2 for a nested child.</summary>
    public int NestingDepth { get; init; } = 1;

    /// <summary>The full type of the enclosing declaration when this one was nested; otherwise <c>null</c>.</summary>
    public string? NestedParentType { get; init; }

    /// <summary>The path of the enclosing declaration when this one was nested; otherwise <c>null</c>.</summary>
    public JsonPath? NestedParentPath { get; init; }

    public bool IsNested => NestedParentPath is not null;

    public bool HasLiteralName => Name is not null && !ExpressionSyntax.IsExpression(Name);

    /// <summary>A short description for messages: <c>type 'name'</c> or the path when either is missing.</summary>
    public string Describe()
    {
        if (Type is not null && Name is not null)
        {
            return $"{Type} '{Name}'";
        }

        return "resource at " + Path;
    }
}

/// <summary>The <c>copy</c> block of a resource, unevaluated.</summary>
public sealed record CopyDeclaration
{
    public JsonNode? Name { get; init; }

    public JsonNode? Count { get; init; }

    public JsonNode? Mode { get; init; }

    public JsonNode? BatchSize { get; init; }

    public required JsonObject Source { get; init; }

    public required JsonPath Path { get; init; }
}
