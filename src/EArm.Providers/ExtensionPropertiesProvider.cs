using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Secrets;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/applications/extensionProperties</c>: a directory extension definition registered on an application. The resource name is
/// <c>&lt;application uniqueName&gt;/&lt;extension name&gt;</c>; the child name is the short name the template uses (<c>jobGroup</c>), and Graph stores it as
/// <c>extension_&lt;appId without dashes&gt;_&lt;name&gt;</c> (Learn: application-post-extensionproperty), which is what <c>reference(...).name</c> returns and what
/// users, groups and the other target objects carry the values under.
/// <para>
/// Requests per operation (Learn: extensionproperty, application-post-extensionproperty, application-list-extensionproperty, extensionproperty-delete):
/// <list type="bullet">
/// <item><b>Read</b>: <c>GET /applications/{parentId}?$select=id,appId</c> (or the <c>uniqueName</c> alternate key) → <c>GET /applications/{id}/extensionProperties?$filter=name eq 'extension_…_name'</c>; a missing application is "does not exist".</item>
/// <item><b>Create</b>: <c>POST /applications/{id}/extensionProperties { name, dataType, targetObjects, isMultiValued }</c>.</item>
/// <item><b>Update</b>: none. Graph has no update for a directory extension: every property is immutable (overlay <c>immutable: ["*"]</c>), so a change plans a
/// <see cref="ChangeMode.Recreate"/> that needs confirmation (<c>-y</c> or the prompt) and loses the stored values.</item>
/// <item><b>Delete</b>: <c>DELETE /applications/{id}/extensionProperties/{extensionId}</c>; a 404 is "already gone". Every delete carries a warning
/// (<see cref="IDeleteWarningSource"/>): the values stored on every target object become unreachable. An extension synced from on-premises Active
/// Directory cannot be deleted (Learn) and is kept (<see cref="IDeleteRefusalSource"/>).</item>
/// </list>
/// </para>
/// </summary>
[EntraProvider(ResourceType)]
public sealed class ExtensionPropertiesProvider : ProviderBase, IOrphanCandidateSource, IDeleteWarningSource, IDeleteRefusalSource
{
    public const string ResourceType = "Microsoft.Entra/applications/extensionProperties";

    /// <summary>The navigation segment under the application.</summary>
    public const string ChildSegment = "extensionProperties";

    private const string ParentKeyProperty = "uniqueName";

    private const string NameProperty = "name";

    private static readonly string[] ParentSelect = ["id", "appId"];

    private static readonly string[] ManagedProperties = ["id", "name", "dataType", "targetObjects", "isMultiValued", "isSyncedFromOnPremises", "appDisplayName"];

    /// <summary>Parent application (id, appId) found by <c>uniqueName</c> or id, so a parent is looked up once per run.</summary>
    private readonly ConcurrentDictionary<string, (string Id, string AppId)> _parents = new(StringComparer.OrdinalIgnoreCase);

