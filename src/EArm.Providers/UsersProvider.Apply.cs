using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core.Secrets;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>The writes of <see cref="UsersProvider"/>: create (with the recycle-bin probe and restore, and the initial password), update and the license action.</summary>
public sealed partial class UsersProvider
{
    // ---- create ----

    public override async Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = desired.Properties
            ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before creating.");

        string name = desired.Id.NameLeaf;
        if (UsersRules.ValidateUserPrincipalName(name) is { } shapeError)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: {shapeError}", desired.Id);
        }

        UsersRules.TaintSecrets(properties, Definition.Secure);
        UsersRules.ApplyCreatedStateDefaults(properties);
        UsersRules.ApplyCreateDefaults(properties, name);
        var key = ResolveKey(desired);

        // The only place the password is plain text: the request body (the Graph transport never logs bodies).
        var body = BuildCreateBody(desired);
        UsersRules.RemoveServerAssigned(body);
        if (JsonObjects.TryGetIgnoreCase(body, UsersRules.PasswordProfile, out var profileNode) && profileNode is not JsonObject)
        {
            throw new ProviderException(EngineDiagnosticCodes.UnresolvedDeployTimeValue, $"{desired.Describe()}: passwordProfile depends on values that are not available at this point of the deployment.", desired.Id);
        }

        var profile = profileNode as JsonObject ?? new JsonObject();
        body[JsonObjects.FindKey(body, UsersRules.PasswordProfile) ?? UsersRules.PasswordProfile] = profile;
        bool generated = !UsersRules.HasPassword(body);
        if (generated)
        {
            string generatedPassword = UsersRules.GeneratePassword();

            // Masked from every message from this moment on (an error text that echoes the request body must not carry it), before anything is sent.
            SecretScrubber.Register(generatedPassword, secureDeclared: true);
            profile[JsonObjects.FindKey(profile, UsersRules.Password) ?? UsersRules.Password] = generatedPassword;
        }

        // The key lookup was a 404, so the only other holder of the UPN is a soft-deleted user (30-day window).
        var warnings = new List<ProviderWarning>();
        if (await FindSoftDeletedAsync(name, desired.Api, graph, warnings, cancellationToken).ConfigureAwait(false) is { } deleted)
        {
            return await RestoreOrFailAsync(desired, key, body, deleted, graph, warnings, cancellationToken).ConfigureAwait(false);
        }

        var response = await graph.PostAsync(new GraphRequest(CollectionPath, desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
        var live = response.Body as JsonObject
            ?? await GetUserAsync(graph, EntityPath(name), desired.Api, RetryHint.ExpectReplicationLag, _selects.For(key, ManagedProperties), cancellationToken).ConfigureAwait(false)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the create returned no object and '{name}' could not be read back.", desired.Id);
        string id = RequireId(live, desired);

        // Publish from the tainted desired properties, never from the revealed body: a secret that flowed into
        // any other property must stay a SecretValue on the object reference() hands out.
        UsersRules.MergeWritten(live, properties);
        JsonObjects.TryGetIgnoreCase(properties, UsersRules.PasswordProfile, out var declaredProfile);
        PublishInitialPassword(live, declaredProfile as JsonObject, JsonObjects.GetString(profile, UsersRules.Password));

        // The object was created a moment ago: the license action by id may hit replication lag. A failure here leaves a user that exists (with a generated password that Graph
        // never returns again): the partial result goes with the exception so the executor records the object and the one-time secret instead of losing both.
        // A cancellation (Ctrl+C) that reaches the license step is such a failure too: the user and its password are recorded, then the run stops.
        var created = new AppliedResource(desired.Id, id, live, key, Extra(generated)) { Warnings = warnings };
        try
        {
            await ApplyLicensesAsync(desired, id, null, RetryHint.ExpectReplicationLag, graph, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GraphException or GraphTransportException or ProviderException or OperationCanceledException)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the user was created (id {id}) but assigning its licenses failed: {ex.Message}", desired.Id, ex) { Partial = created };
        }

        return created;
    }

    /// <summary>
    /// The soft-deleted user holding <paramref name="userPrincipalName"/>, or <c>null</c> (see <see cref="DeletedItemsProbe"/>:
    /// filtered advanced query, a 400 falls back to listing, a 403 skips the probe with a warning). Listing deleted users needs
    /// <c>User.Read.All</c> (Learn: directory-deleteditems-list), which <c>User.ReadWrite.All</c> covers.
    /// </summary>
    private static Task<JsonObject?> FindSoftDeletedAsync(string userPrincipalName, GraphApi api, IGraphClient graph, List<ProviderWarning> warnings, CancellationToken cancellationToken)
    {
        return DeletedItemsProbe.FindAsync(graph, DeletedUsersPath, "user", UsersRules.UserPrincipalName, userPrincipalName, DeletedItemProperties, api, advanced: true, warnings, cancellationToken);
    }
    /// <summary>
    /// Restores the soft-deleted holder of the UPN and brings it to the declared state: the declared properties are
    /// PATCHed (never <c>passwordProfile</c>: a password reset is a separate permission, so the restored user keeps
    /// its old password and <c>extra.generatedPassword</c> is <c>false</c>), then the user is re-read and its direct
    /// licenses reconciled.
    /// </summary>
    private async Task<AppliedResource> RestoreOrFailAsync(ResourceInstance desired, ResourceKey key, JsonObject body, JsonObject deleted, IGraphClient graph, List<ProviderWarning> warnings, CancellationToken cancellationToken)
    {
        string deletedId = JsonObjects.GetId(deleted) ?? "?";
        if (_planOptions.OnSoftDeletedConflict != SoftDeletedConflict.Restore)
        {
            string when = JsonObjects.GetString(deleted, "deletedDateTime") is { } stamp ? $", deleted {stamp}" : string.Empty;
            throw new ProviderException(
                ProviderException.SoftDeletedConflict,
                $"{desired.Describe()}: userPrincipalName '{desired.Id.NameLeaf}' is held by a soft-deleted user (id {deletedId}{when}), which keeps the name for 30 days. Rerun with --on-soft-deleted-conflict restore to restore and update it, or purge it first (DELETE /directory/deletedItems/{deletedId}).",
                desired.Id);
        }

        var restore = new GraphRequest(DeletedItemsPath + "/" + GraphPath.Segment(deletedId) + "/restore", desired.Api, hint: desired.RetryHint);
        var restored = await graph.PostAsync(restore, null, cancellationToken).ConfigureAwait(false);
        string id = JsonObjects.GetId(restored.Body as JsonObject) ?? deletedId;
        string path = EntityPath(id);

        var patch = (JsonObject)JsonValues.Clone(body)!;
        UsersRules.RemoveNeverPatched(patch);
        if (patch.Count > 0)
        {
            await graph.PatchAsync(new GraphRequest(path, desired.Api, hint: RetryHint.ExpectReplicationLag), patch, cancellationToken).ConfigureAwait(false);
        }

        var live = await GetUserAsync(graph, path, desired.Api, RetryHint.ExpectReplicationLag, _selects.For(key, ManagedProperties), cancellationToken).ConfigureAwait(false)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the user ({id}) vanished after being restored.", desired.Id);
        await _licensing.EnrichAsync(live, graph, desired.Api, cancellationToken).ConfigureAwait(false);
        PublishInitialPasswordUnavailable(live);
        var restoredResult = new AppliedResource(desired.Id, id, live, key, Extra(generated: false)) { Warnings = warnings };
        try
        {
            await ApplyLicensesAsync(desired, id, live, RetryHint.ExpectReplicationLag, graph, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GraphException or GraphTransportException or ProviderException)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the user was restored (id {id}) but assigning its licenses failed: {ex.Message}", desired.Id, ex) { Partial = restoredResult };
        }

        return restoredResult;
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

        // No defaults here: an existing user's undeclared properties are never written (see Plan).
        UsersRules.TaintSecrets(properties, Definition.Secure);
        var key = ResolveKey(desired);
        string path = EntityPath(existing.GraphId);

        // The license delta is action-backed; it must not reach the PATCH body.
        var objectChange = change with { Deltas = change.Deltas.Where(d => !d.Path.Equals(LicensesPath)).ToList() };
        var body = BuildPatchBody(desired, existing, objectChange);
        UsersRules.RemoveNeverPatched(body);
        if (body.Count > 0)
        {
            await graph.PatchAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
        }

        var live = await GetUserAsync(graph, path, desired.Api, desired.RetryHint, _selects.For(key, ManagedProperties), cancellationToken).ConfigureAwait(false)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the user ({existing.GraphId}) vanished after the update.", desired.Id);
        await _licensing.EnrichAsync(live, graph, desired.Api, cancellationToken).ConfigureAwait(false);

        await ApplyLicensesAsync(desired, existing.GraphId, existing.Raw, desired.RetryHint, graph, cancellationToken).ConfigureAwait(false);
        PublishInitialPasswordUnavailable(live);
        return new AppliedResource(desired.Id, existing.GraphId, live, key, Extra(generated: false));
    }

    /// <summary>Reconciles the declared direct licenses against <paramref name="liveRaw"/>'s direct assignments with <c>POST /users/{id}/assignLicense</c>.</summary>
    private async Task ApplyLicensesAsync(ResourceInstance desired, string graphId, JsonObject? liveRaw, RetryHint hint, IGraphClient graph, CancellationToken cancellationToken)
    {
        if (!JsonObjects.TryGetIgnoreCase(desired.EdgeProperties, UsersLicensing.AssignedLicenses, out var declared) || declared is null)
        {
            return;
        }

        if (declared is not JsonArray desiredLicenses)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: properties.assignedLicenses must be an array of {{ skuId | skuPartNumber, disabledPlans }} objects.", desired.Id);
        }

        if (desiredLicenses.Count > 0 && UsersLicensing.MissingUsageLocation(desired.Properties, liveRaw))
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: assignedLicenses requires usageLocation on the user.", desired.Id);
        }

        var liveDirect = UsersLicensing.DirectAssignments(liveRaw);
        await _licensing.ApplyAsync(graph, desired.Api, EntityPath(graphId), desiredLicenses, liveDirect, hint, desired.Describe(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes the initial password on the live object as a tainted leaf (never plain text) for <c>reference()</c>:
    /// the declared profile's other members keep whatever taint they carry; the password (declared or generated) is
    /// always a fresh <see cref="SecretValue"/>.
    /// </summary>
    private static void PublishInitialPassword(JsonObject live, JsonObject? declaredProfile, string? password)
    {
        var published = declaredProfile is null ? new JsonObject() : (JsonObject)JsonValues.Clone(declaredProfile)!;
        string key = JsonObjects.FindKey(published, UsersRules.Password) ?? UsersRules.Password;
        if (string.IsNullOrEmpty(password))
        {
            published.Remove(key);
        }
        else
        {
            published[key] = SecretJson.CreateString(password);
        }

        live[JsonObjects.FindKey(live, UsersRules.PasswordProfile) ?? UsersRules.PasswordProfile] = published;
        live[LiveSecrets.InitialPasswordAvailabilityMarker] = true;
    }

    /// <summary>
    /// On every run that does not create the user (Graph never returns a password): <c>passwordProfile.password</c> is published as
    /// <c>null</c> with <see cref="LiveSecrets.InitialPasswordAvailabilityMarker"/> = <c>false</c>, so an output such as
    /// <c>[reference('alice@contoso.com').passwordProfile.password]</c> evaluates to <c>null</c> with the
    /// <see cref="EngineDiagnosticCodes.SecretNotAvailableOnRerun"/> warning instead of failing the run (the <see cref="LiveSecrets"/> contract).
    /// </summary>
    private static void PublishInitialPasswordUnavailable(JsonObject live)
    {
        string profileKey = JsonObjects.FindKey(live, UsersRules.PasswordProfile) ?? UsersRules.PasswordProfile;
        var profile = live[profileKey] as JsonObject ?? new JsonObject();
        profile[JsonObjects.FindKey(profile, UsersRules.Password) ?? UsersRules.Password] = null;
        live[profileKey] = profile;
        live[LiveSecrets.InitialPasswordAvailabilityMarker] = false;
    }
}
