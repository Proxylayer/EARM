using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core.Secrets;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>The writes of <see cref="GroupsProvider"/>: create (with the recycle-bin probe and restore), update (with uniqueName adoption and the dynamic membership pause) and the license action.</summary>
public sealed partial class GroupsProvider
{
    // ---- create ----

    public override async Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);

        var key = ResolveKey(desired);
        var properties = desired.Properties ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before create.");
        var analysis = GroupsShape.Analyze(properties);
        if (GroupsShape.CreateError(analysis, desired.Describe()) is { } createError)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, createError, desired.Id);
        }

        var body = BuildCreateBody(desired);
        string uniqueName = ProviderSupport.SingleKey(key, "uniqueName");
        GroupsShape.ApplyCreateDefaults(body, uniqueName, analysis);
        RemoveUniqueName(body);
        var warnings = new List<ProviderWarning>();
        if (analysis.IsUnified && !AddOwnersAtCreate(body, desired))
        {
            warnings.Add(new ProviderWarning(ProviderWarnings.GroupHasNoOwnerAtCreate, $"{desired.Describe()} is a Microsoft 365 group created without owners: with an application token Graph creates it anonymously (no owner, and the SharePoint site may not be provisioned until an owner exists). Declare properties.owners."));
        }
        else if (!analysis.IsUnified)
        {
            AddOwnersAtCreate(body, desired);
        }

        // Security and Microsoft 365 groups both sit in the recycle bin for 30 days and keep their keys there (Learn group-delete).
        var deleted = analysis.IsUnified
            ? await FindSoftDeletedAsync(uniqueName, JsonObjects.GetString(body, "mailNickname"), desired.Api, graph, warnings, cancellationToken).ConfigureAwait(false)
            : await DeletedItemsProbe.FindAsync(graph, DeletedGroups, "group", "uniqueName", uniqueName, ["id", "displayName", "uniqueName", "mailNickname", "deletedDateTime"], desired.Api, advanced: false, warnings, cancellationToken).ConfigureAwait(false);
        if (deleted is not null)
        {
            return await RestoreOrFailAsync(desired, key, body, deleted, analysis, graph, warnings, cancellationToken).ConfigureAwait(false);
        }

        string path = AlternateKeyPath(key);
        var response = await graph.PatchAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint).WithPrefer("create-if-missing"), body, cancellationToken).ConfigureAwait(false);
        var live = response.Body as JsonObject
            ?? await graph.GetAsync(new GraphRequest(path, desired.Api, hint: RetryHint.ExpectReplicationLag).WithSelect(_selects.For(key, ManagedSelect)), cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{TypeName}: the upsert of '{desired.Name}' returned no object.", desired.Id);
        string id = RequireId(live, desired);

        // A failure here leaves a group that exists: the partial result goes with the exception so the executor records the object instead of losing it.
        var created = new AppliedResource(desired.Id, id, live, key, Extra(analysis)) { Warnings = warnings };
        await ApplyLicensesOrPartialAsync(desired, id, null, created, "created", graph, cancellationToken).ConfigureAwait(false);
        return created;
    }

    /// <summary>
    /// Applies the declared licenses after the object write; a failure is rethrown carrying <paramref name="written"/> (<see cref="ProviderException.Partial"/>),
    /// because the group exists whatever happened to its licenses (review round 3).
    /// </summary>
    private async Task ApplyLicensesOrPartialAsync(ResourceInstance desired, string graphId, JsonObject? live, AppliedResource written, string verb, IGraphClient graph, CancellationToken cancellationToken)
    {
        try
        {
            await ApplyLicensesAsync(desired, graphId, live, graph, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GraphException or GraphTransportException or ProviderException { Partial: null })
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the group was {verb} (id {graphId}) but assigning its licenses failed: {ex.Message}", desired.Id, ex) { Partial = written };
        }
    }

    /// <summary>
    /// Learn (group-upsert / group-post-groups): a group created with an application token and without owners is created
    /// anonymously. When the template declares <c>owners</c> they are bound in the create body (<c>owners@odata.bind</c>, at most 20,
    /// object ids only); the edge pass stays authoritative afterwards. True when at least one owner was bound.
    /// </summary>
    private static bool AddOwnersAtCreate(JsonObject body, ResourceInstance desired)
    {
        if (!JsonObjects.TryGetIgnoreCase(desired.EdgeProperties, "owners", out var declared) || declared is not JsonArray owners)
        {
            return false;
        }

        string prefix = desired.Api.BaseUrl() + "/directoryObjects/";
        var bound = new JsonArray();
        foreach (var owner in owners)
        {
            if (owner is not null && !SecretJson.IsSecret(owner) && !UnknownJson.IsUnknown(owner) && JsonValues.TryGetString(owner, out var id) && Guid.TryParse(id, out _) && bound.Count < 20)
            {
                bound.Add(prefix + id);
            }
        }

        if (bound.Count == 0)
        {
            return false;
        }

        body["owners@odata.bind"] = bound;
        return true;
    }

    /// <summary>The upsert addresses the group by its <c>uniqueName</c> (the URL key); Graph documents it as read-only, so it is never sent in the body.</summary>
    private static void RemoveUniqueName(JsonObject body)
    {
        if (JsonObjects.FindKey(body, "uniqueName") is { } key)
        {
            body.Remove(key);
        }
    }

    private async Task<AppliedResource> RestoreOrFailAsync(ResourceInstance desired, ResourceKey key, JsonObject body, JsonObject deleted, GroupsShape.Analysis analysis, IGraphClient graph, List<ProviderWarning> warnings, CancellationToken cancellationToken)
    {
        string deletedId = JsonObjects.GetId(deleted) ?? "?";
        string held = analysis.IsUnified ? $"mailNickname '{JsonObjects.GetString(deleted, "mailNickname")}'" : $"uniqueName '{JsonObjects.GetString(deleted, "uniqueName")}'";
        string description = $"a soft-deleted {(analysis.IsUnified ? "Microsoft 365" : "security")} group '{JsonObjects.GetString(deleted, "displayName")}' ({deletedId}, deleted {JsonObjects.GetString(deleted, "deletedDateTime") ?? "recently"}) still holds {held}";
        if (_onSoftDeletedConflict != SoftDeletedConflict.Restore)
        {
            throw new ProviderException(ProviderException.SoftDeletedConflict, $"{desired.Describe()} cannot be created: {description}. Restore it with --on-soft-deleted-conflict restore, or purge it from the recycle bin (DELETE /directory/deletedItems/{deletedId}).", desired.Id);
        }

        var restored = await graph.PostAsync(new GraphRequest("/directory/deletedItems/" + GraphPath.Segment(deletedId) + "/restore", desired.Api, hint: desired.RetryHint), null, cancellationToken).ConfigureAwait(false);
        string id = JsonObjects.GetId(restored.Body as JsonObject) ?? deletedId;
        string path = Collection + "/" + GraphPath.Segment(id);
        var patch = (JsonObject)body.DeepClone();
        patch.Remove(JsonObjects.FindKey(patch, "mailNickname") ?? "mailNickname");
        patch.Remove("owners@odata.bind");
        await graph.PatchAsync(new GraphRequest(path, desired.Api, hint: RetryHint.ExpectReplicationLag), patch, cancellationToken).ConfigureAwait(false);
        var live = await graph.GetAsync(new GraphRequest(path, desired.Api, hint: RetryHint.ExpectReplicationLag).WithSelect(_selects.For(key, ManagedSelect)), cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{TypeName}: '{desired.Name}' vanished after being restored.", desired.Id);
        var restoredResult = new AppliedResource(desired.Id, id, live, key, Extra(GroupsShape.Analyze(live))) { Warnings = warnings };
        await ApplyLicensesOrPartialAsync(desired, id, live, restoredResult, "restored", graph, cancellationToken).ConfigureAwait(false);
        return restoredResult;
    }

    // ---- update ----

    public override async Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(graph);

        var key = ResolveKey(desired);
        var properties = desired.Properties ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before update.");
        var analysis = GroupsShape.Analyze(properties);
        string path = Collection + "/" + GraphPath.Segment(existing.GraphId);

        // The license delta is action-backed; it must not reach the PATCH body.
        var objectChange = change with { Deltas = change.Deltas.Where(d => !d.Path.Equals(LicensesPath)).ToList() };
        var body = BuildPatchBody(desired, existing, objectChange);
        RemoveUniqueName(body);

        if (string.IsNullOrEmpty(JsonObjects.GetString(existing.Raw, "uniqueName")))
        {
            // Adopted by display name (see ReadAsync): the key is set first, with its own PATCH, so a refusal changes nothing else.
            await AdoptUniqueNameAsync(graph, existing.GraphId, ProviderSupport.SingleKey(key, "uniqueName"), desired, cancellationToken).ConfigureAwait(false);
        }

        JsonObjects.TryGetIgnoreCase(existing.Properties, "groupTypes", out var liveTypes);
        JsonObjects.TryGetIgnoreCase(properties, "groupTypes", out var desiredTypes);
        if (IsDynamicChange(objectChange, liveTypes, desiredTypes))
        {
            if (GroupsShape.GroupTypesContain(liveTypes, GroupsShape.DynamicMembership))
            {
                // Pause processing before the rule or the dynamic flag changes.
                await graph.PatchAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint), new JsonObject { ["membershipRuleProcessingState"] = GroupsShape.ProcessingPaused }, cancellationToken).ConfigureAwait(false);
            }

            if (analysis.IsDynamic)
            {
                JsonObjects.SetDefault(body, "membershipRuleProcessingState", JsonValue.Create(GroupsShape.ProcessingOn));
            }
        }

        if (body.Count > 0)
        {
            await graph.PatchAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
        }

        var live = await graph.GetAsync(new GraphRequest(path, desired.Api).WithSelect(_selects.For(key, ManagedSelect)), cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{TypeName}: '{desired.Name}' vanished after the update.", desired.Id);

        await ApplyLicensesAsync(desired, existing.GraphId, existing.Raw, graph, cancellationToken).ConfigureAwait(false);
        return new AppliedResource(desired.Id, existing.GraphId, live, key, Extra(analysis));
    }

    /// <summary>
    /// Sets the <c>uniqueName</c> of a group adopted by display name. Learn (group resource) documents the property as "Immutable. Read-only.", so a
    /// refusal (400) is reported as <see cref="EngineDiagnosticCodes.UniqueNameNotSettable"/> with an explanation instead of a bare Graph error.
    /// </summary>
    private static async Task AdoptUniqueNameAsync(IGraphClient graph, string graphId, string uniqueName, ResourceInstance desired, CancellationToken cancellationToken)
    {
        try
        {
            await graph.PatchAsync(new GraphRequest(Collection + "/" + GraphPath.Segment(graphId), desired.Api, hint: desired.RetryHint), new JsonObject { ["uniqueName"] = uniqueName }, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.Status == HttpStatusCode.BadRequest)
        {
            throw new ProviderException(
                EngineDiagnosticCodes.UniqueNameNotSettable,
                $"{desired.Describe()}: Graph refused to set uniqueName '{uniqueName}' on the existing group {graphId}, found by its display name ({ex.GraphMessage}). uniqueName is documented as immutable and read-only (it is assigned by the upsert PATCH /groups(uniqueName='…') that creates the group), so a group created without it cannot be adopted; delete it and let EARM create the group, or rename it so EARM creates a new one.",
                desired.Id,
                ex);
        }
    }

    private async Task ApplyLicensesAsync(ResourceInstance desired, string graphId, JsonObject? live, IGraphClient graph, CancellationToken cancellationToken)
    {
        if (!JsonObjects.TryGetIgnoreCase(desired.EdgeProperties, "assignedLicenses", out var declared) || declared is null)
        {
            return;
        }

        if (declared is not JsonArray desiredLicenses)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: properties.assignedLicenses must be an array of {{ skuId | skuPartNumber, disabledPlans }} objects.", desired.Id);
        }

        JsonObjects.TryGetIgnoreCase(live, "assignedLicenses", out var liveNode);
        await _licensing.ApplyAsync(graph, desired.Api, Collection + "/" + GraphPath.Segment(graphId), desiredLicenses, liveNode as JsonArray, desired.RetryHint, desired.Describe(), cancellationToken).ConfigureAwait(false);
    }
}
