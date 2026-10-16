using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Engine.Providers;
using Microsoft.Extensions.Logging;

namespace EArm.Engine.Execution;

/// <summary>
/// Repeats a provider delete that Graph refused for a reason that clears once an earlier write of the same run has reached the Conditional Access service,
/// on the <see cref="ReplicationWait"/> schedule (every <see cref="ReplicationWait.Interval"/>, up to <see cref="ReplicationWait.Window"/> of scheduled waiting):
/// <list type="bullet">
/// <item><see cref="EngineDiagnosticCodes.NamedLocationTrusted"/> (Graph <c>1177</c>): the provider set <c>isTrusted: false</c> itself a moment earlier, and
/// Graph keeps refusing the delete until the unmark is applied. Always repeated.</item>
/// <item><see cref="EngineDiagnosticCodes.NamedLocationReferenced"/>, <see cref="EngineDiagnosticCodes.AuthenticationStrengthReferenced"/>,
/// <see cref="EngineDiagnosticCodes.AuthenticationContextReferenced"/>: "still referenced by a Conditional Access policy". Repeated only when a Conditional
/// Access policy was deleted earlier in this run (<see cref="RecordDeleted"/>); otherwise the reference is real and the refusal surfaces at once.</item>
/// </list>
/// Every other failure, and the last refusal once the window is spent, propagates unchanged. A cancellation cuts the wait short and surfaces the refusal.
/// </summary>
public sealed class LaggingDeleteRetry
{
    private const string ConditionalAccessPolicyType = "Microsoft.Entra/conditionalAccess/policies";

    private readonly ReplicationWait _wait;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, byte> _deletedTypes = new(StringComparer.OrdinalIgnoreCase);

    public LaggingDeleteRetry(ReplicationWait wait, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(wait);
        ArgumentNullException.ThrowIfNull(logger);
        _wait = wait;
        _logger = logger;
    }

    /// <summary>Records that an object of <paramref name="resourceType"/> was deleted in this run (a later "referenced" refusal may then be lag).</summary>
    public void RecordDeleted(string resourceType)
    {
        ArgumentNullException.ThrowIfNull(resourceType);
        _deletedTypes.TryAdd(ResourceIdentifier.NormalizeType(resourceType), 0);
    }

    /// <summary>Runs <paramref name="delete"/>, repeating it while it fails with a refusal that <see cref="IsLagging"/> classifies as propagation lag.</summary>
    public async Task RunAsync(string resourceId, Func<Task> delete, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resourceId);
        ArgumentNullException.ThrowIfNull(delete);

        var waited = TimeSpan.Zero;
        while (true)
        {
            try
            {
                await delete().ConfigureAwait(false);
                return;
            }
            catch (ProviderException ex) when (IsLagging(ex.Code) && waited + _wait.Interval <= _wait.Window && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await _wait.Delay.DelayAsync(_wait.Interval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    ExceptionDispatchInfo.Throw(ex);
                }

                waited += _wait.Interval;
                EngineLog.RetryingRefusedDelete(_logger, resourceId, ex.Code, waited.TotalSeconds, _wait.Window.TotalSeconds);
            }
        }
    }

    /// <summary>True when a delete refused with <paramref name="code"/> is worth repeating in this run.</summary>
    public bool IsLagging(string? code)
    {
        return code switch
        {
            EngineDiagnosticCodes.NamedLocationTrusted => true,
            EngineDiagnosticCodes.NamedLocationReferenced
                or EngineDiagnosticCodes.AuthenticationStrengthReferenced
                or EngineDiagnosticCodes.AuthenticationContextReferenced => _deletedTypes.ContainsKey(ResourceIdentifier.NormalizeType(ConditionalAccessPolicyType)),
            _ => false,
        };
    }
}
