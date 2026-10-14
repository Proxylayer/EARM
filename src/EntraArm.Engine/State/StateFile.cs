using System;
using System.Collections.Generic;

namespace EntraArm.Engine.State;

/// <summary>
/// The per-deployment state (plan section 1.7), keyed by resource id: which Graph objects this deployment manages,
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

    /// <summary>Entries keyed by ARM-style resource id (case-sensitive names, case-insensitive type casing already canonical).</summary>
    public Dictionary<string, StateEntry> Resources { get; init; } = new(StringComparer.Ordinal);

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
        ArgumentNullException.ThrowIfNull(mode);
        LastStatus = succeeded ? StatusSucceeded : StatusFailed;
        LastMode = mode;
        LastTemplateHash = templateHash;
        LastDeployedOn = now;
    }
}
