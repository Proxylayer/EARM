using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Az;
using EArm.Core;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Engine.State;
using GraphApi = EArm.Az.GraphApi;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/servicePrincipals</c> (plan sections 1.3, 3.2, 3.4; work item WI-08). The resource name is the
/// application (client) id the principal is created for: a literal GUID, or an expression such as
/// <c>[reference('api').appId]</c> that the engine resolves once the application exists.
/// <para>
/// Requests per operation (every read selects <see cref="ManagedProperties"/>):
/// <list type="bullet">
/// <item><b>Read</b>: <c>GET /servicePrincipals/{pinnedId}</c> (state id, when known; ignored when its appId is not the key's) → <c>GET /servicePrincipals(appId='…')</c>; a 404 is "does not exist".</item>
/// <item><b>Create</b>: <c>POST /servicePrincipals</c> with <c>{ appId, accountEnabled, appRoleAssignmentRequired, …declared properties }</c> under the instance's retry hint (<see cref="RetryHint.ExpectReplicationLag"/> when the application was created in this run: the Az layer retries the 400 "does not reference a valid application object" / 404 for up to 90 s). A 400 "same value for property appId already exists" means the principal exists (auto-provisioned, or not yet visible to the earlier read): it is re-read by appId under <see cref="RetryHint.ExpectReplicationLag"/> and adopted, with one <c>PATCH /servicePrincipals/{id}</c> when the declared properties differ from it, then re-read.</item>
/// <item><b>Update</b>: <c>PATCH /servicePrincipals/{id}</c> (merge body: changed scalars, arrays such as <c>tags</c> / <c>notificationEmailAddresses</c> / <c>replyUrls</c> / <c>alternativeNames</c> and nested objects such as <c>samlSingleSignOnSettings</c> whole) → <c>GET /servicePrincipals/{id}</c>.</item>
/// <item><b>Delete</b>: <c>DELETE /servicePrincipals/{id}</c> (soft delete; cascades the principal's assignments and grants); a 404 is "already gone".</item>
/// </list>
/// <c>owners</c>, <c>memberOf</c>, <c>ownerOf</c>, <c>appRoleAssignments</c> and <c>directoryRoles</c> are edge properties
/// (catalog <c>relationships</c>): the engine keeps them out of <c>Properties</c>, so they never reach the body or the
/// diff; the edge pass applies them. <c>id</c> and <c>servicePrincipalType</c> are never written; <c>appId</c> only on
/// create. The overlay defaults (<c>accountEnabled: true</c>, <c>appRoleAssignmentRequired: false</c>) are part of the
/// desired state: <see cref="Plan"/> fills them in, so a live principal that drifted from a default is corrected.
/// </para>
/// <para>
/// A principal's <c>appRoles</c> / <c>oauth2PermissionScopes</c> mirror the application's and lag an application PATCH
/// by seconds; they are selected on reads (for <c>reference()</c>) but stripped from the diff. Callers resolving role
/// or scope ids (<c>appRole()</c>, <c>permissionScope()</c>, app role assignments) should use
/// <see cref="ServicePrincipalLookup.FindByAppIdAsync"/>, which always reads fresh.
/// </para>
/// </summary>
[EntraProvider(ResourceType)]
public sealed class ServicePrincipalsProvider : ProviderBase
{
    public const string ResourceType = "Microsoft.Entra/servicePrincipals";

    public const string OwnedByPermission = "Application.ReadWrite.OwnedBy";

    public const string ReadWriteAllPermission = "Application.ReadWrite.All";

    /// <summary>The managed property set every read selects (plan section 3.2); properties outside it are not diffed.</summary>
    public static readonly string[] ManagedProperties =
    [
        "id", "appId", "displayName", "description", "accountEnabled", "appRoleAssignmentRequired", "tags", "notes",
        "loginUrl", "logoutUrl", "homepage", "preferredSingleSignOnMode", "notificationEmailAddresses", "replyUrls",
        "alternativeNames", "samlSingleSignOnSettings", "appRoles", "oauth2PermissionScopes", "servicePrincipalType",
    ];

