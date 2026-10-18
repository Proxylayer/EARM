using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Graph;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/groupLifecyclePolicies</c>: the tenant's Microsoft 365 group expiration policy. Graph allows one per tenant
/// ("Only one policy exists in the tenant", grouplifecyclepolicy-post-grouplifecyclepolicies) and the object has no display name, so the type is a
/// singleton named <c>default</c>: the key is the type itself, not a Graph property.
/// <para>
/// Requests per operation (https://learn.microsoft.com/en-us/graph/api/resources/grouplifecyclepolicy?view=graph-rest-1.0):
/// <list type="bullet">
/// <item><b>Read</b>: <c>GET /groupLifecyclePolicies</c> (every page): none is "create", one is the policy, several are <see cref="ProviderException.AmbiguousMatch"/>.</item>
/// <item><b>Create</b>: <c>POST /groupLifecyclePolicies</c> (201, the object in the body).</item>
/// <item><b>Update</b>: <c>PATCH /groupLifecyclePolicies/{id}</c> with the changed properties → <c>GET …/{id}</c>.</item>
/// <item><b>Delete</b>: <c>DELETE /groupLifecyclePolicies/{id}</c> (204), which switches group expiration off for the tenant (<see cref="IDeleteWarningSource"/>); a 404 is "already gone".</item>
/// </list>
/// </para>
/// <para>
/// Which groups a <c>Selected</c> policy governs is not a property of the policy: a group declares <c>groupLifecyclePolicies</c> (an ensure-only edge,
/// <see cref="Edges.GroupLifecyclePolicyEdgeKind"/>). Complete mode lists no orphans: the declared singleton is the only policy there can be.
/// </para>
/// </summary>
[EntraProvider(ResourceType)]
public sealed class GroupLifecyclePoliciesProvider : TenantPolicyProviderBase, IDeleteWarningSource, IOrphanCandidateSource
{
    public const string ResourceType = "Microsoft.Entra/groupLifecyclePolicies";

    /// <summary>The only resource name: the tenant has one policy.</summary>
    public const string SingletonName = "default";

    private const string KeyProperty = "id";

    public GroupLifecyclePoliciesProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog, ResourceType)
    {
    }

    public GroupLifecyclePoliciesProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog, ResourceType), catalog)
    {
    }

    protected override string Noun => "group lifecycle policy";

    /// <summary>No property is immutable; nothing is ever recreated.</summary>
    protected override bool RecreateOnImmutableChange => false;

    /// <summary>The create is a POST: repeating it after an unknown outcome could make a second policy (which Graph refuses).</summary>
    public override bool CreateIsRepeatable => false;

    /// <summary>The policy is found by listing the collection, which can miss a policy created seconds ago.</summary>
    public override bool KeyReadLagsCreate => true;

    // ---- read ----

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);
        RequireSingletonName(ProviderSupport.SingleKey(key, KeyProperty));

        var policies = new List<JsonObject>();
        await foreach (var item in graph.GetAllPagesAsync(new GraphRequest(CollectionPath, key.Api), cancellationToken).ConfigureAwait(false))
        {
            if (item is JsonObject policy)
            {
                policies.Add(policy);
            }
        }

        if (policies.Count > 1)
        {
            throw new ProviderException(
                ProviderException.AmbiguousMatch,
                $"{policies.Count} group lifecycle policies exist (ids: {string.Join(", ", policies.Select(p => JsonObjects.GetId(p) ?? "?"))}); Graph allows one per tenant, so delete the others.",
                ResourceIdentifier.Create(ResourceType, SingletonName));
        }

        return policies.Count == 1 ? ToExisting(policies[0]) : null;
    }

    // ---- plan ----

    public override ResourceChange Plan(ResourceInstance desired, ExistingResource? existing, PlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(options);
        if (desired.IsIgnored || desired.Properties is not { } properties)
        {
            return base.Plan(desired, existing, options);
        }

        var problems = Prepare(desired, properties);
        if (problems.Count > 0)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: {string.Join("; ", problems)}.");
        }

        if (existing is not null)
        {
            GroupLifecyclePoliciesRules.AdoptLive(properties, existing.Properties);
        }

        return base.Plan(desired, existing, options);
    }

    // ---- create / update ----

    public override Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        ThrowIfProblems(desired, Prepare(desired, PropertiesOf(desired)));
        var body = BuildCreateBody(desired);
        RemoveProperties(body, GroupLifecyclePoliciesRules.ServerAssignedProperties);
        return PostCreateAsync(desired, body, graph, cancellationToken);
    }

    public override Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = PropertiesOf(desired);
        ThrowIfProblems(desired, Prepare(desired, properties));
        GroupLifecyclePoliciesRules.AdoptLive(properties, existing.Properties);
        var body = BuildPatchBody(desired, existing, change);
        RemoveProperties(body, GroupLifecyclePoliciesRules.ServerAssignedProperties);
        return PatchAndReadAsync(desired, existing, EntityPath(existing.GraphId), body, graph, cancellationToken);
    }

    // ---- delete ----

    public override Task DeleteAsync(ResourceKey key, ExistingResource live, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(graph);
        return DeleteIgnoringMissingAsync(EntityPath(live.GraphId), key.Api, graph, cancellationToken);
    }

    /// <summary>Deleting the policy switches group expiration off for every Microsoft 365 group of the tenant.</summary>
    public string? DescribeDeleteRisk(ResourceKey key, ExistingResource live)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        return "removes the tenant's group expiration policy: Microsoft 365 groups stop expiring and the groups added to a Selected policy lose that setting";
    }

    // ---- orphan report ----

    /// <summary>The tenant has one policy, the declared singleton: nothing can be an orphan (no Graph call).</summary>
    public OrphanScope OrphanScope => OrphanScope.Collection;

    public Task<IReadOnlyList<OrphanCandidate>> ListOrphanCandidatesAsync(OrphanQuery query, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(graph);
        return Task.FromResult<IReadOnlyList<OrphanCandidate>>([]);
    }

    // ---- helpers ----

    private static List<string> Prepare(ResourceInstance desired, JsonObject properties)
    {
        var problems = new List<string>();
        if (!desired.IsSymbolic && !SingletonName.Equals(desired.Id.NameLeaf, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"the name must be '{SingletonName}' (the tenant has one group lifecycle policy)");
        }

        problems.AddRange(GroupLifecyclePoliciesRules.NormalizeDesired(properties));
        return problems;
    }

    private void RequireSingletonName(string name)
    {
        if (!SingletonName.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: the name must be '{SingletonName}' (the tenant has one group lifecycle policy), not '{name}'.");
        }
    }
}
