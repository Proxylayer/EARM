using System;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;

namespace EArm.Engine.Execution;

/// <summary>Runs the Graph calls of a deployment run through the plan's retry policy, or directly when the plan carries none.</summary>
internal sealed class ExecutionRetry
{
    private readonly RetryPolicy? _policy;

    public ExecutionRetry(RetryPolicy? policy)
    {
        _policy = policy;
    }

    /// <param name="idempotent">
    /// <c>false</c> for a create that is a POST (not an upsert): the outer retry then never repeats it after a failure that may have followed a commit (a 5xx, a timeout,
    /// a dropped connection) - those are answered by the key re-read - and only waits out throttling and replication lag (review round 2, S2-07). Reads and deletes are idempotent.
    /// </param>
    public Task<T> WithRetryAsync<T>(Func<CancellationToken, Task<T>> operation, RetryHint hint, CancellationToken cancellationToken, bool idempotent = true)
    {
        return _policy is null
            ? operation(cancellationToken)
            : _policy.ExecuteAsync(operation, hint, null, cancellationToken, idempotent);
    }
}
