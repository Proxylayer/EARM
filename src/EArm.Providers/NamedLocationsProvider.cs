using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/conditionalAccess/namedLocations</c>. The resource
/// name is the <c>displayName</c>; the object is found by it, live, on every run.
/// <para>
/// Requests per operation:
/// <list type="bullet">
/// <item><b>Read</b>: <c>GET /identity/conditionalAccess/namedLocations?$filter=displayName eq '…'</c>: no hit is "create", one hit is the object, several hits are <c>ProviderException(AmbiguousMatch)</c> listing the ids.</item>
/// <item><b>Create</b>: <c>POST /identity/conditionalAccess/namedLocations</c> (the response body is the object).</item>
/// <item><b>Update</b>: <c>PATCH /identity/conditionalAccess/namedLocations/{id}</c>, the body always carrying <c>@odata.type</c> (Graph requires it on this collection) → <c>GET …/{id}</c>.</item>
/// <item><b>Delete</b>: <c>DELETE /identity/conditionalAccess/namedLocations/{id}</c>; a 404 is "already gone"; a 400 (the location is still referenced by a Conditional Access policy) is surfaced as <see cref="ReferencedByPolicy"/>.</item>
/// </list>
/// </para>
/// <para>
/// <c>@odata.type</c> selects the kind and is immutable: a kind change is a <see cref="ChangeMode.Recreate"/>
/// (delete, then create; Graph refuses the delete while a policy references the location). <see cref="Plan"/>
/// normalises the evaluated properties in place (see <see cref="NamedLocationsRules.NormalizeDesired"/>) so that
/// what-if rows, request bodies and the state hash describe the same object; the live object is normalised the
/// same way on read, so reordered or differently spelt ranges and country codes are not drift.
/// </para>
/// </summary>
[EntraProvider(ResourceType)]
public sealed class NamedLocationsProvider : ProviderBase, IOrphanCandidateSource
{
    public const string ResourceType = "Microsoft.Entra/conditionalAccess/namedLocations";

    /// <summary><see cref="ProviderException.Code"/> when Graph refuses to delete a location that a policy still references.</summary>
    public const string ReferencedByPolicy = EngineDiagnosticCodes.NamedLocationReferenced;

    private const string KeyProperty = "displayName";

