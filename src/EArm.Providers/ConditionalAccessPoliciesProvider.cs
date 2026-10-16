using System;
using System.Collections.Generic;
using System.Linq;
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
/// <c>Microsoft.Entra/conditionalAccess/policies</c>. The resource name is
/// the <c>displayName</c>; the object is found by it, live, on every run.
/// <para>
/// Requests per operation:
/// <list type="bullet">
/// <item><b>Read</b>: <c>GET /identity/conditionalAccess/policies?$filter=displayName eq '…'</c>: no hit is "create", one hit is the object, several hits are <c>ProviderException(AmbiguousMatch)</c> listing the ids.</item>
/// <item><b>Create</b>: <c>POST /identity/conditionalAccess/policies</c> (the response body is the object).</item>
/// <item><b>Update</b>: <c>PATCH /identity/conditionalAccess/policies/{id}</c> carrying every changed top-level property whole (<c>conditions</c>, <c>grantControls</c>, <c>sessionControls</c> are replaced, not merged, by Graph; a removed control is an explicit <c>null</c>) → <c>GET …/{id}</c>.</item>
/// <item><b>Delete</b>: <c>DELETE /identity/conditionalAccess/policies/{id}</c>; a 404 is "already gone".</item>
/// </list>
/// </para>
/// <para>
/// <see cref="Plan"/> normalises the evaluated properties in place (defaults, <c>authenticationStrength</c> reduced to
/// <c>{ id }</c>; see <see cref="ConditionalAccessPoliciesRules.NormalizeDesired"/>: the top-level <c>state</c> default
/// applies only to a policy EARM creates, so an existing policy's undeclared <c>state</c> is never written) and refuses (unsupported row)
/// what Graph would reject or what the plan guards against: <c>state: enabled</c> without <c>--allow-ca-enforce</c>,
/// an empty applications or users include, no control at all, <c>block</c> combined with another control, <c>mfa</c>
/// with <c>authenticationStrength</c>, the lockout pattern, and preview-only properties on v1.0. The live object is
/// normalised on read (<see cref="ConditionalAccessPoliciesRules.NormalizeLive"/>) so read-back noise such as
/// <c>customAuthenticationFactors: []</c> or <c>platforms: null</c> is not drift. The last <see cref="PlanOptions"/>
/// seen is kept so a direct <see cref="CreateAsync"/> / <see cref="UpdateAsync"/> applies the same guard.
/// </para>
/// </summary>
[EntraProvider(ResourceType)]
public sealed class ConditionalAccessPoliciesProvider : ProviderBase, IOrphanCandidateSource
{
    public const string ResourceType = "Microsoft.Entra/conditionalAccess/policies";

    /// <summary><see cref="ProviderException.Code"/> when a write is attempted for an unsupported policy (guard or lint) outside the planner.</summary>
    public const string PolicyRejected = EngineDiagnosticCodes.ConditionalAccessPolicyRejected;

    private const string KeyProperty = "displayName";

    /// <summary>
    /// Learn (conditionalaccessgrantcontrols): <c>riskRemediation</c> is an evolvable-enum member of <c>builtInControls</c> that Graph returns only
    /// when the request carries <c>Prefer: include-unknown-enum-members</c>; without it a policy using it reads back as <c>unknownFutureValue</c>.
    /// </summary>
    private const string IncludeUnknownEnumMembers = "include-unknown-enum-members";

    private volatile PlanOptions _planOptions = PlanOptions.Default;

    public ConditionalAccessPoliciesProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog)
    {
        if (!ResourceIdentifier.TypeEquals(definition.Type, ResourceType))
        {
            throw new ArgumentException($"{nameof(ConditionalAccessPoliciesProvider)} implements '{ResourceType}', not '{definition.Type}'.", nameof(definition));
        }
    }

    public ConditionalAccessPoliciesProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog), catalog)
    {
    }

    /// <summary>
    /// The deployment-wide options captured by the last <see cref="Plan"/> call (every call of one run carries the
    /// same values). Settable for tests that call <see cref="CreateAsync"/> or <see cref="UpdateAsync"/> directly.
    /// </summary>
    public PlanOptions PlanOptions
    {
        get => _planOptions;
        set => _planOptions = value ?? throw new ArgumentNullException(nameof(value));
    }

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
                $"{matches.Count} conditional access policies have displayName '{name}' (ids: {ids}); rename or delete the others so the name identifies one object.",
                id);
        }

        return Existing(matches[0]);
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

        ConditionalAccessPoliciesRules.NormalizeDesired(properties, isCreate: existing is null);
        ConditionalAccessPoliciesRules.MarkRemovedControls(properties, existing?.Properties);
        var problems = ConditionalAccessPoliciesRules.Problems(properties, desired.Api, options.AllowCaEnforce);
        if (problems.Count > 0)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: {string.Join("; ", problems)}.");
        }

        var change = base.Plan(desired, existing is null ? null : ProviderSupport.PruneLiveOnly(Definition, properties, existing), options);
        return change.Before is not null && existing is not null
            ? change with { Before = RenderBefore(existing.Properties, properties) }
            : change;
    }

    // ---- create ----

    public override async Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = desired.Properties
            ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before creating.");

        RequireValid(desired, properties, isCreate: true);
        var body = BuildCreateBody(desired);
        ConditionalAccessPoliciesRules.FinishCreateBody(body, properties);
        RequireValidBody(desired, body, isCreate: true);

        var request = new GraphRequest(CollectionPath, desired.Api, hint: desired.RetryHint).WithPrefer(IncludeUnknownEnumMembers);
        var response = await graph.PostAsync(request, body, cancellationToken).ConfigureAwait(false);
        var live = response.Body as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: Graph returned no object for the created policy.", desired.Id);

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

        ConditionalAccessPoliciesRules.MarkRemovedControls(properties, existing.Properties);
        RequireValid(desired, properties, isCreate: false);
        existing = ProviderSupport.PruneLiveOnly(Definition, properties, existing);
        var body = BuildPatchBody(desired, existing, change);
        ConditionalAccessPoliciesRules.FinishPatchBody(body, existing.Properties);
        RequireValidBody(desired, body, isCreate: false);

        string path = EntityPath(existing.GraphId);
        JsonObject live;
        if (body.Count == 0)
        {
            live = (JsonObject)JsonValues.Clone(existing.Raw)!;
        }
        else
        {
            await graph.PatchAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
            live = await graph.GetAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint).WithPrefer(IncludeUnknownEnumMembers), cancellationToken).ConfigureAwait(false) as JsonObject
                ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the policy ({existing.GraphId}) vanished after the update.", desired.Id);
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
    }

    // ---- Graph helpers ----

    /// <summary><c>GET {collection}?$filter=displayName eq '…'</c> (quotes doubled), every page.</summary>
    private async Task<IReadOnlyList<JsonObject>> FindByDisplayNameAsync(IGraphClient graph, string displayName, GraphApi api, CancellationToken cancellationToken)
    {
        string filter = KeyProperty + " eq '" + displayName.Replace("'", "''", StringComparison.Ordinal) + "'";
        var request = new GraphRequest(CollectionPath, api).WithFilter(filter).WithPrefer(IncludeUnknownEnumMembers);
        var matches = new List<JsonObject>();
        await foreach (var item in graph.GetAllPagesAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (item is JsonObject policy && string.Equals(JsonObjects.GetString(policy, KeyProperty), displayName, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(policy);
            }
        }

        return matches;
    }

    /// <summary>The live object wrapped for the planner, its diff view normalised (read-back noise removed).</summary>
    private ExistingResource Existing(JsonObject live)
    {
        var existing = ToExisting(live);
        return existing with { Properties = ConditionalAccessPoliciesRules.NormalizeLive(existing.Properties) };
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

    /// <summary>The same normalisation and checks as <see cref="Plan"/>, for callers that skip planning; a problem is a hard error before any write.</summary>
    private void RequireValid(ResourceInstance desired, JsonObject properties, bool isCreate)
    {
        ConditionalAccessPoliciesRules.NormalizeDesired(properties, isCreate);
        var problems = ConditionalAccessPoliciesRules.Problems(properties, desired.Api, _planOptions.AllowCaEnforce);
        if (problems.Count > 0)
        {
            throw new ProviderException(PolicyRejected, $"{desired.Describe()}: {string.Join("; ", problems)}.", desired.Id);
        }
    }

    /// <summary>Defence in depth (I-01): the guard runs once more on the FINAL request body, which carries the revealed secret values.</summary>
    private void RequireValidBody(ResourceInstance desired, JsonObject body, bool isCreate)
    {
        var problems = ConditionalAccessPoliciesRules.FinalBodyProblems(body, desired.Api, _planOptions.AllowCaEnforce, isCreate);
        if (problems.Count > 0)
        {
            throw new ProviderException(PolicyRejected, $"{desired.Describe()}: {string.Join("; ", problems)}.", desired.Id);
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
