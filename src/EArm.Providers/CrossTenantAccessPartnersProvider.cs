using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Graph;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/policies/crossTenantAccessPolicy/partners</c>: a partner-specific cross-tenant access configuration
/// (crossTenantAccessPolicyConfigurationPartner, https://learn.microsoft.com/en-us/graph/api/resources/crosstenantaccesspolicyconfigurationpartner?view=graph-rest-1.0).
/// The resource name is the partner's <c>tenantId</c> (a GUID; the configuration's key, immutable); a property left <c>null</c> inherits the default
/// configuration.
/// <para>
/// Requests per operation:
/// <list type="bullet">
/// <item><b>Read</b>: <c>GET /policies/crossTenantAccessPolicy/partners/{tenantId}</c>; a 404 is "create".</item>
/// <item><b>Create</b>: <c>POST /policies/crossTenantAccessPolicy/partners</c> <c>{ tenantId, … }</c> (201, the object in the body).</item>
/// <item><b>Update</b>: <c>PATCH …/partners/{tenantId}</c> (204) with the changed setting objects, each sent whole with its undeclared live members filled in → GET.</item>
/// <item><b>Delete</b>: <c>DELETE …/partners/{tenantId}</c> (204); a 404 is "already gone". Graph refuses it while the partner has a user synchronization
/// (identitySynchronization) policy, which EARM does not manage: that 400 is surfaced with that explanation.</item>
/// </list>
/// </para>
/// <para>
/// <c>isInMultiTenantOrganization</c> is set by the multitenant organization and cannot be declared. Complete mode lists every partner configuration.
/// </para>
/// </summary>
[EntraProvider(ResourceType)]
public sealed class CrossTenantAccessPartnersProvider : TenantPolicyProviderBase, IDeleteWarningSource, IOrphanCandidateSource
{
    public const string ResourceType = "Microsoft.Entra/policies/crossTenantAccessPolicy/partners";

    private const string KeyProperty = "tenantId";

    private static readonly Dictionary<string, string> ReadOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        ["isInMultiTenantOrganization"] = "the multitenant organization sets it",
    };

    public CrossTenantAccessPartnersProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog, ResourceType)
    {
    }

    public CrossTenantAccessPartnersProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog, ResourceType), catalog)
    {
    }

    protected override string Noun => "partner cross-tenant access configuration";

    /// <summary>The key is the only immutable property, and a changed key is another partner: nothing is recreated.</summary>
    protected override bool RecreateOnImmutableChange => false;

    /// <summary>The create is a POST, refused once the partner exists: not repeated blindly.</summary>
    public override bool CreateIsRepeatable => false;

    // ---- read ----

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);
        string tenantId = ProviderSupport.SingleKey(key, KeyProperty);
        var live = await graph.GetAsync(new GraphRequest(EntityPath(tenantId), key.Api), cancellationToken).ConfigureAwait(false) as JsonObject;
        return live is null ? null : ToExisting(WithId(live, JsonObjects.GetString(live, KeyProperty) ?? tenantId));
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
            TenantPolicyShape.FillUndeclared(properties, existing.Properties, CrossTenantAccessRules.SettingObjects);
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
        RemoveProperties(body, ["id"]);
        return CreateAndKeyAsync(desired, body, graph, cancellationToken);
    }

    public override Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = PropertiesOf(desired);
        ThrowIfProblems(desired, Prepare(desired, properties));
        TenantPolicyShape.FillUndeclared(properties, existing.Properties, CrossTenantAccessRules.SettingObjects);
        var body = BuildPatchBody(desired, existing, change);
        RemoveProperties(body, ["id", KeyProperty]);
        return PatchAndReadAsync(desired, existing, EntityPath(existing.GraphId), body, graph, cancellationToken);
    }

    // ---- delete ----

    public override async Task DeleteAsync(ResourceKey key, ExistingResource live, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(graph);
        try
        {
            await DeleteIgnoringMissingAsync(EntityPath(live.GraphId), key.Api, graph, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.Status == HttpStatusCode.BadRequest)
        {
            throw new ProviderException(
                EngineDiagnosticCodes.ResourceApplyFailed,
                $"{TypeName} {key.Describe()}: Graph refused to delete the partner configuration of tenant {live.GraphId} ({ex.Code}: {ex.GraphMessage}). A partner with a user synchronization policy (identitySynchronization) cannot be deleted; delete that policy first (EARM does not manage it).",
                null,
                ex);
        }
    }

    public string? DescribeDeleteRisk(ResourceKey key, ExistingResource live)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        return $"tenant {live.GraphId} falls back to the default cross-tenant access settings";
    }

    // ---- orphan report ----

    /// <summary>The orphan report lists every partner configuration, named by its tenant id (partners have no <c>id</c> of their own).</summary>
    public OrphanScope OrphanScope => OrphanScope.Collection;

    public async Task<IReadOnlyList<OrphanCandidate>> ListOrphanCandidatesAsync(OrphanQuery query, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(graph);
        var candidates = new List<OrphanCandidate>();
        await foreach (var item in graph.GetAllPagesAsync(new GraphRequest(CollectionPath, query.Api), cancellationToken).ConfigureAwait(false))
        {
            if (item is JsonObject partner && JsonObjects.GetString(partner, KeyProperty) is { } tenantId)
            {
                candidates.Add(new OrphanCandidate(tenantId, tenantId, null, null));
            }
        }

        return candidates;
    }

    // ---- helpers ----

    private async Task<AppliedResource> CreateAndKeyAsync(ResourceInstance desired, JsonObject body, IGraphClient graph, CancellationToken cancellationToken)
    {
        var response = await graph.PostAsync(new GraphRequest(CollectionPath, desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
        string tenantId = desired.Id.NameLeaf;
        var live = response.Body as JsonObject
            ?? await graph.GetAsync(new GraphRequest(EntityPath(tenantId), desired.Api, hint: desired.RetryHint), cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: Graph returned no object for the created {Noun}.", desired.Id);
        return new AppliedResource(desired.Id, tenantId, WithId(live, tenantId), ResolveKey(desired));
    }

    private static List<string> Prepare(ResourceInstance desired, JsonObject properties)
    {
        var problems = CrossTenantAccessRules.RefuseReadOnly(properties, ReadOnly);
        if (!desired.IsSymbolic && !Guid.TryParse(desired.Id.NameLeaf, out _))
        {
            problems.Add($"the name must be the partner's tenant id (a GUID), not '{desired.Id.NameLeaf}'");
        }

        return problems;
    }
}
