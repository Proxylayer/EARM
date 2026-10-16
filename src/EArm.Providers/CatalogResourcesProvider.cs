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
/// <c>Microsoft.Entra/entitlementManagement/catalogResources</c>: a group added to a catalog. Found by the tuple (<c>catalogId</c>, <c>originId</c>);
/// the GUID name is a label. Every property is part of the identity, so nothing is updated in place.
/// <list type="bullet">
/// <item><b>Read</b>: <c>GET …/catalogs/{catalogId}/resources?$filter=originId eq '…'</c>; the diff view is <c>{ catalogId, originId, originSystem }</c>.</item>
/// <item><b>Create</b>: <c>POST …/resourceRequests</c> <c>{ requestType: adminAdd, resource { originId, originSystem }, catalog { id } }</c>. A request
/// in <c>deliveryFailed</c> is <see cref="RequestFailed"/>; otherwise the catalog's resources are read every <see cref="ReplicationWait.Interval"/> until
/// the group is listed (up to <see cref="ReplicationWait.Window"/>). A 403 is <see cref="AccessDenied"/>, naming the roles a role-assignable group needs.</item>
/// <item><b>Delete</b>: <c>POST …/resourceRequests</c> <c>{ requestType: adminRemove, resource { id }, catalog { id } }</c>; a 400 (a package of the catalog
/// still grants a role of the group) is <see cref="InUse"/>.</item>
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
        return resource is null ? null : KeyView(View(resource, catalogId));
    }

    public override async Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = desired.Properties
            ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before creating.");
        string catalogId = Required(desired, properties, CatalogIdProperty);
        string originId = Required(desired, properties, OriginIdProperty);
        string originSystem = JsonObjects.GetString(properties, OriginSystemProperty) ?? EntitlementManagementGraph.GroupOrigin;

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
                $"{desired.Describe()}: Graph refused to add group {originId} to catalog {catalogId} ({ex.Code}: {ex.GraphMessage}). Adding a group to a catalog needs a catalog owner or Identity Governance Administrator who is also an owner of the group or holds a role that changes its members; a role-assignable group (isAssignableToRole) can only be added by a Global Administrator or Privileged Role Administrator (or an owner of the group). Activate one of those roles, sign in again, and rerun.",
                desired.Id,
                ex);
        }

        if (response.Body is JsonObject request && string.Equals(JsonObjects.GetString(request, "state"), "deliveryFailed", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProviderException(RequestFailed, $"{desired.Describe()}: the request to add group {originId} to catalog {catalogId} ended in state deliveryFailed{Detail(request)}.", desired.Id);
        }

        var resource = await WaitForResourceAsync(graph, catalogId, originId, desired.Api, cancellationToken).ConfigureAwait(false)
            ?? throw new ProviderException(RequestFailed, $"{desired.Describe()}: group {originId} was still not listed in catalog {catalogId} {Settle.Window.TotalSeconds:0} s after the request was accepted; run the deployment again (the next read finds it once Graph has processed the request).", desired.Id);
        return new AppliedResource(desired.Id, JsonObjects.GetId(resource)!, View(resource, catalogId), ResolveKey(desired));
    }

    /// <summary>Nothing is mutable: every property is part of the key (a change is a new tuple, or a recreate of <c>originSystem</c>).</summary>
    public override Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
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
                $"{TypeName} {key.Describe()}: Graph refused to remove resource {live.GraphId} from catalog {catalogId} ({ex.Code}: {ex.GraphMessage}). A group can only leave a catalog once no access package of the catalog grants one of its roles; delete those resource role scopes first.",
                null,
                ex);
        }
    }

    public IEnumerable<ResourceInstance> AppliesBefore(ResourceInstance instance, IReadOnlyList<ResourceInstance> declared)
    {
        return AccessPackageApplyOrder.RoleScopesOfGroup(instance, declared);
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
