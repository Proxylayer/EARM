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

    /// <summary>
    /// <c>"x-earm": { "adoptOnly": true }</c> (optionally with a pinned <c>objectId</c>): the object must already exist; it is adopted and managed, never
    /// created, and <c>deployment delete</c> leaves it alone. <c>null</c> for an ordinary resource.
    /// </summary>
    public AdoptOnlyDeclaration? AdoptOnly { get; init; }

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

/// <summary>
/// A resource's adopt-only declaration, EARM's resource-level extension <c>"x-earm": { "adoptOnly": true, "objectId": "…" }</c>: the object is found by
/// the resource's key as always, but a missing object is an error instead of a create, and a pinned <see cref="ObjectId"/> (a GUID or an expression,
/// unevaluated) must be the found object's Graph id.
/// </summary>
/// <param name="ObjectId">The pinned Graph object id as declared (literal or expression); <c>null</c> when only <c>adoptOnly</c> is declared.</param>
/// <param name="Path">Where <c>x-earm</c> sits in the file.</param>
public sealed record AdoptOnlyDeclaration(JsonNode? ObjectId, JsonPath Path)
{
    /// <summary>The resource-level key.</summary>
    public const string Key = "x-earm";

    public const string AdoptOnlyProperty = "adoptOnly";

    public const string ObjectIdProperty = "objectId";
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
