using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Graph;
using Microsoft.Extensions.Logging;

namespace EArm.Engine.Execution;

/// <summary>The outcome of deleting one declared object.</summary>
public enum DeletionStatus
{
    Deleted,

    /// <summary>Nothing exists under the key any more (deleted meanwhile, or a 404 on delete).</summary>
    AlreadyGone,

    Failed,

    /// <summary>Not attempted: an earlier delete failed (without <c>--continue-on-error</c>), or the run was cancelled.</summary>
    Skipped,

    /// <summary>Deliberately kept, not an error: the object is protected (<see cref="DeleteGuards"/>) or cannot be deleted at all (<see cref="IDeleteRefusalSource"/>).</summary>
    Refused,
}

/// <summary>
/// A declared resource as <c>deployment delete</c> found it: its live object (found by key in the what-if), its <c>displayName</c>, why it must be kept
/// (<see cref="Refusal"/>), why deleting it is risky (<see cref="Risk"/>), or why it could not be checked (<see cref="Problem"/>; then <see cref="Live"/> is <c>null</c>).
/// </summary>
public sealed record DeclaredObject(ResourceInstance Instance, ExistingResource? Live, string? DisplayName, string? Refusal, string? Risk, string? Problem)
{
    public string? GraphId => Live?.GraphId;

    /// <summary>
    /// The code of <see cref="Refusal"/>: <see cref="EngineDiagnosticCodes.ProtectedObject"/> (the signed-in principal, another organization's service
    /// principal) or <see cref="EngineDiagnosticCodes.NotDeletable"/> (an object Graph cannot delete, <see cref="IDeleteRefusalSource"/>).
    /// </summary>
    public string RefusalCode { get; init; } = EngineDiagnosticCodes.ProtectedObject;
}

/// <summary>What happened to one <see cref="DeclaredObject"/>.</summary>
public sealed record DeclaredObjectDeletion(DeclaredObject Target, DeletionStatus Status, string? Error, string? ErrorCode, TimeSpan Duration)
{
    public bool Succeeded => Status is DeletionStatus.Deleted or DeletionStatus.AlreadyGone;
}

/// <summary>The outcome of <see cref="TemplateDeleter.DeleteAsync"/>.</summary>
public sealed record TemplateDeleteResult(IReadOnlyList<DeclaredObjectDeletion> Outcomes, bool Cancelled)
{
    /// <summary>No delete failed or was skipped (protected objects that were kept, <see cref="RefusedCount"/>, are not failures).</summary>
    public bool Succeeded => !Cancelled && Outcomes.All(o => o.Succeeded || o.Status == DeletionStatus.Refused);

    public int DeletedCount => Outcomes.Count(o => o.Status == DeletionStatus.Deleted);

    public int AlreadyGoneCount => Outcomes.Count(o => o.Status == DeletionStatus.AlreadyGone);

    public int RefusedCount => Outcomes.Count(o => o.Status == DeletionStatus.Refused);

    public int FailedCount => Outcomes.Count(o => o.Status == DeletionStatus.Failed);

    public int SkippedCount => Outcomes.Count(o => o.Status == DeletionStatus.Skipped);
}

/// <summary>
/// <c>deployment delete</c>: deletes the objects a template declares, found by their live keys (nothing is recorded locally), in reverse dependency order: the
/// reverse of the plan's batches, ties broken by <see cref="ApplyPhases.DeleteOrderOf"/> and resource id. <see cref="FindDeclaredObjects"/> lists them from a
/// what-if plan (so the caller can show them and ask for confirmation); <see cref="DeleteAsync"/> re-reads each one by key right before deleting it and
/// only deletes the object that was listed. The signed-in principal and service principals of other organizations are never deleted (<see cref="DeleteGuards"/>).
/// </summary>
public sealed class TemplateDeleter
{
    private readonly ProviderRegistry _registry;
    private readonly IGraphClient _graph;
    private readonly ILogger _logger;
    private readonly ReplicationWait _replicationWait;

    /// <param name="replicationWait">
    /// How long a delete refused because an earlier delete of the run has not reached Graph yet is repeated (<see cref="LaggingDeleteRetry"/>); default
    /// <see cref="ReplicationWait.Default"/>.
    /// </param>
    public TemplateDeleter(ProviderRegistry registry, IGraphClient graph, ILogger<TemplateDeleter> logger, ReplicationWait? replicationWait = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _graph = graph;
        _logger = logger;
        _replicationWait = replicationWait ?? ReplicationWait.Default;
    }

