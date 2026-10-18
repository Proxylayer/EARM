using System;
using System.Collections.Generic;
using System.Linq;
using EArm.Engine.Permissions;
using EArm.Graph;
using EArm.Graph.Auth;

namespace EArm.Cli.Commands;

/// <summary>
/// The consents <c>earm auth login</c> obtains: one Microsoft Graph scope set (the sign-in) and optionally the Key Vault consent (a second interactive
/// request as the same account). Graph = the base set (<see cref="GraphCloud.SignInScopes"/>: <c>User.Read</c>) plus
/// <c>--scopes</c> plus the scopes of <c>-f &lt;template&gt;</c> (union). Key Vault = <see cref="GraphCloud.KeyVaultConsentScopes"/> when the template
/// resolves Key Vault references or <c>--scopes</c> names a Key Vault scope.
/// </summary>
internal sealed record LoginConsent(TokenScopes Graph, TokenScopes? KeyVault)
{
    /// <summary>The token requests in order: the Graph sign-in first, then the Key Vault consent.</summary>
    public IReadOnlyList<TokenScopes> Requests => KeyVault is null ? [Graph] : [Graph, KeyVault];

    /// <summary>The consents for <paramref name="cloud"/>.</summary>
    /// <param name="cloud">The cloud of the sign-in.</param>
    /// <param name="scopeArguments">
    /// The <c>--scopes</c> values: short Graph names (<c>Group.ReadWrite.All</c>), Graph scopes qualified with this cloud's Graph origin, or this cloud's Key
    /// Vault scope (<c>https://vault.azure.net/user_impersonation</c>). Each value may hold several, separated by commas or spaces.
    /// </param>
    /// <param name="template">The scopes of <c>-f</c>, or null.</param>
    /// <exception cref="ArgumentException">A value is not a scope of Microsoft Graph or Key Vault in this cloud (or is <c>.default</c> / an OpenID Connect scope).</exception>
    public static LoginConsent Build(GraphCloud cloud, IEnumerable<string> scopeArguments, RunScopeSet? template)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        ArgumentNullException.ThrowIfNull(scopeArguments);
        var graph = new List<string>(cloud.SignInScopes.Names);
        var keyVault = new List<string>();
        foreach (var scope in Split(scopeArguments))
        {
            if (scope.StartsWith(cloud.KeyVaultResource + "/", StringComparison.OrdinalIgnoreCase))
            {
                keyVault.Add(scope);
            }
            else if (scope.Contains("://", StringComparison.Ordinal) && !scope.StartsWith(cloud.GraphOrigin + "/", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"--scopes: '{scope}' is not a Microsoft Graph scope ({cloud.GraphOrigin}/...) or the Key Vault scope ({cloud.KeyVaultConsentScopes}) of the {cloud.Key} cloud.");
            }
            else
            {
                graph.Add(scope);
            }
        }

        if (template is not null)
        {
            graph.AddRange(template.GraphScopes);
            if (template.NeedsKeyVault)
            {
                keyVault.AddRange(cloud.KeyVaultConsentScopes.Scopes);
            }
        }

        try
        {
            return new LoginConsent(
                TokenScopes.Explicit(cloud.GraphOrigin, graph),
                keyVault.Count == 0 ? null : TokenScopes.Explicit(cloud.KeyVaultResource, keyVault));
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException("--scopes: " + ex.Message.Split(" (Parameter", 2)[0], ex);
        }
    }

    private static IEnumerable<string> Split(IEnumerable<string> values)
    {
        return values.SelectMany(value => value.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
