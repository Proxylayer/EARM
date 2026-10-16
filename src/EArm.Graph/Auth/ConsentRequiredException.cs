using System;
using Azure.Identity;
using Microsoft.Identity.Client;

namespace EArm.Graph.Auth;

/// <summary>
/// The stored interactive sign-in is valid, but the user (or an admin) has not consented to some of the requested delegated scopes for the sign-in app:
/// the silent token request failed with <see cref="AuthenticationRequiredException"/> wrapping an <see cref="MsalUiRequiredException"/> for AADSTS65001.
/// Distinct from <see cref="InteractiveSignInExpiredException"/> (signing in again does not help unless the new sign-in requests the scopes).
/// <para>
/// <see cref="IGraphTokenProvider"/> callers see it as the <see cref="Exception.InnerException"/> of a <see cref="GraphTransportException"/>
/// (<see cref="GraphTransportFailure.NotAuthenticated"/>, exit code 3) whose message is <see cref="GraphAuthGuidance.ConsentNeeded"/>; use
/// <see cref="FindIn"/> to get it.
/// </para>
/// </summary>
public sealed class ConsentRequiredException : Exception
{
    public ConsentRequiredException(TokenScopes scopes, string? account, string? clientId, Exception? innerException)
        : base("Consent is required for " + (scopes ?? throw new ArgumentNullException(nameof(scopes))) + ".", innerException)
    {
        Scopes = scopes;
        Account = account;
        ClientId = clientId;
    }

    /// <summary>The scope set whose silent request needed consent (one resource).</summary>
    public TokenScopes Scopes { get; }

    /// <summary>The account of the stored sign-in, when known.</summary>
    public string? Account { get; }

    /// <summary>The client (application) id of the stored sign-in, when known.</summary>
    public string? ClientId { get; }

    /// <summary>The <see cref="ConsentRequiredException"/> in <paramref name="exception"/> or its inner exceptions, or null.</summary>
    public static ConsentRequiredException? FindIn(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ConsentRequiredException consent)
            {
                return consent;
            }
        }

        return null;
    }

    /// <summary>
    /// True when a silent token failure means "consent needed": an <see cref="AuthenticationRequiredException"/> (the credential could not get a token without
    /// a prompt) whose chain holds an <see cref="MsalUiRequiredException"/> classified as consent required, with error code <c>consent_required</c>, or with
    /// AADSTS65001 in its message (observed: <c>invalid_grant</c> "AADSTS65001: The user or administrator has not consented to use the application ...").
    /// Anything else (an expired or revoked refresh token, AADSTS50076 multi-factor, an unreadable cache) is not consent.
    /// </summary>
    internal static bool IsConsentFailure(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var authenticationRequired = false;
        for (var current = failure; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationRequiredException)
            {
                authenticationRequired = true;
            }
            else if (authenticationRequired && current is MsalUiRequiredException ui && IsConsent(ui))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsConsent(MsalUiRequiredException ui)
    {
        return ui.Classification == UiRequiredExceptionClassification.ConsentRequired
            || string.Equals(ui.ErrorCode, "consent_required", StringComparison.OrdinalIgnoreCase)
            || ui.Message.Contains("AADSTS65001", StringComparison.Ordinal);
    }
}
