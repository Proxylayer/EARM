using System;
using System.Collections.Generic;
using EArm.Graph.Auth;

namespace EArm.Graph;

/// <summary>
/// One national cloud: the Graph host, the Microsoft Entra authority host and the Key Vault DNS suffix. The set is a fixed
/// allow-list compiled into the product (<see cref="Public"/>, <see cref="UsGov"/>, <see cref="UsGovDod"/>, <see cref="China"/>);
/// there is deliberately no way to point the transport at an arbitrary endpoint, so a bearer token can only ever be sent to a
/// host this table names.
/// </summary>
public sealed record GraphCloud
{
    /// <summary>The environment variable that selects the cloud: <c>public</c> (default), <c>usgov</c>, <c>usgovdod</c> or <c>china</c>.</summary>
    public const string EnvironmentVariable = "EARM_CLOUD";

    private GraphCloud(string key, string environmentName, string graphHost, string authorityHost, string keyVaultSuffix, int loopbackPort = 0)
    {
        Key = key;
        EnvironmentName = environmentName;
        GraphHost = graphHost;
        AuthorityHost = new Uri("https://" + authorityHost + "/", UriKind.Absolute);
        KeyVaultSuffix = keyVaultSuffix;
        LoopbackPort = loopbackPort;
    }

    /// <summary>Global Azure: <c>graph.microsoft.com</c>, <c>login.microsoftonline.com</c>, <c>vault.azure.net</c>.</summary>
    public static GraphCloud Public { get; } = new("public", "AzureCloud", "graph.microsoft.com", "login.microsoftonline.com", "vault.azure.net");

    /// <summary>Azure US Government (GCC High): <c>graph.microsoft.us</c>, <c>login.microsoftonline.us</c>, <c>vault.usgovcloudapi.net</c>.</summary>
    public static GraphCloud UsGov { get; } = new("usgov", "AzureUSGovernment", "graph.microsoft.us", "login.microsoftonline.us", "vault.usgovcloudapi.net");

    /// <summary>Azure US Government DoD: <c>dod-graph.microsoft.us</c>, <c>login.microsoftonline.us</c>, <c>vault.usgovcloudapi.net</c>.</summary>
    public static GraphCloud UsGovDod { get; } = new("usgovdod", "AzureUSGovernmentDoD", "dod-graph.microsoft.us", "login.microsoftonline.us", "vault.usgovcloudapi.net");

    /// <summary>Azure operated by 21Vianet: <c>microsoftgraph.chinacloudapi.cn</c>, <c>login.chinacloudapi.cn</c>, <c>vault.azure.cn</c>.</summary>
    public static GraphCloud China { get; } = new("china", "AzureChinaCloud", "microsoftgraph.chinacloudapi.cn", "login.chinacloudapi.cn", "vault.azure.cn");

    /// <summary>Every supported cloud, public first.</summary>
    public static IReadOnlyList<GraphCloud> All { get; } = [Public, UsGov, UsGovDod, China];

    /// <summary>The value of <c>EARM_CLOUD</c> / the cloud key: <c>public</c>, <c>usgov</c>, <c>usgovdod</c> or <c>china</c>.</summary>
    public string Key { get; }

    /// <summary>The Azure environment name reported by <c>environment().name</c> (<c>AzureCloud</c>, <c>AzureUSGovernment</c>, ...).</summary>
    public string EnvironmentName { get; }

    /// <summary>The Graph host name, without scheme.</summary>
    public string GraphHost { get; }

    /// <summary>The Microsoft Entra authority host (<c>https://login.microsoftonline.com/</c> for the public cloud).</summary>
    public Uri AuthorityHost { get; }

    /// <summary>The DNS suffix of vault host names (<c>vault.azure.net</c> for the public cloud).</summary>
    public string KeyVaultSuffix { get; }

