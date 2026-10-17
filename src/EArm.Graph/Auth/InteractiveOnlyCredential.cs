using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;

namespace EArm.Graph.Auth;

/// <summary>
/// The interactive credential of <c>auto</c> mode, with the no-fallback rule: any failure to obtain a token silently becomes an
/// <see cref="InteractiveSignInExpiredException"/> (which <see cref="GraphAuthGuidance.NotSignedIn"/> turns into "run 'earm auth login' or pass
/// '--auth default'"). Nothing else is tried. A consent failure (<see cref="ConsentRequiredException.IsConsentFailure"/>) passes through unchanged:
/// the sign-in is fine, it lacks consent, which the token provider reports (or prompts for).
/// </summary>
internal sealed class InteractiveOnlyCredential : TokenCredential
{
    private readonly TokenCredential _inner;
    private readonly string? _account;

    public InteractiveOnlyCredential(TokenCredential inner, string? account)
    {
        _inner = inner;
        _account = account;
    }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        try
        {
            return _inner.GetToken(requestContext, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !ConsentRequiredException.IsConsentFailure(ex))
        {
            throw new InteractiveSignInExpiredException(_account, ex);
        }
    }

    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        try
        {
            return await _inner.GetTokenAsync(requestContext, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !ConsentRequiredException.IsConsentFailure(ex))
        {
            throw new InteractiveSignInExpiredException(_account, ex);
        }
    }
}