    public NamedLocationsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog)
    {
        if (!ResourceIdentifier.TypeEquals(definition.Type, ResourceType))
        {
            throw new ArgumentException($"{nameof(NamedLocationsProvider)} implements '{ResourceType}', not '{definition.Type}'.", nameof(definition));
        }
    }

    public NamedLocationsProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog), catalog)
    {
    }

    /// <summary>A changed <c>@odata.type</c> replaces the location (kind change = recreate).</summary>
    protected override bool RecreateOnImmutableChange => true;

    // ---- read ----

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);

        string name = ProviderSupport.SingleKey(key, KeyProperty);

        var matches = await FindByDisplayNameAsync(graph, name, key.Api, cancellationToken).ConfigureAwait(false);
        if (matches.Count == 0)
        {
            return null;
        }

        if (matches.Count > 1)
        {
            string ids = string.Join(", ", matches.Select(m => JsonObjects.GetId(m) ?? "?"));
            var id = ResourceIdentifier.Create(ResourceType, name);
            throw new ProviderException(
                ProviderException.AmbiguousMatch,
                $"{matches.Count} named locations have displayName '{name}' (ids: {ids}); rename or delete the others so the name identifies one object.",
                id);
        }

        return Existing(matches[0]);
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

        var problems = NamedLocationsRules.NormalizeDesired(properties);
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
        RemoveServerAssigned(body);

        var request = new GraphRequest(CollectionPath, desired.Api, hint: desired.RetryHint);
        var response = await graph.PostAsync(request, body, cancellationToken).ConfigureAwait(false);
        var live = response.Body as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: Graph returned no object for the created named location.", desired.Id);

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
        var body = BuildPatchBody(desired, existing, change);
        RemoveServerAssigned(body);

        string path = EntityPath(existing.GraphId);
        JsonObject live;
        if (body.Count == 0)
        {
            live = (JsonObject)JsonValues.Clone(existing.Raw)!;
        }
        else
        {
            // Graph requires @odata.type in every PATCH of this collection, whether or not the kind is in the delta.
            body = WithKindFirst(body, NamedLocationsRules.KindOf(properties) ?? NamedLocationsRules.KindOf(existing.Raw));
            await graph.PatchAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
            live = await graph.GetAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint), cancellationToken).ConfigureAwait(false) as JsonObject
                ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the named location ({existing.GraphId}) vanished after the update.", desired.Id);
        }

        return new AppliedResource(desired.Id, existing.GraphId, live, ResolveKey(desired));
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
            // Already gone (deleted outside EARM): nothing to do.
        }
        catch (GraphException ex) when (ex.Status == HttpStatusCode.BadRequest)
        {
            // Graph refuses to delete a location a Conditional Access policy still references.
            throw new ProviderException(
                ReferencedByPolicy,
                $"{TypeName} {key.Describe()}: Graph refused to delete named location {live.GraphId} ({ex.Code}: {ex.GraphMessage}). A named location cannot be deleted (or recreated with another kind) while a Conditional Access policy references it; remove it from the policies' conditions.locations first.",
                null,
                ex);
        }
    }

    // ---- Graph helpers ----

    /// <summary><c>GET {collection}?$filter=displayName eq '…'</c> (quotes doubled), every page.</summary>
    private async Task<IReadOnlyList<JsonObject>> FindByDisplayNameAsync(IGraphClient graph, string displayName, GraphApi api, CancellationToken cancellationToken)
    {
        string filter = KeyProperty + " eq '" + displayName.Replace("'", "''", StringComparison.Ordinal) + "'";
        var request = new GraphRequest(CollectionPath, api).WithFilter(filter);
        var matches = new List<JsonObject>();
        await foreach (var item in graph.GetAllPagesAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (item is JsonObject location && string.Equals(JsonObjects.GetString(location, KeyProperty), displayName, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(location);
            }
        }

        return matches;
    }

    /// <summary>The live object wrapped for the planner, its diff view in the same canonical form as a normalised desired object.</summary>
    private ExistingResource Existing(JsonObject live)
    {
        var existing = ToExisting(live);
        return existing with { Properties = NamedLocationsRules.NormalizeLive(existing.Properties) };
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
        var problems = NamedLocationsRules.NormalizeDesired(properties);
        if (problems.Count > 0)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: {string.Join("; ", problems)}.", desired.Id);
        }
    }

    /// <summary>A copy of the body with <c>@odata.type</c> as its first property.</summary>
    private static JsonObject WithKindFirst(JsonObject body, string? kind)
    {
        var ordered = new JsonObject();
        string? kindKey = JsonObjects.FindKey(body, NamedLocationsRules.ODataTypeProperty);
        if (kindKey is not null && JsonValues.TryGetString(body[kindKey], out var declared))
        {
            ordered[NamedLocationsRules.ODataTypeProperty] = declared;
        }
        else if (kind is not null)
        {
            ordered[NamedLocationsRules.ODataTypeProperty] = kind;
        }

        foreach (var property in body)
        {
            if (!property.Key.Equals(NamedLocationsRules.ODataTypeProperty, StringComparison.OrdinalIgnoreCase))
            {
                ordered[property.Key] = JsonValues.Clone(property.Value);
            }
        }

        return ordered;
    }

    /// <summary><c>id</c>, <c>createdDateTime</c> and <c>modifiedDateTime</c> are assigned by Graph and rejected in bodies.</summary>
    private static void RemoveServerAssigned(JsonObject body)
    {
        foreach (string property in NamedLocationsRules.ServerAssignedProperties)
        {
            if (JsonObjects.FindKey(body, property) is { } key)
            {
                body.Remove(key);
            }
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

    // ---- orphan report ----

    /// <summary>The orphan report lists the whole collection.</summary>
    public OrphanScope OrphanScope => OrphanScope.Collection;

    public Task<IReadOnlyList<OrphanCandidate>> ListOrphanCandidatesAsync(OrphanQuery query, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(graph);
        return OrphanListing.CollectionAsync(graph, new GraphRequest(CollectionPath, query.Api).WithSelect("id", KeyProperty), KeyProperty, null, null, cancellationToken);
    }
}
