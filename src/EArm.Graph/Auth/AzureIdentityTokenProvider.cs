using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EArm.Graph.Auth;

/// <summary>
/// <see cref="IGraphTokenProvider"/> over any Azure.Identity <see cref="TokenCredential"/>. Tokens are cached per scope set (<see cref="TokenScopes.CacheKey"/>: order- and case-insensitive) until
/// <see cref="RefreshSkew"/> (5 minutes) before <see cref="AccessToken.ExpiresOn"/>; concurrent callers for the same set share
/// one credential call. A credential failure becomes a <see cref="GraphTransportException"/> with the sign-in guidance, never a raw
/// Azure.Identity exception.
/// </summary>
public sealed class AzureIdentityTokenProvider : IGraphTokenProvider, IDisposable
{
    /// <summary>A cached token is replaced once it has less than this left.</summary>
    public static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

    private readonly TokenCredential _credential;
    private readonly GraphAuthMode _mode;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, GraphToken> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private int _identityLogged;

    public AzureIdentityTokenProvider(TokenCredential credential, GraphAuthMode mode = GraphAuthMode.Default, TimeProvider? timeProvider = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(credential);
        _credential = credential;
        _mode = mode;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    public async ValueTask<GraphToken> GetTokenAsync(TokenScopes scopes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        var key = scopes.CacheKey;
        if (TryGetFresh(key, out var cached))
        {
            return cached;
        }

        var gate = _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetFresh(key, out cached))
            {
                return cached;
            }

            AccessToken accessToken;
            try
            {
                accessToken = await _credential.GetTokenAsync(new TokenRequestContext([.. scopes.Scopes]), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new GraphTransportException(GraphTransportFailure.NotAuthenticated, GraphAuthGuidance.NotSignedIn(_mode, scopes.ToString(), ex), ex);
            }

            var token = new GraphToken(accessToken.Token, accessToken.ExpiresOn);
            _cache[key] = token;
            GraphLog.TokenAcquired(_logger, scopes.ToString(), token.ExpiresOn, _credential.GetType().Name);
            LogIdentityOnce(token);
            return token;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Invalidate(TokenScopes scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        var key = scopes.CacheKey;
        _cache.TryRemove(key, out _);
    }

    /// <summary>
    /// A token after a 401. Without a claims challenge the cached token is dropped and the credential asked again. With one (continuous access evaluation) the
    /// credential is asked for a token carrying those claims (<see cref="TokenRequestContext.Claims"/>), which makes MSAL skip its cache; the result replaces the cached token.
    /// </summary>
    public async ValueTask<GraphToken> RefreshTokenAsync(TokenScopes scopes, string? claims, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        var key = scopes.CacheKey;
        if (string.IsNullOrEmpty(claims))
        {
            Invalidate(scopes);
            return await GetTokenAsync(scopes, cancellationToken).ConfigureAwait(false);
        }

        var gate = _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AccessToken accessToken;
            try
            {
                accessToken = await _credential.GetTokenAsync(new TokenRequestContext([.. scopes.Scopes], claims: claims), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new GraphTransportException(GraphTransportFailure.NotAuthenticated, GraphAuthGuidance.NotSignedIn(_mode, scopes.ToString(), ex), ex);
            }

            var token = new GraphToken(accessToken.Token, accessToken.ExpiresOn);
            _cache[key] = token;
            GraphLog.TokenAcquired(_logger, scopes.ToString(), token.ExpiresOn, _credential.GetType().Name);
            return token;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        foreach (var gate in _locks.Values)
        {
            gate.Dispose();
        }

        _locks.Clear();
    }

    /// <summary>
    /// Logs WHO the first token belongs to (tenant, object id, app id, principal) at Information level, so a user or an audit log can see which identity
    /// EARM acts as. Only the decoded display claims are logged, never the token itself.
    /// </summary>
    private void LogIdentityOnce(GraphToken token)
    {
        if (Interlocked.Exchange(ref _identityLogged, 1) != 0)
        {
            return;
        }

        var credential = _credential.GetType().Name;
        if (TokenClaims.TryDecode(token.Value, out var claims))
        {
            GraphLog.IdentityUsed(_logger, claims.PrincipalName, claims.PrincipalType, claims.TenantId, claims.ObjectId, claims.AppId, credential);
        }
        else
        {
            GraphLog.IdentityUnreadable(_logger, credential);
        }
    }

    private bool TryGetFresh(string key, out GraphToken token)
    {
        if (_cache.TryGetValue(key, out var found) && found.ExpiresOn - RefreshSkew > _time.GetUtcNow())
        {
            token = found;
            return true;
        }

        token = null!;
        return false;
    }
}
