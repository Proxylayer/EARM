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
using EArm.Expressions.Values;
using GraphApi = EArm.Az.GraphApi;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/oauth2PermissionGrants</c> (plan sections 1.3, 3.2; work item WI-12): a delegated permission
/// grant (admin consent) of a client service principal on a resource service principal. The resource name is a
/// deterministic GUID chosen with <c>guid(...)</c>; the Graph object is found by the composite key (<c>clientId</c>,
/// <c>resourceId</c>, <c>consentType</c>, <c>principalId</c>) and its id pinned in the state. Sugar: <c>scopes</c>
/// (an array of permission values) becomes the space-separated <c>scope</c> string. Only <c>scope</c> is mutable
/// (compared as a set of values); a key change under the same name is <see cref="ChangeMode.Recreate"/>.
/// <para>
/// Requests per operation:
/// <list type="bullet">
/// <item><b>Prepare</b> (before the key): <c>GET /servicePrincipals/{resourceId}?$select=id,appId,displayName,appRoles,oauth2PermissionScopes</c> under the instance's retry hint; a scope value the resource does not expose (or has disabled) is a <see cref="ProviderException"/> (<see cref="InvalidGrant"/>): an unsupported what-if row, a failed create. Graph does not validate scope values.</item>
/// <item><b>Read</b>: <c>GET /oauth2PermissionGrants/{pinnedId}</c> (state id, when known) → <c>GET /oauth2PermissionGrants?$filter=clientId eq '…' and resourceId eq '…' and consentType eq '…'</c> (plus <c>and principalId eq '…'</c> for <c>Principal</c>).</item>
/// <item><b>Create</b>: <c>POST /oauth2PermissionGrants { clientId, resourceId, consentType, principalId (null for AllPrincipals), scope }</c> under the instance's retry hint. A 400 "Permission entry already exists" is idempotent success: the grant is looked up by key, patched when its scope set differs, and adopted.</item>
/// <item><b>Update</b>: <c>PATCH /oauth2PermissionGrants/{id} { scope }</c> → <c>GET /oauth2PermissionGrants/{id}</c>.</item>
/// <item><b>Delete</b>: <c>DELETE /oauth2PermissionGrants/{id}</c>; a 404 is "already gone".</item>
/// </list>
/// </para>
/// </summary>
[EntraProvider(ResourceType)]
public sealed class OAuth2PermissionGrantsProvider : ProviderBase
{
    public const string ResourceType = "Microsoft.Entra/oauth2PermissionGrants";

    /// <summary><see cref="ProviderException"/> code: the declared grant cannot be made (unknown scope value, bad consent type, bad shape).</summary>
    public const string InvalidGrant = "PermissionGrantInvalid";

