using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core.Types;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using Microsoft.Extensions.Logging;

namespace EArm.Engine.State;

/// <summary>The outcome of deleting one state-managed object.</summary>
public enum DeleteStatus
{
    /// <summary>The provider deleted the object.</summary>
    Deleted,

    /// <summary>The object was already gone (404 on delete, or neither the key nor the pinned id finds it); the entry is dropped as if deleted.</summary>
    AlreadyGone,

    Failed,

    /// <summary>Not attempted because an earlier delete failed.</summary>
    Skipped,

    /// <summary>
    /// Deliberately not deleted, not an error: the entry is adopted (<c>--delete-adopted</c> not given) or its object belongs to a declared
    /// resource. The entry stays (or, for a shared object, is detached by the caller).
    /// </summary>
    Refused,
}

/// <summary>What happened to one state entry during a delete pass.</summary>
public sealed record DeleteOutcome(StateEntry Entry, DeleteStatus Status, string? Error, string? ErrorCode, TimeSpan Duration)
{
    public bool Succeeded => Status is DeleteStatus.Deleted or DeleteStatus.AlreadyGone;
}

/// <summary>
/// The safety rules of a state-driven delete (review findings S-01, E-01, E-02): the state is only a hint about which object a
/// resource id stands for, so the live object is re-read by KEY and must be the pinned one; adopted objects, the signed-in
/// principal, first-party service principals and objects that declared resources own are never deleted from an orphan entry.
/// </summary>
public sealed record DeleteGuard
{
    /// <summary>The default guard: key verification and the adopted rule on, nothing else known.</summary>
    public static DeleteGuard Default { get; } = new();

    public ResourceTypeCatalog Catalog { get; init; } = ResourceTypeCatalog.Default;

    /// <summary>The object id of the signed-in principal; its object is never deleted.</summary>
    public string? SignedInPrincipalId { get; init; }

    /// <summary>The tenant id, to recognise first-party service principals (<c>appOwnerOrganizationId</c> of another tenant).</summary>
    public string? TenantId { get; init; }

    /// <summary><c>--delete-adopted</c>: entries with <see cref="StateProvenance.Adopted"/> may be deleted too.</summary>
    public bool DeleteAdopted { get; init; }

    /// <summary>Graph ids of the objects the declared (applied or read) resources of this run own; an orphan entry pointing at one is not deleted.</summary>
    public IReadOnlySet<string>? OwnedGraphIds { get; init; }

    /// <summary>The state, to check a child's recorded parent id against the parent's own entry.</summary>
    public StateFile? State { get; init; }
}

/// <summary>
/// Deletes state-managed objects through their providers (<see cref="IResourceProvider.DeleteAsync"/>) inside the
/// retry policy, after the <see cref="DeleteGuard"/> checks. Shared by the executor's Complete-mode pass and <c>deployment delete --delete-resources</c>.
/// </summary>
public static class StateDeletion
{
    public static Task<DeleteOutcome> DeleteAsync(StateEntry entry, ProviderRegistry registry, IGraphClient graph, RetryPolicy? retryPolicy, CancellationToken cancellationToken)
    {
        return DeleteAsync(entry, registry, graph, retryPolicy, DeleteGuard.Default, cancellationToken);
    }

    /// <summary>The outcome of the safety checks of one delete: <see cref="Blocked"/> says why it must not (or need not) happen, else <see cref="Live"/> is the verified object.</summary>
    public sealed record DeleteCheck(DeleteOutcome? Blocked, ExistingResource? Live, IResourceProvider? Provider);