    public ExtensionPropertiesProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog)
    {
        if (!ResourceIdentifier.TypeEquals(definition.Type, ResourceType))
        {
            throw new ArgumentException($"{nameof(ExtensionPropertiesProvider)} implements '{ResourceType}', not '{definition.Type}'.", nameof(definition));
        }
    }

    public ExtensionPropertiesProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog), catalog)
    {
    }

    /// <summary>Learn (application-post-extensionproperty): <c>Application.ReadWrite.OwnedBy</c> for an application this deployment owns, else <c>Application.ReadWrite.All</c>.</summary>
    public override IReadOnlyList<string> RequiredPermissions(ResourceInstance? desired)
    {
        return desired?.Parent is not null ? [ApplicationsProvider.OwnedByPermission] : [ApplicationsProvider.ReadWriteAllPermission];
    }

    /// <summary>The Graph name of a directory extension: <c>extension_&lt;appId without dashes&gt;_&lt;name&gt;</c>.</summary>
    public static string GraphName(string appId, string name)
    {
        ArgumentNullException.ThrowIfNull(appId);
        ArgumentNullException.ThrowIfNull(name);
        return "extension_" + appId.Replace("-", string.Empty, StringComparison.Ordinal) + "_" + name;
    }

    // ---- plan ----

    public override ResourceChange Plan(ResourceInstance desired, ExistingResource? existing, PlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(options);
        if (!desired.IsIgnored && !desired.IsSymbolic && desired.Properties is { } properties
            && JsonObjects.TryGetIgnoreCase(properties, NameProperty, out var declared) && !JsonObjects.IsAbsent(declared) && !SecretJson.IsSecret(declared) && !UnknownJson.IsUnknown(declared)
            && !(JsonValues.TryGetString(declared, out var text) && string.Equals(text, desired.Id.NameLeaf, StringComparison.Ordinal)))
        {
            string message = $"{desired.Describe()}: properties.name ({JsonValues.Render(declared)}) conflicts with the extension name '{desired.Id.NameLeaf}'; give the short name only (Graph adds the 'extension_<appId>_' prefix), or omit the property ({DiagnosticCodes.NameMappedPropertyConflict}).";
            return base.Plan(desired, existing, options) with { Reasons = [message], UnsupportedReason = message };
        }

        return base.Plan(desired, existing, options);
    }

    // ---- read ----

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);
        string name = key.TryGet(NameProperty, out var keyed) ? keyed
            : throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: the key {key.Describe()} carries no extension name.");
        var parent = await TryResolveParentAsync(graph, key.ParentGraphId, ParentUniqueName(key), key.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false);
        if (parent is null)
        {
            return null;
        }

        var live = await FindAsync(graph, parent.Value.Id, GraphName(parent.Value.AppId, name), key.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false);
        return live is null ? null : Existing(live, name);
    }

    // ---- create ----

    public override async Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        var parent = await TryResolveParentAsync(graph, desired.ParentGraphId, desired.Id.Parent?.NameLeaf, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the parent application '{desired.Id.Parent?.NameLeaf}' does not exist.", desired.Id);
        var body = BuildCreateBody(desired);
        if (JsonObjects.FindKey(body, "id") is { } idKey)
        {
            body.Remove(idKey);
        }

        var request = new GraphRequest(ChildCollectionPath(parent.Id), desired.Api, hint: desired.RetryHint);
        var response = await graph.PostAsync(request, body, cancellationToken).ConfigureAwait(false);
        var live = response.Body as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: Graph returned no extension property object.", desired.Id);
        string id = JsonObjects.GetId(live)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: Graph returned an extension property without 'id'.", desired.Id);
        return new AppliedResource(desired.Id, id, live, ResolveKey(desired) with { ParentGraphId = parent.Id });
    }

    // ---- update ----

    /// <summary>Never reached: every property is immutable, so a change is planned as a recreate (delete, then create).</summary>
    public override Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(graph);
        throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: Graph cannot update a directory extension; a changed property is applied by deleting and recreating it.", desired.Id);
    }

    // ---- delete ----

    public override async Task DeleteAsync(ResourceKey key, ExistingResource live, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(graph);
        var parent = await TryResolveParentAsync(graph, key.ParentGraphId, ParentUniqueName(key), key.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false);
        if (parent is null)
        {
            // The application is gone, and its extensions with it.
            return;
        }

        try
        {
            await graph.DeleteAsync(new GraphRequest(ChildCollectionPath(parent.Value.Id) + "/" + GraphPath.Segment(live.GraphId), key.Api), cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            // Already gone.
        }
    }

    /// <summary>Every delete of a directory extension loses data: the values stored under it on every target object become unreachable.</summary>
    public string? DescribeDeleteRisk(ResourceKey key, ExistingResource live)
    {
        ArgumentNullException.ThrowIfNull(live);
        string name = JsonObjects.GetString(live.Raw, NameProperty) ?? live.GraphId;
        string targets = live.Raw["targetObjects"] is JsonArray array ? string.Join(", ", array.Select(t => t?.ToString())) : "its target objects";
        return $"deleting directory extension '{name}' makes the values stored under it on every {targets} object unreachable; recreating the extension does not bring them back.";
    }

    /// <summary>Learn (extensionproperty-delete): only extensions that are not synced from on-premises Active Directory can be deleted.</summary>
    public string? DescribeDeleteRefusal(ResourceKey key, ExistingResource live)
    {
        ArgumentNullException.ThrowIfNull(live);
        return live.Raw["isSyncedFromOnPremises"] is JsonValue synced && synced.TryGetValue<bool>(out bool value) && value
            ? "this directory extension is synced from on-premises Active Directory (Microsoft Entra Connect) and cannot be deleted through Microsoft Graph."
            : null;
    }

    // ---- Graph helpers ----

    private string ChildCollectionPath(string parentId)
    {
        return CollectionPath + "/" + GraphPath.Segment(parentId) + "/" + ChildSegment;
    }

    /// <summary>The extension with this Graph name under the application, or <c>null</c>.</summary>
    private async Task<JsonObject?> FindAsync(IGraphClient graph, string parentId, string graphName, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        var request = new GraphRequest(ChildCollectionPath(parentId), api, hint: hint)
            .WithFilter(NameProperty + " eq '" + ProviderSupport.EscapeLiteral(graphName) + "'")
            .WithSelect(ManagedProperties);
        try
        {
            await foreach (var item in graph.GetAllPagesAsync(request, cancellationToken).ConfigureAwait(false))
            {
                if (item is JsonObject extension && string.Equals(JsonObjects.GetString(extension, NameProperty), graphName, StringComparison.OrdinalIgnoreCase))
                {
                    return extension;
                }
            }
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            return null;
        }

        return null;
    }

    /// <summary>The parent's id and appId (by Graph id, or by the <c>uniqueName</c> alternate key); <c>null</c> when the application does not exist.</summary>
    private async Task<(string Id, string AppId)?> TryResolveParentAsync(IGraphClient graph, string? parentGraphId, string? parentUniqueName, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        string path;
        string cacheKey;
        if (parentGraphId is not null)
        {
            path = CollectionPath + "/" + GraphPath.Segment(parentGraphId);
            cacheKey = "id=" + parentGraphId;
        }
        else if (parentUniqueName is not null)
        {
            path = GraphPath.AlternateKey(CollectionPath, ParentKeyProperty, parentUniqueName);
            cacheKey = ParentKeyProperty + "=" + parentUniqueName;
        }
        else
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: the parent application is not known (neither its Graph id nor its uniqueName).");
        }

        if (_parents.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var parent = await graph.GetAsync(new GraphRequest(path, api, hint: hint).WithSelect(ParentSelect), cancellationToken).ConfigureAwait(false) as JsonObject;
        if (JsonObjects.GetId(parent) is not { } id || JsonObjects.GetString(parent, "appId") is not { } appId)
        {
            return null;
        }

        _parents[cacheKey] = (id, appId);
        return (id, appId);
    }

    private static string? ParentUniqueName(ResourceKey key)
    {
        return key.ParentKey is { } parentKey && parentKey.TryGet(ParentKeyProperty, out var unique) ? unique : null;
    }

    /// <summary>The live extension for the engine: the diff view carries the short name (what the template declares), the raw object the Graph name.</summary>
    private ExistingResource Existing(JsonObject live, string name)
    {
        var existing = ToExisting(live);
        existing.Properties[JsonObjects.FindKey(existing.Properties, NameProperty) ?? NameProperty] = name;
        return existing;
    }

    private static ResourceTypeDefinition RequireDefinition(ResourceTypeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Find(ResourceType)
            ?? throw new InvalidOperationException($"The catalog does not define '{ResourceType}'.");
    }

    // ---- orphan report ----

    /// <summary>The orphan report lists the directory extensions of the applications the template declares.</summary>
    public OrphanScope OrphanScope => OrphanScope.ChildrenOfDeclaredParents;

    public string? OrphanParentType => Definition.ParentType;

    public async Task<IReadOnlyList<OrphanCandidate>> ListOrphanCandidatesAsync(OrphanQuery query, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(graph);
        var candidates = new List<OrphanCandidate>();
        foreach (var parent in query.Parents)
        {
            var resolved = await TryResolveParentAsync(graph, parent.GraphId, null, query.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false);
            if (resolved is null)
            {
                continue;
            }

            string prefix = GraphName(resolved.Value.AppId, string.Empty);
            var request = new GraphRequest(ChildCollectionPath(resolved.Value.Id), query.Api).WithSelect(ManagedProperties);
            await foreach (var item in graph.GetAllPagesAsync(request, cancellationToken).ConfigureAwait(false))
            {
                if (item is JsonObject extension && JsonObjects.GetId(extension) is { } id && JsonObjects.GetString(extension, NameProperty) is { } graphName)
                {
                    string shortName = graphName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? graphName[prefix.Length..] : graphName;
                    candidates.Add(new OrphanCandidate(id, shortName, parent.Name, $"name '{graphName}'"));
                }
            }
        }

        return candidates;
    }
}
