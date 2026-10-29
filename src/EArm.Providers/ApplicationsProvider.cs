using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.Providers;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/applications</c>. The resource name is the Graph
/// <c>uniqueName</c>.
/// <para>
/// Requests per operation:
/// <list type="bullet">
/// <item><b>Read</b>: <c>GET /applications(uniqueName='…')</c> → <c>GET /applications(appId='…')</c> (only when the template declares <c>appId</c>, to adopt a brownfield app); every read selects <see cref="ManagedProperties"/>; a 404 is "does not exist".</item>
/// <item><b>Create</b>: a soft-delete probe (<c>GET /directory/deletedItems/microsoft.graph.application?$filter=uniqueName eq '…'</c>, advanced query) → on a hit either <c>ProviderException(SoftDeletedConflict)</c> or, with <c>--on-soft-deleted-conflict restore</c>, <c>POST /directory/deletedItems/{id}/restore</c>; then the upsert <c>PATCH /applications(uniqueName='…')</c> + <c>Prefer: create-if-missing</c> (201 = created, body is the object; 204 = it existed, re-read by key and adopted); the upsert body carries only the declared properties, never the overlay defaults (it would merge them into an application that appeared after the read); then a second <c>PATCH /applications/{id}</c> when <c>identifierUris</c> carry <c>{appId}</c> (the substituted URIs) or a real create (201) lacks an overlay default's value.</item>
/// <item><b>Update</b>: when roles/scopes are removed or change <c>value</c> / <c>allowedMemberTypes</c> / <c>type</c>, a first <c>PATCH /applications/{id}</c> disabling them; then <c>PATCH /applications/{id}</c> with the merge body; then a re-read.</item>
/// <item><b>Delete</b>: <c>DELETE /applications/{id}</c> (soft delete; cascades the home service principal); a 404 is "already gone".</item>
/// </list>
/// <c>owners</c> is an edge property (catalog <c>relationships</c>): the engine keeps it out of <c>Properties</c>, so it
/// never reaches the body or the diff; the edge pass applies it. <c>appId</c> and <c>id</c> are never written.
/// </para>
/// <para>
/// <see cref="Plan"/> normalises the evaluated properties in place (see <see cref="ApplicationsRules.NormalizeDesired"/>)
/// so that what-if rows and request bodies describe the same object, and it keeps the last
/// <see cref="PlanOptions"/> it saw: the executor plans each instance right before applying it, and <c>CreateAsync</c>
/// has no other way to learn <see cref="PlanOptions.OnSoftDeletedConflict"/>.
/// </para>
/// </summary>
[EntraProvider(ResourceType)]
public sealed partial class ApplicationsProvider : ProviderBase
{
    public const string ResourceType = "Microsoft.Entra/applications";

    public const string OwnedByPermission = "Application.ReadWrite.OwnedBy";

    public const string ReadWriteAllPermission = "Application.ReadWrite.All";

    /// <summary><see cref="ProviderException.Code"/> when Graph refuses to set the uniqueName of an existing application.</summary>
    public const string UniqueNameNotSettable = EngineDiagnosticCodes.UniqueNameNotSettable;

    public const string DeletedItemsPath = "/directory/deletedItems";

    public const string DeletedApplicationsPath = DeletedItemsPath + "/microsoft.graph.application";

    /// <summary>The managed property set every read selects; properties outside it are not diffed.</summary>
    public static readonly string[] ManagedProperties =
    [
        "id", "appId", "uniqueName", "displayName", "signInAudience", "identifierUris", "appRoles", "api", "web", "spa",
        "publicClient", "requiredResourceAccess", "optionalClaims", "groupMembershipClaims", "tags", "notes", "info",
        "isFallbackPublicClient", "keyCredentials", "createdDateTime",
    ];

    private static readonly string[] DeletedItemProperties = ["id", "uniqueName", "displayName", "deletedDateTime"];

    private volatile PlanOptions _planOptions = PlanOptions.Default;

    /// <summary>The top-level properties each template declares, selected next to <see cref="ManagedProperties"/> on its reads (G-08).</summary>
    private readonly DeclaredSelects _selects = new();
    private readonly AlternateKeyFallback _uniqueNameKey = new();

