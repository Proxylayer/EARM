using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EntraArm.Core;
using EntraArm.Core.Types;
using EntraArm.Engine.Providers;

namespace EntraArm.Engine.State;

/// <summary>
/// One resource recorded in the deployment state file (plan section 1.7): the Graph id pinned under the ARM-style
/// resource id, the key the object is addressed by, the parent's Graph id for child types, a content hash of the
/// evaluated properties (informational; the diff is always live-vs-desired), the resource ids it depended on (the
/// Complete-mode delete order), provider-specific extra data (<c>keyId</c>, <c>endDateTime</c>...) and the edges
/// this deployment created from the resource's side (<see cref="Relationships"/>, filled by the edge pass, WI-09).
/// Never holds a secret.
/// </summary>
public sealed record StateEntry
{
    /// <summary>The ARM-style identity (the key of <see cref="StateFile.Resources"/>). Always concrete, never symbolic.</summary>
    public required ResourceIdentifier Id { get; init; }

    public required string Type { get; init; }

    public required string ApiVersion { get; init; }

    /// <summary>Key property name to value (<c>uniqueName</c>, composite properties...).</summary>
    public IReadOnlyDictionary<string, string> Key { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The Graph object id (serialised as <c>id</c>).</summary>
    public required string GraphId { get; init; }

    /// <summary>The parent's Graph object id for child types (serialised as <c>parentId</c>).</summary>
    public string? ParentGraphId { get; init; }

    /// <summary>Provider-specific data (<c>keyId</c>, <c>endDateTime</c>, <c>hint</c>...); never secrets.</summary>
    public JsonObject? Extra { get; init; }

    /// <summary><c>sha256:&lt;hex&gt;</c> of the canonical evaluated properties with secret and deploy-time leaves removed.</summary>
    public string? ContentHash { get; init; }

    /// <summary>Resource ids this resource depended on when it was last applied (reverse order = delete order).</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>When the object was last created or updated by EntraARM.</summary>
    public DateTimeOffset? LastWriteOn { get; init; }

    /// <summary>
    /// The edges this deployment created from this resource's side, per relationship kind
    /// (<c>memberOf</c>, <c>ownerOf</c>, <c>administrativeUnits</c>, <c>manager</c>, <c>appRoleAssignments</c>,
    /// <c>directoryRoles</c>): arrays of target ids, or of objects carrying the Graph assignment id. The edge pass
    /// (WI-09) fills and consumes it; the sink preserves it across runs.
    /// </summary>
    public JsonObject? Relationships { get; init; }

    /// <summary>
    /// The key a provider needs to re-read or delete this object: the recorded key values, the parent's Graph id
    /// and the Graph endpoint the recorded apiVersion maps to.
    /// </summary>
    public ResourceKey ToResourceKey(ResourceTypeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var api = catalog.TryGetGraphApi(Type, ApiVersion, out var coreApi) ? GraphApiMapping.ToAz(coreApi) : Az.GraphApi.V1;
        return new ResourceKey
        {
            Type = Type,
            Api = api,
            Values = Key,
            ParentGraphId = ParentGraphId,
        };
    }
}
