using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EArm.Az;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using Microsoft.Extensions.Logging;

namespace EArm.Engine.State;

/// <summary>The outcome of deleting one state-managed object.</summary>
public enum DeleteStatus
{
    /// <summary>The provider deleted the object.</summary>
    Deleted,

    /// <summary>The object was already gone (404 on delete); the entry is dropped as if deleted.</summary>
    AlreadyGone,

    Failed,

    /// <summary>Not attempted because an earlier delete failed.</summary>
    Skipped,
}

/// <summary>What happened to one state entry during a delete pass.</summary>
public sealed record DeleteOutcome(StateEntry Entry, DeleteStatus Status, string? Error, string? ErrorCode, TimeSpan Duration)
{
    public bool Succeeded => Status is DeleteStatus.Deleted or DeleteStatus.AlreadyGone;
}

/// <summary>
/// Deletes state-managed objects through their providers (<see cref="IResourceProvider.DeleteAsync"/>) inside the
/// retry policy. Shared by the executor's Complete-mode pass and <c>deployment delete --delete-resources</c>.
/// </summary>
public static class StateDeletion
{
    public static async Task<DeleteOutcome> DeleteAsync(StateEntry entry, ProviderRegistry registry, IGraphClient graph, RetryPolicy? retryPolicy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(graph);

        var stopwatch = Stopwatch.StartNew();
        if (!registry.TryGet(entry.Type, out var provider))
        {
            return new DeleteOutcome(entry, DeleteStatus.Failed, $"No resource provider is registered for '{entry.Type}'; the object cannot be deleted by this version.", EngineDiagnosticCodes.ResourceDeleted, stopwatch.Elapsed);
        }

        try
        {
            Func<CancellationToken, Task<bool>> operation = async ct =>
            {
                await provider.DeleteAsync(entry, graph, ct).ConfigureAwait(false);
                return true;
            };

            if (retryPolicy is null)
            {
                await operation(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await retryPolicy.ExecuteAsync(operation, RetryHint.Default, null, cancellationToken).ConfigureAwait(false);
            }

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
            return new DeleteOutcome(entry, DeleteStatus.Failed, ex.Message, ex.Code, stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is GraphException or AzCliException or ToolchainException)
        {
            return new DeleteOutcome(entry, DeleteStatus.Failed, ex.Message, EngineDiagnosticCodes.ResourceDeleted, stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or JsonException or NotSupportedException)
        {
            return new DeleteOutcome(entry, DeleteStatus.Failed, $"{ex.GetType().Name}: {ex.Message}", EngineDiagnosticCodes.ResourceDeleted, stopwatch.Elapsed);
        }
    }
}

/// <summary>The result of <see cref="DeploymentDeleter.DeleteAllAsync"/>.</summary>
public sealed record DeploymentDeleteResult(string DeploymentName, IReadOnlyList<DeleteOutcome> Outcomes, bool StateRemoved)
{
    public bool Succeeded => Outcomes.All(o => o.Succeeded);

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
    public static async Task<DeploymentDeleteResult> DeleteAllAsync(
        StateFile state,
        IStateStore store,
        ProviderRegistry registry,
        IGraphClient graph,
        RetryPolicy? retryPolicy,
        bool continueOnError,
        ILogger logger,
        CancellationToken cancellationToken)
    {
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
            var outcome = await StateDeletion.DeleteAsync(entry, registry, graph, retryPolicy, cancellationToken).ConfigureAwait(false);
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
            else
            {
                EngineLog.DeleteFailed(logger, entry.Id.Id, outcome.Error ?? "unknown error");
                state.Operations.Add(Record(entry, OperationStatus.Failed, started, DateTimeOffset.UtcNow, SecretRedactor.RedactText(outcome.Error), outcome.ErrorCode));
                stop = !continueOnError;
            }

            await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        bool allGone = state.Resources.Count == 0 && outcomes.All(o => o.Succeeded);
        if (allGone)
        {
            await store.DeleteAsync(state.DeploymentName, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            state.LastStatus = StateFile.StatusFailed;
            state.LastDeployedOn = DateTimeOffset.UtcNow;
            await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
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
