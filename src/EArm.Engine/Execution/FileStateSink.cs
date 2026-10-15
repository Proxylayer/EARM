using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Core.Secrets;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Engine.State;
using EArm.Expressions.Values;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EArm.Engine.Execution;

/// <summary>
/// The <see cref="IStateSink"/> that keeps a <see cref="StateFile"/> current through an <see cref="IStateStore"/>:
/// every applied resource upserts its entry (key, Graph id, parent id, content hash, dependency ids, write time,
/// provider extra data) and the state is saved atomically <b>after every resource</b> (not cancellable: see
/// <c>MutateAsync</c>; only <see cref="BeginAsync"/> honours the caller's token); failures keep the previous
/// entry and record the (redacted) error; the mode pass removes detached and deleted entries; completion records
/// the run's status, time, mode and template hash. Thread-safe: the executor applies resources in parallel.
/// </summary>
public sealed class FileStateSink : IStateSink
{
    private readonly IStateStore _store;
    private readonly DeploymentPlan _plan;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly TimeSpan _persistTimeout;

    /// <summary>The default longest a state save may take once the work it records has happened (the caller's cancellation is not applied to it).</summary>
    internal static readonly TimeSpan PersistTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How many times one state save is attempted before the I/O failure is reported.</summary>
    public const int SaveAttempts = 5;

    /// <summary>The pause before the second save attempt; it doubles for every further attempt.</summary>
    public TimeSpan SaveRetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(50);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _started = new(StringComparer.Ordinal);
    private bool _begun;

