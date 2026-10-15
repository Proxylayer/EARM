using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Types;
using EArm.Engine.Providers;

namespace EArm.Engine.State;

/// <summary>
/// One resource recorded in the deployment state file: the Graph id pinned under the ARM-style
/// resource id, the key the object is addressed by, the parent's Graph id for child types, a content hash of the
/// evaluated properties (informational; the diff is always live-vs-desired), the resource ids it depended on (the
/// Complete-mode delete order), provider-specific extra data (<c>keyId</c>, <c>endDateTime</c>...) and the edges
/// this deployment created from the resource's side (<see cref="Relationships"/>, filled by the edge pass).
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

    /// <summary>
    /// <see cref="StateProvenance.Created"/> when this deployment created the object, <see cref="StateProvenance.Adopted"/> when it existed
    /// before (serialised as <c>provenance</c>: <c>created</c> | <c>adopted</c>; absent = <c>adopted</c>). Adopted objects are never deleted
    /// by Complete mode or <c>deployment delete --delete-resources</c> without <c>--delete-adopted</c>.
    /// </summary>
    public StateProvenance Provenance { get; init; } = StateProvenance.Created;

    /// <summary>
    /// <c>false</c> when the entry was read from a state file that had no <c>provenance</c> at all (written by an older version): the entry is treated as
    /// <see cref="StateProvenance.Adopted"/>, and messages say "no provenance record" instead of claiming the object existed before.
    /// </summary>
    public bool ProvenanceRecorded { get; init; } = true;

    /// <summary>
    /// The immutable fingerprint of the object: the <c>createdDateTime</c> Graph reported when it was created (or adopted), serialised as
    /// <c>createdDateTime</c>. Optional (older state files and types without the property have none). When present, a state-driven
    /// delete or update requires the live object to carry the same value (review round 2, finding S2-01): an entry re-pointed at another
    /// object, even one with the same key, no longer matches.
    /// </summary>
    public DateTimeOffset? CreatedDateTime { get; init; }

    /// <summary>When the object was last created or updated by EARM.</summary>
    public DateTimeOffset? LastWriteOn { get; init; }

    /// <summary>
    /// The edges this deployment created from this resource's side, per relationship kind
    /// (<c>memberOf</c>, <c>ownerOf</c>, <c>administrativeUnits</c>, <c>manager</c>, <c>appRoleAssignments</c>,
    /// <c>directoryRoles</c>): arrays of target ids, or of objects carrying the Graph assignment id. The edge pass
    /// fills and consumes it; the sink preserves it across runs.
    /// </summary>
    public JsonObject? Relationships { get; init; }

    /// <summary>
    /// The key a provider needs to re-read or delete this object: the recorded key values, the parent's Graph id
    /// and the Graph endpoint the recorded apiVersion maps to.
    /// </summary>
    public ResourceKey ToResourceKey(ResourceTypeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var api = catalog.TryGetGraphApi(Type, ApiVersion, out var coreApi) ? GraphApiMapping.ToAz(coreApi) : Graph.GraphApi.V1;
        return new ResourceKey
        {
            Type = Type,
            Api = api,
            Values = Key,
            ParentGraphId = ParentGraphId,
        };
    }
}
