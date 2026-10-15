namespace EArm.Az;

/// <summary>How <see cref="RetryPolicy"/> should treat failures of a request.</summary>
public enum RetryHint
{
    /// <summary>Throttling, 5xx, transient az failures and one 401 token refresh are retried; 404/400 are terminal.</summary>
    Default,

    /// <summary>
    /// As <see cref="Default"/>, plus 404 <c>Request_ResourceNotFound</c> and the replication-flavoured 400 messages
    /// are retried every 5 s for up to 90 s. Use when a referenced object was created in this run.
    /// </summary>
    ExpectReplicationLag,

    /// <summary>No retries at all; every failure surfaces immediately.</summary>
    None,
}
