using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Az;
using EntraArm.Core;
using EntraArm.Core.Diagnostics;
using EntraArm.Core.Json;
using EntraArm.Core.Secrets;
using EntraArm.Core.Types;
using EntraArm.Engine;
using EntraArm.Engine.Providers;
using EntraArm.Engine.State;
using EntraArm.Expressions.Values;
using GraphApi = EntraArm.Az.GraphApi;

namespace EntraArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/applications</c> (plan sections 1.3, 3.2; work item WI-07). The resource name is the Graph
/// <c>uniqueName</c>.
/// <para>
/// Requests per operation:
/// <list type="bullet">
/// <item><b>Read</b>: <c>GET /applications/{pinnedId}</c> (state id, when known) → <c>GET /applications(uniqueName='…')</c> → <c>GET /applications(appId='…')</c> (only when the template declares <c>appId</c>, to adopt a brownfield app); every read selects <see cref="ManagedProperties"/>; a 404 is "does not exist".</item>
/// <item><b>Create</b>: a soft-delete probe (<c>GET /directory/deletedItems/microsoft.graph.application?$filter=uniqueName eq '…'</c>, advanced query) → on a hit either <c>ProviderException(SoftDeletedConflict)</c> or, with <c>--on-soft-deleted-conflict restore</c>, <c>POST /directory/deletedItems/{id}/restore</c>; then the upsert <c>PATCH /applications(uniqueName='…')</c> + <c>Prefer: create-if-missing</c> (201 = created, body is the object; 204 = it existed, re-read by key and adopted); then, when <c>identifierUris</c> carry <c>{appId}</c>, a second <c>PATCH /applications/{id}</c> with the substituted URIs.</item>
/// <item><b>Update</b>: when roles/scopes are removed or change <c>value</c> / <c>allowedMemberTypes</c> / <c>type</c>, a first <c>PATCH /applications/{id}</c> disabling them; then <c>PATCH /applications/{id}</c> with the merge body; then a re-read.</item>
/// <item><b>Delete</b>: <c>DELETE /applications/{id}</c> (soft delete; cascades the home service principal); a 404 is "already gone".</item>
/// </list>
/// <c>owners</c> is an edge property (catalog <c>relationships</c>): the engine keeps it out of <c>Properties</c>, so it
/// never reaches the body or the diff; the edge pass applies it. <c>appId</c> and <c>id</c> are never written.
/// </para>
/// <para>
/// <see cref="Plan"/> normalises the evaluated properties in place (see <see cref="ApplicationsRules.NormalizeDesired"/>)
/// so that what-if rows, request bodies and the state hash all describe the same object, and it keeps the last
/// <see cref="PlanOptions"/> it saw: the executor plans each instance right before applying it, and <c>CreateAsync</c>
/// has no other way to learn <see cref="PlanOptions.OnSoftDeletedConflict"/>.
/// </para>
/// </summary>
[EntraProvider(ResourceType)]
public sealed class ApplicationsProvider : ProviderBase
{
    public const string ResourceType = "Microsoft.Entra/applications";

    public const string OwnedByPermission = "Application.ReadWrite.OwnedBy";

    public const string ReadWriteAllPermission = "Application.ReadWrite.All";

    public const string DeletedItemsPath = "/directory/deletedItems";

    public const string DeletedApplicationsPath = DeletedItemsPath + "/microsoft.graph.application";

    /// <summary>The managed property set every read selects (plan section 3.2); properties outside it are not diffed.</summary>
    public static readonly string[] ManagedProperties =
    [
        "id", "appId", "uniqueName", "displayName", "signInAudience", "identifierUris", "appRoles", "api", "web", "spa",
        "publicClient", "requiredResourceAccess", "optionalClaims", "groupMembershipClaims", "tags", "notes", "info",
        "isFallbackPublicClient", "keyCredentials",
    ];

    private static readonly string[] DeletedItemProperties = ["id", "uniqueName", "displayName", "deletedDateTime"];

    private volatile PlanOptions _planOptions = PlanOptions.Default;

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
    /// managing the apps it owns); otherwise <c>Application.ReadWrite.All</c> (plan section 3.4). The planner asks
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