    /// <param name="persistTimeout">The bound on one state change plus save (default <see cref="PersistTimeout"/>); a save that exceeds it is abandoned and reported as an <see cref="IOException"/>.</param>
    public FileStateSink(IStateStore store, StateFile state, DeploymentPlan plan, ILogger? logger = null, TimeProvider? timeProvider = null, TimeSpan? persistTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(plan);
        _store = store;
        State = state;
        _plan = plan;
        _logger = logger ?? NullLogger.Instance;
        _time = timeProvider ?? TimeProvider.System;
        _persistTimeout = persistTimeout ?? PersistTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_persistTimeout, TimeSpan.Zero, nameof(persistTimeout));
    }

    /// <summary>The state being maintained (the same instance the executor consults for pinned ids and the mode pass).</summary>
    public StateFile State { get; }

    /// <summary>Marks the run as started (<c>Running</c>, operations cleared, tenant pinned) and saves; called once before execution.</summary>
    public async Task BeginAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_begun)
            {
                return;
            }

            _begun = true;
            if (_plan.TenantId is { } tenant)
            {
                State.EnsureTenant(tenant);
            }

            State.BeginRun(_plan.Mode.ToString(), _plan.TemplateHash, _time.GetUtcNow());
            await SaveWithRetryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task ResourceStartingAsync(ResourceInstance instance, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);
        lock (_started)
        {
            _started[instance.PlannedId.Id] = _time.GetUtcNow();
        }

        return Task.CompletedTask;
    }

    public Task ResourceAppliedAsync(ResourceInstance instance, ResourceChange change, AppliedResource applied, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(applied);
        if (instance.IsSymbolic)
        {
            throw new InvalidOperationException($"{instance.Describe()}: a resource with a deploy-time name cannot be recorded in the state; its name must be resolved first ({instance.PlannedId.Id}).");
        }

        if (SecretJson.ContainsSecret(applied.Extra) || UnknownJson.ContainsUnknown(applied.Extra))
        {
            throw new InvalidOperationException($"{instance.Describe()}: the provider returned state extra data containing a secret or a deploy-time placeholder; the state file must never contain either.");
        }

        var now = _time.GetUtcNow();
        var previous = State.Find(instance.Id.Id);
        var entry = new StateEntry
        {
            Id = instance.Id,
            Type = instance.Type,
            ApiVersion = instance.ApiVersion,
            Key = new Dictionary<string, string>(applied.Key.Values, StringComparer.OrdinalIgnoreCase),
            GraphId = applied.GraphId,
            ParentGraphId = applied.Key.ParentGraphId ?? instance.ParentGraphId,
            ContentHash = StateHashing.ContentHash(instance.Properties),
            Provenance = applied.Provenance ?? previous?.Provenance ?? StateProvenance.Created,

            // The immutable fingerprint of the object: what Graph reports as createdDateTime (kept from the previous entry while it is the same object and the live read does not carry it).
            CreatedDateTime = ProviderReads.CreatedDateTimeOf(applied.Live)
                ?? (previous is not null && string.Equals(previous.GraphId, applied.GraphId, StringComparison.OrdinalIgnoreCase) ? previous.CreatedDateTime : null),
            ProvenanceRecorded = true,
            DependsOn = DependencyIds(instance),
            LastWriteOn = change.IsWrite ? now : previous?.LastWriteOn ?? now,
            Extra = applied.Extra?.DeepClone().AsObject(),
            Relationships = previous?.Relationships,
        };

        return MutateAsync(() =>
        {
            State.Upsert(entry);
            State.Operations.Add(Record(instance, OperationStatus.Succeeded, change.Type, applied.GraphId, null, null, now));
        });
    }

    public Task ResourceFailedAsync(ResourceInstance instance, string error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var now = _time.GetUtcNow();
        return MutateAsync(() => State.Operations.Add(Record(instance, OperationStatus.Failed, _plan.ChangeFor(instance)?.Type, State.GraphIdOf(instance.Id.Id), SecretRedactor.RedactText(error), EngineDiagnosticCodes.ResourceApplyFailed, now)));
    }

    public Task ResourceSkippedAsync(ResourceInstance instance, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var now = _time.GetUtcNow();
        return MutateAsync(() => State.Operations.Add(Record(instance, OperationStatus.Skipped, null, null, reason, EngineDiagnosticCodes.ResourceSkipped, now)));
    }

    public Task ResourceIgnoredAsync(ResourceInstance instance, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var now = _time.GetUtcNow();
        return MutateAsync(() => State.Operations.Add(Record(instance, OperationStatus.Ignored, ChangeType.Ignore, null, reason, null, now)));
    }

    public Task ResourceDetachedAsync(StateEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var now = _time.GetUtcNow();
        return MutateAsync(() =>
        {
            State.Remove(entry.Id.Id);
            State.Operations.Add(Record(entry, OperationStatus.Detached, ChangeType.Ignore, null, null, now));
        });
    }

    public Task ResourceDeletedAsync(StateEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var now = _time.GetUtcNow();
        return MutateAsync(() =>
        {
            State.Remove(entry.Id.Id);
            State.Operations.Add(Record(entry, OperationStatus.Succeeded, ChangeType.Delete, null, null, now));
        });
    }

    public Task ResourceDeleteFailedAsync(StateEntry entry, string error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var now = _time.GetUtcNow();
        return MutateAsync(() => State.Operations.Add(Record(entry, OperationStatus.Failed, ChangeType.Delete, SecretRedactor.RedactText(error), EngineDiagnosticCodes.ResourceDeleted, now)));
    }

    public Task ResourceEdgesAppliedAsync(ResourceInstance instance, JsonObject? relationships, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (SecretJson.ContainsSecret(relationships) || UnknownJson.ContainsUnknown(relationships))
        {
            throw new InvalidOperationException($"{instance.Describe()}: the edge pass produced relationship records containing a secret or a deploy-time placeholder; the state file must never contain either.");
        }

        return MutateAsync(() =>
        {
            if (State.Find(instance.Id.Id) is { } entry)
            {
                State.Upsert(entry with { Relationships = relationships?.DeepClone().AsObject() });
            }
        });
    }

    public Task ResourceEdgesFailedAsync(ResourceInstance instance, string error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var now = _time.GetUtcNow();
        return MutateAsync(() => State.Operations.Add(Record(instance, OperationStatus.Failed, _plan.ChangeFor(instance)?.Type, State.GraphIdOf(instance.Id.Id), SecretRedactor.RedactText(error), EngineDiagnosticCodes.EdgeApplyFailed, now)));
    }

    public Task CompletedAsync(DeploymentStatus status, CancellationToken cancellationToken)
    {
        string text = status switch
        {
            DeploymentStatus.Succeeded => StateFile.StatusSucceeded,
            DeploymentStatus.Cancelled => StateFile.StatusCancelled,
            _ => StateFile.StatusFailed,
        };
        return MutateAsync(() => State.CompleteRun(text, _plan.Mode.ToString(), _plan.TemplateHash, _time.GetUtcNow()));
    }

    // ---- helpers ----

    /// <summary>
    /// Applies one state change and saves, independent of the caller's cancellation. Every change recorded here describes work that
    /// already happened (an object written to or deleted from the tenant, a failure, a skip, the run's completion), and a cancellation
    /// that arrives right after a Graph write must not drop its entry: the object would exist in the tenant but not in the state, and
    /// anything Graph generates (a secret, a key id) would be orphaned. A cancelled run stops starting NEW work (the executor checks
    /// the token between resources and batches); this persistence is bounded by the persist timeout instead (<see cref="PersistTimeout"/> by default,
    /// measured on the sink's <see cref="TimeProvider"/>). A save that does not finish in time is abandoned even when the store ignores
    /// its token, and surfaces as an <see cref="IOException"/> so the executor reports a state-save failure instead of hanging.
    /// </summary>
    private async Task MutateAsync(Action mutation)
    {
        using var timeout = new CancellationTokenSource(_persistTimeout, _time);
        try
        {
            await _gate.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                mutation();
                await SaveWithRetryAsync(timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            string message = $"saving the state did not complete within {_persistTimeout.TotalSeconds:0.###} seconds and was abandoned";
            EngineLog.StateSaveFailed(_logger, message);
            throw new IOException(message);
        }
    }

    /// <summary>
    /// Saves the state, retrying an I/O or permission failure (an antivirus scan holding the file, a briefly locked folder) up to
    /// <see cref="SaveAttempts"/> times with a growing pause. The final failure propagates as <see cref="IOException"/> /
    /// <see cref="UnauthorizedAccessException"/>; the executor turns it into a failed resource instead of a crash (review finding E-03c).
    /// </summary>
    private async Task SaveWithRetryAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await _store.SaveAsync(State, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= SaveAttempts)
                {
                    EngineLog.StateSaveFailed(_logger, ex.Message);
                    throw;
                }

                EngineLog.StateSaveRetry(_logger, ex.Message, attempt);
                await Task.Delay(TimeSpan.FromTicks(SaveRetryBaseDelay.Ticks << (attempt - 1)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The concrete resource ids of the instance's dependencies (symbolic planned ids mapped through their resolved instance).</summary>
    private IReadOnlyList<string> DependencyIds(ResourceInstance instance)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var dependency in instance.Dependencies)
        {
            var target = _plan.FindInstance(dependency);
            if (target is null)
            {
                ids.Add(dependency.Id);
                continue;
            }

            if (target.IsSymbolic)
            {
                // Never applied (its name never resolved); it cannot be in the state either.
                continue;
            }

            ids.Add(target.Id.Id);
        }

        return [.. ids];
    }

    private OperationRecord Record(ResourceInstance instance, OperationStatus status, ChangeType? changeType, string? graphId, string? error, string? code, DateTimeOffset finished)
    {
        DateTimeOffset? started;
        lock (_started)
        {
            started = _started.TryGetValue(instance.PlannedId.Id, out var found) ? found : null;
        }

        int? batch = _plan.Batches.FirstOrDefault(b => b.Instances.Contains(instance))?.Index;
        return new OperationRecord
        {
            ResourceId = instance.Id.Id,
            Type = instance.Type,
            Status = status,
            ChangeType = changeType,
            Batch = batch,
            StartedOn = started ?? finished,
            FinishedOn = finished,
            GraphId = graphId,
            Error = error,
            ErrorCode = code,
        };
    }

    private static OperationRecord Record(StateEntry entry, OperationStatus status, ChangeType changeType, string? error, string? code, DateTimeOffset finished)
    {
        return new OperationRecord
        {
            ResourceId = entry.Id.Id,
            Type = entry.Type,
            Status = status,
            ChangeType = changeType,
            StartedOn = finished,
            FinishedOn = finished,
            GraphId = entry.GraphId,
            Error = error,
            ErrorCode = code,
        };
    }
}
