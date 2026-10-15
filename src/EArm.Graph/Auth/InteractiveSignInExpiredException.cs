using System;

namespace EArm.Graph.Auth;

/// <summary>
/// The cached interactive sign-in could not produce a token silently (expired refresh token, revoked session, a cache that cannot be read). Raised by
/// the credential of <c>auto</c> mode; <see cref="GraphAuthGuidance.NotSignedIn"/> renders it as "run 'earm auth login' or pass '--auth default'".
/// Never swallowed into a fallback to another identity.
/// </summary>
internal sealed class InteractiveSignInExpiredException : Exception
{
    public InteractiveSignInExpiredException(string? account, Exception inner)
        : base("The interactive sign-in could not obtain a token silently.", inner)
    {
        Account = account;
    }

    /// <summary>The account of the stored sign-in record, when known.</summary>
    public string? Account { get; }
}
