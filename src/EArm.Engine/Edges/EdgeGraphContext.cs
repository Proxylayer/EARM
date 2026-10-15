using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Az;
using EArm.Core;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using Microsoft.Extensions.Logging;

namespace EArm.Engine.Edges;

/// <summary>
/// Everything the edge-kind handlers need while an edge pass runs: the Graph client (with the plan's outer retry
/// policy), the live resolver (template objects by Graph id, so reads of just-created targets need no Graph
/// call), per-run caches (<c>memberOf</c> reads, <c>directoryObjects</c> type lookups) and the signed-in principal.
/// One instance per pass; reads run sequentially.
/// </summary>
public sealed class EdgeGraphContext
{
    private readonly IGraphClient _graph;
    private readonly RetryPolicy? _retry;
    private readonly Dictionary<string, ResourceInstance> _instancesByGraphId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<JsonObject>> _memberOf = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _collections = new(StringComparer.OrdinalIgnoreCase);
    private bool _signedInResolved;
    private string? _signedInPrincipalId;

    public EdgeGraphContext(IGraphClient graph, RetryPolicy? retryPolicy, LiveReferenceResolver resolver, IEnumerable<ResourceInstance> instances, ILogger logger, string? signedInPrincipalId = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(logger);
        _graph = graph;
        _retry = retryPolicy;
        Resolver = resolver;
        Logger = logger;
        _signedInPrincipalId = signedInPrincipalId;
        _signedInResolved = signedInPrincipalId is not null;

        foreach (var instance in instances)
        {
            if (!instance.IsSymbolic && (resolver.TryGetGraphId(instance.Id, out var graphId) || resolver.TryGetGraphId(instance.PlannedId, out graphId)))
            {
                _instancesByGraphId[graphId] = instance;
            }
        }
    }

    public LiveReferenceResolver Resolver { get; }

    public ILogger Logger { get; }

    public IGraphClient Graph => _graph;

    /// <summary>The template instance whose live object has this Graph id, when any.</summary>
    public ResourceInstance? FindInstanceByGraphId(string graphId)
    {
        ArgumentNullException.ThrowIfNull(graphId);
        return _instancesByGraphId.TryGetValue(graphId, out var instance) ? instance : null;
    }

    /// <summary>The published live object of a template instance, when any.</summary>
    public JsonObject? LiveObjectOf(ResourceInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (Resolver.TryGetLive(instance.Id, out var live, out _) || Resolver.TryGetLive(instance.PlannedId, out live, out _))
        {
            return live;
        }

        return null;
    }

    public Task<T> WithRetryAsync<T>(Func<CancellationToken, Task<T>> operation, RetryHint hint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return _retry is null ? operation(cancellationToken) : _retry.ExecuteAsync(operation, hint, null, cancellationToken);
    }

    /// <summary>GET inside the retry policy; <c>null</c> for a 404.</summary>
    public Task<JsonNode?> GetAsync(GraphRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return WithRetryAsync(ct => _graph.GetAsync(request, ct), RetryHint.Default, cancellationToken);
    }

    /// <summary>Every object of a paged collection, inside the retry policy; empty for a 404.</summary>
    public async Task<IReadOnlyList<JsonObject>> GetAllAsync(GraphRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return await WithRetryAsync(async ct =>
            {
                var items = new List<JsonObject>();
                await foreach (var item in _graph.GetAllPagesAsync(request, ct).ConfigureAwait(false))
                {
                    if (item is JsonObject obj)
                    {
                        items.Add(obj);
                    }
                }

                return (IReadOnlyList<JsonObject>)items;
            }, RetryHint.Default, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            return [];
        }
    }

