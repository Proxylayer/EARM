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

/// <summary>
/// Options that apply to every command (added to the root command as recursive options). <see cref="CreateAll"/> builds a fresh set for each
/// root command, and the accessors below find the options of the parsed command line by name: a <see cref="Symbol"/> records every command
/// it is added to, so one static instance shared by every root command would be mutated by each command line built (and two built at once,
/// as parallel tests do, would race on it).
/// </summary>
internal static class GlobalOptions
{
    public const string OutputName = "--output";
    public const string QueryName = "--query";
    public const string AuthName = "--auth";
    public const string TenantName = "--tenant";
    public const string ClientIdName = "--client-id";
    public const string VerboseName = "--verbose";
    public const string DebugName = "--debug";
    public const string EvaluationTimeoutName = "--evaluation-timeout";

    /// <summary>A new instance of every global option, in the order the help lists them.</summary>
    public static Option[] CreateAll()
    {
        return
        [
            CreateOutputOption(),
            CreateQueryOption(),
            CreateAuthOption(),
            CreateTenantOption(),
            CreateClientIdOption(),
            CreateVerboseOption(),
            CreateDebugOption(),
            CreateEvaluationTimeoutOption(),
        ];
    }

    /// <summary><c>--tenant</c>: the tenant to sign in to (else <c>EARM_TENANT_ID</c>).</summary>
    public static Option<string?> CreateTenantOption()
    {
        return new Option<string?>(TenantName)
        {
            Description = "Tenant id or domain to sign in to. Default: EARM_TENANT_ID, then whatever the credential selects.",
            Recursive = true,
        };
    }

    /// <summary><c>--client-id</c>: the public-client app registration of the interactive sign-in (else <c>EARM_CLIENT_ID</c>).</summary>
    public static Option<string?> CreateClientIdOption()
    {
        return new Option<string?>(ClientIdName)
        {
            Description = "App registration (client id) used by the interactive sign-in 'earm auth login'. Default: EARM_CLIENT_ID, then the client of the stored sign-in, then Microsoft Graph Command Line Tools (none in the china cloud).",
            Recursive = true,
        };
    }

    public static Option<bool> CreateVerboseOption()
    {
        return new Option<bool>(VerboseName)
        {
            Description = "Debug logging to stderr: every Graph call (method, path, status, timing, request id; query secrets redacted, tokens and bodies never logged).",
            Recursive = true,
        };
    }

    public static Option<bool> CreateDebugOption()
    {
        return new Option<bool>(DebugName)
        {
            Description = "Trace logging to stderr: as --verbose plus token acquisition and retry details (tokens are never logged). Also prints the full exception of an unexpected error; EARM_DEBUG=1 does the same.",
            Recursive = true,
        };
    }

    /// <summary>The environment variable that turns on <c>--debug</c> output (stack traces of unexpected errors): <c>EARM_DEBUG=1</c>.</summary>
    public const string DebugVariable = "EARM_DEBUG";

    /// <summary>True when <c>--debug</c> is given or <c>EARM_DEBUG</c> is <c>1</c>/<c>true</c>.</summary>
    public static bool IsDebug(ParseResult parseResult)
    {
        return Value<bool>(parseResult, DebugName) || IsDebugEnvironment();
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
        string? text = Value<string?>(parseResult, EvaluationTimeoutName);
        return text is not null && EvaluationLimits.TryParseTimeout(text, out var timeout) ? new EvaluationLimits { Timeout = timeout } : EvaluationLimits.FromEnvironment();
    }

    /// <summary>
    /// The format the verbs write: <see cref="OutputFormat.Json"/> for every document format, and for <c>--query</c> without an explicit <c>-o</c> (EARM's
    /// default is <c>table</c>, and a query needs a document).
    /// </summary>
    public static OutputFormat GetOutputFormat(ParseResult parseResult)
    {
        string? value = Value<string>(parseResult, OutputName);
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
        string? text = Value<string?>(parseResult, QueryName);
        JmesPathQuery? query = null;
        if (!string.IsNullOrWhiteSpace(text) && JmesPathQuery.TryCreate(text, out var compiled, out _))
        {
            query = compiled;
        }

        DocumentFormat? format = Value<string>(parseResult, OutputName)?.ToLowerInvariant() switch
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
        return HasQuery(parseResult) && IsOutputExplicit(parseResult) && string.Equals(Value<string>(parseResult, OutputName), "table", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasQuery(ParseResult parseResult)
    {
        return !string.IsNullOrWhiteSpace(Value<string?>(parseResult, QueryName));
    }

    private static bool IsOutputExplicit(ParseResult parseResult)
    {
        return Find<string>(parseResult, OutputName) is { } output && parseResult.GetResult(output) is { Implicit: false };
    }

    /// <summary>Information by default; <c>--verbose</c> is Debug; <c>--debug</c> (or <c>EARM_DEBUG=1</c>) is Trace.</summary>
    public static LogLevel GetLogLevel(ParseResult parseResult)
    {
        if (IsDebug(parseResult))
        {
            return LogLevel.Trace;
        }

        return Value<bool>(parseResult, VerboseName) ? LogLevel.Debug : LogLevel.Information;
    }

    /// <summary>The connection settings from the global options (environment fallbacks are applied when the services are built).</summary>
    public static ConnectionSettings BuildConnection(ParseResult parseResult, int parallelism)
    {
        return new ConnectionSettings
        {
            Auth = Value<string?>(parseResult, AuthName),
            Tenant = Value<string?>(parseResult, TenantName),
            ClientId = Value<string?>(parseResult, ClientIdName),
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

    /// <summary>The global option <paramref name="name"/> of the parsed command line (a recursive option of its root command), if it has one.</summary>
    private static Option<T>? Find<T>(ParseResult parseResult, string name)
    {
        foreach (var option in parseResult.RootCommandResult.Command.Options)
        {
            if (option is Option<T> typed && typed.Name == name)
            {
                return typed;
            }
        }

        return null;
    }

    /// <summary>The value of the global option <paramref name="name"/>; the type default when the command line has no such option.</summary>
    private static T? Value<T>(ParseResult parseResult, string name)
    {
        return Find<T>(parseResult, name) is { } option ? parseResult.GetValue(option) : default;
    }

    private static Option<string?> CreateEvaluationTimeoutOption()
    {
        // Read as text and parsed by EvaluationLimits.TryParseTimeout: a typed double option throws from GetValue on "abc" and parses "NaN".
        var option = new Option<string?>(EvaluationTimeoutName)
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
        var option = new Option<string?>(AuthName)
        {
            Description = "How to obtain tokens: auto (default: a cached 'earm auth login' sign-in when there is one, else the default chain), default (environment variables, workload identity, managed identity) or interactive. Default: EARM_AUTH.",
            Recursive = true,
        };
        option.AcceptOnlyFromAmongIgnoreCase("auto", "default", "interactive");
        return option;
    }

    private static Option<string> CreateOutputOption()
    {
        var option = new Option<string>(OutputName, "-o")
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
        var option = new Option<string?>(QueryName)
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
