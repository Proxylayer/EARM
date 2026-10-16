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
/// (opt-in) the provider first asks it for consent to exactly that scope set (for a Key Vault <c>.default</c> request: the vault's delegated
/// <see cref="GraphCloud.KeyVaultConsentScopes"/>, which is what <c>.default</c> needs consented) and retries silently once, with a NEW credential from
/// the renew callback when one is given (a new credential over the same stored sign-in is what was verified against a real tenant).
/// </summary>
public sealed class AzureIdentityTokenProvider : IGraphTokenProvider, IDisposable
{
    /// <summary>A cached token is replaced once it has less than this left.</summary>
    public static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

    private readonly Func<TokenCredential>? _renewCredential;
    private TokenCredential _credential;
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
    /// <param name="renewCredential">
    /// Builds a new credential over the same stored sign-in; after a completed consent the provider switches to it for the silent retry and every later
    /// request. Null keeps the original credential.
    /// </param>
    public AzureIdentityTokenProvider(TokenCredential credential, GraphAuthMode mode = GraphAuthMode.Default, TimeProvider? timeProvider = null, ILogger? logger = null, SignInAccount? signIn = null, IConsentPrompter? consentPrompter = null, Func<TokenCredential>? renewCredential = null)
    {
        ArgumentNullException.ThrowIfNull(credential);
        _credential = credential;
        _mode = mode;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _signIn = signIn;
        _consentPrompter = consentPrompter;
        _renewCredential = renewCredential;
        ConsentPrompter = consentPrompter is null ? null : new RenewingPrompter(this);
    }

    /// <summary>True when the provider prompts for missing consent instead of failing.</summary>
    public bool PromptsForConsent => _consentPrompter is not null;

    /// <summary>
    /// The provider's prompter for consent found missing OUTSIDE a failed request (the preflight's <c>scp</c> check): prompts through the same one-at-a-time
    /// gate, then renews the credential and drops the cached tokens, exactly like the consent of a failed request. Null when the provider does not prompt.
    /// </summary>
    public IConsentPrompter? ConsentPrompter { get; }

    /// <summary>The credential the provider currently asks (replaced after a completed consent when a renew callback was given).</summary>
    internal TokenCredential Credential => Volatile.Read(ref _credential);

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
            GraphLog.TokenAcquired(_logger, scopes.ToString(), token.ExpiresOn, Credential.GetType().Name);
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
            GraphLog.TokenAcquired(_logger, scopes.ToString(), token.ExpiresOn, Credential.GetType().Name);
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
            return await Credential.GetTokenAsync(context, cancellationToken).ConfigureAwait(false);
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

        // What to consent: the requested set itself, except a Key Vault .default request, which needs the vault's delegated scope consented.
        var consent = new ConsentRequiredException(ConsentSetFor(scopes), _signIn?.Username, _signIn?.ClientId, failure);
        if (_consentPrompter is null)
        {
            throw ConsentNeeded(consent, prefix: null);
        }

        await PromptAsync(consent, cancellationToken).ConfigureAwait(false);
        try
        {
            // The one silent retry, on the renewed credential: the consent prompt stored the new grant in the shared persistent cache.
            return await Credential.GetTokenAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception retry) when (ConsentRequiredException.IsConsentFailure(retry))
        {
            throw ConsentNeeded(new ConsentRequiredException(consent.Scopes, _signIn?.Username, _signIn?.ClientId, retry), "Consent was completed, but the silent request for " + scopes + " still needs consent.");
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
            RenewCredential();
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

    /// <summary>After a completed consent: a new credential over the same sign-in (when a renew callback was given), and no cached token from before the grant.</summary>
    private void RenewCredential()
    {
        if (_renewCredential is not null)
        {
            Volatile.Write(ref _credential, _renewCredential());
        }

        _cache.Clear();
    }

    /// <summary>The scopes a consent prompt for <paramref name="requested"/> asks for: a Key Vault <c>.default</c> request needs the vault's <c>user_impersonation</c>.</summary>
    internal static TokenScopes ConsentSetFor(TokenScopes requested)
    {
        if (requested.IsDefault)
        {
            foreach (var cloud in GraphCloud.All)
            {
                if (string.Equals(requested.Resource, cloud.KeyVaultResource, StringComparison.OrdinalIgnoreCase))
                {
                    return cloud.KeyVaultConsentScopes;
                }
            }
        }

        return requested;
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

        var credential = Credential.GetType().Name;
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

    /// <summary><see cref="ConsentPrompter"/>: the provider's gate, prompter and credential renewal, for consent a caller found missing.</summary>
    private sealed class RenewingPrompter : IConsentPrompter
    {
        private readonly AzureIdentityTokenProvider _owner;

        public RenewingPrompter(AzureIdentityTokenProvider owner)
        {
            _owner = owner;
        }

        public async Task ConsentAsync(TokenScopes scopes, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(scopes);
            await _owner._promptGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                GraphLog.ConsentPrompting(_owner._logger, scopes.ToString());
                await _owner._consentPrompter!.ConsentAsync(scopes, cancellationToken).ConfigureAwait(false);
                _owner.RenewCredential();
            }
            finally
            {
                _owner._promptGate.Release();
            }
        }
    }
}
