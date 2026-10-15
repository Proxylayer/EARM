using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Types;
using EArm.Engine.Providers;

namespace EArm.TestSupport;

/// <summary>
/// A live object a provider test deletes: builds the two arguments of <see cref="IResourceProvider.DeleteAsync"/> directly. <see cref="ResourceKey"/>
/// carries <see cref="Key"/>, the Graph endpoint <see cref="ApiVersion"/> maps to, <see cref="ParentGraphId"/> and (for a child) the parent's
/// <c>uniqueName</c> taken from <see cref="Id"/>; <see cref="Live"/> carries <see cref="GraphId"/> plus the <see cref="Extra"/> live properties.
/// </summary>
public sealed record DeleteTarget
{
    public required ResourceIdentifier Id { get; init; }

    public required string Type { get; init; }

    public string? ApiVersion { get; init; }

    public IReadOnlyDictionary<string, string> Key { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public required string GraphId { get; init; }

    public string? ParentGraphId { get; init; }

    /// <summary>Additional properties of the live object (for example the <c>keyId</c> of a password credential).</summary>
    public JsonObject? Extra { get; init; }

    public ResourceKey ResourceKey
    {
        get
        {
            var api = ApiVersion is not null && ResourceTypeCatalog.Default.TryGetGraphApi(Type, ApiVersion, out var coreApi) ? GraphApiMapping.ToAz(coreApi) : EArm.Graph.GraphApi.V1;
            return new ResourceKey
            {
                Type = Type,
                Api = api,
                Values = Key,
                ParentGraphId = ParentGraphId,
                ParentKey = Id.Parent is { } parent
                    ? new ResourceKey
                    {
                        Type = parent.Type,
                        Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["uniqueName"] = parent.NameLeaf },
                    }
                    : null,
            };
        }
    }

    public ExistingResource Live
    {
        get
        {
            var raw = Extra?.DeepClone() as JsonObject ?? new JsonObject();
            raw["id"] = GraphId;
            return new ExistingResource(GraphId, (JsonObject)raw.DeepClone(), raw);
        }
    }
}