    /// <summary>
    /// Checks the <see cref="DeleteGuard"/>, runs <paramref name="beforeDelete"/> (the executor removes the recorded edges there: only for
    /// an object that is really going to be deleted) and deletes.
    /// </summary>
    public static async Task<DeleteOutcome> DeleteAsync(StateEntry entry, ProviderRegistry registry, IGraphClient graph, RetryPolicy? retryPolicy, DeleteGuard guard, CancellationToken cancellationToken, Func<DeleteCheck, Task>? beforeDelete = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(guard);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var check = await CheckAsync(entry, registry, graph, retryPolicy, guard, cancellationToken).ConfigureAwait(false);
            if (check.Blocked is { } blocked)
            {
                return blocked;
            }

            if (beforeDelete is not null)
            {
                await beforeDelete(check).ConfigureAwait(false);
            }

            await RunAsync<bool>(retryPolicy, async ct =>
            {
                await check.Provider!.DeleteAsync(entry.ToResourceKey(guard.Catalog), check.Live!, graph, ct).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);

            return new DeleteOutcome(entry, DeleteStatus.Deleted, null, null, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            return new DeleteOutcome(entry, DeleteStatus.AlreadyGone, null, null, stopwatch.Elapsed);
        }
        catch (ProviderException ex)
        {
            return new DeleteOutcome(entry, DeleteStatus.Failed, SecretScrubber.Scrub(ex.Message), ex.Code, stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is GraphException or GraphTransportException)
        {
            return new DeleteOutcome(entry, DeleteStatus.Failed, SecretScrubber.Scrub(ex.Message), EngineDiagnosticCodes.ResourceDeleted, stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or JsonException or NotSupportedException)
        {
            return new DeleteOutcome(entry, DeleteStatus.Failed, SecretScrubber.Scrub($"{ex.GetType().Name}: {ex.Message}"), EngineDiagnosticCodes.ResourceDeleted, stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// The safety checks of <see cref="DeleteAsync(StateEntry, ProviderRegistry, IGraphClient, RetryPolicy?, DeleteGuard, CancellationToken)"/> without the delete
    /// (also used by what-if to render the Delete rows). Provider and Graph exceptions propagate.
    /// </summary>
    public static async Task<DeleteCheck> CheckAsync(StateEntry entry, ProviderRegistry registry, IGraphClient graph, RetryPolicy? retryPolicy, DeleteGuard guard, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(guard);

        var stopwatch = Stopwatch.StartNew();

        // The entry's own key must be the key its resource id implies (an entry can be built or edited by anyone with write access to the state):
        // otherwise the re-read below would verify "the object" against a key of someone else's choosing (review round 2, S2-01).
        string? invalid = StateKeyRules.Validate(guard.Catalog, entry.Id, entry.Key) ?? StateKeyRules.ValidateRelationships(entry.Relationships);
        if (invalid is not null)
        {
            return Blocked(new DeleteOutcome(entry, DeleteStatus.Failed, $"'{entry.Id.Id}' (pinned Graph id {entry.GraphId}) was not deleted: the state entry is invalid ({invalid}); the state file is corrupt or was edited. Fix it, or remove it with 'earm state forget'.", EngineDiagnosticCodes.StateEntryInvalid, stopwatch.Elapsed));
        }

        if (entry.Provenance == StateProvenance.Adopted && !guard.DeleteAdopted)
        {
            string why = entry.ProvenanceRecorded
                ? "was adopted: it existed before this deployment managed it, so it is not deleted; pass --delete-adopted to delete it."
                : "has no provenance record (an older state file), so it is treated as adopted and is not deleted; if EARM created it, mark it with 'earm state set-provenance --resource-id <id> --provenance created', or pass --delete-adopted to delete it.";
            return Blocked(new DeleteOutcome(entry, DeleteStatus.Refused, $"'{entry.Id.Id}' (Graph id {entry.GraphId}) {why}", EngineDiagnosticCodes.AdoptedObjectNotDeleted, stopwatch.Elapsed));
        }

        if (guard.SignedInPrincipalId is { } signedIn && string.Equals(signedIn, entry.GraphId, StringComparison.OrdinalIgnoreCase))
        {
            return Blocked(new DeleteOutcome(entry, DeleteStatus.Failed, $"'{entry.Id.Id}' points at Graph id {entry.GraphId}, which is the signed-in principal; it is never deleted by a state entry (the state file is corrupt or was edited).", EngineDiagnosticCodes.ProtectedObject, stopwatch.Elapsed));
        }

        if (guard.OwnedGraphIds is { } owned && owned.Contains(entry.GraphId))
        {
            return Blocked(new DeleteOutcome(entry, DeleteStatus.Refused, $"'{entry.Id.Id}' points at Graph id {entry.GraphId}, which a resource declared in the template owns; it was not deleted.", EngineDiagnosticCodes.OrphanSharesObject, stopwatch.Elapsed));
        }

        if (!registry.TryGet(entry.Type, out var provider))
        {
            return Blocked(new DeleteOutcome(entry, DeleteStatus.Failed, $"No resource provider is registered for '{entry.Type}'; the object cannot be deleted by this version.", EngineDiagnosticCodes.ResourceDeleted, stopwatch.Elapsed));
        }

        if (entry.Id.Parent is { } parentId && entry.ParentGraphId is { } recordedParent && guard.State?.Find(parentId.Id) is { } parentEntry
            && !string.Equals(parentEntry.GraphId, recordedParent, StringComparison.OrdinalIgnoreCase))
        {
            return Blocked(new DeleteOutcome(entry, DeleteStatus.Failed, $"'{entry.Id.Id}' records parent Graph id {recordedParent}, but the state pins its parent '{parentId.Id}' to {parentEntry.GraphId}; nothing was deleted (the state file is inconsistent).", EngineDiagnosticCodes.StateEntryMismatch, stopwatch.Elapsed));
        }

        // Identity is resolved live: re-read the object by its KEY (providers never read by a recorded id). No object under the key is "already gone".
        var key = entry.ToResourceKey(guard.Catalog);
        var live = await RunAsync(retryPolicy, ct => provider.ReadAsync(key, graph, ct), cancellationToken).ConfigureAwait(false);
        if (live is null)
        {
            return Blocked(new DeleteOutcome(entry, DeleteStatus.AlreadyGone, null, null, stopwatch.Elapsed));
        }

        if (!string.Equals(live.GraphId, entry.GraphId, StringComparison.OrdinalIgnoreCase))
        {
            // The key leads to another object than the one recorded (the recorded one was deleted and the key reused, or the entry was edited): that object
            // is not the one this entry was written for, so it is never deleted on the entry's behalf.
            return Blocked(Mismatch(entry, $"the key ({key.Describe()}) names Graph id {live.GraphId}, not the recorded object", stopwatch));
        }

        // The immutable fingerprint recorded when the object was created/adopted: the live object must carry the same createdDateTime. An entry that
        // was re-pointed at another object (even one that happens to have the same key) does not match.
        if (entry.CreatedDateTime is { } recordedCreated && ProviderReads.CreatedDateTimeOf(live.Raw) is { } liveCreated && liveCreated != recordedCreated)
        {
            return Blocked(Mismatch(entry, $"the object with Graph id {entry.GraphId} was created {liveCreated:O}, but the entry recorded {recordedCreated:O}; it is a different object than the one the entry was written for", stopwatch));
        }

        // A first-party / multi-tenant service principal is only ever deleted when this deployment created it (provenance created: EARM POSTed the
        // service principal of that app into this tenant); an adopted one belongs to someone else (review round 2, S2-16).
        if (IsFirstPartyServicePrincipal(entry.Type, live.Raw, guard.TenantId) && entry.Provenance != StateProvenance.Created)
        {
            return Blocked(new DeleteOutcome(entry, DeleteStatus.Failed, $"'{entry.Id.Id}' (Graph id {entry.GraphId}) is a service principal owned by another organization (a Microsoft first-party or multi-tenant app) that this deployment did not create; it is never deleted by a state entry.", EngineDiagnosticCodes.ProtectedObject, stopwatch.Elapsed));
        }

        return new DeleteCheck(null, live, provider);
    }

    private static DeleteCheck Blocked(DeleteOutcome outcome)
    {
        return new DeleteCheck(outcome, null, null);
    }

    /// <summary>True for a servicePrincipals object whose <c>appOwnerOrganizationId</c> is another tenant's (first-party and multi-tenant apps).</summary>
    public static bool IsFirstPartyServicePrincipal(string type, JsonObject? raw, string? tenantId)
    {
        if (!type.EndsWith("/servicePrincipals", StringComparison.OrdinalIgnoreCase) || raw is null || string.IsNullOrWhiteSpace(tenantId))
        {
            return false;
        }

        string? owner = JsonObjects.GetString(raw, "appOwnerOrganizationId");
        return !string.IsNullOrWhiteSpace(owner) && !string.Equals(owner, tenantId, StringComparison.OrdinalIgnoreCase);
    }

    private static DeleteOutcome Mismatch(StateEntry entry, string detail, Stopwatch stopwatch)
    {
        return new DeleteOutcome(entry, DeleteStatus.Failed, $"'{entry.Id.Id}' (pinned Graph id {entry.GraphId}) was not deleted: {detail}. The state entry does not describe the live object (it was edited, or the object was replaced); fix it with 'earm state forget' / 'earm state adopt'.", EngineDiagnosticCodes.StateEntryMismatch, stopwatch.Elapsed);
    }

    private static Task<T> RunAsync<T>(RetryPolicy? retryPolicy, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        return retryPolicy is null ? operation(cancellationToken) : retryPolicy.ExecuteAsync(operation, RetryHint.Default, null, cancellationToken);
    }
}

/// <summary>The result of <see cref="DeploymentDeleter.DeleteAllAsync"/>.</summary>
public sealed record DeploymentDeleteResult(string DeploymentName, IReadOnlyList<DeleteOutcome> Outcomes, bool StateRemoved)
{
    /// <summary>No delete failed or was skipped (objects left alone on purpose, <see cref="RefusedCount"/>, are not failures).</summary>
    public bool Succeeded => Outcomes.All(o => o.Succeeded || o.Status == DeleteStatus.Refused);

    public int RefusedCount => Outcomes.Count(o => o.Status == DeleteStatus.Refused);

    public int DeletedCount => Outcomes.Count(o => o.Status == DeleteStatus.Deleted);

    public int AlreadyGoneCount => Outcomes.Count(o => o.Status == DeleteStatus.AlreadyGone);

    public int FailedCount => Outcomes.Count(o => o.Status == DeleteStatus.Failed);

    public int SkippedCount => Outcomes.Count(o => o.Status == DeleteStatus.Skipped);
}

/// <summary>
/// <c>deployment delete --delete-resources</c>: deletes every state-managed object in reverse dependency order
/// (<see cref="ModePass.OrderForDeletion"/>), saving the state after each one so an interrupted run leaves an
/// accurate record, then removes the state itself once nothing is left.
/// </summary>
public static class DeploymentDeleter
{
    public static Task<DeploymentDeleteResult> DeleteAllAsync(
        StateFile state,
        IStateStore store,
        ProviderRegistry registry,
        IGraphClient graph,
        RetryPolicy? retryPolicy,
        bool continueOnError,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        return DeleteAllAsync(state, store, registry, graph, retryPolicy, continueOnError, DeleteGuard.Default, logger, cancellationToken);
    }

    public static async Task<DeploymentDeleteResult> DeleteAllAsync(
        StateFile state,
        IStateStore store,
        ProviderRegistry registry,
        IGraphClient graph,
        RetryPolicy? retryPolicy,
        bool continueOnError,
        DeleteGuard guard,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(logger);

        var outcomes = new List<DeleteOutcome>();
        bool stop = false;
        var now = DateTimeOffset.UtcNow;
        state.Operations.Clear();
        state.LastStatus = StateFile.StatusRunning;

        try
        {
            foreach (var entry in ModePass.OrderForDeletion(state.Resources.Values.ToList()))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stop)
                {
                    outcomes.Add(new DeleteOutcome(entry, DeleteStatus.Skipped, "an earlier delete failed", EngineDiagnosticCodes.ResourceSkipped, TimeSpan.Zero));
                    state.Operations.Add(Record(entry, OperationStatus.Skipped, now, now, "an earlier delete failed", EngineDiagnosticCodes.ResourceSkipped));
                    continue;
                }

                EngineLog.DeletingOrphan(logger, entry.Id.Id, entry.GraphId);
                var started = DateTimeOffset.UtcNow;
                var outcome = await StateDeletion.DeleteAsync(entry, registry, graph, retryPolicy, guard with { State = state }, cancellationToken).ConfigureAwait(false);
                outcomes.Add(outcome);
                if (outcome.Succeeded)
                {
                    if (outcome.Status == DeleteStatus.AlreadyGone)
                    {
                        EngineLog.OrphanAlreadyGone(logger, entry.Id.Id);
                    }

                    state.Remove(entry.Id.Id);
                    state.Operations.Add(Record(entry, OperationStatus.Succeeded, started, DateTimeOffset.UtcNow, null, null));
                }
                else if (outcome.Status == DeleteStatus.Refused)
                {
                    // Left alone on purpose (adopted, or owned by another resource): the entry stays, the run goes on.
                    EngineLog.DeleteRefused(logger, entry.Id.Id, outcome.Error ?? "refused");
                    state.Operations.Add(Record(entry, OperationStatus.Ignored, started, DateTimeOffset.UtcNow, SecretRedactor.RedactText(outcome.Error), outcome.ErrorCode));
                }
                else
                {
                    EngineLog.DeleteFailed(logger, entry.Id.Id, outcome.Error ?? "unknown error");
                    state.Operations.Add(Record(entry, OperationStatus.Failed, started, DateTimeOffset.UtcNow, SecretRedactor.RedactText(outcome.Error), outcome.ErrorCode));
                    stop = !continueOnError;
                }

                await store.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never leave lastStatus at Running behind: record how the run ended (saved independent of the caller token: the entries already deleted must stay recorded).
            state.LastStatus = ex is OperationCanceledException ? StateFile.StatusCancelled : StateFile.StatusFailed;
            state.LastDeployedOn = DateTimeOffset.UtcNow;
            try
            {
                await store.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception saveFailure) when (saveFailure is IOException or UnauthorizedAccessException)
            {
                EngineLog.StateSaveFailed(logger, saveFailure.Message);
            }

            throw;
        }

        bool allGone = state.Resources.Count == 0 && outcomes.All(o => o.Succeeded);
        if (allGone)
        {
            await store.DeleteAsync(state.DeploymentName, CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            state.LastStatus = outcomes.All(o => o.Succeeded || o.Status == DeleteStatus.Refused) ? StateFile.StatusSucceeded : StateFile.StatusFailed;
            state.LastDeployedOn = DateTimeOffset.UtcNow;
            await store.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
        }

        return new DeploymentDeleteResult(state.DeploymentName, outcomes, allGone);
    }

    private static OperationRecord Record(StateEntry entry, OperationStatus status, DateTimeOffset started, DateTimeOffset finished, string? error, string? code)
    {
        return new OperationRecord
        {
            ResourceId = entry.Id.Id,
            Type = entry.Type,
            Status = status,
            ChangeType = ChangeType.Delete,
            StartedOn = started,
            FinishedOn = finished,
            GraphId = entry.GraphId,
            Error = error,
            ErrorCode = code,
        };
    }
}
