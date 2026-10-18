using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using EArm.Graph;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/entitlementManagement/catalogResources</c>: a group, an application (service principal) or a SharePoint Online site added to a catalog
/// (<see cref="AccessPackageResourceOrigin"/>). Found by the tuple (<c>catalogId</c>, <c>originId</c>), a site URL compared case-insensitively and without
/// its trailing slash; the GUID name is a label. Every property is part of the identity, so nothing is updated in place.
/// <list type="bullet">
/// <item><b>Read</b>: <c>GET …/catalogs/{catalogId}/resources?$filter=originId eq '…'</c>; the diff view is <c>{ catalogId, originId, originSystem }</c>.</item>
/// <item><b>Create</b>: <c>POST …/resourceRequests</c> <c>{ requestType: adminAdd, resource { originId, originSystem }, catalog { id } }</c> (Learn:
/// entitlementmanagement-post-resourcerequests, examples 1 to 3: the same body for every kind). A request in <c>deliveryFailed</c> is <see cref="RequestFailed"/>;
/// otherwise the catalog's resources are read every <see cref="ReplicationWait.Interval"/> until the resource is listed (up to <see cref="ReplicationWait.Window"/>).
/// A 403 is <see cref="AccessDenied"/>, naming the roles the kind of resource needs.</item>
/// <item><b>Delete</b>: <c>POST …/resourceRequests</c> <c>{ requestType: adminRemove, resource { id }, catalog { id } }</c>; a 400 (a package of the catalog
/// still grants a role of the resource) is <see cref="InUse"/>.</item>
/// </list>
/// </summary>
[EntraProvider(ResourceType)]
public sealed class CatalogResourcesProvider : ProviderBase, IApplyOrderSource
{
    public const string ResourceType = "Microsoft.Entra/entitlementManagement/catalogResources";

    /// <summary><see cref="ProviderException.Code"/> when Graph refuses to add the group (403).</summary>
    public const string AccessDenied = EngineDiagnosticCodes.CatalogResourceAccessDenied;

    /// <summary><see cref="ProviderException.Code"/> when the request failed or the group never showed in the catalog.</summary>
    public const string RequestFailed = EngineDiagnosticCodes.CatalogResourceRequestFailed;

    /// <summary><see cref="ProviderException.Code"/> when Graph refuses the removal because a package still uses the group.</summary>
    public const string InUse = EngineDiagnosticCodes.CatalogResourceInUse;

    private const string CatalogIdProperty = "catalogId";

    private const string OriginIdProperty = "originId";

    private const string OriginSystemProperty = "originSystem";

    public CatalogResourcesProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : this(definition, catalog, null)
    {
    }

