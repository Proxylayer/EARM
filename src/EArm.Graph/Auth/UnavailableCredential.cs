using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;

namespace EArm.Graph.Auth;

/// <summary>A credential that is never available; its message ends up in the "credentials tried" list.</summary>
internal sealed class UnavailableCredential : TokenCredential
{
    private readonly string _message;

    public UnavailableCredential(string message)
    {
        _message = message;
    }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        throw new CredentialUnavailableException(_message);
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        throw new CredentialUnavailableException(_message);
    }
}
