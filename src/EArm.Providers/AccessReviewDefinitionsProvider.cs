using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Graph;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/accessReviews/definitions</c>: standalone access reviews (accessReviewScheduleDefinition,
/// https://learn.microsoft.com/en-us/graph/api/resources/accessreviewscheduledefinition?view=graph-rest-1.0). The resource name is the
/// <c>displayName</c>; the definition is found by it, live, on every run (Graph does not enforce unique names; EARM does).
/// <para>
/// Requests per operation:
/// <list type="bullet">
/// <item><b>Read</b>: <c>GET /identityGovernance/accessReviews/definitions</c> (every page), matched client-side on <c>displayName</c> (Learn:
/// accessreviewset-list-definitions supports <c>$filter</c> only as <c>contains</c> on the scope query): none is "create", one is the definition,
/// several are <see cref="ProviderException.AmbiguousMatch"/>.</item>
/// <item><b>Plan</b>: only what the template declares is compared (<see cref="AccessReviewDefinitionShape.ProjectLive"/>). <c>scope</c> and
/// <c>instanceEnumerationScope</c> are immutable (the update page does not accept them): a change is refused, not replaced. A reviewer change is refused
/// while the reviewers are not all individual users (<see cref="AccessReviewDefinitionShape.ReviewerChangeRefusal"/>).</item>
/// <item><b>Create</b>: <c>POST …/definitions</c> (201, the object in the body).</item>
/// <item><b>Update</b>: Graph only offers a full-replace <c>PUT …/definitions/{id}</c> (204): the definition is read again and PUT with the declared values
/// merged in (<see cref="AccessReviewDefinitionShape.MergeForPut"/>), so undeclared settings keep their live values → <c>GET …/{id}</c>. Updates apply to
/// future instances only.</item>
/// <item><b>Delete</b>: <c>DELETE …/definitions/{id}</c> (204); a 404 is "already gone". The instances and their decisions go with it (<see cref="IDeleteWarningSource"/>).</item>
/// </list>
/// </para>
/// <para>
/// Scope and reviewer queries are plain strings; built with <c>concat</c> / <c>format</c> from <c>reference(...).id</c> of a group or access package in the
/// same template they are deploy-time values (the reference orders the review after its target).
/// </para>
/// </summary>
[EntraProvider(ResourceType)]
public sealed class AccessReviewDefinitionsProvider : ProviderBase, IDeleteWarningSource, IOrphanCandidateSource
{
    public const string ResourceType = "Microsoft.Entra/accessReviews/definitions";

    private const string KeyProperty = "displayName";

    public AccessReviewDefinitionsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!ResourceIdentifier.TypeEquals(definition.Type, ResourceType))
        {
            throw new ArgumentException($"{nameof(AccessReviewDefinitionsProvider)} implements '{ResourceType}', not '{definition.Type}'.", nameof(definition));
        }
    }

    public AccessReviewDefinitionsProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog), catalog)
    {
    }

    /// <summary>An immutable change (<c>scope</c>, <c>instanceEnumerationScope</c>) is refused: replacing a review would discard its instances and decisions.</summary>
    protected override bool RecreateOnImmutableChange => false;

    // ---- read ----

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);
        string name = ProviderSupport.SingleKey(key, KeyProperty);
        var live = await FindSingleAsync(graph, name, key.Api, cancellationToken).ConfigureAwait(false);
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

        var problems = AccessReviewDefinitionShape.Problems(properties);
        if (problems.Count > 0)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: {string.Join("; ", problems)}.");
        }

        var live = existing?.Properties;
        if (existing is not null && AccessReviewDefinitionShape.ProjectLive(properties, existing.Properties) is JsonObject projected)
        {
            existing = existing with { Properties = projected };
        }

        var planned = base.Plan(desired, existing, options);
        if (planned.Type is not ChangeType.Modify || planned.HasError || live is null
            || AccessReviewDefinitionShape.ReviewerChangeRefusal(properties, live) is not { } refusal)
        {
            return planned;
        }

        string message = $"{desired.Describe()}: {refusal}.";
        return planned with { Reasons = [.. planned.Reasons, message], UnsupportedReason = message };
    }

    // ---- create ----

    public override async Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        ThrowIfProblems(desired);
        var body = BuildCreateBody(desired);
        RemoveServerAssigned(body);
        var response = await graph.PostAsync(new GraphRequest(CollectionPath, desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
        var live = response.Body as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: Graph returned no object for the created access review.", desired.Id);
        string id = JsonObjects.GetId(live)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{TypeName}: Graph returned an object without 'id' for '{desired.Name}'.", desired.Id);
        return new AppliedResource(desired.Id, id, live, ResolveKey(desired));
    }

    // ---- update ----

    public override async Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(graph);
        ThrowIfProblems(desired);
        string path = EntityPath(existing.GraphId);
        var current = await graph.GetAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint), cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the access review ({existing.GraphId}) vanished before the update.", desired.Id);

        var desiredBody = BuildCreateBody(desired);
        RemoveServerAssigned(desiredBody);
        var document = AccessReviewDefinitionShape.MergeForPut(current, desiredBody);
        document["id"] = existing.GraphId;
        await graph.PutAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint), document, cancellationToken).ConfigureAwait(false);

        var live = await graph.GetAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint), cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the access review ({existing.GraphId}) vanished after the update.", desired.Id);
        return new AppliedResource(desired.Id, existing.GraphId, live, ResolveKey(desired));
    }

    // ---- delete ----

    public override async Task DeleteAsync(ResourceKey key, ExistingResource live, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(graph);
        try
        {
            await graph.DeleteAsync(new GraphRequest(EntityPath(live.GraphId), key.Api), cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            // Already gone (deleted outside EARM): nothing to do.
        }
    }

    public string? DescribeDeleteRisk(ResourceKey key, ExistingResource live)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        return "the access review's instances and the decisions recorded in them are deleted with it";
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

    // ---- helpers ----

    /// <summary>
    /// <c>GET {collection}</c> (every page), matched client-side and case-insensitively on <c>displayName</c>: none is <c>null</c>, one is the definition,
    /// several are <see cref="ProviderException.AmbiguousMatch"/> listing the ids.
    /// </summary>
    private async Task<JsonObject?> FindSingleAsync(IGraphClient graph, string displayName, GraphApi api, CancellationToken cancellationToken)
    {
        var matches = new List<JsonObject>();
        await foreach (var item in graph.GetAllPagesAsync(new GraphRequest(CollectionPath, api), cancellationToken).ConfigureAwait(false))
        {
            if (item is JsonObject definition && string.Equals(JsonObjects.GetString(definition, KeyProperty), displayName, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(definition);
            }
        }

        if (matches.Count > 1)
        {
            string ids = string.Join(", ", matches.Select(m => JsonObjects.GetId(m) ?? "?"));
            throw new ProviderException(
                ProviderException.AmbiguousMatch,
                $"{matches.Count} access reviews have displayName '{displayName}' (ids: {ids}); rename or delete the others so the name identifies one review.",
                ResourceIdentifier.Create(ResourceType, displayName));
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    private string EntityPath(string graphId)
    {
        return CollectionPath + "/" + GraphPath.Segment(graphId);
    }

    private static ResourceTypeDefinition RequireDefinition(ResourceTypeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Find(ResourceType)
            ?? throw new InvalidOperationException($"The catalog does not define '{ResourceType}'.");
    }

    /// <summary>The same checks as <see cref="Plan"/>, for callers that skip planning; problems are a hard error here.</summary>
    private static void ThrowIfProblems(ResourceInstance desired)
    {
        var properties = desired.Properties
            ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated first.");
        var problems = AccessReviewDefinitionShape.Problems(properties);
        if (problems.Count > 0)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: {string.Join("; ", problems)}.", desired.Id);
        }
    }

    /// <summary><c>id</c> and the read-only members are assigned by Graph; never taken from the template.</summary>
    private static void RemoveServerAssigned(JsonObject body)
    {
        foreach (string property in AccessReviewDefinitionShape.ReadOnlyProperties.Append("id"))
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
}