    /// <summary>
    /// <see cref="FindDeclaredObjects"/> with each delete warning computed by <see cref="IDeleteWarningSource.DescribeDeleteRiskAsync"/>, so a provider reads
    /// what only the warning needs (an access package's delivered assignments) once, here, instead of on every key read of every run.
    /// </summary>
    public async Task<IReadOnlyList<DeclaredObject>> FindDeclaredObjectsAsync(DeploymentPlan plan, CancellationToken cancellationToken)
    {
        var found = FindDeclaredObjects(plan);
        var result = new List<DeclaredObject>(found.Count);
        foreach (var target in found)
        {
            if (target.Live is { } live && target.Instance.IsEvaluated && plan.Registry.Get(target.Instance.Type) is IDeleteWarningSource warnings and IResourceProvider provider)
            {
                string? risk = await warnings.DescribeDeleteRiskAsync(provider.ResolveKey(target.Instance), live, _graph, cancellationToken).ConfigureAwait(false);
                result.Add(target with { Risk = risk });
                continue;
            }

            result.Add(target);
        }

        return result;
    }

    /// <summary>
    /// The declared resources that exist (or could not be checked), in delete order, from a plan computed in <see cref="PlanMode.WhatIf"/>. Resources whose
    /// condition is false, whose name is still deploy-time, or that do not exist are left out: there is nothing to delete for them. The delete warnings come
    /// from what the plan's key reads returned (<see cref="IDeleteWarningSource.DescribeDeleteRisk"/>); <see cref="FindDeclaredObjectsAsync"/> lets a provider
    /// read more for them.
    /// </summary>
    public static IReadOnlyList<DeclaredObject> FindDeclaredObjects(DeploymentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.PlanMode != PlanMode.WhatIf)
        {
            throw new ArgumentException("The declared objects are found by a what-if plan (PlanMode.WhatIf).", nameof(plan));
        }

        var found = new List<DeclaredObject>();
        foreach (var batch in plan.Batches.Reverse())
        {
            foreach (var instance in batch.Instances.OrderBy(i => ApplyPhases.DeleteOrderOf(i.Type)).ThenBy(i => i.Id.Id, StringComparer.Ordinal))
            {
                // An "existing": true resource is only read by the template, never owned by it: deleting the template leaves it alone.
                // A nested deployment owns nothing itself: the resources of its template are in the plan and are deleted as declared objects.
                // An adopt-only resource ("x-earm": { "adoptOnly": true }) was never created by the template: it is managed, but not deleted with it.
                if (instance.IsSymbolic || instance.IsIgnored || instance.Declaration.IsExisting || instance.Declaration.AdoptOnly is not null || instance.IsDeployment)
                {
                    continue;
                }

                if (!plan.Resolver.TryGetLive(instance.Id, out var raw, out var graphId))
                {
                    if (plan.ChangeFor(instance) is { HasError: true } failed)
                    {
                        found.Add(new DeclaredObject(instance, null, null, null, null, failed.UnsupportedReason));
                    }

                    continue;
                }

                var live = new ExistingResource(graphId, raw, raw);
                var provider = plan.Registry.Get(instance.Type);
                string? risk = provider is IDeleteWarningSource warnings && instance.IsEvaluated ? warnings.DescribeDeleteRisk(provider.ResolveKey(instance), live) : null;
                var (refusal, refusalCode) = Refusal(plan, provider, instance, live);
                found.Add(new DeclaredObject(instance, live, JsonObjects.GetString(raw, "displayName"), refusal, risk, null) { RefusalCode = refusalCode });
            }
        }