        return key;
    }

    // ---- read ----

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, string? stateId, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);

        if (stateId is not null && await GetApplicationAsync(graph, EntityPath(stateId), key.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false) is { } pinned)
        {
            return ToExisting(pinned);
        }

        bool hasKey = false;
        if (key.TryGet("uniqueName", out var uniqueName))
        {
            hasKey = true;
            if (await GetApplicationAsync(graph, GraphPath.AlternateKey(CollectionPath, "uniqueName", uniqueName), key.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false) is { } byName)
            {
                return ToExisting(byName);
            }
        }

        if (key.TryGet("appId", out var appId))
        {
            hasKey = true;
            if (await GetApplicationAsync(graph, GraphPath.AlternateKey(CollectionPath, "appId", appId), key.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false) is { } byAppId)
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

    // ---- plan ----

    public override ResourceChange Plan(ResourceInstance desired, ExistingResource? existing, PlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(options);
        _planOptions = options;

        if (desired.IsIgnored || desired.Properties is not { } properties)
        {
            return base.Plan(desired, existing, options);
        }

        string name = desired.Id.NameLeaf;
        if (DeclaredNameConflict(properties, name, desired) is { } conflict)
        {
            return Unsupported(desired, existing, conflict);
        }

        ApplicationsRules.NormalizeDesired(properties, desired.IsSymbolic ? null : name);

        var duplicates = ApplicationsRules.DuplicateValues(properties);
        if (duplicates.Count > 0)
        {
            string list = string.Join(", ", duplicates.Select(v => "'" + v + "'"));
            return Unsupported(desired, existing, $"{desired.Describe()}: value{(duplicates.Count == 1 ? string.Empty : "s")} {list} occur{(duplicates.Count == 1 ? "s" : string.Empty)} more than once across appRoles and api.oauth2PermissionScopes; Graph requires 'value' to be unique across both.");
        }

        var reasons = new List<string>();
        if (existing is null)
        {
            if (JsonObjects.GetString(properties, "appId") is { } declaredAppId)
            {
                return Unsupported(desired, null, $"{desired.Describe()}: properties.appId ('{declaredAppId}') is declared but no application with uniqueName '{name}' or that appId exists; Graph assigns the appId on create, so remove the property, or declare the appId of an existing application to adopt it.");
            }

            if (ApplicationsRules.HasDeferredIdentifierUris(properties))
            {
                reasons.Add($"identifierUris containing '{ApplicationsRules.AppIdPlaceholder}' are written by a second PATCH once Graph has assigned the appId");
            }

            var create = base.Plan(desired, null, options);
            return reasons.Count == 0 ? create : create with { Reasons = [.. create.Reasons, .. reasons] };
        }

        if (JsonObjects.GetString(existing.Raw, "appId") is { } liveAppId)
        {
            ApplicationsRules.SubstituteAppId(properties, liveAppId);
        }

        string? liveUniqueName = JsonObjects.GetString(existing.Raw, "uniqueName");
        if (liveUniqueName is not null && !string.Equals(liveUniqueName, name, StringComparison.Ordinal))
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: the live application ({existing.GraphId}) has uniqueName '{liveUniqueName}', which cannot be changed to '{name}' (uniqueName is immutable).");
        }

        var normalized = NormalizedExisting(existing, properties);
        var change = base.Plan(desired, normalized, options);
        if (change.HasError)
        {
            return change;
        }

        if (liveUniqueName is null)
        {
            // Brownfield adoption: the object was found by appId; the update sets its uniqueName.
            reasons.Add($"the existing application ({existing.GraphId}) has no uniqueName; it is adopted by setting uniqueName to '{name}'");
            var adoption = new PropertyDelta(JsonPath.Of("properties", "uniqueName"), PropertyChangeType.Create, null, JsonValue.Create(name));
            change = change with { Type = ChangeType.Modify, Mode = ChangeMode.Patch, Deltas = [.. change.Deltas, adoption] };
        }

        if (change.Type == ChangeType.Modify)
        {
            if (ApplicationsRules.DisableStep(ApplicationsRules.AppRolesOf(properties), ApplicationsRules.AppRolesOf(normalized.Properties), ApplicationsRules.AppRoleBreakingProperties) is not null)
            {
                reasons.Add("appRoles that are removed or change value/allowedMemberTypes are disabled by a first PATCH before the final array is written");
            }

            if (ApplicationsRules.DisableStep(ApplicationsRules.ScopesOf(properties), ApplicationsRules.ScopesOf(normalized.Properties), ApplicationsRules.ScopeBreakingProperties) is not null)
            {
                reasons.Add("api.oauth2PermissionScopes that are removed or change value/type are disabled by a first PATCH before the final array is written");
            }
        }

        return reasons.Count == 0 ? change : change with { Reasons = [.. change.Reasons, .. reasons] };
    }

    // ---- create ----

    public override async Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = desired.Properties
            ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before creating.");

        string name = desired.Id.NameLeaf;
        var key = ResolveKey(desired);
        var derived = ApplicationsRules.NormalizeDesired(properties, name);
        var body = BuildCreateBody(desired);
        RemoveServerAssigned(body);
        bool deferredUris = ApplicationsRules.RemoveDeferredIdentifierUris(body);

        // The key lookup was a 404, so the only other holder of the uniqueName is a soft-deleted object (30-day window).
        if (await FindSoftDeletedAsync(name, desired.Api, graph, cancellationToken).ConfigureAwait(false) is { } deleted)
        {
            string deletedId = JsonObjects.GetId(deleted) ?? "?";
            if (_planOptions.OnSoftDeletedConflict != SoftDeletedConflict.Restore)
            {
                string when = JsonObjects.GetString(deleted, "deletedDateTime") is { } stamp ? $", deleted {stamp}" : string.Empty;
                throw new ProviderException(
                    ProviderException.SoftDeletedConflict,
                    $"{desired.Describe()}: uniqueName '{name}' is held by a soft-deleted application (id {deletedId}{when}), which keeps the key for 30 days. Rerun with --on-soft-deleted-conflict restore to restore and update it, or purge it first (DELETE /directory/deletedItems/{deletedId}).",
                    desired.Id);
            }

            var restore = new GraphRequest(DeletedItemsPath + "/" + GraphPath.Segment(deletedId) + "/restore", desired.Api, hint: desired.RetryHint);
            await graph.PostAsync(restore, null, cancellationToken).ConfigureAwait(false);
        }

        string upsertPath = GraphPath.AlternateKey(CollectionPath, "uniqueName", name);
        var upsert = new GraphRequest(upsertPath, desired.Api, hint: desired.RetryHint).WithPrefer("create-if-missing");
        var response = await graph.PatchAsync(upsert, body, cancellationToken).ConfigureAwait(false);

        JsonObject live;
        if (response.IsCreated && response.Body is JsonObject created)
        {
            live = created;
        }
        else
        {
            // 204: the object existed (or was just restored) and the body was merged into it; adopt it by re-reading.
            live = await GetApplicationAsync(graph, upsertPath, desired.Api, RetryHint.ExpectReplicationLag, cancellationToken).ConfigureAwait(false)
                ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the upsert returned no object and '{name}' could not be read back.", desired.Id);
        }

        string id = RequireId(live, desired);
        if (deferredUris)
        {
            string appId = JsonObjects.GetString(live, "appId")
                ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: Graph returned no appId, so identifierUris containing '{ApplicationsRules.AppIdPlaceholder}' cannot be written.", desired.Id);
            ApplicationsRules.SubstituteAppId(properties, appId);
            JsonObjects.TryGetIgnoreCase(properties, "identifierUris", out var uris);
            var finalUris = SecretJson.Reveal(JsonValues.Clone(uris));

            // The object was created a moment ago: the PATCH by id may hit replication lag.
            var second = new GraphRequest(EntityPath(id), desired.Api, hint: RetryHint.ExpectReplicationLag);
            await graph.PatchAsync(second, new JsonObject { ["identifierUris"] = finalUris }, cancellationToken).ConfigureAwait(false);
            live[JsonObjects.FindKey(live, "identifierUris") ?? "identifierUris"] = JsonValues.Clone(finalUris);
        }

        return new AppliedResource(desired.Id, id, live, key, derived.ToExtra());
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

        string name = desired.Id.NameLeaf;
        var key = ResolveKey(desired);
        var derived = ApplicationsRules.NormalizeDesired(properties, name);
        if (JsonObjects.GetString(existing.Raw, "appId") is { } liveAppId)
        {
            ApplicationsRules.SubstituteAppId(properties, liveAppId);
        }

        var normalized = NormalizedExisting(existing, properties);
        var body = BuildPatchBody(desired, normalized, change);
        RemoveServerAssigned(body);
        if (JsonObjects.GetString(existing.Raw, "uniqueName") is null)
        {
            body["uniqueName"] = name;
        }

        string path = EntityPath(existing.GraphId);
        var request = new GraphRequest(path, desired.Api, hint: desired.RetryHint);

        var disabledRoles = ApplicationsRules.DisableStep(ApplicationsRules.AppRolesOf(properties), ApplicationsRules.AppRolesOf(normalized.Properties), ApplicationsRules.AppRoleBreakingProperties);
        var disabledScopes = ApplicationsRules.DisableStep(ApplicationsRules.ScopesOf(properties), ApplicationsRules.ScopesOf(normalized.Properties), ApplicationsRules.ScopeBreakingProperties);
        if (disabledRoles is not null || disabledScopes is not null)
        {
            // Step 1 of 2: Graph refuses to remove an enabled role/scope or change its value/allowedMemberTypes/type.
            var first = new JsonObject();
            if (disabledRoles is not null)
            {
                first["appRoles"] = disabledRoles;
            }

            if (disabledScopes is not null)
            {
                JsonObjects.TryGetIgnoreCase(normalized.Properties, "api", out var liveApi);
                var api = (JsonObject?)JsonValues.Clone(liveApi) ?? new JsonObject();
                api[JsonObjects.FindKey(api, "oauth2PermissionScopes") ?? "oauth2PermissionScopes"] = disabledScopes;
                first["api"] = api;
            }

            await graph.PatchAsync(request, first, cancellationToken).ConfigureAwait(false);
        }

        if (body.Count > 0)
        {
            await graph.PatchAsync(request, body, cancellationToken).ConfigureAwait(false);
        }

        var live = await GetApplicationAsync(graph, path, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the application ({existing.GraphId}) vanished after the update.", desired.Id);
        return new AppliedResource(desired.Id, existing.GraphId, live, key, derived.ToExtra());
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
            // Already gone (deleted outside EntraARM, or purged): nothing to do.
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

    private static async Task<JsonObject?> GetApplicationAsync(IGraphClient graph, string path, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        var request = new GraphRequest(path, api, hint: hint).WithSelect(ManagedProperties);
        return await graph.GetAsync(request, cancellationToken).ConfigureAwait(false) as JsonObject;
    }

    /// <summary>
    /// The soft-deleted application holding <paramref name="uniqueName"/>, or <c>null</c>. Uses the filtered advanced
    /// query of plan section 3.2; should Graph reject the filter on this endpoint (400), the deleted applications are
    /// listed and matched client-side instead (a 30-day window, usually small).
    /// </summary>
    private static async Task<JsonObject?> FindSoftDeletedAsync(string uniqueName, GraphApi api, IGraphClient graph, CancellationToken cancellationToken)
    {
        string filter = "uniqueName eq '" + uniqueName.Replace("'", "''", StringComparison.Ordinal) + "'";
        var filtered = new GraphRequest(DeletedApplicationsPath, api)
            .WithFilter(filter)
            .WithCount()
            .WithConsistencyLevelEventual()
            .WithSelect(DeletedItemProperties);
        try
        {
            return await FirstDeletedMatchAsync(graph, filtered, uniqueName, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.Status == HttpStatusCode.BadRequest)
        {
            var scan = new GraphRequest(DeletedApplicationsPath, api).WithSelect(DeletedItemProperties).WithTop(999);
            return await FirstDeletedMatchAsync(graph, scan, uniqueName, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<JsonObject?> FirstDeletedMatchAsync(IGraphClient graph, GraphRequest request, string uniqueName, CancellationToken cancellationToken)
    {
        await foreach (var item in graph.GetAllPagesAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (item is JsonObject deleted && string.Equals(JsonObjects.GetString(deleted, "uniqueName"), uniqueName, StringComparison.OrdinalIgnoreCase))
            {
                return deleted;
            }
        }

        return null;
    }

    private ExistingResource NormalizedExisting(ExistingResource existing, JsonObject desired)
    {
        return existing with { Properties = ApplicationsRules.NormalizeLive(existing, desired) };
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

    /// <summary>Mirrors <see cref="ProviderBase.InjectName"/> at plan time: a declared <c>uniqueName</c> must equal the resource name.</summary>
    private static string? DeclaredNameConflict(JsonObject properties, string name, ResourceInstance desired)
    {
        if (!JsonObjects.TryGetIgnoreCase(properties, "uniqueName", out var declared) || JsonObjects.IsAbsent(declared) || SecretJson.IsSecret(declared) || UnknownJson.IsUnknown(declared))
        {
            return null;
        }

        if (JsonValues.TryGetString(declared, out var text) && string.Equals(text, name, StringComparison.Ordinal))
        {
            return null;
        }

        return $"{desired.Describe()}: properties.uniqueName ({JsonValues.Render(declared)}) conflicts with the resource name '{name}'; the name is written to uniqueName, so either omit the property or make it equal ({DiagnosticCodes.NameMappedPropertyConflict}).";
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
