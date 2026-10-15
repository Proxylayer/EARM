using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core.Json;

namespace EArm.Core.Types;

/// <summary>
/// The <c>x-earm</c> overlay of one resource type schema (<c>schemas/2026-10-01/types/Microsoft.Entra/*.json</c>):
/// everything the executor needs to know about a type, loaded from the same file editors validate against so
/// schema and behaviour cannot drift.
/// </summary>
public sealed record ResourceTypeDefinition
{
    /// <summary>Full type in canonical casing, for example <c>Microsoft.Entra/applications</c>.</summary>
    public required string Type { get; init; }

    /// <summary>The parent type for child types; <c>null</c> for top-level types.</summary>
    public string? ParentType { get; init; }

    /// <summary>Number of name segments an instance has (1 for top-level, 2 for a child of a top-level type).</summary>
    public int Depth { get; init; } = 1;

    public string? Description { get; init; }

    /// <summary>Graph REST path; may contain <c>{parentId}</c> / <c>{resourceId}</c> placeholders.</summary>
    public required string GraphPath { get; init; }

    /// <summary><c>apiVersion</c> values accepted for this type and the Graph endpoint each maps to.</summary>
    public required IReadOnlyDictionary<string, GraphApi> ApiVersions { get; init; }

    /// <summary>The Graph property the ARM-style <c>name</c> is injected into; <c>null</c> for GUID-named (composite-key) types.</summary>
    public string? NameMapsTo { get; init; }

    public ResourceNameKind NameKind { get; init; } = ResourceNameKind.DisplayName;

    /// <summary>Regex a literal final name segment must match, when the type constrains it.</summary>
    public string? NamePattern { get; init; }

    /// <summary>Properties that identify the object in Graph (alternate key or composite key).</summary>
    public IReadOnlyList<string> KeyProperties { get; init; } = [];

    public IReadOnlyList<string> RequiredOnCreate { get; init; } = [];

    /// <summary>Property defaults the provider injects on create (dotted paths as keys).</summary>
    public JsonObject? Defaults { get; init; }

    /// <summary>Property paths that cannot change after create (error or recreate, per type).</summary>
    public IReadOnlyList<JsonPath> Immutable { get; init; } = [];

    /// <summary>True when every property is immutable (<c>"immutable": ["*"]</c>): the object is replaced, never edited.</summary>
    public bool ImmutableAll { get; init; }

    /// <summary>Property paths only sent on create (never diffed or PATCHed afterwards).</summary>
    public IReadOnlyList<JsonPath> CreateOnly { get; init; } = [];

    /// <summary>Property paths whose values are secrets (tainted on load, never logged).</summary>
    public IReadOnlyList<JsonPath> Secure { get; init; } = [];

    /// <summary>Relationship / assignment properties applied by the edge pass.</summary>
    public IReadOnlyList<RelationshipSpec> Relationships { get; init; } = [];

    /// <summary>Stable member key per array-of-objects property (<c>appRoles</c> -> <c>id</c>, <c>ipRanges</c> -> <c>cidrAddress</c>).</summary>
    public IReadOnlyDictionary<string, string> MemberKey { get; init; } = new Dictionary<string, string>();

    /// <summary>Server-managed property paths removed from live objects before diffing.</summary>
    public IReadOnlyList<JsonPath> StripOnRead { get; init; } = [];

    /// <summary>Sub-property paths Graph refuses to set to <c>null</c> (<c>false</c>) or accepts (<c>true</c>).</summary>
    public IReadOnlyDictionary<string, bool> Nullable { get; init; } = new Dictionary<string, bool>();

    public PermissionSet Permissions { get; init; } = PermissionSet.Empty;

    /// <summary>The whole schema file (properties schema plus overlay), for <c>schema print &lt;type&gt;</c>.</summary>
    public required JsonObject Schema { get; init; }

    /// <summary>The embedded resource name the definition was read from.</summary>
    public required string SchemaResourceName { get; init; }

    /// <summary>The last type segment (<c>applications</c>, <c>federatedIdentityCredentials</c>, <c>policies</c>).</summary>
    public string TypeLeaf => Type[(Type.LastIndexOf('/') + 1)..];

    public bool IsChildType => ParentType is not null;

    /// <summary>True when a relationship property of this name exists.</summary>
    public bool IsRelationshipProperty(string property)
    {
        foreach (var relationship in Relationships)
        {
            if (relationship.Property.Equals(property, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public bool TryGetGraphApi(string? apiVersion, out GraphApi api)
    {
        api = GraphApi.V1;
        return apiVersion is not null && ApiVersions.TryGetValue(apiVersion, out api);
    }

    /// <summary>The retired label of the former Graph v1.0 <c>apiVersion</c>; it was renamed <see cref="PreviewApiVersion"/> and is now an <c>UnknownApiVersion</c> error.</summary>
    public const string RetiredApiVersion = "2026-10-01";

    /// <summary>The one normal <c>apiVersion</c>; maps to Graph v1.0.</summary>
    public const string PreviewApiVersion = "2026-10-01-preview";

    /// <summary>The passthrough <c>apiVersion</c> (no normalisation guarantees); maps to Graph beta.</summary>
    public const string PreviewBetaApiVersion = "2026-10-01-preview.beta";

    /// <summary>
    /// The <c>UnknownApiVersion</c> message for <paramref name="apiVersion"/>: the rename hint for the retired
    /// <see cref="RetiredApiVersion"/> label, otherwise the list of versions this type supports.
    /// </summary>
    public string DescribeUnknownApiVersion(string apiVersion)
    {
        string valid = string.Join(", ", ApiVersions.Keys);
        if (string.Equals(apiVersion, RetiredApiVersion, StringComparison.OrdinalIgnoreCase))
        {
            return $"apiVersion '{apiVersion}' was renamed '{PreviewApiVersion}' (the format is in preview); use '{PreviewApiVersion}' (Graph v1.0) or '{PreviewBetaApiVersion}' (Graph beta). Valid: {valid}.";
        }

        return $"apiVersion '{apiVersion}' is not valid for type '{Type}'; supported: {valid}.";
    }
}
