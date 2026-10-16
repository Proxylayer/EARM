using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using EArm.Graph;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/entitlementManagement/catalogs</c> (accessPackageCatalog). The resource name is the <c>displayName</c>; the catalog is
/// found by it, live, on every run.
/// <list type="bullet">
/// <item><b>Read</b>: <c>GET /identityGovernance/entitlementManagement/catalogs?$filter=displayName eq '…'</c>; several hits are <c>AmbiguousMatch</c>.</item>
/// <item><b>Create</b>: <c>POST …/catalogs</c> with the overlay's create-only defaults (<c>state: published</c>, <c>isExternallyVisible: false</c>).</item>
/// <item><b>Update</b>: <c>PATCH …/catalogs/{id}</c> with the changed declared properties, then <c>GET</c>.</item>
/// <item><b>Delete</b>: <c>DELETE …/catalogs/{id}</c>; a 404 is "already gone"; a 400 (the catalog still holds access packages) is <see cref="NotEmpty"/>.</item>
/// </list>
/// </summary>
[EntraProvider(ResourceType)]
public sealed class AccessPackageCatalogsProvider : ProviderBase, IOrphanCandidateSource
{
    public const string ResourceType = "Microsoft.Entra/entitlementManagement/catalogs";

    /// <summary><see cref="ProviderException.Code"/> when Graph refuses to delete a catalog that still holds access packages.</summary>
    public const string NotEmpty = EngineDiagnosticCodes.CatalogNotEmpty;

    private const string KeyProperty = "displayName";

    public AccessPackageCatalogsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog)
    {
        if (!ResourceIdentifier.TypeEquals(definition.Type, ResourceType))
        {
            throw new ArgumentException($"{nameof(AccessPackageCatalogsProvider)} implements '{ResourceType}', not '{definition.Type}'.", nameof(definition));
        }
    }

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);
        string name = ProviderSupport.SingleKey(key, KeyProperty);
        var live = await EntitlementManagementGraph.FindSingleByDisplayNameAsync(graph, CollectionPath, name, "catalog", ResourceType, key.Api, null, cancellationToken).ConfigureAwait(false);
        return live is null ? null : ToExisting(live);
    }

    public override async Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        var body = BuildCreateBody(desired);
        var response = await graph.PostAsync(new GraphRequest(CollectionPath, desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
        var live = response.Body as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: Graph returned no object for the created catalog.", desired.Id);
        string id = JsonObjects.GetId(live)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: Graph returned a catalog without 'id'.", desired.Id);
        return new AppliedResource(desired.Id, id, live, ResolveKey(desired));
    }

    public override async Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(graph);
        var body = BuildPatchBody(desired, existing, change);
        if (body.Count == 0)
        {
            return new AppliedResource(desired.Id, existing.GraphId, (JsonObject)JsonValues.Clone(existing.Raw)!, ResolveKey(desired));
        }

        var path = new GraphRequest(EntityPath(existing.GraphId), desired.Api, hint: desired.RetryHint);
        await graph.PatchAsync(path, body, cancellationToken).ConfigureAwait(false);
        var live = await graph.GetAsync(path, cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the catalog ({existing.GraphId}) vanished after the update.", desired.Id);
        return new AppliedResource(desired.Id, existing.GraphId, live, ResolveKey(desired));
    }

    public override async Task DeleteAsync(ResourceKey key, ExistingResource live, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(graph);
        try
        {
            await graph.DeleteAsync(new GraphRequest(EntityPath(live.GraphId), key.Api), cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            // Already gone.
        }
        catch (GraphException ex) when (ex.Status == HttpStatusCode.BadRequest)
        {
            throw new ProviderException(
                NotEmpty,
                $"{TypeName} {key.Describe()}: Graph refused to delete catalog {live.GraphId} ({ex.Code}: {ex.GraphMessage}). A catalog can only be deleted once it holds no access packages; delete its packages first (a package with assignments cannot be deleted either).",
                null,
                ex);
        }
    }

    private string EntityPath(string graphId)
    {
        return CollectionPath + "/" + GraphPath.Segment(graphId);
    }

    // ---- orphan report ----

    /// <summary>The orphan report lists every user-managed catalog of the tenant (a small collection); the built-in General catalog (<c>catalogType: serviceDefault</c>) is never reported.</summary>
    public OrphanScope OrphanScope => OrphanScope.Collection;

    public Task<IReadOnlyList<OrphanCandidate>> ListOrphanCandidatesAsync(OrphanQuery query, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(graph);
        return OrphanListing.CollectionAsync(
            graph,
            new GraphRequest(CollectionPath, query.Api).WithSelect("id", KeyProperty, "catalogType"),
            KeyProperty,
            live => !string.Equals(JsonObjects.GetString(live, "catalogType"), "serviceDefault", StringComparison.OrdinalIgnoreCase),
            null,
            cancellationToken);
    }
}
