using System;

namespace EArm.Graph.Auth;

/// <summary>Resolves the token scopes of a client: the given set, or the cloud's <c>.default</c> fallback; a set for another resource is refused.</summary>
internal static class TokenScopeGuard
{
    /// <exception cref="ArgumentException">The scopes are not for the fallback's resource (the token would be attached to requests for the wrong audience).</exception>
    public static TokenScopes Resolve(TokenScopes? scopes, TokenScopes fallback, GraphCloud cloud, string parameterName)
    {
        var resolved = scopes ?? fallback;
        if (!string.Equals(resolved.Resource, fallback.Resource, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"The token scopes '{resolved}' are not for {fallback.Resource}, the expected resource in the {cloud.Key} cloud.", parameterName);
        }

        return resolved;
    }
}
