using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Types;
using EArm.Engine.Providers;
using EArm.Graph;

namespace EArm.Providers;

/// <summary>
/// The display-name-keyed application policies (<see cref="TokenLifetimePoliciesProvider"/>, <see cref="ClaimsMappingPoliciesProvider"/>,
/// <see cref="AppManagementPoliciesProvider"/>). The resource name is the <c>displayName</c>; the policy is found by it, live, on every run.
/// <para>
/// Requests per operation:
/// <list type="bullet">
/// <item><b>Read</b>: <c>GET /policies/{collection}</c> (every page), matched client-side on <c>displayName</c> (Learn documents <c>$filter</c> on these
/// lists but not on <c>displayName</c>): none is "create", one is the policy, several are <see cref="ProviderException.AmbiguousMatch"/>.</item>
/// <item><b>Create</b>: <c>POST /policies/{collection}</c> (201, the object in the body).</item>
/// <item><b>Update</b>: <c>PATCH /policies/{collection}/{id}</c> with the changed properties (204) → <c>GET …/{id}</c>.</item>
/// <item><b>Delete</b>: <c>DELETE /policies/{collection}/{id}</c> (204); a 404 is "already gone". The applications and service principals it is
/// assigned to fall back to the default (<see cref="IDeleteWarningSource"/>).</item>
/// </list>
/// </para>
/// <para>
/// A subclass normalises the declared properties (<see cref="Normalize"/>) and adopts equivalent live spellings (<see cref="AdoptLive"/>) before the
/// shared diff. The assignments are principal-side edges of applications and service principals; Complete mode lists the whole collection.
/// </para>
/// </summary>
public abstract class DisplayNamePolicyProviderBase : TenantPolicyProviderBase, IDeleteWarningSource, IOrphanCandidateSource
{
    private const string KeyProperty = "displayName";

    /// <summary>Properties Graph assigns; never sent in a body.</summary>
    private static readonly string[] ServerAssignedProperties = ["id", "deletedDateTime"];

    protected DisplayNamePolicyProviderBase(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog, string resourceType)
        : base(definition, catalog, resourceType)
    {
    }

    /// <summary>The template property of the assignment edge (<c>tokenLifetimePolicies</c>), for messages.</summary>
    protected abstract string AssignmentProperty { get; }

    /// <summary>Checks and normalises the evaluated properties in place; the problems found.</summary>
    protected abstract List<string> Normalize(JsonObject properties);

    /// <summary>Makes declared values that equal the live ones in Graph's sense take the live spelling; nothing by default.</summary>
    protected virtual void AdoptLive(JsonObject properties, JsonObject live)
    {
    }

    /// <summary>Nothing is immutable; nothing is recreated.</summary>
    protected override bool RecreateOnImmutableChange => false;

    // ---- read ----

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);
        string name = ProviderSupport.SingleKey(key, KeyProperty);
        var live = await FindSingleAsync(graph, KeyProperty, name, key.Api, cancellationToken).ConfigureAwait(false);
        return live is null ? null : ToExisting(live);
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

        var problems = Normalize(properties);
        if (problems.Count > 0)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: {string.Join("; ", problems)}.");
        }

        if (existing is not null)
        {
            AdoptLive(properties, existing.Properties);
        }

        return base.Plan(desired, existing, options);
    }

    // ---- create / update ----

    public override Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        ThrowIfProblems(desired, Normalize(PropertiesOf(desired)));
        var body = BuildCreateBody(desired);
        RemoveProperties(body, ServerAssignedProperties);
        return PostCreateAsync(desired, body, graph, cancellationToken);
    }

    public override Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = PropertiesOf(desired);
        ThrowIfProblems(desired, Normalize(properties));
        AdoptLive(properties, existing.Properties);
        var body = BuildPatchBody(desired, existing, change);
        RemoveProperties(body, ServerAssignedProperties);
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

    public virtual string? DescribeDeleteRisk(ResourceKey key, ExistingResource live)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        return $"the applications and service principals the {Noun} is assigned to ({AssignmentProperty}) fall back to the default behaviour";
    }

    // ---- orphan report ----

    /// <summary>The orphan report lists the whole collection.</summary>
    public OrphanScope OrphanScope => OrphanScope.Collection;

    public Task<IReadOnlyList<OrphanCandidate>> ListOrphanCandidatesAsync(OrphanQuery query, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(graph);
        return OrphanListing.CollectionAsync(graph, new GraphRequest(CollectionPath, query.Api).WithSelect("id", KeyProperty), KeyProperty, null, null, cancellationToken);
    }
}
