using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Diagnostics;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Graph;
using Microsoft.Extensions.Logging;

namespace EArm.Engine.Execution;

/// <summary>The recreate path of a deployment run: replaces an object whose immutable properties changed (delete the old object, then create it again).</summary>
internal sealed class ResourceReplacer
{
    private readonly DeploymentPlan _plan;
    private readonly ExecuteOptions _options;
    private readonly IGraphClient _graph;
    private readonly ResourceCreator _creator;
    private readonly LaggingDeleteRetry _laggingDeletes;
    private readonly ExecutionRetry _retry;
    private readonly RunDiagnostics _diagnostics;
    private readonly ILogger _logger;

    public ResourceReplacer(DeploymentPlan plan, ExecuteOptions options, IGraphClient graph, ResourceCreator creator, LaggingDeleteRetry laggingDeletes, ExecutionRetry retry, RunDiagnostics diagnostics, ILogger logger)
    {
        _plan = plan;
        _options = options;
        _graph = graph;
        _creator = creator;
        _laggingDeletes = laggingDeletes;
        _retry = retry;
        _diagnostics = diagnostics;
        _logger = logger;
    }

    /// <summary>The outcome of <see cref="RecreateAsync"/>; <see cref="Applied"/> is null when nothing was replaced.</summary>
    public sealed record RecreateOutcome(AppliedResource? Applied, string? Failure, string? FailureCode);

    /// <summary>
    /// Replaces an object whose immutable properties changed: the old object (found by key, so the replacement has the same key and identity) is deleted first,
    /// then the object is created again. Guarded like every delete (<see cref="DeleteGuards"/>: never the signed-in principal or a service principal owned by
    /// another organization) and only done when confirmed (<see cref="ExecuteOptions.ApprovedRecreates"/>); otherwise nothing is deleted or created.
    /// </summary>
    public async Task<RecreateOutcome> RecreateAsync(IResourceProvider provider, ResourceInstance instance, ResourceKey key, ExistingResource existing, CancellationToken cancellationToken)
    {
        if (DeleteGuards.Refusal(instance.Id.Id, instance.Type, existing, _plan.SignedInPrincipalId, _plan.TenantId) is { } refusal)
        {
            return new RecreateOutcome(null, DeploymentPlanner.RecreateRefusal(instance, refusal), EngineDiagnosticCodes.ProtectedObject);
        }

        if (_options.ApprovedRecreates is { } approved && !approved.Contains(instance.Id.Id))
        {
            return new RecreateOutcome(null, $"{instance.Describe()}: an immutable property changed, so the object ({existing.GraphId}) must be deleted and created again, but that replacement was not confirmed. Confirm it at the prompt or pass -y. Nothing was changed.", EngineDiagnosticCodes.RecreateNotConfirmed);
        }

        if (provider is IDeleteWarningSource warnings && await warnings.DescribeDeleteRiskAsync(key, existing, _graph, cancellationToken).ConfigureAwait(false) is { } risk)
        {
            EngineLog.DeleteWarning(_logger, instance.Id.Id, risk);
            _diagnostics.AddForInstance(instance, Diagnostic.Warning(EngineDiagnosticCodes.DestructiveDelete, $"'{instance.Id.Id}': {risk}"));
        }

        try
        {
            await DeleteOldAsync(provider, instance, key, existing, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            // Already gone: nothing left to delete.
        }

        var recreated = await _creator.CreateAsync(provider, instance, key, cancellationToken).ConfigureAwait(false);
        return new RecreateOutcome(recreated.Applied, recreated.Failure, null);
    }

    /// <summary>
    /// Deletes the old object of a replacement; a refusal that clears once an earlier write of this run reaches Graph (a named location still "trusted" right
    /// after the provider unmarked it) is repeated on the replication schedule (<see cref="LaggingDeleteRetry"/>).
    /// </summary>
    private async Task DeleteOldAsync(IResourceProvider provider, ResourceInstance instance, ResourceKey key, ExistingResource old, CancellationToken cancellationToken)
    {
        await _laggingDeletes.RunAsync(instance.Id.Id, () => _retry.WithRetryAsync(async ct =>
        {
            await provider.DeleteAsync(key, old, _graph, ct).ConfigureAwait(false);
            return true;
        }, instance.RetryHint, cancellationToken), cancellationToken).ConfigureAwait(false);
        _laggingDeletes.RecordDeleted(instance.Type);
    }
}
