using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Az;
using EntraArm.Core;
using EntraArm.Core.Types;
using EntraArm.Engine;
using EntraArm.Engine.Providers;
using EntraArm.Engine.State;
using EntraArm.Expressions.Values;
using GraphApi = EntraArm.Az.GraphApi;

namespace EntraArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/conditionalAccess/authenticationStrengths</c> (plan sections 1.3, 3.2; work item WI-20). The
/// resource name is the <c>displayName</c>; the Graph id is pinned in the state after the first run. Only custom
/// strengths (<c>policyType: custom</c>) are managed; the built-in ones (<c>Multifactor authentication</c>,
/// <c>Passwordless MFA</c>, <c>Phishing-resistant MFA</c>) are read-only and can be referenced with
/// <c>reference(resourceId('Microsoft.Entra/conditionalAccess/authenticationStrengths', '&lt;displayName&gt;'), apiVersion).id</c>
/// (the existing-resource lookup matches built-ins too) or through the well-known ids.
/// <para>
/// Requests per operation:
/// <list type="bullet">
/// <item><b>Read</b>: <c>GET /policies/authenticationStrengthPolicies/{pinnedId}</c> (state id, when known; a 404 is drift) → <c>GET /policies/authenticationStrengthPolicies</c> (every page; <c>$filter</c> on this collection is unverified, so the displayName match is client-side): one custom hit is the object, several are <c>ProviderException(AmbiguousMatch)</c> listing the ids, none but a built-in hit is the built-in (which <see cref="Plan"/> refuses to manage), nothing is "create".</item>
/// <item><b>Create</b>: <c>POST /policies/authenticationStrengthPolicies</c> <c>{ displayName, description, allowedCombinations }</c> (the response body is the object).</item>
/// <item><b>Update</b>: <c>PATCH …/{id}</c> only when <c>displayName</c> / <c>description</c> changed (never carrying <c>allowedCombinations</c>) → <c>POST …/{id}/updateAllowedCombinations { allowedCombinations }</c> only when the combination set differs → <c>GET …/{id}</c>.</item>
/// <item><b>Delete</b>: <c>DELETE …/{id}</c>; a 404 is "already gone"; a 400 (the strength is still referenced by a Conditional Access policy) is surfaced as <see cref="Referenced"/>.</item>
/// </list>
/// </para>
/// <para>
/// <c>allowedCombinations</c> is compared as a set of normalised strings (see
/// <see cref="AuthenticationStrengthsRules.NormalizeCombination"/>): when the sets are equal the desired array adopts
/// the live spelling before the shared diff runs, so reordered or differently cased combinations are not drift. The
/// values Graph receives are the template's own (trimmed). <c>policyType</c> is immutable and stripped on read; a
/// declared <c>custom</c> is dropped as implied, anything else is an unsupported row.
/// </para>
/// </summary>
[EntraProvider(ResourceType)]
public sealed class AuthenticationStrengthsProvider : ProviderBase
{
    public const string ResourceType = "Microsoft.Entra/conditionalAccess/authenticationStrengths";

    /// <summary><see cref="ProviderException.Code"/> when Graph refuses to delete a strength that a policy still references.</summary>
    public const string Referenced = "AuthenticationStrengthReferenced";

    /// <summary><see cref="ProviderException.Code"/> when a write is attempted against a built-in strength outside the planner.</summary>
    public const string BuiltInRejected = "AuthenticationStrengthBuiltIn";

    private const string KeyProperty = AuthenticationStrengthsRules.DisplayNameProperty;

