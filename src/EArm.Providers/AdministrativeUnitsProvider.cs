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
using EArm.Engine.State;
using EArm.Expressions.Values;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/administrativeUnits</c>. The resource name is the
/// <c>displayName</c>; the Graph id is pinned in the state after the first run. The <c>members</c> property is the
/// target side of the administrative unit membership edge (part A of the edge pass) and never reaches the body or
/// the diff.
/// <para>
/// Requests per operation (every read selects <see cref="AdministrativeUnitsRules.ManagedProperties"/>):
/// <list type="bullet">
/// <item><b>Read</b>: <c>GET /directory/administrativeUnits/{pinnedId}</c> (state id, when known; a 404 is drift) → <c>GET /directory/administrativeUnits?$filter=displayName eq '…'</c>: no hit is "create", one hit is the object, several hits are <c>ProviderException(AmbiguousMatch)</c> listing the ids.</item>
/// <item><b>Create</b>: a best-effort soft-delete probe (<c>GET /directory/deletedItems/microsoft.graph.administrativeUnit?$filter=displayName eq '…'</c>; a rejected query is ignored) → on a hit either <c>ProviderException(SoftDeletedConflict)</c> or, with <c>--on-soft-deleted-conflict restore</c>, <c>POST /directory/deletedItems/{id}/restore</c> + <c>PATCH</c> + <c>GET</c>; else <c>POST /directory/administrativeUnits</c> (the response body is the object).</item>
/// <item><b>Update</b>: <c>PATCH /directory/administrativeUnits/{id}</c> (merge body) → <c>GET …/{id}</c>.</item>
/// <item><b>Delete</b>: <c>DELETE /directory/administrativeUnits/{id}</c> (soft delete, 30 days); a 404 is "already gone".</item>
/// </list>
/// </para>
/// <para>
/// Type-specific rules (<see cref="AdministrativeUnitsRules"/>): <c>membershipType</c> is <c>Assigned</c> or
/// <c>Dynamic</c>; a dynamic unit requires <c>membershipRule</c> and is written with
/// <c>membershipRuleProcessingState: On</c> unless declared; a dynamic unit declaring <c>members</c> fails edge
/// validation (the edge pass rejects dynamic targets); <c>isMemberManagementRestricted</c> is immutable (the overlay
/// makes a change an error). Restricted units: Graph only lets holders of a role scoped to the unit add members, and
/// part A (members) runs before part B (scoped role assignments), so declare the members of a restricted unit in a
/// later run than the unit and its scoped <c>Microsoft.Entra/roleAssignments</c>; the what-if row carries that reason.
/// </para>
/// </summary>
[EntraProvider(ResourceType)]
public sealed class AdministrativeUnitsProvider : ProviderBase
{
    public const string ResourceType = "Microsoft.Entra/administrativeUnits";

    /// <summary>The type-cast recycle bin of administrative units.</summary>
    public const string DeletedUnitsPath = "/directory/deletedItems/microsoft.graph.administrativeUnit";

    private const string KeyProperty = "displayName";

    private const string MembersProperty = "members";

    /// <summary>Deployment-wide snapshot of <see cref="PlanOptions.OnSoftDeletedConflict"/> (plan options only reach <see cref="Plan"/>).</summary>
    private SoftDeletedConflict _onSoftDeletedConflict = SoftDeletedConflict.Error;

