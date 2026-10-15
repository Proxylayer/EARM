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
using EArm.Engine.State;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/conditionalAccess/authenticationContextClassReferences</c>. The resource name <b>is</b> the Graph id (<c>c1</c>..<c>c99</c>); there is no displayName lookup and the
/// state id is the name.
/// <para>
/// Requests per operation:
/// <list type="bullet">
/// <item><b>Read</b>: <c>GET /identity/conditionalAccess/authenticationContextClassReferences/{id}</c>; a 404 is "create".</item>
/// <item><b>Create</b>: <c>PATCH …/{id}</c> with <c>{ displayName, description, isAvailable }</c> (Graph upserts on this collection); the response object is used when Graph returns one, otherwise <c>GET …/{id}</c>.</item>
/// <item><b>Update</b>: <c>PATCH …/{id}</c> (merge body of the changed scalars) → <c>GET …/{id}</c>.</item>
/// <item><b>Delete</b>: <c>GET …/{id}</c> (a 404 is "already gone") → <c>PATCH …/{id} { isAvailable: false }</c> when the context is still available (Graph refuses to delete an available context) → <c>DELETE …/{id}</c>; a 400 (still available, or still referenced by a Conditional Access policy's <c>includeAuthenticationContextClassReferences</c>) is surfaced as <see cref="Referenced"/>.</item>
/// </list>
/// </para>
/// <para>
/// <see cref="Plan"/> validates the name against <see cref="AuthenticationContextClassReferencesRules.IdPattern"/>,
/// requires <c>displayName</c> and defaults <c>isAvailable</c> to <c>true</c> in the evaluated properties, so a context
/// switched off outside EARM shows as drift. The id is immutable by definition: a different name is a different
/// resource, never a recreate.
/// </para>
/// </summary>
[EntraProvider(ResourceType)]
public sealed class AuthenticationContextClassReferencesProvider : ProviderBase
{
    public const string ResourceType = "Microsoft.Entra/conditionalAccess/authenticationContextClassReferences";

    /// <summary><see cref="ProviderException.Code"/> when Graph refuses to delete a context that is still available or still referenced by a policy.</summary>
    public const string Referenced = EngineDiagnosticCodes.AuthenticationContextReferenced;

    private const string KeyProperty = "id";

    public AuthenticationContextClassReferencesProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog)
    {
        if (!ResourceIdentifier.TypeEquals(definition.Type, ResourceType))
        {
            throw new ArgumentException($"{nameof(AuthenticationContextClassReferencesProvider)} implements '{ResourceType}', not '{definition.Type}'.", nameof(definition));
        }
    }

    public AuthenticationContextClassReferencesProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog), catalog)
    {
    }

    /// <summary>The id is the name: it cannot change in place, and a changed name is simply another resource.</summary>
    protected override bool RecreateOnImmutableChange => false;

    // ---- read ----

    /// <summary>Reads by the id the name implies; <paramref name="stateId"/> is the same id and is not consulted.</summary>
    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, string? stateId, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);

        string id = ProviderSupport.SingleKey(key, KeyProperty);

        if (AuthenticationContextClassReferencesRules.NameProblem(id) is { } problem)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: {problem}.");
        }

        var live = await graph.GetAsync(new GraphRequest(EntityPath(id), key.Api), cancellationToken).ConfigureAwait(false) as JsonObject;
        return live is null ? null : ToExisting(live);
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

        var problems = Problems(desired, properties);
        if (problems.Count > 0)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: {string.Join("; ", problems)}.");
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
        AuthenticationContextClassReferencesRules.RemoveServerAssigned(body);

        string id = desired.Id.NameLeaf;
        string path = EntityPath(id);
        var response = await graph.PatchAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
        var live = response.Body as JsonObject;
        if (live is null || JsonObjects.GetId(live) is null)
        {
            // Graph answers the upsert with 201 + object or 204; read the object back when no body came.
            live = await graph.GetAsync(new GraphRequest(path, desired.Api, hint: RetryHint.ExpectReplicationLag), cancellationToken).ConfigureAwait(false) as JsonObject
                ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the authentication context ({id}) could not be read back after the upsert.", desired.Id);
        }

        return new AppliedResource(desired.Id, JsonObjects.GetId(live) ?? id, live, ResolveKey(desired));
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
        var body = BuildPatchBody(desired, existing, change);
        AuthenticationContextClassReferencesRules.RemoveServerAssigned(body);

        string path = EntityPath(existing.GraphId);
        JsonObject live;
        if (body.Count == 0)
        {
            live = (JsonObject)JsonValues.Clone(existing.Raw)!;
        }
        else
        {
            await graph.PatchAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
            live = await graph.GetAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint), cancellationToken).ConfigureAwait(false) as JsonObject
                ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the authentication context ({existing.GraphId}) vanished after the update.", desired.Id);
        }

        return new AppliedResource(desired.Id, existing.GraphId, live, ResolveKey(desired));
    }

    // ---- delete ----

    public override async Task DeleteAsync(StateEntry entry, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(graph);
        var api = entry.ToResourceKey(Catalog).Api;
        string path = EntityPath(entry.GraphId);

        var live = await graph.GetAsync(new GraphRequest(path, api), cancellationToken).ConfigureAwait(false) as JsonObject;
        if (live is null)
        {
            // Already gone (deleted outside EARM): nothing to do.
            return;
        }

        try
        {
            if (AuthenticationContextClassReferencesRules.IsAvailable(live) != false)
            {
                // Graph only deletes an unavailable context: switch it off first.
                await graph.PatchAsync(new GraphRequest(path, api), AuthenticationContextClassReferencesRules.UnavailableBody(), cancellationToken).ConfigureAwait(false);
            }

            await graph.DeleteAsync(new GraphRequest(path, api), cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            // Gone between the read and the delete: nothing to do.
        }
        catch (GraphException ex) when (ex.Status == HttpStatusCode.BadRequest)
        {
            throw new ProviderException(
                Referenced,
                $"{entry.Id.Id}: Graph refused to delete authentication context class reference {entry.GraphId} ({ex.Code}: {ex.GraphMessage}). A context can only be deleted once it is unavailable (isAvailable: false) and no Conditional Access policy references it; remove it from the policies' conditions.applications.includeAuthenticationContextClassReferences first.",
                entry.Id,
                ex);
        }
    }

    // ---- helpers ----

    private string EntityPath(string id)
    {
        return CollectionPath + "/" + GraphPath.Segment(id);
    }

    private static ResourceTypeDefinition RequireDefinition(ResourceTypeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Find(ResourceType)
            ?? throw new InvalidOperationException($"The catalog does not define '{ResourceType}'.");
    }

    /// <summary>The name check (skipped while the name is symbolic) and the in-place normalisation of the properties.</summary>
    private static List<string> Problems(ResourceInstance desired, JsonObject properties)
    {
        var problems = new List<string>();
        if (!desired.IsSymbolic && AuthenticationContextClassReferencesRules.NameProblem(desired.Id.NameLeaf) is { } nameProblem)
        {
            problems.Add(nameProblem);
        }

        problems.AddRange(AuthenticationContextClassReferencesRules.NormalizeDesired(properties));
        return problems;
    }

    /// <summary>The same checks as <see cref="Plan"/>, for callers that skip planning; a problem is a hard error before any write.</summary>
    private static void RequireValid(ResourceInstance desired, JsonObject properties)
    {
        var problems = Problems(desired, properties);
        if (problems.Count > 0)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: {string.Join("; ", problems)}.", desired.Id);
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
}
