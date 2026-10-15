using System;
using System.Collections.Generic;

namespace EArm.Engine.State;

/// <summary>
/// The per-deployment state, keyed by resource id: which Graph objects this deployment manages,
/// pinned by Graph id, plus the outcome of the last run. Persisted through <see cref="IStateStore"/> after every
/// resource operation, so a failed run leaves an accurate partial record. Safe to commit: ids and hashes only,
/// never secrets (<see cref="StateSerializer"/> refuses to write one).
/// </summary>
public sealed class StateFile
{
    public const int CurrentSchemaVersion = 1;

    /// <summary><see cref="LastStatus"/> while a run is in progress (a crash leaves it behind).</summary>
    public const string StatusRunning = "Running";

    public const string StatusSucceeded = "Succeeded";

    public const string StatusFailed = "Failed";

    /// <summary>The run was cancelled (Ctrl-C) after it had started; whatever was applied is recorded.</summary>
    public const string StatusCancelled = "Cancelled";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public required string DeploymentName { get; init; }

    /// <summary>The tenant the state belongs to; a run against another tenant is refused (<see cref="TenantMismatchException"/>).</summary>
    public string? TenantId { get; set; }

    public DateTimeOffset? CreatedOn { get; set; }

    public DateTimeOffset? LastDeployedOn { get; set; }

    /// <summary><c>Running</c>, <c>Succeeded</c> or <c>Failed</c>.</summary>
    public string? LastStatus { get; set; }

    /// <summary><c>Incremental</c> or <c>Complete</c>.</summary>
    public string? LastMode { get; set; }

    public string? LastTemplateHash { get; set; }

    /// <summary>
    /// Entries keyed by ARM-style resource id. Keys compare case-insensitively: Graph matches every key an id maps to
    /// case-insensitively, so ids that differ only by case are the same resource (review finding E-01).
    /// </summary>
    public Dictionary<string, StateEntry> Resources { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The per-resource operations of the last run, in execution order (<c>deployment operation list</c>).</summary>
    public List<OperationRecord> Operations { get; init; } = [];

    /// <summary>A fresh state for a deployment that has never run.</summary>
    public static StateFile CreateNew(string deploymentName, string? tenantId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);
        return new StateFile
        {
            DeploymentName = deploymentName,
            TenantId = tenantId,
            CreatedOn = now,
        };
    }

    /// <summary>The pinned Graph id of a resource, or <c>null</c> when the state does not know it.</summary>
    public string? GraphIdOf(string resourceId)
    {
        ArgumentNullException.ThrowIfNull(resourceId);
        return Resources.TryGetValue(resourceId, out var entry) ? entry.GraphId : null;
    }

    public StateEntry? Find(string resourceId)
    {
        ArgumentNullException.ThrowIfNull(resourceId);
        return Resources.TryGetValue(resourceId, out var entry) ? entry : null;
    }

    /// <summary>Adds or replaces the entry under its resource id.</summary>
    public void Upsert(StateEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Remove first so a case-only rename replaces the key as well as the value (the dictionary keeps the old spelling otherwise).
        Resources.Remove(entry.Id.Id);
        Resources[entry.Id.Id] = entry;
    }

    public bool Remove(string resourceId)
    {
        ArgumentNullException.ThrowIfNull(resourceId);
        return Resources.Remove(resourceId);
    }

    /// <summary>
    /// Pins the state to <paramref name="tenantId"/>: a state without a tenant adopts it; a state of another tenant
    /// throws <see cref="TenantMismatchException"/> (CLI exit 3).
    /// </summary>
    public void EnsureTenant(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (string.IsNullOrEmpty(TenantId))
        {
            TenantId = tenantId;
            return;
        }

        if (!string.Equals(TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
        {
            throw new TenantMismatchException(DeploymentName, TenantId, tenantId);
        }
    }

    /// <summary>Marks the start of a run: status <c>Running</c>, previous operations cleared.</summary>
    public void BeginRun(string mode, string templateHash, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(mode);
        CreatedOn ??= now;
        LastStatus = StatusRunning;
        LastMode = mode;
        LastTemplateHash = templateHash;
        Operations.Clear();
    }

    /// <summary>Records the outcome of a run.</summary>
    public void CompleteRun(bool succeeded, string mode, string templateHash, DateTimeOffset now)
    {
        CompleteRun(succeeded ? StatusSucceeded : StatusFailed, mode, templateHash, now);
    }

    /// <summary>Records the outcome of a run as one of the <c>Status*</c> constants.</summary>
    public void CompleteRun(string status, string mode, string templateHash, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(mode);
        LastStatus = status;
        LastMode = mode;
        LastTemplateHash = templateHash;
        LastDeployedOn = now;
    }
}