    /// <summary>The Graph origin: <c>https://{GraphHost}</c>.</summary>
    public string GraphOrigin => LoopbackPort == 0
        ? "https://" + GraphHost
        : "http://" + GraphHost + ":" + LoopbackPort.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Non-zero only for the test-only cloud made by <see cref="LoopbackForTests"/>: the plain-http port of the in-process server. No public
    /// member, environment variable or command-line option can produce such a cloud, so the https-only origin allow-list stays absolute in the product.
    /// </summary>
    internal int LoopbackPort { get; }

    /// <summary>
    /// TEST ONLY: a cloud whose Graph origin is <c>http://127.0.0.1:{port}</c>, so the production <see cref="HttpClient"/> settings can be
    /// exercised against an in-process server. Internal (visible to the test assemblies only) and never part of <see cref="All"/> or <see cref="Parse"/>.
    /// </summary>
    internal static GraphCloud LoopbackForTests(int port)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        return new GraphCloud("loopback-test", "LoopbackTest", "127.0.0.1", "login.microsoftonline.com", "vault.azure.net", port);
    }

    /// <summary>The scope that requests a Graph token with every permission granted to the identity.</summary>
    public string GraphScope => GraphOrigin + "/.default";

    /// <summary>The scope that requests a Key Vault data-plane token (<c>https://vault.azure.net/.default</c> in the public cloud).</summary>
    public string KeyVaultScope => KeyVaultResource + "/.default";

    /// <summary>The Key Vault data-plane resource origin (<c>https://vault.azure.net</c> in the public cloud).</summary>
    public string KeyVaultResource => "https://" + KeyVaultSuffix;

    /// <summary>The <c>.default</c> scope set of Graph (<see cref="GraphScope"/>): the default token request of the Graph client and the preflight.</summary>
    public TokenScopes GraphScopes => TokenScopes.Default(GraphOrigin);

    /// <summary>The <c>.default</c> scope set of Key Vault (<see cref="KeyVaultScope"/>): the default token request of the Key Vault client.</summary>
    public TokenScopes KeyVaultScopes => TokenScopes.Default(KeyVaultResource);

    /// <summary>
    /// The delegated base set of a plain <c>earm auth login</c>: <c>User.Read</c> (the signed-in profile) and <c>Organization.Read.All</c> (the preflight's
    /// <c>GET /organization</c>), which every deployment run requests too.
    /// </summary>
    public TokenScopes SignInScopes => TokenScopes.Explicit(GraphOrigin, "User.Read", "Organization.Read.All");

    /// <summary>
    /// The delegated Key Vault scope (<c>https://vault.azure.net/user_impersonation</c> in the public cloud) that needs a one-time interactive consent; after
    /// it, <see cref="KeyVaultScopes"/> (<c>.default</c>) is obtained silently.
    /// </summary>
    public TokenScopes KeyVaultConsentScopes => TokenScopes.Explicit(KeyVaultResource, "user_impersonation");

    /// <summary>The cloud for a key (case-insensitive); null or empty means <see cref="Public"/>.</summary>
    /// <exception cref="GraphConfigurationException">The value is not one of the supported cloud keys; the message lists the valid values.</exception>
    public static GraphCloud Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Public;
        }

        var trimmed = value.Trim();
        foreach (var cloud in All)
        {
            if (string.Equals(cloud.Key, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return cloud;
            }
        }

        throw new GraphConfigurationException($"Unknown cloud '{Describe(trimmed)}' ({EnvironmentVariable}). Valid values: public, usgov, usgovdod, china.");
    }

    /// <summary>The cloud selected by <c>EARM_CLOUD</c> (public when unset).</summary>
    public static GraphCloud FromEnvironment()
    {
        return Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));
    }

    public override string ToString()
    {
        return Key;
    }

    /// <summary>A user-supplied value for a message: control characters become <c>?</c>, long values are cut.</summary>
    internal static string Describe(string value)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var c in value.Length > 40 ? value[..40] : value)
        {
            builder.Append(char.IsControl(c) ? '?' : c);
        }

        return value.Length > 40 ? builder + "..." : builder.ToString();
    }
}
