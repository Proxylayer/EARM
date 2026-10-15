using System;

namespace EArm.Graph;

/// <summary>Why a request failed before (or instead of) a Graph error response came back.</summary>
public enum GraphTransportFailure
{
    /// <summary>No credential produced a token (or Graph / Key Vault rejected it with 401 after one refresh). Terminal; the message says how to sign in.</summary>
    NotAuthenticated,

    /// <summary>DNS, connection, TLS or other socket-level failure. Retried (3 attempts total).</summary>
    Network,

    /// <summary>The per-call timeout expired. Retried (3 attempts total).</summary>
    Timeout,

    /// <summary>The service answered with something unusable (not JSON, no <c>responses</c> array, a pagination loop). Terminal.</summary>
    InvalidResponse,

    /// <summary>A request URL failed the origin check (foreign host, plain http, userinfo, port, path escape). Terminal; nothing was sent.</summary>
    UnsafeUrl,

    /// <summary>The response body is larger than <see cref="GraphOptions.MaxResponseBytes"/> (counted after decompression). Terminal; the body is discarded.</summary>
    ResponseTooLarge,

    /// <summary>The request body is larger than <see cref="GraphOptions.MaxRequestBodyBytes"/>. Terminal; nothing was sent.</summary>
    RequestTooLarge,

    /// <summary>A collection spans more pages or items than <see cref="GraphOptions.MaxPages"/> / <see cref="GraphOptions.MaxItems"/> allow. Terminal.</summary>
    PagingLimitExceeded,
}

/// <summary>
/// The transport failed without a Graph error body: no usable credential, a network or timeout failure, an unusable
/// response, or a URL that does not belong to the Graph origin. Graph-level errors are <see cref="GraphException"/> instead.
/// </summary>
public sealed class GraphTransportException : Exception
{
    public GraphTransportException(GraphTransportFailure failure, string message, Exception? innerException = null, bool ambiguous = false)
        : base(SecretRedactor.RedactText(message), innerException)
    {
        Failure = failure;
        Ambiguous = ambiguous;
    }

    public GraphTransportFailure Failure { get; }

    /// <summary>
    /// True when a WRITE may have reached Graph and been processed although no usable answer came back (a timeout, a dropped connection, an
    /// oversized or unreadable response). The outcome is then unknown: re-read by key instead of repeating a non-idempotent write.
    /// Always false for reads and for failures that happened before anything was sent (DNS, connection refused, TLS, an unsafe URL, an oversized request).
    /// </summary>
    public bool Ambiguous { get; }

    /// <summary>True for <see cref="GraphTransportFailure.Network"/> and <see cref="GraphTransportFailure.Timeout"/>.</summary>
    public bool IsRetryable => Failure is GraphTransportFailure.Network or GraphTransportFailure.Timeout;

    public bool IsNotAuthenticated => Failure == GraphTransportFailure.NotAuthenticated;

    /// <summary>The full text (including inner exceptions) with registered secret values and secret-looking fragments masked.</summary>
    public override string ToString()
    {
        return SecretRedactor.RedactText(base.ToString());
    }
}