    /// <param name="definition">The catalog definition of the type.</param>
    /// <param name="catalog">The catalog.</param>
    /// <param name="settle">How long a create waits for the group to show in the catalog; <see cref="ReplicationWait.Default"/> when null.</param>
    public CatalogResourcesProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog, ReplicationWait? settle)
        : base(definition, catalog)
    {
        if (!ResourceIdentifier.TypeEquals(definition.Type, ResourceType))
        {
            throw new ArgumentException($"{nameof(CatalogResourcesProvider)} implements '{ResourceType}', not '{definition.Type}'.", nameof(definition));
        }

        Settle = settle ?? ReplicationWait.Default;
    }

    /// <summary>How long a create waits for the requested resource to show in the catalog.</summary>
    public ReplicationWait Settle { get; init; }

    /// <summary>The catalog's resource list can miss a resource for a moment after its request.</summary>
    public override bool KeyReadLagsCreate => true;

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);
        if (!key.TryGet(CatalogIdProperty, out var catalogId) || !key.TryGet(OriginIdProperty, out var originId))
        {
            return null;
        }

        var resource = await EntitlementManagementGraph.FindCatalogResourceAsync(graph, catalogId, originId, key.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false);
        if (resource is null)
        {
            return null;
        }

        // The match ignores case and a site URL's trailing slash: the view keeps the declared spelling, so that difference is no drift.
        var view = View(resource, catalogId);
        view[OriginIdProperty] = originId;
        return KeyView(view);
    }

    public override async Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = desired.Properties
            ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before creating.");
        string catalogId = Required(desired, properties, CatalogIdProperty);
        string declaredOrigin = Required(desired, properties, OriginIdProperty);
        string originId = AccessPackageResourceOrigin.NormalizeId(declaredOrigin);
        string originSystem = AccessPackageResourceOrigin.Canonical(JsonObjects.GetString(properties, OriginSystemProperty))
            ?? throw new ProviderException(
                EngineDiagnosticCodes.ResourceApplyFailed,
                $"{desired.Describe()}: originSystem '{JsonObjects.GetString(properties, OriginSystemProperty)}' is not one EARM manages ({string.Join(", ", AccessPackageResourceOrigin.Supported)}).",
                desired.Id);
        string noun = AccessPackageResourceOrigin.Noun(originSystem);

        var body = new JsonObject
        {
            ["requestType"] = "adminAdd",
            ["resource"] = new JsonObject { [OriginIdProperty] = originId, [OriginSystemProperty] = originSystem },
            ["catalog"] = new JsonObject { ["id"] = catalogId },
        };

        GraphResponse response;
        try
        {
            response = await graph.PostAsync(new GraphRequest(CollectionPath, desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.IsForbidden)
        {
            throw new ProviderException(
                AccessDenied,
                $"{desired.Describe()}: Graph refused to add {noun} {originId} to catalog {catalogId} ({ex.Code}: {ex.GraphMessage}). {AccessDeniedAdvice(originSystem)} Activate one of those roles, sign in again, and rerun.",
                desired.Id,
                ex);
        }

        if (response.Body is JsonObject request && string.Equals(JsonObjects.GetString(request, "state"), "deliveryFailed", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProviderException(RequestFailed, $"{desired.Describe()}: the request to add {noun} {originId} to catalog {catalogId} ended in state deliveryFailed{Detail(request)}.", desired.Id);
        }

        string siteNote = AccessPackageResourceOrigin.IsSite(originSystem)
            ? " Graph lists a SharePoint Online site in a catalog only to a delegated caller (Learn: accesspackagecatalog-list-resources), so deploy a site as a signed-in user, not as an application."
            : string.Empty;
        var resource = await WaitForResourceAsync(graph, catalogId, originId, desired.Api, cancellationToken).ConfigureAwait(false)
            ?? throw new ProviderException(RequestFailed, $"{desired.Describe()}: {noun} {originId} was still not listed in catalog {catalogId} {Settle.Window.TotalSeconds:0} s after the request was accepted; run the deployment again (the next read finds it once Graph has processed the request).{siteNote}", desired.Id);
        var view = View(resource, catalogId);
        view[OriginIdProperty] = declaredOrigin;
        return new AppliedResource(desired.Id, JsonObjects.GetId(resource)!, view, ResolveKey(desired));
    }

    /// <summary>
    /// The roles Graph wants of the caller for each kind of resource (Learn: entitlementmanagement-post-resourcerequests, entitlement-management-delegate,
    /// "Required roles to add resources to a catalog").
    /// </summary>
    private static string AccessDeniedAdvice(string originSystem)
    {
        if (originSystem == AccessPackageResourceOrigin.Application)
        {
            return "Adding an application to a catalog needs an Identity Governance Administrator, or a catalog owner who is also an owner of the application or an Application Administrator / Cloud Application Administrator (a role that may update its app role assignments); an application caller needs Application.ReadWrite.All.";
        }

        if (originSystem == AccessPackageResourceOrigin.SharePointOnline)
        {
            return "Adding a SharePoint Online site to a catalog needs a catalog owner or Identity Governance Administrator who is also a SharePoint Administrator or an administrator of the site; an application caller needs Sites.FullControl.All.";
        }

        return "Adding a group to a catalog needs a catalog owner or Identity Governance Administrator who is also an owner of the group or holds a role that changes its members; a role-assignable group (isAssignableToRole) can only be added by a Global Administrator or Privileged Role Administrator (or an owner of the group).";
    }

    /// <summary>Nothing is mutable: every property is part of the key (a change is a new tuple, or a recreate of <c>originSystem</c>).</summary>
    public override Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(graph);
        return Task.FromResult(new AppliedResource(desired.Id, existing.GraphId, existing.Raw, ResolveKey(desired)));
    }

    public override async Task DeleteAsync(ResourceKey key, ExistingResource live, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(graph);
        string catalogId = JsonObjects.GetString(live.Raw, CatalogIdProperty) ?? (key.TryGet(CatalogIdProperty, out var fromKey) ? fromKey : string.Empty);
        var body = new JsonObject
        {
            ["requestType"] = "adminRemove",
            ["resource"] = new JsonObject { ["id"] = live.GraphId },
            ["catalog"] = new JsonObject { ["id"] = catalogId },
        };

        try
        {
            await graph.PostAsync(new GraphRequest(CollectionPath, key.Api), body, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            // The resource (or its catalog) is already gone.
        }
        catch (GraphException ex) when (ex.Status == HttpStatusCode.BadRequest)
        {
            throw new ProviderException(
                InUse,
                $"{TypeName} {key.Describe()}: Graph refused to remove resource {live.GraphId} from catalog {catalogId} ({ex.Code}: {ex.GraphMessage}). A {AccessPackageResourceOrigin.Noun(JsonObjects.GetString(live.Raw, OriginSystemProperty) ?? EntitlementManagementGraph.GroupOrigin)} can only leave a catalog once no access package of the catalog grants one of its roles; delete those resource role scopes first.",
                null,
                ex);
        }
    }

    public IEnumerable<ResourceInstance> AppliesBefore(ResourceInstance instance, IReadOnlyList<ResourceInstance> declared)
    {
        return AccessPackageApplyOrder.RoleScopesOfResource(instance, declared);
    }

    // ---- permissions ----

    /// <summary>
    /// EntitlementManagement.ReadWrite.All plus the permission on the resource itself (Learn: entitlementmanagement-post-resourcerequests): Group.ReadWrite.All
    /// for a group, Application.ReadWrite.All for an application, Sites.FullControl.All for a site; all three when <c>originSystem</c> is computed.
    /// </summary>
    public override IReadOnlyList<string> RequiredPermissions(ResourceInstance? desired)
    {
        if (desired is null)
        {
            return Definition.Permissions.Application;
        }

        var properties = desired.Properties ?? desired.Declaration.Properties as JsonObject;
        string? declared = properties is null ? null : JsonObjects.GetString(properties, OriginSystemProperty);
        string? origin = declared is not null && declared.TrimStart().StartsWith('[') ? null : AccessPackageResourceOrigin.Canonical(declared);
        var permissions = new List<string> { "EntitlementManagement.ReadWrite.All" };
        if (origin is null or AccessPackageResourceOrigin.Group)
        {
            permissions.Add("Group.ReadWrite.All");
        }

        if (origin is null or AccessPackageResourceOrigin.Application)
        {
            permissions.Add("Application.ReadWrite.All");
        }

        if (origin is null or AccessPackageResourceOrigin.SharePointOnline)
        {
            permissions.Add("Sites.FullControl.All");
        }

        return permissions;
    }

    private async Task<JsonObject?> WaitForResourceAsync(IGraphClient graph, string catalogId, string originId, GraphApi api, CancellationToken cancellationToken)
    {
        var waited = TimeSpan.Zero;
        while (true)
        {
            var resource = await EntitlementManagementGraph.FindCatalogResourceAsync(graph, catalogId, originId, api, RetryHint.ExpectReplicationLag, cancellationToken).ConfigureAwait(false);
            if (resource is not null && JsonObjects.GetId(resource) is not null)
            {
                return resource;
            }

            if (waited + Settle.Interval > Settle.Window)
            {
                return null;
            }

            await Settle.Delay.DelayAsync(Settle.Interval, cancellationToken).ConfigureAwait(false);
            waited += Settle.Interval;
        }
    }

    /// <summary>The diff view: the key tuple and origin system, plus the Graph object for <c>reference()</c>.</summary>
    private static JsonObject View(JsonObject resource, string catalogId)
    {
        var view = (JsonObject)resource.DeepClone();
        view[CatalogIdProperty] = catalogId;
        view[OriginSystemProperty] ??= EntitlementManagementGraph.GroupOrigin;
        return view;
    }

    /// <summary>Only the key tuple and origin system take part in the diff (Graph's display name, description and timestamps are not declared).</summary>
    private ExistingResource KeyView(JsonObject live)
    {
        var existing = ToExisting(live);
        var properties = new JsonObject
        {
            [CatalogIdProperty] = JsonObjects.GetString(live, CatalogIdProperty),
            [OriginIdProperty] = JsonObjects.GetString(live, OriginIdProperty),
            [OriginSystemProperty] = JsonObjects.GetString(live, OriginSystemProperty),
        };
        return existing with { Properties = properties };
    }

    private static string Required(ResourceInstance desired, JsonObject properties, string name)
    {
        return JsonObjects.GetString(properties, name) is { Length: > 0 } value
            ? value
            : throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: {name} is required.", desired.Id);
    }

    private static string Detail(JsonObject request)
    {
        return JsonObjects.GetString(request, "justification") is { Length: > 0 } text ? $" ({text})" : string.Empty;
    }
}
