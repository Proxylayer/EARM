using System;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;

namespace EArm.Engine;

/// <summary>
/// Runs the engine's Graph calls (planning, the what-if pass, the executor, the edge pass, the orphan report, <c>deployment delete</c>) through the run's
/// retry policy, or directly when there is none.
/// </summary>
internal sealed class GraphCallRetry
{
    private readonly RetryPolicy? _policy;

    public GraphCallRetry(RetryPolicy? policy)
    {
        _policy = policy;
    }

    /// <param name="operation">The Graph call; it receives the token to observe.</param>
    /// <param name="hint">How the policy classifies a 404 or 400 (a plain read, or a write that may race replication).</param>
    /// <param name="cancellationToken">The caller's token.</param>
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
