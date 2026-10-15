using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine.Providers;
using EArm.Engine.State;

namespace EArm.Engine.Execution;

/// <summary>
/// Hook for state persistence (plan section 1.7): the executor reports every resource outcome as it happens so a
/// failed run leaves an accurate partial record. <see cref="FileStateSink"/> implements it on top of an
/// <see cref="IStateStore"/>; <see cref="NullStateSink"/> discards everything. The members with default bodies were
/// added with the state store (WI-06) and are optional for implementers.
/// </summary>
public interface IStateSink
{
    /// <summary>A resource was created, updated, or found unchanged (<paramref name="change"/> says which).</summary>
    Task ResourceAppliedAsync(ResourceInstance instance, ResourceChange change, AppliedResource applied, CancellationToken cancellationToken);

    /// <summary>Applying a resource failed; the state keeps whatever it knew before.</summary>
    Task ResourceFailedAsync(ResourceInstance instance, string error, CancellationToken cancellationToken);

    /// <summary>The run finished (after outputs and the mode pass); <paramref name="status"/> is the deployment status to record.</summary>
    Task CompletedAsync(DeploymentStatus status, CancellationToken cancellationToken);

    /// <summary>The executor is about to evaluate, read and apply the resource (start of its operation record).</summary>
    Task ResourceStartingAsync(ResourceInstance instance, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>The resource was not attempted because a dependency (or an earlier batch) failed.</summary>
    Task ResourceSkippedAsync(ResourceInstance instance, string reason, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>The resource was ignored (<c>condition</c> false or an <c>Ignore</c> change).</summary>
    Task ResourceIgnoredAsync(ResourceInstance instance, string reason, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>Mode pass, Incremental: the entry is no longer declared and is dropped from the state (the object stays).</summary>
    Task ResourceDetachedAsync(StateEntry entry, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>Mode pass, Complete: the object was deleted (or found already gone); the entry is dropped.</summary>
    Task ResourceDeletedAsync(StateEntry entry, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>Mode pass, Complete: deleting the object failed; the entry stays.</summary>
    Task ResourceDeleteFailedAsync(StateEntry entry, string error, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Edge pass (WI-09): the resource's relationships were reconciled; <paramref name="relationships"/> is what the
    /// entry must now record (the principal-side edges this deployment created), or <c>null</c> for none.
    /// </summary>
    Task ResourceEdgesAppliedAsync(ResourceInstance instance, JsonObject? relationships, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>Edge pass: reconciling the resource's relationships failed; the entry keeps its previous records.</summary>
    Task ResourceEdgesFailedAsync(ResourceInstance instance, string error, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

/// <summary>Records nothing.</summary>
public sealed class NullStateSink : IStateSink
{
    public static NullStateSink Instance { get; } = new();

    public Task ResourceAppliedAsync(ResourceInstance instance, ResourceChange change, AppliedResource applied, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task ResourceFailedAsync(ResourceInstance instance, string error, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task CompletedAsync(DeploymentStatus status, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
