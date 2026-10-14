using System;
using System.CommandLine;
using EntraArm.Az;
using Microsoft.Extensions.Logging;

namespace EntraArm.Cli;

/// <summary>The output format selected with <c>-o/--output</c>.</summary>
internal enum OutputFormat
{
    Table,
    Json,
    None,
}

/// <summary>Options that apply to every command (added to the root command as recursive options).</summary>
internal static class GlobalOptions
{
    public static Option<string> Output { get; } = CreateOutputOption();

    /// <summary><c>--pwsh-path</c>: explicit PowerShell 7 executable (else <c>ENTRAARM_PWSH_PATH</c>, then PATH).</summary>
    public static Option<string?> PwshPath { get; } = new("--pwsh-path")
    {
        Description = "Path to pwsh (PowerShell 7.4+). Default: ENTRAARM_PWSH_PATH, then pwsh on PATH.",
        Recursive = true,
    };

    /// <summary><c>--az-command</c>: explicit Azure CLI command (else <c>ENTRAARM_AZ_COMMAND</c>, then <c>Get-Command az</c> in pwsh).</summary>
    public static Option<string?> AzCommand { get; } = new("--az-command")
    {
        Description = "Azure CLI command or script to run instead of 'az' (for example a test double). Default: ENTRAARM_AZ_COMMAND, then az on PATH.",
        Recursive = true,
    };

    /// <summary><c>--temp-dir</c>: directory for request/body files (else <c>ENTRAARM_TEMP_DIR</c>, then the per-user default).</summary>
    public static Option<string?> TempDir { get; } = new("--temp-dir")
    {
        Description = "Directory for the short-lived az request and body files (owner-only). Default: ENTRAARM_TEMP_DIR, then %LOCALAPPDATA%\\EntraArm\\tmp.",
        Recursive = true,
    };

    /// <summary><c>--state-dir</c>: where deployment state files live (else <c>ENTRAARM_STATE_DIR</c>, then <c>.entraarm/state</c> next to the template).</summary>
    public static Option<string?> StateDir { get; } = new("--state-dir")
    {
        Description = "Directory of the deployment state files. Default: ENTRAARM_STATE_DIR, then .entraarm/state next to the template (or under the current directory).",
        Recursive = true,
    };

    public static Option<bool> Verbose { get; } = new("--verbose")
    {
        Description = "Debug logging to stderr: every az call (url parameters and headers redacted, bodies never logged).",
        Recursive = true,
    };

    public static Option<bool> Debug { get; } = new("--debug")
    {
        Description = "Trace logging to stderr: as --verbose plus the az argument vector and stderr (redacted).",
        Recursive = true,
    };

    public static OutputFormat GetOutputFormat(ParseResult parseResult)
    {
        string? value = parseResult.GetValue(Output);
        return value?.ToLowerInvariant() switch
        {
            "json" => OutputFormat.Json,
            "none" => OutputFormat.None,
            _ => OutputFormat.Table,
        };
    }

    /// <summary>Information by default; <c>--verbose</c> is Debug; <c>--debug</c> is Trace.</summary>
    public static LogLevel GetLogLevel(ParseResult parseResult)
    {
        if (parseResult.GetValue(Debug))
        {
            return LogLevel.Trace;
        }

        return parseResult.GetValue(Verbose) ? LogLevel.Debug : LogLevel.Information;
    }

    /// <summary>The Azure CLI transport settings from the global options (environment fallbacks are applied by <see cref="AzOptions"/>).</summary>
    public static AzOptions BuildAzOptions(ParseResult parseResult, int parallelism)
    {
        return new AzOptions
        {
            PwshPath = parseResult.GetValue(PwshPath),
            AzCommand = parseResult.GetValue(AzCommand),
            TempDirectory = parseResult.GetValue(TempDir),
            MaxParallel = Math.Max(1, parallelism),
        };
    }

    /// <summary>A console logger writing single-line entries to stderr (stdout stays reserved for command output).</summary>
    public static ILoggerFactory CreateLoggerFactory(LogLevel minimumLevel)
    {
        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(minimumLevel);
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            });
            builder.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        });
    }

    private static Option<string> CreateOutputOption()
    {
        var option = new Option<string>("--output", "-o")
        {
            Description = "Output format: table (default), json or none.",
            Recursive = true,
            DefaultValueFactory = _ => "table",
        };
        option.AcceptOnlyFromAmong("table", "json", "none");
        return option;
    }
}