    /// <summary>
    /// The direct <c>memberOf</c> of a principal (groups, administrative units, directory roles), read once per
    /// principal and shared by the membership kinds. Each object carries <c>@odata.type</c> and <c>id</c>.
    /// </summary>
    public async Task<IReadOnlyList<JsonObject>> ReadMemberOfAsync(string collection, string principalId, GraphApi api, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(principalId);
        string key = api + "|" + collection + "|" + Edge.NormalizeId(principalId);
        if (_memberOf.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var request = new GraphRequest("/" + collection.Trim('/') + "/" + GraphPath.Segment(principalId) + "/memberOf", api).WithSelect("id").WithTop(999);
        var items = await GetAllAsync(request, cancellationToken).ConfigureAwait(false);
        _memberOf[key] = items;
        return items;
    }

    /// <summary>
    /// The Graph collection an object lives in (<c>applications</c>, <c>servicePrincipals</c>, <c>groups</c>,
    /// <c>users</c>, <c>directory/administrativeUnits</c>): from the template when the id is a published template
    /// object, else <c>GET /directoryObjects/{id}?$select=id</c> (<c>@odata.type</c>). <c>null</c> when the object
    /// does not exist or has an unsupported type.
    /// </summary>
    public async Task<string?> ResolveCollectionAsync(string graphId, GraphApi api, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graphId);
        if (FindInstanceByGraphId(graphId) is { } instance && CollectionOf(instance.Type) is { } known)
        {
            return known;
        }

        string key = Edge.NormalizeId(graphId);
        if (_collections.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var node = await GetAsync(new GraphRequest("/directoryObjects/" + GraphPath.Segment(graphId), api).WithSelect("id"), cancellationToken).ConfigureAwait(false);
        string? collection = node is JsonObject obj ? CollectionOfODataType(JsonObjects.GetString(obj, "@odata.type")) : null;
        _collections[key] = collection;
        return collection;
    }

    /// <summary>
    /// The object id of the signed-in principal: supplied by the caller (preflight), else <c>GET /me?$select=id</c>
    /// once per pass (app-only tokens have no <c>/me</c>; the lookup then yields <c>null</c>).
    /// </summary>
    public async Task<string?> GetSignedInPrincipalIdAsync(GraphApi api, CancellationToken cancellationToken)
    {
        if (_signedInResolved)
        {
            return _signedInPrincipalId;
        }

        _signedInResolved = true;
        try
        {
            var me = await _graph.GetAsync(new GraphRequest("/me", api).WithSelect("id"), cancellationToken).ConfigureAwait(false);
            _signedInPrincipalId = JsonObjects.GetId(me as JsonObject);
        }
        catch (GraphException)
        {
            _signedInPrincipalId = null;
        }
        catch (AzCliException)
        {
            _signedInPrincipalId = null;
        }

        return _signedInPrincipalId;
    }

    /// <summary>The Graph collection of a resource type; <c>null</c> for types that are not directory objects.</summary>
    public static string? CollectionOf(string resourceType)
    {
        ArgumentNullException.ThrowIfNull(resourceType);
        string type = ResourceIdentifier.NormalizeType(resourceType);
        string prefix = ResourceIdentifier.ProviderNamespace + "/";
        string relative = type.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? type[prefix.Length..] : type;
        return relative.ToLowerInvariant() switch
        {
            "users" => "users",
            "groups" => "groups",
            "serviceprincipals" => "servicePrincipals",
            "applications" => "applications",
            "administrativeunits" => "directory/administrativeUnits",
            _ => null,
        };
    }

    /// <summary>The Graph collection for an <c>@odata.type</c> (<c>#microsoft.graph.group</c> → <c>groups</c>); <c>null</c> when unsupported.</summary>
    public static string? CollectionOfODataType(string? odataType)
    {
        if (string.IsNullOrEmpty(odataType))
        {
            return null;
        }

        return odataType.ToLowerInvariant() switch
        {
            "#microsoft.graph.user" => "users",
            "#microsoft.graph.group" => "groups",
            "#microsoft.graph.serviceprincipal" => "servicePrincipals",
            "#microsoft.graph.application" => "applications",
            "#microsoft.graph.administrativeunit" => "directory/administrativeUnits",
            _ => null,
        };
    }

    /// <summary>An absolute <c>@odata.id</c> for a <c>$ref</c> body.</summary>
    public static string ODataId(GraphApi api, string collection, string id)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(id);
        return api.BaseUrl() + "/" + collection.Trim('/') + "/" + GraphPath.Segment(id);
    }
}
