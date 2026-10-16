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
/// Azure.Identity exception. A silent failure that means "consent needed" (<see cref="ConsentRequiredException.IsConsentFailure"/>) becomes a
/// <see cref="GraphTransportException"/> whose inner exception is a <see cref="ConsentRequiredException"/>; with an <see cref="IConsentPrompter"/>
/// (opt-in) the provider first asks it for consent to exactly that scope set and retries silently once.
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
    private readonly SignInAccount? _signIn;
    private readonly IConsentPrompter? _consentPrompter;

    /// <summary>One consent prompt at a time, even when several scope sets need consent concurrently.</summary>
    private readonly SemaphoreSlim _promptGate = new(1, 1);
    private int _identityLogged;

    /// <param name="credential">The credential that produces the tokens.</param>
    /// <param name="mode">The authentication mode (for the guidance text).</param>
    /// <param name="timeProvider">The clock of the token cache.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <param name="signIn">The stored interactive sign-in the credential uses, when it uses one (named in the consent guidance).</param>
    /// <param name="consentPrompter">Opt-in incremental consent: called once per consent failure, followed by one silent retry. Null fails with <see cref="ConsentRequiredException"/>.</param>
    public AzureIdentityTokenProvider(TokenCredential credential, GraphAuthMode mode = GraphAuthMode.Default, TimeProvider? timeProvider = null, ILogger? logger = null, SignInAccount? signIn = null, IConsentPrompter? consentPrompter = null)
    {
        ArgumentNullException.ThrowIfNull(credential);
        _credential = credential;
        _mode = mode;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _signIn = signIn;
        _consentPrompter = consentPrompter;
    }

    /// <summary>True when the provider prompts for missing consent instead of failing.</summary>
    public bool PromptsForConsent => _consentPrompter is not null;

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

            var accessToken = await AcquireAsync(scopes, new TokenRequestContext([.. scopes.Scopes]), cancellationToken).ConfigureAwait(false);
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
            var accessToken = await AcquireAsync(scopes, new TokenRequestContext([.. scopes.Scopes], claims: claims), cancellationToken).ConfigureAwait(false);
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
        _promptGate.Dispose();
    }

    /// <summary>
    /// One credential call with the failure mapping: a consent failure becomes <see cref="ConsentRequiredException"/> (after the opt-in prompt and one silent
    /// retry), everything else the sign-in guidance. Always thrown as a <see cref="GraphTransportException"/> (<see cref="GraphTransportFailure.NotAuthenticated"/>).
    /// </summary>
    private async Task<AccessToken> AcquireAsync(TokenScopes scopes, TokenRequestContext context, CancellationToken cancellationToken)
    {
        Exception failure;
        try
        {
            return await _credential.GetTokenAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (!ConsentRequiredException.IsConsentFailure(failure))
        {
            throw NotAuthenticated(scopes, failure);
        }

        var consent = new ConsentRequiredException(scopes, _signIn?.Username, _signIn?.ClientId, failure);
        if (_consentPrompter is null)
        {
            throw ConsentNeeded(consent, prefix: null);
        }

        await PromptAsync(consent, cancellationToken).ConfigureAwait(false);
        try
        {
            // The one silent retry: the consent prompt stored the new grant in the shared persistent cache.
            return await _credential.GetTokenAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception retry) when (ConsentRequiredException.IsConsentFailure(retry))
        {
            throw ConsentNeeded(new ConsentRequiredException(scopes, _signIn?.Username, _signIn?.ClientId, retry), "Consent was completed, but the silent request for " + scopes + " still needs consent.");
        }
        catch (Exception retry)
        {
            throw NotAuthenticated(scopes, retry);
        }
    }

    private async Task PromptAsync(ConsentRequiredException consent, CancellationToken cancellationToken)
    {
        await _promptGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GraphLog.ConsentPrompting(_logger, consent.Scopes.ToString());
            await _consentPrompter!.ConsentAsync(consent.Scopes, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var detail = GraphAuthGuidance.CredentialLines(ex);
            var reason = detail.Count > 0 ? detail[0] : ex.GetType().Name;
            throw ConsentNeeded(new ConsentRequiredException(consent.Scopes, consent.Account, consent.ClientId, ex), "Consent for " + consent.Scopes + " was not completed: " + reason);
        }
        finally
        {
            _promptGate.Release();
        }
    }

    private GraphTransportException NotAuthenticated(TokenScopes scopes, Exception failure)
    {
        return new GraphTransportException(GraphTransportFailure.NotAuthenticated, GraphAuthGuidance.NotSignedIn(_mode, scopes.ToString(), failure), failure);
    }

    private static GraphTransportException ConsentNeeded(ConsentRequiredException consent, string? prefix)
    {
        var message = GraphAuthGuidance.ConsentNeeded(consent);
        return new GraphTransportException(GraphTransportFailure.NotAuthenticated, prefix is null ? message : prefix + Environment.NewLine + message, consent);
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
