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
/// <c>Microsoft.Entra/entitlementManagement/accessPackages</c> (accessPackage). The resource name is the <c>displayName</c>, kept unique in the
/// tenant (Graph allows duplicates, EARM refuses them with <c>AmbiguousMatch</c>); <c>catalogId</c> binds the package to its catalog on create and is
/// immutable (Learn: resources/accesspackage, the <c>catalog</c> relationship is required on create and read-only).
/// <list type="bullet">
/// <item><b>Read</b>: <c>GET …/accessPackages?$filter=displayName eq '…'&amp;$expand=catalog</c>; the diff view carries <c>catalogId</c> from <c>catalog.id</c>.</item>
/// <item><b>Create</b>: <c>POST …/accessPackages</c> with <c>catalog { id }</c> instead of <c>catalogId</c> and the create-only default <c>isHidden: false</c>.</item>
/// <item><b>Update</b>: <c>PATCH …/accessPackages/{id}</c> (<c>displayName</c>, <c>description</c>, <c>isHidden</c>), then <c>GET</c>.</item>
/// <item><b>Delete</b>: <c>DELETE …/accessPackages/{id}</c>; Graph refuses a package with assignments: <see cref="AssignmentsExist"/>.</item>
/// </list>
/// </summary>
[EntraProvider(ResourceType)]
public sealed class AccessPackagesProvider : ProviderBase, IOrphanCandidateSource
{
    public const string ResourceType = "Microsoft.Entra/entitlementManagement/accessPackages";

    /// <summary><see cref="ProviderException.Code"/> when Graph refuses to delete a package that still has assignments.</summary>
    public const string AssignmentsExist = EngineDiagnosticCodes.AccessPackageAssignmentsExist;

    internal const string CatalogIdProperty = "catalogId";

    private const string KeyProperty = "displayName";

    private const string CatalogExpand = "catalog";

    public AccessPackagesProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog)
    {
        if (!ResourceIdentifier.TypeEquals(definition.Type, ResourceType))
        {
            throw new ArgumentException($"{nameof(AccessPackagesProvider)} implements '{ResourceType}', not '{definition.Type}'.", nameof(definition));
        }
    }

    /// <summary>A changed <c>catalogId</c> is an error, never a move or a recreate.</summary>
    protected override bool RecreateOnImmutableChange => false;

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);
        string name = ProviderSupport.SingleKey(key, KeyProperty);
        var live = await FindAsync(graph, name, key.Api, cancellationToken).ConfigureAwait(false);
        return live is null ? null : ToExisting(View(live));
    }

    /// <summary>The package with that displayName (catalog expanded), <c>null</c> when there is none; several are <c>AmbiguousMatch</c>.</summary>
    internal static Task<JsonObject?> FindAsync(IGraphClient graph, string displayName, Graph.GraphApi api, CancellationToken cancellationToken)
    {
        return EntitlementManagementGraph.FindSingleByDisplayNameAsync(graph, EntitlementManagementGraph.AccessPackagesPath, displayName, "access package", ResourceType, api, CatalogExpand, cancellationToken);
    }

    public override async Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        var body = BuildCreateBody(desired);
        string catalogId = JsonObjects.FindKey(body, CatalogIdProperty) is { } catalogKey ? JsonObjects.GetString(body, catalogKey) ?? string.Empty : string.Empty;
        if (catalogId.Length == 0)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: catalogId is required to create an access package.", desired.Id);
        }

        body.Remove(JsonObjects.FindKey(body, CatalogIdProperty)!);
        body["catalog"] = new JsonObject { ["id"] = catalogId };
        var response = await graph.PostAsync(new GraphRequest(CollectionPath, desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
        var live = response.Body as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: Graph returned no object for the created access package.", desired.Id);
        string id = JsonObjects.GetId(live)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: Graph returned an access package without 'id'.", desired.Id);
        if (EntitlementManagementGraph.CatalogIdOf(live) is null)
        {
            live["catalog"] = new JsonObject { ["id"] = catalogId };
        }

        return new AppliedResource(desired.Id, id, View(live), ResolveKey(desired));
    }

    public override async Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(graph);
        var body = BuildPatchBody(desired, existing, change);
        if (JsonObjects.FindKey(body, CatalogIdProperty) is { } catalogKey)
        {
            body.Remove(catalogKey);
        }

        if (body.Count == 0)
        {
            return new AppliedResource(desired.Id, existing.GraphId, (JsonObject)JsonValues.Clone(existing.Raw)!, ResolveKey(desired));
        }

        await graph.PatchAsync(new GraphRequest(EntityPath(existing.GraphId), desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
        var live = await EntitlementManagementGraph.GetPackageAsync(graph, existing.GraphId, desired.Api, desired.RetryHint, CatalogExpand, cancellationToken).ConfigureAwait(false)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the access package ({existing.GraphId}) vanished after the update.", desired.Id);
        return new AppliedResource(desired.Id, existing.GraphId, View(live), ResolveKey(desired));
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
            bool assignments = EntitlementManagementGraph.IsAssignmentsRefusal(ex);
            throw new ProviderException(
                assignments ? AssignmentsExist : EngineDiagnosticCodes.ResourceApplyFailed,
                assignments
                    ? $"{TypeName} {key.Describe()}: Graph refused to delete access package {live.GraphId} because it still has assignments ({ex.Code}: {ex.GraphMessage}). Assignments are people's access and EARM never removes them; remove them in the portal (or wait for them to expire), then run the delete again."
                    : $"{TypeName} {key.Describe()}: Graph refused to delete access package {live.GraphId} ({ex.Code}: {ex.GraphMessage}).",
                null,
                ex);
        }
    }

    /// <summary>The live package with <c>catalogId</c> copied from the expanded <c>catalog</c>, so the immutable binding takes part in the diff.</summary>
    private static JsonObject View(JsonObject live)
    {
        var view = (JsonObject)JsonValues.Clone(live)!;
        if (EntitlementManagementGraph.CatalogIdOf(live) is { } catalogId)
        {
            view[CatalogIdProperty] = catalogId;
        }

        return view;
    }

    private string EntityPath(string graphId)
    {
        return CollectionPath + "/" + GraphPath.Segment(graphId);
    }

    // ---- orphan report ----

    /// <summary>The orphan report lists every access package of the tenant.</summary>
    public OrphanScope OrphanScope => OrphanScope.Collection;

    public Task<IReadOnlyList<OrphanCandidate>> ListOrphanCandidatesAsync(OrphanQuery query, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(graph);
        return OrphanListing.CollectionAsync(graph, new GraphRequest(CollectionPath, query.Api).WithSelect("id", KeyProperty), KeyProperty, null, null, cancellationToken);
    }
}
