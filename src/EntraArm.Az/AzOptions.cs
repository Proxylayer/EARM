using System;
using System.Collections.Generic;
using System.IO;

namespace EntraArm.Az;

/// <summary>
/// Settings for the PowerShell-hosted Azure CLI transport. CLI flags populate the properties; the
/// <c>ENTRAARM_*</c> environment variables are consulted when a property is left null.
/// </summary>
public sealed class AzOptions
{
    public const string PwshPathVariable = "ENTRAARM_PWSH_PATH";
    public const string AzCommandVariable = "ENTRAARM_AZ_COMMAND";
    public const string TempDirectoryVariable = "ENTRAARM_TEMP_DIR";

    /// <summary>Explicit path to <c>pwsh</c> (<c>--pwsh-path</c>). Null: <c>ENTRAARM_PWSH_PATH</c>, then <c>pwsh</c> on PATH.</summary>
    public string? PwshPath { get; set; }

    /// <summary>
    /// Explicit Azure CLI command (<c>--az-command</c>): a path to <c>az</c>, <c>az.cmd</c>, or a <c>.ps1</c> test double.
    /// Null: <c>ENTRAARM_AZ_COMMAND</c>, then <c>Get-Command az</c> inside pwsh.
    /// </summary>
    public string? AzCommand { get; set; }

    /// <summary>Directory for request, body and output files. Null: <c>ENTRAARM_TEMP_DIR</c>, then <c>%LOCALAPPDATA%\EntraArm\tmp</c>.</summary>
    public string? TempDirectory { get; set; }

    /// <summary>Per-call timeout after which the pwsh process tree is killed. Default 120 s.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>Maximum number of concurrent az invocations. Default 4.</summary>
    public int MaxParallel { get; set; } = 4;

    /// <summary>
    /// Extra environment variables for the pwsh child process (in addition to the inherited environment and the
    /// <c>AZURE_CORE_*</c> variables the runner always sets). Tests use this to steer the fake az.
    /// </summary>
    public Dictionary<string, string> AdditionalEnvironment { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The configured pwsh path, or null when pwsh must be located on PATH.</summary>
    public string? ResolvePwshPathSetting()
    {
        return FirstNonEmpty(PwshPath, Environment.GetEnvironmentVariable(PwshPathVariable));
    }

    /// <summary>The configured az command, or null when pwsh resolves <c>az</c> itself.</summary>
    public string? ResolveAzCommandSetting()
    {
        return FirstNonEmpty(AzCommand, Environment.GetEnvironmentVariable(AzCommandVariable));
    }

    /// <summary>The temp directory to use (configured, environment, or the per-user default).</summary>
    public string ResolveTempDirectory()
    {
        return FirstNonEmpty(TempDirectory, Environment.GetEnvironmentVariable(TempDirectoryVariable)) ?? DefaultTempDirectory();
    }

    /// <summary><c>%LOCALAPPDATA%\EntraArm\tmp</c> on Windows, <c>$XDG_DATA_HOME/EntraArm/tmp</c> (usually <c>~/.local/share</c>) elsewhere.</summary>
    public static string DefaultTempDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrEmpty(localAppData))
        {
            localAppData = Path.Combine(Path.GetTempPath(), "EntraArm-" + Environment.UserName);
        }

        return Path.Combine(localAppData, "EntraArm", "tmp");
    }

    private static string? FirstNonEmpty(string? first, string? second)
    {
        if (!string.IsNullOrWhiteSpace(first))
        {
            return first;
        }

        return string.IsNullOrWhiteSpace(second) ? null : second;
    }
}