    public AdministrativeUnitsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog)
    {
        if (!ResourceIdentifier.TypeEquals(definition.Type, ResourceType))
        {
            throw new ArgumentException($"{nameof(AdministrativeUnitsProvider)} implements '{ResourceType}', not '{definition.Type}'.", nameof(definition));
        }
    }

    public AdministrativeUnitsProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog), catalog)
    {
    }

    /// <summary>The top-level properties each template declares, selected next to <see cref="AdministrativeUnitsRules.ManagedProperties"/> on its reads (G-08).</summary>
    private readonly DeclaredSelects _selects = new();

    public override ResourceKey ResolveKey(ResourceInstance desired)
    {
        var key = base.ResolveKey(desired);
        _selects.Register(key, Definition, desired);
        return key;
    }

    // ---- read ----

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, string? stateId, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);

        if (stateId is not null && await graph.GetAsync(Select(new GraphRequest(EntityPath(stateId), key.Api), key), cancellationToken).ConfigureAwait(false) is JsonObject pinned)
        {
            return Existing(pinned);
        }

        string name = ProviderSupport.SingleKey(key, KeyProperty);

        var matches = await FindByDisplayNameAsync(graph, name, key, cancellationToken).ConfigureAwait(false);
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
                $"{matches.Count} administrative units have displayName '{name}' (ids: {ids}); pin the intended one with 'earm state adopt --resource-id {id.Id} --object-id <id>' or rename the others.",
                id);
        }

        return Existing(matches[0]);
    }

    // ---- plan ----

    public override ResourceChange Plan(ResourceInstance desired, ExistingResource? existing, PlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(options);
        _onSoftDeletedConflict = options.OnSoftDeletedConflict;

        if (desired.IsIgnored || desired.Properties is not { } properties)
        {
            return base.Plan(desired, existing, options);
        }

        var problems = AdministrativeUnitsRules.NormalizeDesired(properties);
        if (problems.Count > 0)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: {string.Join("; ", problems)}.");
        }

        var change = base.Plan(desired, existing, options);
        if (!change.HasError && AdministrativeUnitsRules.IsRestricted(properties) && DeclaresMembers(desired))
        {
            // Scoped roles must exist before members can be added to a restricted unit, and the
            // membership edge (part A) is applied before the role assignments (part B).
            change = change with
            {
                Reasons = [.. change.Reasons, "the unit restricts member management (isMemberManagementRestricted): Graph accepts members only from a principal holding a role scoped to the unit, and members are applied before scoped role assignments, so declare members in a run after the unit and its scoped roleAssignments exist"],
            };
        }

        return change;
    }

    // ---- create ----

    public override async Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = desired.Properties
            ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before creating.");

        RequireValid(desired, properties);
        var key = ResolveKey(desired);
        var body = BuildCreateBody(desired);
        RemoveServerAssigned(body);

        var warnings = new List<ProviderWarning>();
        if (await DeletedItemsProbe.FindAsync(graph, DeletedUnitsPath, "administrativeUnit", KeyProperty, desired.Id.NameLeaf, ["id", "displayName", "deletedDateTime"], desired.Api, advanced: false, warnings, cancellationToken).ConfigureAwait(false) is { } deleted)
        {
            return await RestoreOrFailAsync(desired, key, body, deleted, graph, warnings, cancellationToken).ConfigureAwait(false);
        }

        var response = await graph.PostAsync(new GraphRequest(CollectionPath, desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
        var live = response.Body as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: Graph returned no object for the created administrative unit.", desired.Id);

        return new AppliedResource(desired.Id, RequireId(live, desired), live, key) { Warnings = warnings };
    }

    private async Task<AppliedResource> RestoreOrFailAsync(ResourceInstance desired, ResourceKey key, JsonObject body, JsonObject deleted, IGraphClient graph, List<ProviderWarning> warnings, CancellationToken cancellationToken)
    {
        string deletedId = JsonObjects.GetId(deleted) ?? "?";
        string when = JsonObjects.GetString(deleted, "deletedDateTime") is { } deletedOn ? $", deleted {deletedOn}" : string.Empty;
        if (_onSoftDeletedConflict != SoftDeletedConflict.Restore)
        {
            throw new ProviderException(
                ProviderException.SoftDeletedConflict,
                $"{desired.Describe()} cannot be created: a soft-deleted administrative unit with displayName '{desired.Id.NameLeaf}' (id {deletedId}{when}) is still in the recycle bin for up to 30 days. Rerun with --on-soft-deleted-conflict restore to restore and update it, or purge it first (DELETE /directory/deletedItems/{deletedId}).",
                desired.Id);
        }

        var restored = await graph.PostAsync(new GraphRequest("/directory/deletedItems/" + GraphPath.Segment(deletedId) + "/restore", desired.Api, hint: desired.RetryHint), null, cancellationToken).ConfigureAwait(false);
        string id = JsonObjects.GetId(restored.Body as JsonObject) ?? deletedId;
        string path = EntityPath(id);
        await graph.PatchAsync(new GraphRequest(path, desired.Api, hint: RetryHint.ExpectReplicationLag), body, cancellationToken).ConfigureAwait(false);
        var live = await graph.GetAsync(Select(new GraphRequest(path, desired.Api, hint: RetryHint.ExpectReplicationLag), key), cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{TypeName}: '{desired.Name}' vanished after being restored.", desired.Id);
        return new AppliedResource(desired.Id, id, live, key) { Warnings = warnings };
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
            await graph.PatchAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
            live = await graph.GetAsync(Select(new GraphRequest(path, desired.Api, hint: desired.RetryHint), ResolveKey(desired)), cancellationToken).ConfigureAwait(false) as JsonObject
                ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the administrative unit ({existing.GraphId}) vanished after the update.", desired.Id);
        }

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
            // Already gone (deleted outside EARM): nothing to do.
        }
    }

    // ---- Graph helpers ----

    /// <summary><c>GET /directory/administrativeUnits?$filter=displayName eq '…'</c> (quotes doubled), every page, matched client-side.</summary>
    private async Task<IReadOnlyList<JsonObject>> FindByDisplayNameAsync(IGraphClient graph, string displayName, ResourceKey key, CancellationToken cancellationToken)
    {
        string filter = KeyProperty + " eq '" + ProviderSupport.EscapeLiteral(displayName) + "'";
        var request = Select(new GraphRequest(CollectionPath, key.Api).WithFilter(filter), key);
        var matches = new List<JsonObject>();
        await foreach (var item in graph.GetAllPagesAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (item is JsonObject unit && string.Equals(JsonObjects.GetString(unit, KeyProperty), displayName, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(unit);
            }
        }

        return matches;
    }

    private GraphRequest Select(GraphRequest request, ResourceKey key)
    {
        return request.WithSelect(_selects.For(key, AdministrativeUnitsRules.ManagedProperties));
    }

    /// <summary>The live object wrapped for the planner, its diff view in canonical form (<c>membershipType</c> defaulted to <c>Assigned</c>).</summary>
    private ExistingResource Existing(JsonObject live)
    {
        var existing = ToExisting(live);
        return existing with { Properties = AdministrativeUnitsRules.NormalizeLive(existing.Properties) };
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

    private static bool DeclaresMembers(ResourceInstance desired)
    {
        return JsonObjects.TryGetIgnoreCase(desired.EdgeProperties, MembersProperty, out var members) && !JsonObjects.IsAbsent(members) && (members is not JsonArray array || array.Count > 0);
    }

    /// <summary>The same normalisation as <see cref="Plan"/>, for callers that skip planning; problems are a hard error here.</summary>
    private static void RequireValid(ResourceInstance desired, JsonObject properties)
    {
        var problems = AdministrativeUnitsRules.NormalizeDesired(properties);
        if (problems.Count > 0)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: {string.Join("; ", problems)}.", desired.Id);
        }
    }

    /// <summary><c>id</c> and <c>deletedDateTime</c> are assigned by Graph and rejected in bodies.</summary>
    private static void RemoveServerAssigned(JsonObject body)
    {
        foreach (string property in new[] { "id", "deletedDateTime" })
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
}
