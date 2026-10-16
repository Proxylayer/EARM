using EArm.Engine.Permissions;

namespace EArm.Cli.Hosting;

/// <summary>
/// How one online run authenticates beyond the connection settings: the delegated scopes of its verb (requested only when the run signs in as a user with
/// the stored sign-in; app-only and default-chain identities keep <c>.default</c>) and how a consent prompt is shown.
/// </summary>
/// <param name="Scopes">The run's scopes (<see cref="RunScopes"/>); null when they could not be computed or the command has none.</param>
/// <param name="DeviceCode"><c>--device-code</c>: a consent prompt uses the device-code flow instead of a browser.</param>
internal sealed record RunAuthentication(RunScopeSet? Scopes, bool DeviceCode)
{
    /// <summary>No run scopes: every identity requests <c>.default</c>, and nothing prompts.</summary>
    public static RunAuthentication None { get; } = new(null, false);
}
