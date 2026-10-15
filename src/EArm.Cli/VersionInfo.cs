using System.Reflection;

namespace EArm.Cli;

/// <summary>
/// Version of the <c>earm</c> CLI assembly, as reported by <c>earm --version</c>.
/// </summary>
internal static class VersionInfo
{
    /// <summary>
    /// The <see cref="AssemblyInformationalVersionAttribute"/> value of the CLI assembly,
    /// falling back to the assembly version when the attribute is absent.
    /// </summary>
    public static string InformationalVersion { get; } = ResolveInformationalVersion();

    private static string ResolveInformationalVersion()
    {
        Assembly assembly = typeof(VersionInfo).Assembly;

        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational;
        }

        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}