    public OAuth2PermissionGrantsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog)
    {
        if (!ResourceIdentifier.TypeEquals(definition.Type, ResourceType))
        {
            throw new ArgumentException($"{nameof(OAuth2PermissionGrantsProvider)} implements '{ResourceType}', not '{definition.Type}'.", nameof(definition));
        }
    }

    public OAuth2PermissionGrantsProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog), catalog)
    {
    }

    /// <summary>A key change (clientId, resourceId, consentType, principalId) replaces the grant.</summary>
    protected override bool RecreateOnImmutableChange => true;

    // ---- prepare (sugar + live validation) ----

    public override async Task PrepareAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = desired.Properties
            ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before preparing.");

        foreach (var property in properties.ToList())
        {
            if (!OAuth2PermissionGrantsRules.AllowedProperties.Contains(property.Key, StringComparer.OrdinalIgnoreCase))
            {
                throw Invalid(desired, $"properties.{property.Key} is not a property of a permission grant; expected clientId, resourceId, consentType, principalId and scope (or scopes).");
            }
        }

        // consentType: AllPrincipals (principalId null) or Principal (principalId required).
        string? consentType = null;
        if (JsonObjects.TryGetIgnoreCase(properties, "consentType", out var consentNode) && !JsonObjects.IsAbsent(consentNode) && !UnknownJson.IsUnknown(consentNode))
        {
            consentType = OAuth2PermissionGrantsRules.NormalizeConsentType(JsonObjects.GetString(properties, "consentType"))
                ?? throw Invalid(desired, $"consentType must be {OAuth2PermissionGrantsRules.ConsentAllPrincipals} or {OAuth2PermissionGrantsRules.ConsentPrincipal}, got {JsonValues.Render(consentNode)}.");
            Set(properties, "consentType", JsonValue.Create(consentType));
        }

        bool hasPrincipal = JsonObjects.TryGetIgnoreCase(properties, "principalId", out var principalNode) && !JsonObjects.IsAbsent(principalNode);
        if (consentType == OAuth2PermissionGrantsRules.ConsentAllPrincipals)
        {
            if (hasPrincipal)
            {
                throw Invalid(desired, "principalId must be omitted (or null) when consentType is AllPrincipals; use consentType Principal to consent for one user.");
            }

            Set(properties, "principalId", null);
        }
        else if (consentType == OAuth2PermissionGrantsRules.ConsentPrincipal)
        {
            if (!hasPrincipal)
            {
                throw Invalid(desired, "principalId (the user's object id) is required when consentType is Principal.");
            }

            if (JsonObjects.GetString(properties, "principalId") is { } principalId && !Guid.TryParse(principalId, out _))
            {
                throw Invalid(desired, $"principalId '{principalId}' is not an object id (GUID).");
            }
        }

        // scopes (array sugar) → scope (string); then the scope string normalised to a de-duplicated value list.
        if (JsonObjects.TryGetIgnoreCase(properties, "scopes", out var scopesNode) && !JsonObjects.IsAbsent(scopesNode))
        {
            JsonNode? fromArray = ScopeFromArray(desired, scopesNode);
            if (JsonObjects.TryGetIgnoreCase(properties, "scope", out var scopeNode) && !JsonObjects.IsAbsent(scopeNode))
            {
                if (JsonValues.TryGetString(fromArray, out var joined) && JsonValues.TryGetString(scopeNode, out var declaredScope) && !OAuth2PermissionGrantsRules.SameSet(joined, declaredScope))
                {
                    throw Invalid(desired, $"scope ('{declaredScope}') and scopes ({joined}) list different permission values; declare one of them.");
                }
            }
            else
            {
                Set(properties, "scope", fromArray);
            }

            Remove(properties, "scopes");
        }

        string? scope = JsonObjects.GetString(properties, "scope");
        if (scope is not null)
        {
            var tokens = OAuth2PermissionGrantsRules.Tokens(scope);
            if (tokens.Count == 0)
            {
                throw Invalid(desired, "scope must list at least one permission value (for example 'User.Read openid').");
            }

            Set(properties, "scope", JsonValue.Create(OAuth2PermissionGrantsRules.Join(tokens)));

            // Graph accepts any text as scope; the values are checked against the resource principal here.
            if (JsonObjects.GetString(properties, "resourceId") is { } resourceId)
            {
                if (!Guid.TryParse(resourceId, out _))
                {
                    throw Invalid(desired, $"resourceId '{resourceId}' is not an object id (GUID).");
                }

                var resource = await AppRoleAssignmentsLookup.FindByIdAsync(graph, resourceId, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false)
                    ?? throw Invalid(desired, $"resource service principal '{resourceId}' does not exist.");
                var unknown = OAuth2PermissionGrantsRules.UnknownScopes(tokens, resource);
                if (unknown.Count > 0)
                {
                    throw Invalid(desired, $"scope value{(unknown.Count == 1 ? string.Empty : "s")} {string.Join(", ", unknown)} {(unknown.Count == 1 ? "is" : "are")} not delegated permission{(unknown.Count == 1 ? string.Empty : "s")} (oauth2PermissionScopes) of service principal '{resource.DisplayName ?? resource.AppId}' ({resource.Id}); exposed values: {OAuth2PermissionGrantsRules.ScopeValues(resource)}.");
                }

                var disabled = OAuth2PermissionGrantsRules.DisabledScopes(tokens, resource);
                if (disabled.Count > 0)
                {
                    throw Invalid(desired, $"scope value{(disabled.Count == 1 ? string.Empty : "s")} {string.Join(", ", disabled)} {(disabled.Count == 1 ? "is" : "are")} disabled on service principal '{resource.DisplayName ?? resource.AppId}' ({resource.Id}); enable the permission before granting it.");
                }
            }
        }
    }

    private JsonNode? ScopeFromArray(ResourceInstance desired, JsonNode? scopes)
    {
        if (UnknownJson.IsUnknown(scopes))
        {
            return JsonValues.Clone(scopes);
        }

        if (scopes is not JsonArray array)
        {
            throw Invalid(desired, $"scopes must be an array of permission values, got {JsonValues.Describe(scopes)}.");
        }

        var tokens = new List<string>();
        foreach (var item in array)
        {
            if (UnknownJson.IsUnknown(item))
            {
                return JsonValues.Clone(item);
            }

            if (!JsonValues.TryGetString(item, out var text) || string.IsNullOrWhiteSpace(text))
            {
                throw Invalid(desired, $"scopes entries must be permission values (strings), got {JsonValues.Describe(item)}.");
            }

            tokens.AddRange(OAuth2PermissionGrantsRules.Tokens(text));
        }

        return JsonValue.Create(OAuth2PermissionGrantsRules.Join(OAuth2PermissionGrantsRules.Tokens(OAuth2PermissionGrantsRules.Join(tokens))));
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

        if (!desired.IsSymbolic && !Guid.TryParse(desired.Id.NameLeaf, out _))
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: the name '{desired.Id.NameLeaf}' is not a GUID; name permission grants with a deterministic GUID, for example [guid(variables('apiName'), variables('graphAppId'), 'AllPrincipals')].");
        }

        foreach (var property in properties)
        {
            if (!OAuth2PermissionGrantsRules.AllowedProperties.Contains(property.Key, StringComparer.OrdinalIgnoreCase))
            {
                return Unsupported(desired, existing, $"{desired.Describe()}: properties.{property.Key} is not a property of a permission grant; expected clientId, resourceId, consentType, principalId and scope (or scopes).");
            }
        }

        if (existing is null)
        {
            // Not yet prepared (a symbolic name is planned before PrepareAsync runs): scopes stands in for scope.
            bool hasScope = JsonObjects.TryGetIgnoreCase(properties, "scope", out var scopeNode) && !JsonObjects.IsAbsent(scopeNode);
            if (!hasScope && JsonObjects.TryGetIgnoreCase(properties, "scopes", out var scopesNode) && !JsonObjects.IsAbsent(scopesNode))
            {
                properties["scope"] = UnknownJson.IsUnknown(scopesNode, out var unknown) ? UnknownJson.Create(unknown) : UnknownJson.Create("scopes", UnknownOrigins.Reference);
            }

            return base.Plan(desired, null, options);
        }

        // scope is a set of values: the same values in another order are no change.
        string? desiredScope = JsonObjects.GetString(properties, "scope");
        string? liveScope = JsonObjects.GetString(existing.Properties, "scope");
        if (desiredScope is not null && liveScope is not null && OAuth2PermissionGrantsRules.SameSet(desiredScope, liveScope) && !string.Equals(desiredScope, liveScope, StringComparison.Ordinal))
        {
            var normalized = (JsonObject)JsonValues.Clone(existing.Properties)!;
            normalized[JsonObjects.FindKey(normalized, "scope") ?? "scope"] = desiredScope;
            existing = existing with { Properties = normalized };
        }

        return base.Plan(desired, existing, options);
    }

    // ---- read ----

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, string? stateId, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);

        if (stateId is not null)
        {
            var pinned = await graph.GetAsync(new GraphRequest(EntityPath(stateId), key.Api), cancellationToken).ConfigureAwait(false) as JsonObject;
            if (pinned is not null)
            {
                return ToExisting(pinned);
            }
        }

        var found = await FindByKeyAsync(graph, key, RetryHint.Default, cancellationToken).ConfigureAwait(false);
        return found is null ? null : ToExisting(found);
    }

    /// <summary>The grant matching the composite key through <c>$filter</c>, or <c>null</c>.</summary>
    private async Task<JsonObject?> FindByKeyAsync(IGraphClient graph, ResourceKey key, RetryHint hint, CancellationToken cancellationToken)
    {
        if (!key.TryGet("clientId", out var clientId) || !key.TryGet("resourceId", out var resourceId) || !key.TryGet("consentType", out var consentType))
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: the key {key.Describe()} does not carry clientId, resourceId and consentType.");
        }

        string filter = $"clientId eq '{Literal(clientId)}' and resourceId eq '{Literal(resourceId)}' and consentType eq '{Literal(consentType)}'";
        bool hasPrincipal = key.TryGet("principalId", out var principalId) && !string.IsNullOrEmpty(principalId);
        if (hasPrincipal)
        {
            filter += $" and principalId eq '{Literal(principalId)}'";
        }

        var request = new GraphRequest(CollectionPath, key.Api, hint: hint).WithFilter(filter);
        await foreach (var item in graph.GetAllPagesAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (item is not JsonObject grant)
            {
                continue;
            }

            // Defensive client-side match (Graph's $filter on grants is exact, but a null principalId must not match a Principal grant).
            if (!JsonObjects.KeyEquals(JsonObjects.GetString(grant, "clientId"), clientId)
                || !JsonObjects.KeyEquals(JsonObjects.GetString(grant, "resourceId"), resourceId)
                || !string.Equals(JsonObjects.GetString(grant, "consentType"), consentType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? livePrincipal = JsonObjects.GetString(grant, "principalId");
            if (hasPrincipal ? JsonObjects.KeyEquals(livePrincipal, principalId) : livePrincipal is null)
            {
                return grant;
            }
        }

        return null;
    }

    // ---- create ----

    public override async Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = desired.Properties
            ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before creating.");

        var key = ResolveKey(desired);
        string scope = JsonObjects.GetString(properties, "scope")
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{desired.Describe()}: scope is required to create a permission grant.", desired.Id);
        var body = CreateBody(key, scope);

        JsonObject live;
        try
        {
            var response = await graph.PostAsync(new GraphRequest(CollectionPath, desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
            live = response.Body as JsonObject
                ?? await FindByKeyAsync(graph, key, RetryHint.ExpectReplicationLag, cancellationToken).ConfigureAwait(false)
                ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the create returned no object and the grant could not be read back.", desired.Id);
        }
        catch (GraphException ex) when (ex.IsAlreadyExists)
        {
            // "Permission entry already exists": the grant exists (consented elsewhere or not yet visible to the
            // planning read). Look it up, bring its scope to the declared set, adopt it.
            var found = await FindByKeyAsync(graph, key, RetryHint.ExpectReplicationLag, cancellationToken).ConfigureAwait(false)
                ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: Graph reported that the grant already exists, but it could not be read back.", desired.Id);
            string id = RequireId(found, desired);
            if (!OAuth2PermissionGrantsRules.SameSet(scope, JsonObjects.GetString(found, "scope")))
            {
                await graph.PatchAsync(new GraphRequest(EntityPath(id), desired.Api, hint: RetryHint.ExpectReplicationLag), new JsonObject { ["scope"] = scope }, cancellationToken).ConfigureAwait(false);
            }

            live = await graph.GetAsync(new GraphRequest(EntityPath(id), desired.Api, hint: RetryHint.ExpectReplicationLag), cancellationToken).ConfigureAwait(false) as JsonObject
                ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the adopted grant ({id}) vanished after the update.", desired.Id);
        }

        return new AppliedResource(desired.Id, RequireId(live, desired), live, key);
    }

    /// <summary><c>{ clientId, resourceId, consentType, principalId (null for AllPrincipals), scope }</c>.</summary>
    private static JsonObject CreateBody(ResourceKey key, string scope)
    {
        key.TryGet("clientId", out var clientId);
        key.TryGet("resourceId", out var resourceId);
        key.TryGet("consentType", out var consentType);
        bool hasPrincipal = key.TryGet("principalId", out var principalId) && !string.IsNullOrEmpty(principalId);
        return new JsonObject
        {
            ["clientId"] = clientId,
            ["resourceId"] = resourceId,
            ["consentType"] = consentType,
            ["principalId"] = hasPrincipal ? JsonValue.Create(principalId) : null,
            ["scope"] = scope,
        };
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
        string scope = JsonObjects.GetString(properties, "scope")
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: scope is the only property that can change; the update carries none.", desired.Id);

        string path = EntityPath(existing.GraphId);
        await graph.PatchAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint), new JsonObject { ["scope"] = scope }, cancellationToken).ConfigureAwait(false);
        var live = await graph.GetAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint), cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the grant ({existing.GraphId}) vanished after the update.", desired.Id);
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
            // Already gone.
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

    /// <summary>An OData string literal body: single quotes doubled (the value goes through <c>--url-parameters</c>, so no URL encoding here).</summary>
    private static string Literal(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private ProviderException Invalid(ResourceInstance desired, string message)
    {
        return new ProviderException(InvalidGrant, $"{desired.Describe()}: {message}", desired.Id);
    }

    private static void Set(JsonObject properties, string name, JsonNode? value)
    {
        string key = JsonObjects.FindKey(properties, name) ?? name;
        properties[key] = value;
    }

    private static void Remove(JsonObject properties, string name)
    {
        if (JsonObjects.FindKey(properties, name) is { } key)
        {
            properties.Remove(key);
        }
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
