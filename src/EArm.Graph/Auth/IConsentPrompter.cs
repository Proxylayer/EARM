using System.Threading;
using System.Threading.Tasks;

namespace EArm.Graph.Auth;

/// <summary>
/// Obtains consent for delegated scopes interactively (incremental consent) for the stored sign-in. Given to
/// <see cref="AzureIdentityTokenProvider"/> (through <see cref="GraphAuthFactory.Create(GraphAuthOptions, IConsentPrompter?)"/>) to opt in: when a silent
/// request fails for consent, the provider calls <see cref="ConsentAsync"/> once for exactly that scope set and retries silently once. Without a prompter
/// the provider fails with <see cref="ConsentRequiredException"/> instead (non-interactive runs). The real implementation comes from
/// <see cref="GraphAuthFactory.CreateConsentPrompter"/>.
/// </summary>
public interface IConsentPrompter
{
    /// <summary>Runs the interactive request for <paramref name="scopes"/> as the stored account. Throws when consent was not completed.</summary>
    Task ConsentAsync(TokenScopes scopes, CancellationToken cancellationToken);
}