        return found;
    }

    /// <summary>
    /// Deletes <paramref name="targets"/> in the given order, stopping at the first failure unless <paramref name="continueOnError"/>. Each object is re-read by
    /// its key first: when the key leads to another object than the one listed, nothing is deleted for it. A delete in flight is never cancelled; a cancellation
    /// stops before the next one (the rest is skipped, <see cref="TemplateDeleteResult.Cancelled"/>). A delete that Graph refuses because an earlier delete
    /// of this run has not reached it yet (a strength, named location or context still "referenced" by the policy deleted a moment ago; a named location still
    /// "trusted" right after it was unmarked) is repeated on the replication schedule before it counts as failed (<see cref="LaggingDeleteRetry"/>).
    /// </summary>
    public async Task<TemplateDeleteResult> DeleteAsync(DeploymentPlan plan, IReadOnlyList<DeclaredObject> targets, bool continueOnError, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(targets);

        var outcomes = new List<DeclaredObjectDeletion>(targets.Count);
        var lagging = new LaggingDeleteRetry(_replicationWait, _logger);
        bool stop = false;
        bool cancelled = false;
        foreach (var target in targets)
        {
            if (!cancelled && cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
            }

            if (cancelled || (stop && target.Refusal is null))
            {
                outcomes.Add(new DeclaredObjectDeletion(target, DeletionStatus.Skipped, cancelled ? "the run was cancelled" : "an earlier delete failed", EngineDiagnosticCodes.ResourceSkipped, TimeSpan.Zero));
                continue;
            }

            var outcome = await DeleteOneAsync(plan, target, lagging, cancellationToken).ConfigureAwait(false);
            outcomes.Add(outcome);
            if (outcome.Status == DeletionStatus.Deleted)
            {
                lagging.RecordDeleted(target.Instance.Type);
            }
            else if (outcome.Status == DeletionStatus.Failed)
            {
                EngineLog.DeleteFailed(_logger, target.Instance.Id.Id, outcome.Error ?? "unknown error");
                stop = !continueOnError;
            }
            else if (outcome.Status == DeletionStatus.Refused)
            {
                EngineLog.DeleteRefused(_logger, target.Instance.Id.Id, outcome.Error ?? "protected");
            }
        }

        return new TemplateDeleteResult(outcomes, cancelled);
    }

    private async Task<DeclaredObjectDeletion> DeleteOneAsync(DeploymentPlan plan, DeclaredObject target, LaggingDeleteRetry lagging, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var instance = target.Instance;
        if (target.Problem is { } problem || target.Live is null)
        {
            return new DeclaredObjectDeletion(target, DeletionStatus.Failed, $"{instance.Describe()} could not be checked: {target.Problem ?? "its live object is unknown"}", EngineDiagnosticCodes.ReadFailed, stopwatch.Elapsed);
        }

        if (target.Refusal is { } refusal)
        {
            return new DeclaredObjectDeletion(target, DeletionStatus.Refused, refusal, target.RefusalCode, stopwatch.Elapsed);
        }

        try
        {
            // The delete in flight is never cancelled (its outcome must be known); the caller's token is checked between objects.
            var provider = _registry.Get(instance.Type);
            var key = provider.ResolveKey(instance);
            var live = await WithRetryAsync(plan, ct => provider.ReadAsync(key, _graph, ct), CancellationToken.None).ConfigureAwait(false);
            if (live is null)
            {
                return new DeclaredObjectDeletion(target, DeletionStatus.AlreadyGone, null, null, stopwatch.Elapsed);
            }

            if (!string.Equals(live.GraphId, target.Live.GraphId, StringComparison.OrdinalIgnoreCase))
            {
                return new DeclaredObjectDeletion(target, DeletionStatus.Failed, $"{instance.Describe()} was not deleted: its key ({key.Describe()}) now names Graph id {live.GraphId}, not the object that was listed ({target.Live.GraphId}). Run the command again.", EngineDiagnosticCodes.ResourceDeleted, stopwatch.Elapsed);
            }

            if (Refusal(plan, provider, instance, live) is ({ } refusedNow, var refusedCode))
            {
                return new DeclaredObjectDeletion(target, DeletionStatus.Refused, refusedNow, refusedCode, stopwatch.Elapsed);
            }

            EngineLog.Deleting(_logger, instance.Id.Id, live.GraphId);
            // The caller's token only cuts a wait between two attempts short (see LaggingDeleteRetry), never an attempt.
            await lagging.RunAsync(instance.Id.Id, () => WithRetryAsync(plan, async ct =>
            {
                await provider.DeleteAsync(key, live, _graph, ct).ConfigureAwait(false);
                return true;
            }, CancellationToken.None), cancellationToken).ConfigureAwait(false);
            return new DeclaredObjectDeletion(target, DeletionStatus.Deleted, null, null, stopwatch.Elapsed);
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            return new DeclaredObjectDeletion(target, DeletionStatus.AlreadyGone, null, null, stopwatch.Elapsed);
        }
        catch (ProviderException ex)
        {
            return new DeclaredObjectDeletion(target, DeletionStatus.Failed, SecretRegistration.Scrub(ex.Message), ex.Code, stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is GraphException or GraphTransportException)
        {
            return new DeclaredObjectDeletion(target, DeletionStatus.Failed, SecretRegistration.Scrub(ex.Message), EngineDiagnosticCodes.ResourceDeleted, stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or JsonException or NotSupportedException)
        {
            return new DeclaredObjectDeletion(target, DeletionStatus.Failed, SecretRegistration.Scrub($"{ex.GetType().Name}: {ex.Message}"), EngineDiagnosticCodes.ResourceDeleted, stopwatch.Elapsed);
        }
    }

    /// <summary>Why the object must be kept: the delete guards first (<see cref="EngineDiagnosticCodes.ProtectedObject"/>), then the provider (<see cref="EngineDiagnosticCodes.NotDeletable"/>).</summary>
    private static (string? Refusal, string Code) Refusal(DeploymentPlan plan, IResourceProvider provider, ResourceInstance instance, ExistingResource live)
    {
        if (DeleteGuards.Refusal(instance.Id.Id, instance.Type, live, plan.SignedInPrincipalId, plan.TenantId) is { } guarded)
        {
            return (guarded, EngineDiagnosticCodes.ProtectedObject);
        }

        if (provider is IDeleteRefusalSource refusals && instance.IsEvaluated && refusals.DescribeDeleteRefusal(provider.ResolveKey(instance), live) is { } refused)
        {
            return (refused, EngineDiagnosticCodes.NotDeletable);
        }

        return (null, EngineDiagnosticCodes.ProtectedObject);
    }

    private static Task<T> WithRetryAsync<T>(DeploymentPlan plan, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        return plan.RetryPolicy is null
            ? operation(cancellationToken)
            : plan.RetryPolicy.ExecuteAsync(operation, RetryHint.Default, null, cancellationToken);
    }
}
