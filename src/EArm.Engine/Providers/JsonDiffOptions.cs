using System;
using System.Collections.Generic;
using EArm.Core.Json;
using EArm.Core.Types;

namespace EArm.Engine.Providers;

/// <summary>Per-type knobs of <see cref="JsonDiff"/>, usually built from the catalog overlay.</summary>
public sealed record JsonDiffOptions
{
    public static JsonDiffOptions Default { get; } = new();

    /// <summary>Stable member key per array-of-objects property (dotted path relative to <c>properties</c> → key property).</summary>
    public IReadOnlyDictionary<string, string> MemberKey { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sub-property paths Graph refuses to set to <c>null</c> (<c>false</c>): live-only values there are reported as <see cref="PropertyChangeType.NoEffect"/>.</summary>
    public IReadOnlyDictionary<string, bool> Nullable { get; init; } = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Paths (relative to <c>properties</c>) that are never compared (create-only properties on an existing object).</summary>
    public IReadOnlyList<JsonPath> Exclude { get; init; } = [];

    /// <summary>
    /// Paths (relative to <c>properties</c>) the type declares <c>secure</c>: write-only secrets Graph never returns (a password, a client secret). A
    /// secure value there is never compared and is not sent again to an existing object (see <see cref="JsonDiff"/>).
    /// </summary>
    public IReadOnlyList<JsonPath> Secure { get; init; } = [];

    /// <summary>The prefix every delta path carries; ARM uses <c>properties</c>.</summary>
    public JsonPath BasePath { get; init; } = JsonPath.Of("properties");

    /// <summary>Options for a type: member keys and nullability from the overlay; create-only paths excluded when diffing against an existing object.</summary>
    public static JsonDiffOptions ForDefinition(ResourceTypeDefinition definition, bool existing)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new JsonDiffOptions
        {
            MemberKey = definition.MemberKey,
            Nullable = definition.Nullable,
            Exclude = existing ? definition.CreateOnly : [],
            Secure = definition.Secure,
        };
    }
}
