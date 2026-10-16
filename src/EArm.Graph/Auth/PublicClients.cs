using System;

namespace EArm.Graph.Auth;

/// <summary>
/// The Microsoft first-party public clients (application ids) the interactive sign-in knows by name, and the default one per cloud.
/// A user signs in to one of these apps; nobody registers an application. <c>--client-id</c> / <c>EARM_CLIENT_ID</c> overrides the default.
/// </summary>
public static class PublicClients
{
    /// <summary>
    /// Microsoft Graph Command Line Tools: the first-party public client that Microsoft Graph PowerShell and the Microsoft Graph CLI sign in with.
    /// It requests delegated Microsoft Graph scopes dynamically (incremental consent), which is what EARM's per-run scope sets need, and it can also
    /// obtain Key Vault tokens (<c>user_impersonation</c>). Application id per Microsoft Learn: "Microsoft Graph Command Line Tools
    /// 14d82eec-204b-4c2f-b7e8-296a70dab67e" (Learn: troubleshoot/entra/entra-id/governance/verify-first-party-apps-sign-in, and
    /// powershell/microsoftgraph/authentication-commands). It is not available in Azure operated by 21Vianet (the <c>china</c> cloud).
    /// Consent granted to it is shared by every tool that signs in with it.
    /// </summary>
    public const string GraphCommandLineTools = "14d82eec-204b-4c2f-b7e8-296a70dab67e";

    /// <summary>
    /// Microsoft Azure CLI: Azure.Identity's built-in default public client, which earlier EARM versions signed in with. It is pre-authorized for a
    /// fixed set of Microsoft Graph permissions only, so requesting another delegated scope fails with AADSTS65002.
    /// </summary>
    public const string AzureCli = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

    /// <summary>The display name of a known client id, or null.</summary>
    public static string? NameOf(string? clientId)
    {
        if (string.Equals(clientId, GraphCommandLineTools, StringComparison.OrdinalIgnoreCase))
        {
            return "Microsoft Graph Command Line Tools";
        }

        if (string.Equals(clientId, AzureCli, StringComparison.OrdinalIgnoreCase))
        {
            return "Microsoft Azure CLI";
        }

        return null;
    }

    /// <summary>True for the Azure CLI client of earlier sign-ins.</summary>
    public static bool IsAzureCli(string? clientId)
    {
        return string.Equals(clientId, AzureCli, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The default interactive client of <paramref name="cloud"/>: <see cref="GraphCommandLineTools"/>, or null where it does not exist (<c>china</c>).</summary>
    public static string? TryDefaultFor(GraphCloud cloud)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        return string.Equals(cloud.Key, GraphCloud.China.Key, StringComparison.Ordinal) ? null : GraphCommandLineTools;
    }

    /// <summary>The default interactive client of <paramref name="cloud"/>.</summary>
    /// <exception cref="GraphConfigurationException">The cloud has no default client (<c>china</c>): pass <c>--client-id</c>.</exception>
    public static string DefaultFor(GraphCloud cloud)
    {
        return TryDefaultFor(cloud)
            ?? throw new GraphConfigurationException(
                $"The {cloud.Key} cloud has no default sign-in app: Microsoft Graph Command Line Tools ({GraphCommandLineTools}) is not available in Azure operated by 21Vianet. "
                + $"Pass --client-id (or set {GraphAuthOptions.ClientIdVariable}) with the application id of a public-client app registration in your tenant that allows delegated Microsoft Graph permissions.");
    }
}