    public ApplicationsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog)
    {
        if (!ResourceIdentifier.TypeEquals(definition.Type, ResourceType))
        {
            throw new ArgumentException($"{nameof(ApplicationsProvider)} implements '{ResourceType}', not '{definition.Type}'.", nameof(definition));
        }
    }

    public ApplicationsProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog), catalog)
    {
    }

    /// <summary>
    /// The deployment-wide options captured by the last <see cref="Plan"/> call (every call of one run carries the
    /// same values). Settable for tests that call <see cref="CreateAsync"/> directly.
    /// </summary>
    public PlanOptions PlanOptions
    {
        get => _planOptions;
        set => _planOptions = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// <c>Application.ReadWrite.OwnedBy</c> suffices when the template declares <c>owners</c> (the caller keeps
    /// managing the apps it owns); otherwise <c>Application.ReadWrite.All</c>. The planner asks
    /// before the properties are evaluated, so the raw declaration is consulted when the edge properties are not
    /// split yet.
    /// </summary>
    public override IReadOnlyList<string> RequiredPermissions(ResourceInstance? desired)
    {
        return desired is not null && DeclaresOwners(desired) ? [OwnedByPermission] : [ReadWriteAllPermission];
    }

    private static bool DeclaresOwners(ResourceInstance desired)
    {
        var source = desired.EdgeProperties ?? desired.Declaration.Properties as JsonObject;
        return JsonObjects.TryGetIgnoreCase(source, "owners", out var owners) && !JsonObjects.IsAbsent(owners);
    }

    /// <summary>An immutable change (uniqueName, appId, id, publisherDomain) is a hard error for applications.</summary>
    protected override bool RecreateOnImmutableChange => false;

    // ---- key ----

    /// <summary>The uniqueName from the name, plus the declared <c>appId</c> (a GUID) as a secondary lookup key for adoption.</summary>
    public override ResourceKey ResolveKey(ResourceInstance desired)
    {
        var key = base.ResolveKey(desired);
        if (desired.Properties is { } properties && JsonObjects.GetString(properties, "appId") is { } appId && Guid.TryParse(appId, out _))
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in key.Values)
            {
                values[pair.Key] = pair.Value;
            }

            values["appId"] = appId;
            key = key with { Values = values };
        }

        _selects.Register(key, Definition, desired);
        return key;
    }

    // ---- read ----

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);

        var select = _selects.For(key, ManagedProperties);
        bool hasKey = false;
        if (key.TryGet("uniqueName", out var uniqueName))
        {
            hasKey = true;
            if (await GetByUniqueNameAsync(graph, uniqueName, key.Api, RetryHint.Default, select, cancellationToken).ConfigureAwait(false) is { } byName)
            {
                return ToExisting(byName);
            }
        }

        if (key.TryGet("appId", out var appId))
        {
            hasKey = true;
            if (await GetApplicationAsync(graph, GraphPath.AlternateKey(CollectionPath, "appId", appId), key.Api, RetryHint.Default, select, cancellationToken).ConfigureAwait(false) is { } byAppId)
            {
                return ToExisting(byAppId);
            }
        }

        if (!hasKey)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: the key {key.Describe()} carries neither uniqueName nor appId.");
        }

        return null;
    }

    // ---- delete ----

    public override async Task DeleteAsync(ResourceKey key, ExistingResource live, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(graph);
        var api = key.Api;
        try
        {
            await graph.DeleteAsync(new GraphRequest(EntityPath(live.GraphId), api), cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            // Already gone (deleted outside EARM, or purged): nothing to do.
        }
    }

    // ---- helpers ----

    private static ResourceTypeDefinition RequireDefinition(ResourceTypeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Find(ResourceType)
            ?? throw new InvalidOperationException($"The catalog does not define '{ResourceType}'.");
    }

    private string EntityPath(string graphId)
    {
        return CollectionPath + "/" + GraphPath.Segment(graphId);
    }

    private static async Task<JsonObject?> GetApplicationAsync(IGraphClient graph, string path, GraphApi api, RetryHint hint, string[] select, CancellationToken cancellationToken)
    {
        var request = new GraphRequest(path, api, hint: hint).WithSelect(select);
        return await graph.GetAsync(request, cancellationToken).ConfigureAwait(false) as JsonObject;
    }

    /// <summary>
    /// <c>GET /applications(uniqueName='…')</c>. Graph documents <c>uniqueName</c> as an alternate key for the upsert only
    /// (the application resource page calls it "Immutable. Read-only."), so a tenant that rejects the key form with a 400
    /// is asked with <c>$filter=uniqueName eq '…'</c> (advanced query) instead; after the first refusal the later lookups of the run ask with the filter
    /// directly (<see cref="AlternateKeyFallback"/>). A 404 stays "does not exist".
    /// </summary>
    private async Task<JsonObject?> GetByUniqueNameAsync(IGraphClient graph, string uniqueName, GraphApi api, RetryHint hint, string[] select, CancellationToken cancellationToken)
    {
        if (!_uniqueNameKey.Refused)
        {
            try
            {
                return await GetApplicationAsync(graph, GraphPath.AlternateKey(CollectionPath, "uniqueName", uniqueName), api, hint, select, cancellationToken).ConfigureAwait(false);
            }
            catch (GraphException ex) when (ex.Status == HttpStatusCode.BadRequest)
            {
                _uniqueNameKey.MarkRefused();
            }
        }

        var request = new GraphRequest(CollectionPath, api, hint: hint)
            .WithFilter("uniqueName eq '" + ProviderSupport.EscapeLiteral(uniqueName) + "'")
            .WithCount()
            .WithConsistencyLevelEventual()
            .WithSelect(select);
        await foreach (var item in graph.GetAllPagesAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (item is JsonObject match && string.Equals(JsonObjects.GetString(match, "uniqueName"), uniqueName, StringComparison.OrdinalIgnoreCase))
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>
    /// Sets the <c>uniqueName</c> of an application that has none (adoption by <c>appId</c>) with
    /// its own PATCH. Graph documents the property as immutable, so a refusal (400) is reported as <see cref="UniqueNameNotSettable"/>
    /// with an explanation instead of a bare Graph error.
    /// </summary>
    public async Task AdoptUniqueNameAsync(IGraphClient graph, string graphId, string uniqueName, GraphApi api, RetryHint hint, string describe, ResourceIdentifier? resourceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(graphId);
        ArgumentException.ThrowIfNullOrWhiteSpace(uniqueName);
        try
        {
            await graph.PatchAsync(new GraphRequest(EntityPath(graphId), api, hint: hint), new JsonObject { ["uniqueName"] = uniqueName }, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.Status == HttpStatusCode.BadRequest)
        {
            throw new ProviderException(
                UniqueNameNotSettable,
                $"{describe}: Graph refused to set uniqueName '{uniqueName}' on the existing application {graphId} ({ex.GraphMessage}). uniqueName is documented as immutable and read-only (it can be assigned by the upsert PATCH /applications(uniqueName='…') that creates the application), so an application created without it cannot be adopted by name; recreate it through EARM, or remove the declared appId and let EARM create a new application.",
                resourceId,
                ex);
        }
    }

    /// <summary>
    /// The soft-deleted application holding <paramref name="uniqueName"/>, or <c>null</c> (see <see cref="DeletedItemsProbe"/>).
    /// Listing deleted applications needs <c>Application.Read.All</c> (Learn: directory-deleteditems-list), which an
    /// <c>Application.ReadWrite.OwnedBy</c>-only identity lacks: a 403 skips the probe with <see cref="ProviderWarnings.SoftDeleteProbeSkipped"/>.
    /// </summary>
    private static Task<JsonObject?> FindSoftDeletedAsync(string uniqueName, GraphApi api, IGraphClient graph, List<ProviderWarning> warnings, CancellationToken cancellationToken)
    {
        return DeletedItemsProbe.FindAsync(graph, DeletedApplicationsPath, "application", "uniqueName", uniqueName, DeletedItemProperties, api, advanced: true, warnings, cancellationToken);
    }
    /// <summary>The upsert addresses the object by its <c>uniqueName</c> (the URL key); Graph documents the property as read-only, so it is never sent in the body.</summary>
    private static void RemoveUniqueName(JsonObject body)
    {
        if (JsonObjects.FindKey(body, "uniqueName") is { } key)
        {
            body.Remove(key);
        }
    }

    /// <summary><c>appId</c> and <c>id</c> are assigned by Graph and rejected in bodies; a declared <c>appId</c> is only a lookup key.</summary>
    private static void RemoveServerAssigned(JsonObject body)
    {
        foreach (string property in new[] { "appId", "id" })
        {
            if (JsonObjects.FindKey(body, property) is { } key)
            {
                body.Remove(key);
            }
        }
    }

    private string RequireId(JsonObject live, ResourceInstance desired)
    {
        return JsonObjects.GetId(live)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{TypeName}: Graph returned an object without 'id' for '{desired.Name}'.", desired.Id);
    }
}
