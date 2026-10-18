using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core.Secrets;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>The writes of <see cref="ApplicationsProvider"/>: create (with the recycle-bin probe and restore) and update.</summary>
public sealed partial class ApplicationsProvider
{
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
        RemoveUniqueName(body);
        var warnings = new List<ProviderWarning>();
        AppliedResource? restoredPartial = null;
        bool deferredUris = ApplicationsRules.RemoveDeferredIdentifierUris(body);

        // The key lookup was a 404, so the only other holder of the uniqueName is a soft-deleted object (30-day window).
        if (await FindSoftDeletedAsync(name, desired.Api, graph, warnings, cancellationToken).ConfigureAwait(false) is { } deleted)
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
            var restoredResponse = await graph.PostAsync(restore, null, cancellationToken).ConfigureAwait(false);

            // From here on the application exists again (it is out of the recycle bin): a failure of a later step must hand it over, or the run loses the object.
            var restoredLive = (restoredResponse.Body as JsonObject is { } restoredBody ? (JsonObject)restoredBody.DeepClone() : (JsonObject)deleted.DeepClone());
            restoredLive["id"] = deletedId;
            restoredPartial = new AppliedResource(desired.Id, deletedId, restoredLive, key, derived.ToExtra()) { Warnings = warnings };
        }

        try
        {
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
                live = await GetByUniqueNameAsync(graph, name, desired.Api, RetryHint.ExpectReplicationLag, _selects.For(key, ManagedProperties), cancellationToken).ConfigureAwait(false)
                    ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the upsert returned no object and '{name}' could not be read back.", desired.Id);
            }

            string id = RequireId(live, desired);
            var written = new AppliedResource(desired.Id, id, live, key, derived.ToExtra()) { Warnings = warnings };
            if (deferredUris)
            {
                // The object exists now: a failure of the second PATCH hands it over (ProviderException.Partial) so the executor records it.
                try
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
                catch (Exception ex) when (ex is GraphException or GraphTransportException or ProviderException { Partial: null })
                {
                    throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the application was created (id {id}) but writing its identifierUris failed: {ex.Message}", desired.Id, ex) { Partial = written };
                }
            }

            return written;
        }
        catch (Exception ex) when (restoredPartial is not null && (ex is GraphException or GraphTransportException || ex is ProviderException { Partial: null }))
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the soft-deleted application ({restoredPartial.GraphId}) was restored but bringing it to the declared state failed: {ex.Message}", desired.Id, ex) { Partial = restoredPartial };
        }
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
        RemoveUniqueName(body);

        string path = EntityPath(existing.GraphId);
        var request = new GraphRequest(path, desired.Api, hint: desired.RetryHint);
        if (JsonObjects.GetString(existing.Raw, "uniqueName") is null)
        {
            await AdoptUniqueNameAsync(graph, existing.GraphId, name, desired.Api, desired.RetryHint, desired.Describe(), desired.Id, cancellationToken).ConfigureAwait(false);
        }

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

        var live = await GetApplicationAsync(graph, path, desired.Api, desired.RetryHint, _selects.For(key, ManagedProperties), cancellationToken).ConfigureAwait(false)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the application ({existing.GraphId}) vanished after the update.", desired.Id);
        return new AppliedResource(desired.Id, existing.GraphId, live, key, derived.ToExtra());
    }
}
