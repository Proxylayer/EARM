using System;
using EArm.Graph;
using EArm.Graph.Auth;

namespace EArm.Cli.Hosting;

/// <summary>
/// How a command reaches Microsoft Graph: the raw <c>--auth</c>, <c>--tenant</c>, <c>--client-id</c> values (the <c>EARM_*</c> variables are the
/// fallbacks, applied by <see cref="GraphAuthOptions.Resolve"/>), the parallelism, and an optional test seam.
/// </summary>
internal sealed record ConnectionSettings
{
    /// <summary><c>--auth</c>: auto, default or interactive; null falls back to <c>EARM_AUTH</c>.</summary>
    public string? Auth { get; init; }

    /// <summary><c>--tenant</c>; null falls back to <c>EARM_TENANT_ID</c>.</summary>
    public string? Tenant { get; init; }

    /// <summary><c>--client-id</c>; null falls back to <c>EARM_CLIENT_ID</c>.</summary>
    public string? ClientId { get; init; }

    /// <summary>Maximum number of concurrent HTTP calls.</summary>
    public int Parallelism { get; init; } = 4;

    /// <summary>Replaces the real HTTP handler, token provider, delays and sign-in store in in-process tests. Never set by the command line.</summary>
    public ServiceOverrides? Overrides { get; init; }

    /// <summary>The cloud (<c>EARM_CLOUD</c>) and the authentication options.</summary>
    /// <exception cref="ArgumentException">An unknown cloud, mode, tenant or client id.</exception>
    public (GraphCloud Cloud, GraphAuthOptions Auth) Resolve()
    {
        var overrides = Overrides ?? ServiceOverrides.Ambient;
        var cloud = overrides?.Cloud ?? GraphCloud.FromEnvironment();
        return (cloud, GraphAuthOptions.Resolve(Auth, Tenant, ClientId, cloud));
    }
}
