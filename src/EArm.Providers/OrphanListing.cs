using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine.Providers;
using EArm.Graph;

namespace EArm.Providers;

/// <summary>The listing helpers the providers' <see cref="IOrphanCandidateSource"/> implementations share (read-only).</summary>
public static class OrphanListing
{
    /// <summary>
    /// Every page of <paramref name="request"/>, each object with an <c>id</c> (and accepted by <paramref name="include"/>) as a candidate named by
    /// <paramref name="nameProperty"/> (the Graph id when the object has none).
    /// </summary>
    public static async Task<IReadOnlyList<OrphanCandidate>> CollectionAsync(IGraphClient graph, GraphRequest request, string nameProperty, Func<JsonObject, bool>? include, Func<JsonObject, string?>? describe, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(nameProperty);
        var candidates = new List<OrphanCandidate>();
        await foreach (var item in graph.GetAllPagesAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (item is JsonObject live && JsonObjects.GetId(live) is { } id && (include is null || include(live)))
            {
                candidates.Add(new OrphanCandidate(id, JsonObjects.GetString(live, nameProperty) ?? id, null, describe?.Invoke(live)));
            }
        }

        return candidates;
    }

    /// <summary>The distinct, non-empty values of the key property <paramref name="property"/> across <paramref name="keys"/> (the declared principals or clients).</summary>
    public static IReadOnlyList<string> DistinctKeyValues(IEnumerable<ResourceKey> keys, string property)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(property);
        return keys
            .Select(k => k.TryGet(property, out var value) ? value : null)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>A short description of an assignment-like object from the named properties that are present (<c>principalId 'x', appRoleId 'y'</c>).</summary>
    public static string Describe(JsonObject live, params string[] properties)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(properties);
        return string.Join(", ", properties
            .Select(p => (Name: p, Value: JsonObjects.GetString(live, p)))
            .Where(p => !string.IsNullOrEmpty(p.Value))
            .Select(p => $"{p.Name} '{p.Value}'"));
    }
}
