using System;

namespace EArm.Graph.Auth;

/// <summary>How EARM obtains tokens.</summary>
public enum GraphAuthMode
{
    /// <summary>The default: a cached interactive sign-in (<c>earm auth login</c>) when one exists, otherwise the default chain.</summary>
    Auto,

    /// <summary>Azure.Identity's <c>DefaultAzureCredential</c> with every credential that starts an external process (Azure CLI, azd, Azure PowerShell, Visual Studio) excluded.</summary>
    Default,

    /// <summary>The cached interactive sign-in only; fails with a pointer to <c>earm auth login</c> when there is none.</summary>
    Interactive,
}

/// <summary>Authentication settings: <c>--auth</c>, <c>--tenant</c>, <c>--client-id</c> and the matching <c>EARM_*</c> variables.</summary>
public sealed record GraphAuthOptions
{
    public const string AuthVariable = "EARM_AUTH";
    public const string TenantVariable = "EARM_TENANT_ID";
    public const string ClientIdVariable = "EARM_CLIENT_ID";

    public GraphAuthMode Mode { get; init; } = GraphAuthMode.Auto;

    /// <summary>The tenant to sign in to (<c>--tenant</c> / <c>EARM_TENANT_ID</c>); null lets the credential choose.</summary>
    public string? TenantId { get; init; }

    /// <summary>The public-client app registration used by the interactive sign-in (<c>--client-id</c> / <c>EARM_CLIENT_ID</c>); null uses Azure.Identity's default public client.</summary>
    public string? ClientId { get; init; }

    public GraphCloud Cloud { get; init; } = GraphCloud.Public;

    /// <summary>Command-line values with environment fallbacks: a non-empty argument wins over the variable.</summary>
    /// <exception cref="GraphConfigurationException">An unknown mode, or a tenant / client id that is not a GUID or domain name.</exception>
    public static GraphAuthOptions Resolve(string? auth, string? tenant, string? clientId, GraphCloud cloud)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        var modeText = FirstNonEmpty(auth, Environment.GetEnvironmentVariable(AuthVariable));
        return new GraphAuthOptions
        {
            Mode = ParseMode(modeText),
            TenantId = ValidateTenant(FirstNonEmpty(tenant, Environment.GetEnvironmentVariable(TenantVariable))),
            ClientId = ValidateClientId(FirstNonEmpty(clientId, Environment.GetEnvironmentVariable(ClientIdVariable))),
            Cloud = cloud,
        };
    }

    public static GraphAuthMode ParseMode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return GraphAuthMode.Auto;
        }

        return text.Trim().ToLowerInvariant() switch
        {
            "auto" => GraphAuthMode.Auto,
            "default" => GraphAuthMode.Default,
            "interactive" => GraphAuthMode.Interactive,
            _ => throw new GraphConfigurationException($"Unknown authentication mode '{GraphCloud.Describe(text.Trim())}' (--auth / {AuthVariable}). Valid values: auto, default, interactive."),
        };
    }

    public static string ModeName(GraphAuthMode mode)
    {
        return mode switch
        {
            GraphAuthMode.Auto => "auto",
            GraphAuthMode.Default => "default",
            GraphAuthMode.Interactive => "interactive",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown authentication mode."),
        };
    }

    private static string? ValidateTenant(string? tenant)
    {
        if (tenant is null)
        {
            return null;
        }

        // A tenant id (GUID) or a verified domain name; also the well-known aliases organizations / common / consumers.
        var ok = tenant.Length <= 253;
        foreach (var c in tenant)
        {
            ok &= char.IsAsciiLetterOrDigit(c) || c is '-' or '.';
        }

        if (!ok)
        {
            throw new GraphConfigurationException($"The tenant '{GraphCloud.Describe(tenant)}' (--tenant / {TenantVariable}) is not a tenant id or a domain name. Use a GUID, a domain such as contoso.onmicrosoft.com, or organizations / common / consumers.");
        }

        return tenant;
    }

    private static string? ValidateClientId(string? clientId)
    {
        if (clientId is null)
        {
            return null;
        }

        if (!Guid.TryParse(clientId, out _))
        {
            throw new GraphConfigurationException($"The client id '{GraphCloud.Describe(clientId)}' (--client-id / {ClientIdVariable}) is not a GUID.");
        }

        return clientId;
    }

    private static string? FirstNonEmpty(string? first, string? second)
    {
        if (!string.IsNullOrWhiteSpace(first))
        {
            return first.Trim();
        }

        return string.IsNullOrWhiteSpace(second) ? null : second.Trim();
    }
}
