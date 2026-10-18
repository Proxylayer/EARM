using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Graph;

namespace EArm.Providers;

/// <summary>
/// A tenant policy that always exists at a fixed Graph path and is named <c>default</c> (the tenant default app management policy, the default
/// cross-tenant access configuration), modelled like the authentication methods policy: never created (a missing object is an unsupported row and a
/// failed create), updated with a merge PATCH of the changed top-level properties (→ GET), never an orphan. Below a declared top-level object, the
/// undeclared members of the objects <see cref="FillFromLive"/> names are copied from the live object first, so a PATCH that replaces the object whole
/// keeps them. A delete is refused (<see cref="NotDeletable"/>) unless a subclass documents a reset.
/// </summary>
public abstract class SingletonPolicyProviderBase : TenantPolicyProviderBase, IDeleteRefusalSource, IOrphanCandidateSource
{
    /// <summary>The only resource name.</summary>
    public const string SingletonName = "default";

    /// <summary><see cref="ProviderException.Code"/> when a delete is asked for a singleton Graph cannot delete.</summary>
    public const string NotDeletable = EngineDiagnosticCodes.NotDeletable;

    private const string KeyProperty = "id";

    protected SingletonPolicyProviderBase(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog, string resourceType)
        : base(definition, catalog, resourceType)
    {
    }

    /// <summary>The top-level objects whose undeclared members keep their live values (Graph replaces a declared object whole).</summary>
    protected virtual IReadOnlyList<string> FillFromLive => [];

    /// <summary>True when a delete resets the object (<see cref="ResetAsync"/>); otherwise a delete is refused.</summary>
    protected virtual bool CanReset => false;

    /// <summary>What a delete does to the object, or why it is kept, in the <c>deployment delete</c> listing.</summary>
    protected abstract string RefusalText { get; }

    /// <summary>Checks and normalises the evaluated properties in place; none by default.</summary>
    protected virtual List<string> Normalize(JsonObject properties)
    {
        return [];
    }

    /// <summary>The reset a delete performs when <see cref="CanReset"/>.</summary>
    protected virtual Task ResetAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken)
    {
        throw new NotSupportedException($"{TypeName} has no reset.");
    }

    protected override bool RecreateOnImmutableChange => false;

    public override bool CreateIsRepeatable => false;

    // ---- read ----

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);
        RequireSingletonName(ProviderSupport.SingleKey(key, KeyProperty));
        var live = await graph.GetAsync(new GraphRequest(CollectionPath, key.Api), cancellationToken).ConfigureAwait(false) as JsonObject;
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

        var problems = Prepare(desired, properties);
        if (problems.Count > 0)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: {string.Join("; ", problems)}.");
        }

        if (existing is null)
        {
            return Unsupported(desired, null, MissingMessage(desired));
        }

        TenantPolicyShape.FillUndeclared(properties, existing.Properties, FillFromLive);
        return base.Plan(desired, existing, options);
    }

    // ---- create / update ----

    /// <summary>Never creates: the object always exists.</summary>
    public override Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, MissingMessage(desired), desired.Id);
    }

    public override Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = PropertiesOf(desired);
        ThrowIfProblems(desired, Prepare(desired, properties));
        TenantPolicyShape.FillUndeclared(properties, existing.Properties, FillFromLive);
        var body = BuildPatchBody(desired, existing, change);
        RemoveProperties(body, ["id"]);
        return PatchAndReadAsync(desired, existing, CollectionPath, body, graph, cancellationToken);
    }

    // ---- delete ----

    public override async Task DeleteAsync(ResourceKey key, ExistingResource live, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(graph);
        if (!CanReset)
        {
            throw new ProviderException(NotDeletable, $"{TypeName} {key.Describe()}: {RefusalText}");
        }

        await ResetAsync(key, graph, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A singleton without a reset is kept.</summary>
    public string? DescribeDeleteRefusal(ResourceKey key, ExistingResource live)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        return CanReset ? null : RefusalText;
    }

    // ---- orphan report ----

    /// <summary>The singleton always exists, so nothing can be an orphan (no Graph call).</summary>
    public OrphanScope OrphanScope => OrphanScope.Collection;

    public Task<IReadOnlyList<OrphanCandidate>> ListOrphanCandidatesAsync(OrphanQuery query, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(graph);
        return Task.FromResult<IReadOnlyList<OrphanCandidate>>([]);
    }

    // ---- helpers ----

    private List<string> Prepare(ResourceInstance desired, JsonObject properties)
    {
        var problems = new List<string>();
        if (!desired.IsSymbolic && !SingletonName.Equals(desired.Id.NameLeaf, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"the name must be '{SingletonName}' (the tenant has one {Noun})");
        }

        problems.AddRange(Normalize(properties));
        return problems;
    }

    private string MissingMessage(ResourceInstance desired)
    {
        return $"{desired.Describe()}: the {Noun} could not be read (GET {CollectionPath} answered 404). It is a tenant singleton that always exists and that EARM never creates; check the permissions and the tenant's cloud.";
    }

    private void RequireSingletonName(string name)
    {
        if (!SingletonName.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: the name must be '{SingletonName}' (the tenant has one {Noun}), not '{name}'.");
        }
    }
}
