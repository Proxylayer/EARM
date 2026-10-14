using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace EntraArm.Az;

/// <summary>A usable PowerShell 7 installation.</summary>
public sealed record PwshInfo(string Path, Version Version);

/// <summary>
/// Finds <c>pwsh</c> (<c>--pwsh-path</c> / <c>ENTRAARM_PWSH_PATH</c>, then PATH, then well-known install
/// locations) and verifies once per process that it is PowerShell 7.4 or newer by running
/// <c>pwsh -NoProfile -NonInteractive -NoLogo -Command $PSVersionTable.PSVersion.ToString()</c>.
/// </summary>
public sealed class PwshLocator
{
    public static readonly Version MinimumVersion = new(7, 4);

    private const string InstallHint = "Install PowerShell 7.4+ (winget install Microsoft.PowerShell, https://aka.ms/powershell) or pass --pwsh-path <path to pwsh>.";
    private static readonly TimeSpan VersionProbeTimeout = TimeSpan.FromSeconds(30);

    private readonly AzOptions _options;
    private readonly ILogger<PwshLocator> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PwshInfo? _cached;

    public PwshLocator(AzOptions options, ILogger<PwshLocator> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Locates and version-checks pwsh; the result is cached for the lifetime of this instance.</summary>
    public async Task<PwshInfo> LocateAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is null)
            {
                _cached = await LocateCoreAsync(cancellationToken).ConfigureAwait(false);
            }

            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PwshInfo> LocateCoreAsync(CancellationToken cancellationToken)
    {
        var path = ResolvePath();
        var version = await QueryVersionAsync(path, cancellationToken).ConfigureAwait(false);
        if (version < MinimumVersion)
        {
            throw new ToolchainException("pwsh", $"PowerShell {version} at '{path}' is too old; EntraARM requires PowerShell {MinimumVersion}+. {InstallHint}");
        }

        AzLog.PwshLocated(_logger, path, version);
        return new PwshInfo(path, version);
    }

    private string ResolvePath()
    {
        var configured = _options.ResolvePwshPathSetting();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured))
            {
                return Path.GetFullPath(configured);
            }

            if (!configured.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !configured.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                var onPath = FindOnPath(configured);
                if (onPath is not null)
                {
                    return onPath;
                }
            }

            throw new ToolchainException("pwsh", $"PowerShell was not found at the configured path '{configured}' (--pwsh-path / {AzOptions.PwshPathVariable}). {InstallHint}");
        }

        return FindOnPath("pwsh")
            ?? FindWellKnown()
            ?? throw new ToolchainException("pwsh", $"PowerShell 7 ('pwsh') was not found on PATH. {InstallHint}");
    }

    /// <summary>Searches PATH for an executable. On Windows only <c>.exe</c> is accepted (EntraARM never runs .cmd/.bat launchers).</summary>
    internal static string? FindOnPath(string name)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVariable))
        {
            return null;
        }

        var candidates = CandidateFileNames(name);
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var candidate in candidates)
            {
                string full;
                try
                {
                    full = Path.GetFullPath(Path.Combine(directory, candidate));
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (File.Exists(full))
                {
                    return full;
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<string> CandidateFileNames(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [name];
        }

        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? [name] : [name + ".exe", name];
    }

    private static string? FindWellKnown()
    {
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[]
                     {
                         Environment.GetEnvironmentVariable("ProgramFiles"),
                         Environment.GetEnvironmentVariable("ProgramW6432"),
                         Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                     })
            {
                if (!string.IsNullOrEmpty(root))
                {
                    candidates.Add(Path.Combine(root, "PowerShell", "7", "pwsh.exe"));
                    candidates.Add(Path.Combine(root, "PowerShell", "7-preview", "pwsh.exe"));
                }
            }

            var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrEmpty(localAppData))
            {
                candidates.Add(Path.Combine(localAppData, "Microsoft", "WindowsApps", "pwsh.exe"));
            }
        }
        else
        {
            candidates.Add("/usr/bin/pwsh");
            candidates.Add("/usr/local/bin/pwsh");
            candidates.Add("/opt/microsoft/powershell/7/pwsh");
            candidates.Add("/usr/local/microsoft/powershell/7/pwsh");
            candidates.Add("/snap/bin/pwsh");
            candidates.Add("/opt/homebrew/bin/pwsh");
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task<Version> QueryVersionAsync(string pwshPath, CancellationToken cancellationToken)
    {
        var startInfo = PwshProcess.CreateStartInfo(pwshPath);
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("$PSVersionTable.PSVersion.ToString()");

        var outcome = await PwshProcess.RunAsync(startInfo, VersionProbeTimeout, cancellationToken).ConfigureAwait(false);
        if (outcome.TimedOut)
        {
            throw new ToolchainException("pwsh", $"PowerShell at '{pwshPath}' did not report its version within {VersionProbeTimeout.TotalSeconds:F0} s. {InstallHint}");
        }

        if (outcome.ExitCode != 0 || !TryParseVersion(outcome.StandardOutput, out var version))
        {
            var detail = string.IsNullOrWhiteSpace(outcome.StandardError) ? outcome.StandardOutput.Trim() : outcome.StandardError.Trim();
            throw new ToolchainException("pwsh", $"PowerShell at '{pwshPath}' did not report a usable version (exit {outcome.ExitCode}): {detail}. {InstallHint}");
        }

        return version;
    }

    /// <summary>Parses the first line of <c>$PSVersionTable.PSVersion.ToString()</c> output, e.g. <c>7.6.6</c> or <c>7.5.0-preview.3</c>.</summary>
    internal static bool TryParseVersion(string text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var line = text.Trim();
        var newline = line.IndexOfAny(['\r', '\n']);
        if (newline >= 0)
        {
            line = line[..newline].Trim();
        }

        var suffix = line.IndexOfAny(['-', '+', ' ']);
        if (suffix >= 0)
        {
            line = line[..suffix];
        }

        if (Version.TryParse(line, out var parsed))
        {
            version = parsed;
            return true;
        }

        return false;
    }
}
