using System;
using System.CommandLine;
using EArm.Cli.Hosting;
using EArm.Cli.Output;
using EArm.Expressions.Evaluation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace EArm.Cli;

/// <summary>
/// The output format the verbs write, selected with <c>-o/--output</c>. Every document format (<c>json</c>, <c>jsonc</c>, <c>yaml</c>, <c>yamlc</c>,
/// <c>tsv</c>) is <see cref="Json"/> here: the verbs write their one JSON document and <see cref="StructuredOutput"/> renders it.
/// </summary>
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

    /// <summary><c>--query</c>: a JMESPath expression applied to the JSON document of the verb (as the Azure CLI's <c>--query</c>).</summary>
    public static Option<string?> Query { get; } = CreateQueryOption();

    /// <summary><c>--auth</c>: how tokens are obtained (else <c>EARM_AUTH</c>, then auto).</summary>
    public static Option<string?> Auth { get; } = CreateAuthOption();

    /// <summary><c>--tenant</c>: the tenant to sign in to (else <c>EARM_TENANT_ID</c>).</summary>
    public static Option<string?> Tenant { get; } = new("--tenant")
    {
        Description = "Tenant id or domain to sign in to. Default: EARM_TENANT_ID, then whatever the credential selects.",
        Recursive = true,
    };

    /// <summary><c>--client-id</c>: the public-client app registration of the interactive sign-in (else <c>EARM_CLIENT_ID</c>).</summary>
    public static Option<string?> ClientId { get; } = new("--client-id")
    {
        Description = "App registration (client id) used by the interactive sign-in 'earm auth login'. Default: EARM_CLIENT_ID, then the client of the stored sign-in, then Microsoft Graph Command Line Tools (none in the china cloud).",
        Recursive = true,
    };

    public static Option<bool> Verbose { get; } = new("--verbose")
    {
        Description = "Debug logging to stderr: every Graph call (method, path, status, timing, request id; query secrets redacted, tokens and bodies never logged).",
        Recursive = true,
    };

    public static Option<bool> Debug { get; } = new("--debug")
    {
        Description = "Trace logging to stderr: as --verbose plus token acquisition and retry details (tokens are never logged). Also prints the full exception of an unexpected error; EARM_DEBUG=1 does the same.",
        Recursive = true,
    };

    /// <summary>
    /// <c>--evaluation-timeout</c>: the wall-clock budget of template evaluation, in seconds (else <c>EARM_EVALUATION_TIMEOUT</c>, then 60;
    /// <c>0</c> disables the time limit, the step limit still applies).
    /// </summary>
    public static Option<string?> EvaluationTimeout { get; } = CreateEvaluationTimeoutOption();

    /// <summary>The environment variable that turns on <c>--debug</c> output (stack traces of unexpected errors): <c>EARM_DEBUG=1</c>.</summary>
    public const string DebugVariable = "EARM_DEBUG";

    /// <summary>True when <c>--debug</c> is given or <c>EARM_DEBUG</c> is <c>1</c>/<c>true</c>.</summary>
    public static bool IsDebug(ParseResult parseResult)
    {
        return parseResult.GetValue(Debug) || IsDebugEnvironment();
    }

    public static bool IsDebugEnvironment()
    {
        string? value = Environment.GetEnvironmentVariable(DebugVariable);
        return value is not null && (value.Trim() == "1" || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The evaluation limits of the run: <c>--evaluation-timeout</c>, else the environment, else the defaults.</summary>
    public static EvaluationLimits GetEvaluationLimits(ParseResult parseResult)
    {
        // The text is validated while parsing; a value that did not pass leaves the run on the defaults (the usage error is reported anyway).
        string? text = parseResult.GetValue(EvaluationTimeout);
        return text is not null && EvaluationLimits.TryParseTimeout(text, out var timeout) ? new EvaluationLimits { Timeout = timeout } : EvaluationLimits.FromEnvironment();
    }

    /// <summary>
    /// The format the verbs write: <see cref="OutputFormat.Json"/> for every document format, and for <c>--query</c> without an explicit <c>-o</c> (EARM's
    /// default is <c>table</c>, and a query needs a document).
    /// </summary>
    public static OutputFormat GetOutputFormat(ParseResult parseResult)
    {
        string? value = parseResult.GetValue(Output);
        return value?.ToLowerInvariant() switch
        {
            "json" or "jsonc" or "yaml" or "yamlc" or "tsv" => OutputFormat.Json,
            "none" => OutputFormat.None,
            _ when HasQuery(parseResult) && !IsOutputExplicit(parseResult) => OutputFormat.Json,
            _ => OutputFormat.Table,
        };
    }

    /// <summary>
    /// The rendering of the verb's JSON document that <c>-o</c> and <c>--query</c> ask for; <c>null</c> when the verb's output goes out as it is
    /// (<c>table</c>, <c>none</c>, or plain <c>json</c> without a query).
    /// </summary>
    public static StructuredOutput? GetStructuredOutput(ParseResult parseResult)
    {
        string? text = parseResult.GetValue(Query);
        JmesPathQuery? query = null;
        if (!string.IsNullOrWhiteSpace(text) && JmesPathQuery.TryCreate(text, out var compiled, out _))
        {
            query = compiled;
        }

        DocumentFormat? format = parseResult.GetValue(Output)?.ToLowerInvariant() switch
        {
            "json" => DocumentFormat.Json,
            "jsonc" => DocumentFormat.Jsonc,
            "yaml" => DocumentFormat.Yaml,
            "yamlc" => DocumentFormat.Yamlc,
            "tsv" => DocumentFormat.Tsv,
            "none" => null,
            _ => query is not null && !IsOutputExplicit(parseResult) ? DocumentFormat.Json : null,
        };

        if (format is null || (format == DocumentFormat.Json && query is null))
        {
            return null;
        }

        return new StructuredOutput(format.Value, query);
    }

    /// <summary>True when <c>--query</c> is combined with an explicit <c>-o table</c>: the table is text, so there is nothing to query (a usage error).</summary>
    public static bool IsQueryOnTable(ParseResult parseResult)
    {
        return HasQuery(parseResult) && IsOutputExplicit(parseResult) && string.Equals(parseResult.GetValue(Output), "table", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasQuery(ParseResult parseResult)
    {
        return !string.IsNullOrWhiteSpace(parseResult.GetValue(Query));
    }

    private static bool IsOutputExplicit(ParseResult parseResult)
    {
        return parseResult.GetResult(Output) is { Implicit: false };
    }

    /// <summary>Information by default; <c>--verbose</c> is Debug; <c>--debug</c> (or <c>EARM_DEBUG=1</c>) is Trace.</summary>
    public static LogLevel GetLogLevel(ParseResult parseResult)
    {
        if (IsDebug(parseResult))
        {
            return LogLevel.Trace;
        }

        return parseResult.GetValue(Verbose) ? LogLevel.Debug : LogLevel.Information;
    }

    /// <summary>The connection settings from the global options (environment fallbacks are applied when the services are built).</summary>
    public static ConnectionSettings BuildConnection(ParseResult parseResult, int parallelism)
    {
        return new ConnectionSettings
        {
            Auth = parseResult.GetValue(Auth),
            Tenant = parseResult.GetValue(Tenant),
            ClientId = parseResult.GetValue(ClientId),
            Parallelism = Math.Max(1, parallelism),
        };
    }
    /// <summary>A console logger writing single-line entries to stderr (stdout stays reserved for command output).</summary>
    public static ILoggerFactory CreateLoggerFactory(LogLevel minimumLevel)
    {
        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(minimumLevel);
            builder.AddConsoleFormatter<SanitizingConsoleFormatter, ConsoleFormatterOptions>();
            builder.AddConsole(options =>
            {
                options.FormatterName = SanitizingConsoleFormatter.FormatterName;
                options.LogToStandardErrorThreshold = LogLevel.Trace;
            });
        });
    }

    private static Option<string?> CreateEvaluationTimeoutOption()
    {
        // Read as text and parsed by EvaluationLimits.TryParseTimeout: a typed double option throws from GetValue on "abc" and parses "NaN".
        var option = new Option<string?>("--evaluation-timeout")
        {
            Description = "Seconds one evaluation (a variable, the properties of a resource, an output) may take before the run fails with EvaluationBudgetExceeded; 0 disables the time limit. Default: EARM_EVALUATION_TIMEOUT, then 60.",
            Recursive = true,
        };
        option.Validators.Add(result =>
        {
            string? value = result.GetValueOrDefault<string?>();
            if (value is not null && !EvaluationLimits.TryParseTimeout(value, out _))
            {
                result.AddError($"--evaluation-timeout must be a number of seconds between 0 and {EvaluationLimits.MaxTimeoutSeconds}.");
            }
        });
        return option;
    }

    private static Option<string?> CreateAuthOption()
    {
        var option = new Option<string?>("--auth")
        {
            Description = "How to obtain tokens: auto (default: a cached 'earm auth login' sign-in when there is one, else the default chain), default (environment variables, workload identity, managed identity) or interactive. Default: EARM_AUTH.",
            Recursive = true,
        };
        option.AcceptOnlyFromAmongIgnoreCase("auto", "default", "interactive");
        return option;
    }

    private static Option<string> CreateOutputOption()
    {
        var option = new Option<string>("--output", "-o")
        {
            Description = "Output format: table (default), json, jsonc (coloured JSON), yaml, yamlc (coloured YAML), tsv or none.",
            Recursive = true,
            DefaultValueFactory = _ => "table",
        };
        option.AcceptOnlyFromAmongIgnoreCase("table", "json", "jsonc", "yaml", "yamlc", "tsv", "none");
        return option;
    }

    private static Option<string?> CreateQueryOption()
    {
        var option = new Option<string?>("--query")
        {
            Description = "JMESPath query (https://jmespath.org) applied to the JSON document of the command. Without -o it selects -o json.",
            Recursive = true,
        };
        option.Validators.Add(result =>
        {
            string? value = result.GetValueOrDefault<string?>();
            if (value is not null && !JmesPathQuery.TryCreate(value, out _, out string? error))
            {
                result.AddError(error!);
            }
        });
        return option;
    }
}