    public ServicePrincipalsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog)
    {
        if (!ResourceIdentifier.TypeEquals(definition.Type, ResourceType))
        {
            throw new ArgumentException($"{nameof(ServicePrincipalsProvider)} implements '{ResourceType}', not '{definition.Type}'.", nameof(definition));
        }
    }

    public ServicePrincipalsProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog), catalog)
    {
    }

    /// <summary>
    /// <c>Application.ReadWrite.OwnedBy</c> suffices when the principal's application is a resource of this template
    /// (the name references it, or <c>dependsOn</c> names it: the caller owns what it creates); otherwise the
    /// application is pre-existing or first-party and <c>Application.ReadWrite.All</c> is required (plan section 3.4).
    /// </summary>
    public override IReadOnlyList<string> RequiredPermissions(ResourceInstance? desired)
    {
        return desired is not null && ServicePrincipalsRules.ReferencesTemplateApplication(TemplateTargets(desired)) ? [OwnedByPermission] : [ReadWriteAllPermission];
    }

    private static IEnumerable<ResourceIdentifier> TemplateTargets(ResourceInstance desired)
    {
        foreach (var dependency in desired.Dependencies)
        {
            yield return dependency;
        }

        foreach (var occurrence in desired.NameReferences)
        {
            if (occurrence.IsSameTemplate && occurrence.Target is { } target)
            {
                yield return target;
            }
        }
    }

    /// <summary>An immutable change (appId, servicePrincipalType) is a hard error for service principals.</summary>
    protected override bool RecreateOnImmutableChange => false;

    // ---- read ----

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, string? stateId, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);

        bool hasAppId = key.TryGet("appId", out var appId);
        if (stateId is not null && await GetServicePrincipalAsync(graph, EntityPath(stateId), key.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false) is { } pinned)
        {
            // A pinned object for another application is drift (the state outlived a recreate): fall back to the key.
            if (!hasAppId || JsonObjects.KeyEquals(JsonObjects.GetString(pinned, "appId"), appId))
            {
                return ToExisting(pinned);
            }
        }

        if (!hasAppId)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: the key {key.Describe()} carries no appId.");
        }

        var byAppId = await GetServicePrincipalAsync(graph, AppIdPath(appId), key.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false);
        return byAppId is null ? null : ToExisting(byAppId);
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

        string name = desired.Id.NameLeaf;
        if (!desired.IsSymbolic)
        {
            if (!ServicePrincipalsRules.IsAppId(name))
            {
                return Unsupported(desired, existing, $"{desired.Describe()}: the name '{name}' is not an application (client) id; a service principal is named by the appId (a GUID) of the application it is created for, for example [reference('my-app').appId].");
            }

            if (ServicePrincipalsRules.ReconcileDeclaredAppId(properties, name, desired.Describe()) is { } conflict)
            {
                return Unsupported(desired, existing, conflict);
            }
        }

        // The overlay defaults are part of the desired state (they are written on create and corrected on drift).
        ApplyDefaults(properties);

        if (existing is null)
        {
            return base.Plan(desired, null, options);
        }

        string? liveAppId = JsonObjects.GetString(existing.Raw, "appId");
        if (!desired.IsSymbolic && liveAppId is not null && !JsonObjects.KeyEquals(liveAppId, name))
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: the live service principal ({existing.GraphId}) belongs to application '{liveAppId}', not '{name}' (appId is immutable); remove the stale state entry or declare the right application id.");
        }

        return base.Plan(desired, NormalizedExisting(existing, properties), options);
    }

    // ---- create ----

    public override async Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        if (desired.Properties is null)
        {
            throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before creating.");
        }

        string name = desired.Id.NameLeaf;
        var key = ResolveKey(desired);
        var body = BuildCreateBody(desired);
        ServicePrincipalsRules.RemoveServerAssigned(body, keepAppId: true);
        body = ServicePrincipalsRules.OrderCreateBody(body, LeadingCreateProperties());

        var request = new GraphRequest(CollectionPath, desired.Api, hint: desired.RetryHint);
        JsonObject live;
        try
        {
            var response = await graph.PostAsync(request, body, cancellationToken).ConfigureAwait(false);
            live = response.Body as JsonObject
                ?? await GetServicePrincipalAsync(graph, AppIdPath(name), desired.Api, RetryHint.ExpectReplicationLag, cancellationToken).ConfigureAwait(false)
                ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the create returned no object and the principal for appId '{name}' could not be read back.", desired.Id);
        }
        catch (GraphException ex) when (ex.IsAlreadyExists)
        {
            // "Another object with the same value for property appId already exists": the principal exists (created
            // by consent or another run, or not yet visible to the read that planned this create). Adopt it and bring
            // the declared properties onto it.
            live = await AdoptExistingAsync(desired, name, body, graph, cancellationToken).ConfigureAwait(false);
        }

        return new AppliedResource(desired.Id, RequireId(live, desired), live, key);
    }

    private async Task<JsonObject> AdoptExistingAsync(ResourceInstance desired, string appId, JsonObject createBody, IGraphClient graph, CancellationToken cancellationToken)
    {
        var live = await GetServicePrincipalAsync(graph, AppIdPath(appId), desired.Api, RetryHint.ExpectReplicationLag, cancellationToken).ConfigureAwait(false)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: Graph reported that a service principal for appId '{appId}' already exists, but none could be read back.", desired.Id);

        var declared = (JsonObject)createBody.DeepClone();
        ServicePrincipalsRules.RemoveServerAssigned(declared, keepAppId: false);
        var existing = ToExisting(live);
        var options = DiffOptionsFor(existing: true);
        var deltas = JsonDiff.Diff(declared, existing.Properties, options);
        if (declared.Count == 0 || !deltas.Any(d => d.HasEffect))
        {
            return live;
        }

        var patch = JsonDiff.BuildPatchBody(declared, existing.Properties, deltas, options);
        ServicePrincipalsRules.RemoveServerAssigned(patch, keepAppId: false);
        string path = EntityPath(existing.GraphId);
        if (patch.Count > 0)
        {
            // The object may have appeared a moment ago: the PATCH by id may hit replication lag.
            await graph.PatchAsync(new GraphRequest(path, desired.Api, hint: RetryHint.ExpectReplicationLag), patch, cancellationToken).ConfigureAwait(false);
        }

        return await GetServicePrincipalAsync(graph, path, desired.Api, RetryHint.ExpectReplicationLag, cancellationToken).ConfigureAwait(false)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the adopted service principal ({existing.GraphId}) vanished after the update.", desired.Id);
    }

    // ---- update ----

    public override async Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = desired.Properties
            ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before updating.");

        var key = ResolveKey(desired);
        var body = BuildPatchBody(desired, NormalizedExisting(existing, properties), change);
        ServicePrincipalsRules.RemoveServerAssigned(body, keepAppId: false);

        string path = EntityPath(existing.GraphId);
        if (body.Count > 0)
        {
            await graph.PatchAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
        }

        var live = await GetServicePrincipalAsync(graph, path, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the service principal ({existing.GraphId}) vanished after the update.", desired.Id);
        return new AppliedResource(desired.Id, existing.GraphId, live, key);
    }

    // ---- delete ----

    public override async Task DeleteAsync(StateEntry entry, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(graph);
        var api = entry.ToResourceKey(Catalog).Api;
        try
        {
            await graph.DeleteAsync(new GraphRequest(EntityPath(entry.GraphId), api), cancellationToken).ConfigureAwait(false);
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

    private string AppIdPath(string appId)
    {
        return GraphPath.AlternateKey(CollectionPath, "appId", appId);
    }

    /// <summary><c>appId</c> first, then the overlay defaults in overlay order; the declared properties follow.</summary>
    private IEnumerable<string> LeadingCreateProperties()
    {
        yield return "appId";
        if (Definition.Defaults is { } defaults)
        {
            foreach (var pair in defaults)
            {
                yield return pair.Key;
            }
        }
    }

    private static async Task<JsonObject?> GetServicePrincipalAsync(IGraphClient graph, string path, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        var request = new GraphRequest(path, api, hint: hint).WithSelect(ManagedProperties);
        return await graph.GetAsync(request, cancellationToken).ConfigureAwait(false) as JsonObject;
    }

    private static ExistingResource NormalizedExisting(ExistingResource existing, JsonObject desired)
    {
        return existing with { Properties = ServicePrincipalsRules.NormalizeLive(existing, desired) };
    }

    private ResourceChange Unsupported(ResourceInstance desired, ExistingResource? existing, string message)
    {
        return new ResourceChange
        {
            Id = desired.Id,
            ResourceType = TypeName,
            Type = existing is null ? ChangeType.Create : ChangeType.Modify,
            Mode = existing is null ? ChangeMode.Create : ChangeMode.Patch,
            GraphId = existing?.GraphId,
            Before = existing is null ? null : RenderBefore(existing.Properties, desired.Properties),
            After = JsonDiff.RenderObject(desired.Properties),
            IsUnknown = desired.HasUnknownProperties || desired.IsSymbolic,
            IsSymbolicId = desired.IsSymbolic,
            Reasons = [message],
            UnsupportedReason = message,
        };
    }

    private string RequireId(JsonObject live, ResourceInstance desired)
    {
        return JsonObjects.GetId(live)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{TypeName}: Graph returned an object without 'id' for '{desired.Name}'.", desired.Id);
    }
}
