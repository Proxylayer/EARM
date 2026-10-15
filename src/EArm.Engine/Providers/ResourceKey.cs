using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Engine.Providers;

/// <summary>
/// How a provider finds a resource's Graph object: the alternate key or composite key values derived from the
/// template (<c>uniqueName</c>, <c>userPrincipalName</c>, <c>appId</c>, <c>displayName</c>, the composite of an
/// assignment...), plus the parent's key for child types. <see cref="UnresolvedProperties"/> lists key properties
/// whose values are only known at deploy time (symbolic names, unknown composite properties); such a key cannot be
/// looked up yet.
/// </summary>
public sealed record ResourceKey
{
    /// <summary>The resource type the key belongs to.</summary>
    public required string Type { get; init; }

    /// <summary>The Graph endpoint to read through (from the resource's apiVersion).</summary>
    public GraphApi Api { get; init; } = GraphApi.V1;

    /// <summary>Key property name to value, in overlay order.</summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The parent's key for child types (<c>parent/child</c> names); <c>null</c> for top-level types.</summary>
    public ResourceKey? ParentKey { get; init; }

    /// <summary>The parent's Graph object id when it is already known (applied in this run, or read earlier).</summary>
    public string? ParentGraphId { get; init; }

    /// <summary>Key properties whose values are not available yet.</summary>
    public IReadOnlyList<string> UnresolvedProperties { get; init; } = [];

    public bool IsResolved => UnresolvedProperties.Count == 0 && (ParentKey is null || ParentKey.IsResolved);

    /// <summary>The single key value for alternate-key types; throws for composite keys.</summary>
    public string Single
    {
        get
        {
            if (Values.Count != 1)
            {
                throw new InvalidOperationException($"Key of '{Type}' has {Values.Count} values; expected exactly one.");
            }

            return Values.Values.First();
        }
    }

    /// <summary>The single key property name for alternate-key types.</summary>
    public string SingleName
    {
        get
        {
            if (Values.Count != 1)
            {
                throw new InvalidOperationException($"Key of '{Type}' has {Values.Count} values; expected exactly one.");
            }

            return Values.Keys.First();
        }
    }

    public bool TryGet(string property, out string value)
    {
        ArgumentNullException.ThrowIfNull(property);
        foreach (var pair in Values)
        {
            if (pair.Key.Equals(property, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    /// <summary>Renders <c>uniqueName='x'</c> or <c>principalId='..', resourceId='..'</c>, prefixed by the parent key.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        if (ParentKey is not null)
        {
            sb.Append(ParentKey.Describe()).Append(" / ");
        }

        sb.Append(string.Join(", ", Values.Select(p => $"{p.Key}='{p.Value}'")));
        if (UnresolvedProperties.Count > 0)
        {
            sb.Append(" (unresolved: ").Append(string.Join(", ", UnresolvedProperties)).Append(')');
        }

        return sb.ToString();
    }

    public override string ToString()
    {
        return Describe();
    }
}
