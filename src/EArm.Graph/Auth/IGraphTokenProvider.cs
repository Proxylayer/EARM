using System;
using System.Threading;
using System.Threading.Tasks;

namespace EArm.Graph.Auth;

/// <summary>An access token and its expiry. The value is a secret: never log it, never put it in an exception message.</summary>
/// <param name="Value">The bearer token.</param>
/// <param name="ExpiresOn">When the token stops being valid.</param>
public sealed record GraphToken(string Value, DateTimeOffset ExpiresOn)
{
    /// <summary>Never prints the token, even through <c>ToString</c> or a logged record.</summary>
    public override string ToString()
    {
        return $"GraphToken(expires {ExpiresOn:u})";
    }
}

/// <summary>
/// Supplies bearer tokens for a <see cref="TokenScopes"/> set of one resource (<c>https://graph.microsoft.com/.default</c>, <c>https://vault.azure.net/.default</c>,
/// or explicit delegated scopes). Equal sets (same scopes in any order or case) share one cached token; different sets get separate tokens.
/// </summary>
public interface IGraphTokenProvider
{
    /// <summary>A token for <paramref name="scopes"/>, served from the cache until shortly before it expires.</summary>
    /// <exception cref="GraphTransportException">No credential produced a token (<see cref="GraphTransportFailure.NotAuthenticated"/>).</exception>
    ValueTask<GraphToken> GetTokenAsync(TokenScopes scopes, CancellationToken cancellationToken);

    /// <summary>Drops the cached token for the scope set, so the next <see cref="GetTokenAsync"/> asks the credential again (used after a 401).</summary>
    void Invalidate(TokenScopes scopes);

    /// <summary>
    /// A fresh token after a 401 (the one refresh): the cached token is dropped first. <paramref name="claims"/> is the decoded JSON of a continuous access
    /// evaluation challenge (<c>WWW-Authenticate: Bearer ... claims="..."</c>); a provider that can request a token with claims does so, which bypasses its
    /// cache. The default implementation ignores the claims. The caller compares the result with the token that was rejected: an identical token ends the call.
    /// </summary>
    ValueTask<GraphToken> RefreshTokenAsync(TokenScopes scopes, string? claims, CancellationToken cancellationToken)
    {
        Invalidate(scopes);
        return GetTokenAsync(scopes, cancellationToken);
    }
}
