using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core.Json;
using EArm.Core.Types;
using EArm.Engine.State;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Engine.Providers;

/// <summary>
/// The per-type contract the engine drives. One implementation per
/// <c>Microsoft.Entra/&lt;type&gt;</c>, registered in a <see cref="ProviderRegistry"/>. Providers are stateless
/// across resources; everything per call travels in the arguments. <see cref="ProviderBase"/> supplies the shared
/// behaviour (key from <c>name</c>, name injection, defaults, server-managed stripping, diff, immutable detection).
/// </summary>
public interface IResourceProvider
{
    /// <summary>The full type, for example <c>Microsoft.Entra/applications</c>.</summary>
    string TypeName { get; }

    /// <summary>Accepted <c>apiVersion</c> values and the Graph endpoint each maps to.</summary>
    IReadOnlyDictionary<string, GraphApi> ApiVersions { get; }

    /// <summary>Graph application permissions needed to manage the type (or this particular instance when given).</summary>
    IReadOnlyList<string> RequiredPermissions(ResourceInstance? desired);

    /// <summary>Property paths (relative to <c>properties</c>) that cannot change after create.</summary>
    IReadOnlySet<JsonPath> ImmutableProperties { get; }

    /// <summary>Relationship/assignment properties the edge pass applies instead of PATCHing.</summary>
    IReadOnlyList<RelationshipSpec> Relationships { get; }

    /// <summary>
    /// Resolves against Graph what the key and the plan need before <see cref="ResolveKey"/> runs on an evaluated
    /// instance: name sugar such as <c>resourceAppId</c> → <c>resourceId</c>, <c>appRole</c> → <c>appRoleId</c> or
    /// <c>scopes</c> → <c>scope</c>, and validations that need live data (an app role the resource does not define, a
    /// scope value it does not expose). The provider rewrites <see cref="ResourceInstance.Properties"/> in place; a
    /// <see cref="ProviderException"/> makes the resource an unsupported row (what-if) or a failed operation (create).
    /// Deploy-time-unknown leaves (what-if) are left in place or moved under the canonical property. Runs inside the
    /// planner's retry policy with the default hint; put <see cref="ResourceInstance.RetryHint"/> on your own requests.
    /// The default does nothing.
    /// </summary>
    Task PrepareAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>Derives the Graph key from the instance's name, parent and (for GUID-named types) composite properties.</summary>
    ResourceKey ResolveKey(ResourceInstance desired);

    /// <summary>
    /// Finds the live object: by <paramref name="stateId"/> (pinned Graph id) first, then by key. Returns <c>null</c>
    /// when nothing exists. Throws <see cref="ProviderException"/> (<see cref="ProviderException.AmbiguousMatch"/>)
    /// when a displayName lookup matches several objects.
    /// </summary>
    Task<ExistingResource?> ReadAsync(ResourceKey key, string? stateId, IGraphClient graph, CancellationToken cancellationToken);

    /// <summary>Computes the change between the desired instance (evaluated) and the live object.</summary>
    ResourceChange Plan(ResourceInstance desired, ExistingResource? existing, PlanOptions options);

    /// <summary>
    /// True when repeating a create whose outcome is unknown (a 5xx, a lost connection) is harmless because the create is an upsert (a <c>PATCH</c> with <c>Prefer: create-if-missing</c> by alternate key:
    /// applications, groups, authentication contexts, federated identity credentials). Every other create (<c>POST</c>) is never repeated blindly by the executor's retry policy: only a 429, replication lag or a token
    /// refresh is waited out, and an unknown outcome is answered by re-reading the object by key (review round 2, S2-07). The default is the conservative <c>false</c>.
    /// </summary>
    bool CreateIsRepeatable => false;

    Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken);

    Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken);

    Task DeleteAsync(StateEntry entry, IGraphClient graph, CancellationToken cancellationToken);
}