    public AuthenticationStrengthsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog)
    {
        if (!ResourceIdentifier.TypeEquals(definition.Type, ResourceType))
        {
            throw new ArgumentException($"{nameof(AuthenticationStrengthsProvider)} implements '{ResourceType}', not '{definition.Type}'.", nameof(definition));
        }
    }

    public AuthenticationStrengthsProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog), catalog)
    {
    }

    /// <summary><c>policyType</c> cannot change (custom only); a changed immutable is an error, never a recreate.</summary>
    protected override bool RecreateOnImmutableChange => false;

    // ---- read ----

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, string? stateId, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);

        if (stateId is not null && await graph.GetAsync(new GraphRequest(EntityPath(stateId), key.Api), cancellationToken).ConfigureAwait(false) is JsonObject pinned)
        {
            return ToExisting(pinned);
        }

        if (!key.TryGet(KeyProperty, out var name))
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: the key {key.Describe()} carries no displayName.");
        }

        var (custom, builtIn) = await FindByDisplayNameAsync(graph, name, key.Api, cancellationToken).ConfigureAwait(false);
        if (custom.Count > 1)
        {
            string ids = string.Join(", ", custom.Select(m => JsonObjects.GetId(m) ?? "?"));
            var id = ResourceIdentifier.Create(ResourceType, name);
            throw new ProviderException(
                ProviderException.AmbiguousMatch,
                $"{custom.Count} authentication strengths have displayName '{name}' (ids: {ids}); pin the intended one with 'entraarm state adopt --resource-id {id.Id} --object-id <id>' or rename the others.",
                id);
        }

        if (custom.Count == 1)
        {
            return ToExisting(custom[0]);
        }

        // A built-in with that displayName is returned so reference(resourceId(...), apiVersion) resolves it; Plan refuses to manage it.
        return builtIn.Count > 0 ? ToExisting(builtIn[0]) : null;
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

        var problems = AuthenticationStrengthsRules.NormalizeDesired(properties);
        if (problems.Count > 0)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: {string.Join("; ", problems)}.");
        }

        if (existing is not null)
        {
            if (AuthenticationStrengthsRules.IsBuiltIn(existing.Raw))
            {
                return Unsupported(desired, existing, BuiltInMessage(desired, existing));
            }

            AuthenticationStrengthsRules.AdoptLiveCombinations(properties, existing.Properties);
        }

        return base.Plan(desired, existing, options);
    }

    // ---- create ----

    public override async Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = desired.Properties
            ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before creating.");

        RequireValid(desired, properties);
        var body = BuildCreateBody(desired);
        AuthenticationStrengthsRules.RemoveServerAssigned(body);

        var request = new GraphRequest(CollectionPath, desired.Api, hint: desired.RetryHint);
        var response = await graph.PostAsync(request, body, cancellationToken).ConfigureAwait(false);
        var live = response.Body as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: Graph returned no object for the created authentication strength.", desired.Id);

        return new AppliedResource(desired.Id, RequireId(live, desired), live, ResolveKey(desired));
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

        RequireValid(desired, properties);
        if (AuthenticationStrengthsRules.IsBuiltIn(existing.Raw))
        {
            throw new ProviderException(BuiltInRejected, BuiltInMessage(desired, existing), desired.Id);
        }

        var body = BuildPatchBody(desired, existing, change);
        AuthenticationStrengthsRules.RemoveServerAssigned(body);
        AuthenticationStrengthsRules.RemoveCombinations(body);
        bool combinationsChanged = AuthenticationStrengthsRules.DeclaresCombinations(properties)
            && !AuthenticationStrengthsRules.CombinationsEqual(properties, existing.Properties);

        string path = EntityPath(existing.GraphId);
        if (body.Count == 0 && !combinationsChanged)
        {
            return new AppliedResource(desired.Id, existing.GraphId, (JsonObject)JsonValues.Clone(existing.Raw)!, ResolveKey(desired));
        }

        if (body.Count > 0)
        {
            await graph.PatchAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
        }

        if (combinationsChanged)
        {
            var action = new GraphRequest(path + "/" + AuthenticationStrengthsRules.UpdateCombinationsAction, desired.Api, hint: desired.RetryHint);
            await graph.PostAsync(action, AuthenticationStrengthsRules.UpdateCombinationsBody(properties), cancellationToken).ConfigureAwait(false);
        }

        var live = await graph.GetAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint), cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the authentication strength ({existing.GraphId}) vanished after the update.", desired.Id);
        return new AppliedResource(desired.Id, existing.GraphId, live, ResolveKey(desired));
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
            // Already gone (deleted outside EntraARM): nothing to do.
        }
        catch (GraphException ex) when (ex.Status == HttpStatusCode.BadRequest)
        {
            // Graph refuses to delete a strength a Conditional Access policy still references (plan section 3.2).
            throw new ProviderException(
                Referenced,
                $"{entry.Id.Id}: Graph refused to delete authentication strength {entry.GraphId} ({ex.Code}: {ex.GraphMessage}). A custom authentication strength cannot be deleted while a Conditional Access policy references it; remove it from the policies' grantControls.authenticationStrength first (built-in strengths can never be deleted).",
                entry.Id,
                ex);
        }
    }

    // ---- Graph helpers ----

    /// <summary><c>GET {collection}</c>, every page, matched client-side on displayName (case-insensitive) and split by <c>policyType</c>.</summary>
    private async Task<(List<JsonObject> Custom, List<JsonObject> BuiltIn)> FindByDisplayNameAsync(IGraphClient graph, string displayName, GraphApi api, CancellationToken cancellationToken)
    {
        var custom = new List<JsonObject>();
        var builtIn = new List<JsonObject>();
        await foreach (var item in graph.GetAllPagesAsync(new GraphRequest(CollectionPath, api), cancellationToken).ConfigureAwait(false))
        {
            if (item is JsonObject strength && string.Equals(JsonObjects.GetString(strength, KeyProperty), displayName, StringComparison.OrdinalIgnoreCase))
            {
                (AuthenticationStrengthsRules.IsBuiltIn(strength) ? builtIn : custom).Add(strength);
            }
        }

        return (custom, builtIn);
    }

    private string EntityPath(string graphId)
    {
        return CollectionPath + "/" + GraphPath.Segment(graphId);
    }

    // ---- helpers ----

    private static ResourceTypeDefinition RequireDefinition(ResourceTypeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Find(ResourceType)
            ?? throw new InvalidOperationException($"The catalog does not define '{ResourceType}'.");
    }

    /// <summary>The same normalisation as <see cref="Plan"/>, for callers that skip planning; problems are a hard error here.</summary>
    private static void RequireValid(ResourceInstance desired, JsonObject properties)
    {
        var problems = AuthenticationStrengthsRules.NormalizeDesired(properties);
        if (problems.Count > 0)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: {string.Join("; ", problems)}.", desired.Id);
        }
    }

    private static string BuiltInMessage(ResourceInstance desired, ExistingResource existing)
    {
        return $"{desired.Describe()}: '{desired.Id.NameLeaf}' is a built-in authentication strength ({existing.GraphId}) and cannot be managed; reference it instead with reference(resourceId('{ResourceType}', '{desired.Id.NameLeaf}'), '{desired.ApiVersion}').id, or give the custom strength another name.";
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
