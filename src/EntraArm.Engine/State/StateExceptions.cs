using System;

namespace EntraArm.Engine.State;

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
        return $"Deployment '{deploymentName}' is locked by {who} ({lockPath}). Wait for it to finish, or run 'entraarm state unlock -n {deploymentName}' (or pass --force-unlock) if that process is gone.";
    }
}

/// <summary>The state file belongs to another tenant than the Azure CLI session (CLI exit 3).</summary>
public sealed class TenantMismatchException : StateException
{
    public TenantMismatchException(string deploymentName, string stateTenantId, string currentTenantId)
        : base(EngineDiagnosticCodes.TenantMismatch, deploymentName, $"Deployment '{deploymentName}' was deployed to tenant {stateTenantId}, but the Azure CLI session is signed in to tenant {currentTenantId}. Sign in to the right tenant (az login --tenant {stateTenantId} --allow-no-subscriptions) or use a different deployment name / --state-dir.")
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
