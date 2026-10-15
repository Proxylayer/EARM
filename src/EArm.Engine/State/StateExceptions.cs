using System;

namespace EArm.Engine.State;

/// <summary>Base of the state-store failures; <see cref="Code"/> is an <see cref="EngineDiagnosticCodes"/> constant the CLI maps to an exit code.</summary>
public abstract class StateException : Exception
{
    protected StateException(string code, string deploymentName, string message, Exception? inner = null)
        : base(message, inner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(deploymentName);
        Code = code;
        DeploymentName = deploymentName;
    }

    public string Code { get; }

    public string DeploymentName { get; }
}

/// <summary>Another process holds the deployment's lock file (CLI exit 4).</summary>
public sealed class StateLockedException : StateException
{
    public StateLockedException(string deploymentName, string lockPath, StateLockInfo? holder)
        : base(EngineDiagnosticCodes.StateLocked, deploymentName, FormatMessage(deploymentName, lockPath, holder))
    {
        LockPath = lockPath;
        Holder = holder;
    }

    public string LockPath { get; }

    public StateLockInfo? Holder { get; }

    private static string FormatMessage(string deploymentName, string lockPath, StateLockInfo? holder)
    {
        string who = holder is null ? "another process" : holder.Describe();
        return $"Deployment '{deploymentName}' is locked by {who} ({lockPath}). Wait for it to finish, or run 'earm state unlock -n {deploymentName}' (or pass --force-unlock) if that process is gone.";
    }
}

/// <summary>The state file belongs to another tenant than the identity EARM is signed in as (CLI exit 3).</summary>
public sealed class TenantMismatchException : StateException
{
    public TenantMismatchException(string deploymentName, string stateTenantId, string currentTenantId)
        : base(EngineDiagnosticCodes.TenantMismatch, deploymentName, $"Deployment '{deploymentName}' was deployed to tenant {stateTenantId}, but EARM is signed in to tenant {currentTenantId}. Sign in to the right tenant (set AZURE_TENANT_ID, or run 'earm auth login --tenant {stateTenantId}') or use a different deployment name / --state-dir.")
    {
        StateTenantId = stateTenantId;
        CurrentTenantId = currentTenantId;
    }

    public string StateTenantId { get; }

    public string CurrentTenantId { get; }
}

/// <summary>The state file has a newer <c>schemaVersion</c> than this executor understands, or is not a state file at all.</summary>
public sealed class StateSchemaUnsupportedException : StateException
{
    public StateSchemaUnsupportedException(string deploymentName, string message, Exception? inner = null)
        : base(EngineDiagnosticCodes.StateSchemaUnsupported, deploymentName, message, inner)
    {
    }
}

/// <summary>
/// The state directory or one of its files cannot be used safely: the directory is not a directory, or it (or a state, lock or
/// temp file) is a symbolic link or junction (review finding S-06). The CLI reports it like any state failure (exit 1).
/// </summary>
public sealed class StateStoreException : StateException
{
    public StateStoreException(string deploymentName, string message, Exception? inner = null)
        : base(EngineDiagnosticCodes.StateStoreUnsafe, deploymentName, message, inner)
    {
    }
}

/// <summary>
/// A state entry fails the integrity rules of <see cref="StateKeyRules"/> / <see cref="StateIdShapes"/>: its key is not the key its resource id implies,
/// or a recorded id is malformed. The whole state is refused (nothing is acted on); fix the file or run <c>earm state forget</c> for the entry.
/// </summary>
public sealed class StateEntryInvalidException : StateException
{
    public StateEntryInvalidException(string deploymentName, string resourceId, string detail)
        : base(EngineDiagnosticCodes.StateEntryInvalid, deploymentName, $"The state entry '{resourceId}' of deployment '{deploymentName}' is invalid: {detail}. The state file is corrupt or was edited; nothing was acted on. Fix the entry, or remove it with 'earm state forget -n {deploymentName} --resource-id {resourceId}' (the object stays in the tenant).")
    {
        ResourceId = resourceId;
        Detail = detail;
    }

    public string ResourceId { get; }

    public string Detail { get; }
}
