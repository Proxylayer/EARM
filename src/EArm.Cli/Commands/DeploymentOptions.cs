using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using EArm.Cli.Hosting;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Engine.Timing;
using EArm.Engine.WhatIf;
using Microsoft.Extensions.Logging;

namespace EArm.Cli.Commands;

/// <summary>Everything the online verbs need, read from the parsed command line.</summary>
internal sealed record DeploymentInvocation
{
    public required string TemplateFile { get; init; }

    public IReadOnlyList<string> ParameterTokens { get; init; } = [];

    public string? Name { get; init; }

    public DeploymentMode Mode { get; init; } = DeploymentMode.Incremental;

    public int Parallelism { get; init; } = 4;

    public bool AllowCaEnforce { get; init; }

    public SoftDeletedConflict OnSoftDeletedConflict { get; init; } = SoftDeletedConflict.Error;

    // what-if
    public ResultFormat ResultFormat { get; init; } = ResultFormat.FullResourcePayloads;

    /// <summary><c>--exclude-change-types</c> / <c>--what-if-exclude-change-types</c>: change types the what-if does not show (never what is deployed).</summary>
    public IReadOnlySet<WhatIfChangeType> ExcludeChangeTypes { get; init; } = new HashSet<WhatIfChangeType>();

    /// <summary><c>--no-prompt</c>: never ask for the value of a parameter that has none (the run then fails with <c>MissingParameterValue</c>).</summary>
    public bool NoPrompt { get; init; }

    public bool ExitCodeOnChanges { get; init; }

    public bool NoPrettyPrint { get; init; }

    // create
    public bool WhatIf { get; init; }

    public bool ConfirmWithWhatIf { get; init; }

    /// <summary><c>--proceed-if-no-change</c>: with <c>-c</c>, deploy without asking when the what-if shows no change.</summary>
    public bool ProceedIfNoChange { get; init; }

    public bool Yes { get; init; }

    public bool ContinueOnError { get; init; }

    public bool RevealSecureOutputs { get; init; }

    public string? SecureOutputsFile { get; init; }

    public required OutputFormat Output { get; init; }

    public required ConnectionSettings Connection { get; init; }

    public LogLevel LogLevel { get; init; } = LogLevel.Information;

    /// <summary><c>--device-code</c>: a consent prompt the run needs uses the device-code flow instead of a browser.</summary>
    public bool DeviceCode { get; init; }

    /// <summary><c>--timings</c>: print the phase timings table on stderr when the run ends.</summary>
    public bool ShowTimings { get; init; }

    /// <summary>The phase timings of this run (log event 2090 per phase; the <c>--timings</c> table).</summary>
    public RunTimings Timings { get; init; } = new();

    /// <summary><c>-n</c>, or the template file stem without <c>.entra</c> (<c>orders-api.entra.json</c> → <c>orders-api</c>).</summary>
    public string EffectiveName
    {
        get
        {
            return !string.IsNullOrWhiteSpace(Name) ? Name : DefaultName(TemplateFile);
        }
    }

    /// <summary>The deployment name a run of <paramref name="templateFile"/> has without <c>-n</c>: the file stem without <c>.entra</c>.</summary>
    public static string DefaultName(string templateFile)
    {
        string stem = Path.GetFileNameWithoutExtension(templateFile);
        return stem.EndsWith(".entra", StringComparison.OrdinalIgnoreCase) ? stem[..^".entra".Length] : stem;
    }
}

/// <summary>
/// The options shared by <c>deployment create</c> and <c>deployment what-if</c> (mirroring <c>az deployment group</c>),
/// plus the verb-specific ones. One instance per command build; <see cref="Read"/> turns a parse result into a
/// <see cref="DeploymentInvocation"/>.
/// </summary>
internal sealed class DeploymentOptions
{
    // The what-if result format and exclusion options of the command built (what-if and create name them differently, as the Azure CLI does).
    private Option<string>? _resultFormat;
    private Option<string[]>? _exclude;

    public Option<string> TemplateFile { get; } = new("--template-file", "-f")
    {
        Description = "Path to the template (*.entra.json).",
        Required = true,
    };

    public Option<string[]> Parameters { get; } = ParameterArguments.CreateOption();

    public Option<string?> Name { get; } = new("--name", "-n")
    {
        Description = "Deployment name (a label shown in the output). Default: the template file stem.",
    };

    public Option<string> Mode { get; } = CreateChoice("--mode", "Incremental: create/update declared resources. Complete: additionally report (never delete) the live objects of the declared types that the template does not declare.", "Incremental", "Incremental", "Complete");

    public Option<int> Parallelism { get; } = CreateBounded("--parallelism", "Maximum number of resources read/applied concurrently inside a dependency batch.", 4, minimum: 1);

    public Option<bool> AllowCaEnforce { get; } = new("--allow-ca-enforce")
    {
        Description = "Apply a Conditional Access policy's declared state 'enabled'. Without it the enforcement is held back with a warning: a new policy is created report-only, an existing policy keeps its live state.",
    };

    public Option<string> OnSoftDeletedConflict { get; } = CreateChoice("--on-soft-deleted-conflict", "What to do when the key of a resource to create is held by a soft-deleted object.", "error", "error", "restore");

    public Option<bool> DeviceCode { get; } = new("--device-code")
    {
        Description = "When the signed-in user must consent to a delegated scope this run needs, prompt with the device-code flow instead of a browser (interactive terminals only).",
    };

    public Option<bool> NoPrompt { get; } = CreateNoPrompt();

    public Option<bool> Timings { get; } = CreateTimings();

    // what-if
    public Option<string> ResultFormat { get; } = CreateResultFormat("--result-format", "-r");

    public Option<string[]> ExcludeChangeTypes { get; } = CreateExcludeChangeTypes("--exclude-change-types", ["-x"], "Space-separated change types the what-if does not show (Create, Delete, Deploy, Ignore, Modify, NoChange, Unsupported); they are still deployed.");

    public Option<bool> ExitCodeOnChanges { get; } = new("--exit-code-on-changes")
    {
        Description = "Exit with code 5 when the what-if reports any Create, Modify or Delete (create: when the successful deployment applied one).",
    };

    public Option<bool> NoPrettyPrint { get; } = new("--no-pretty-print")
    {
        Description = "Compact JSON output (-o json).",
    };

    // create
    public Option<bool> WhatIf { get; } = new("--what-if", "-w")
    {
        Description = "Show what the deployment would do and exit without applying anything.",
    };

    public Option<string> WhatIfResultFormat { get; } = CreateResultFormat("--what-if-result-format", "-r", "--result-format");

    public Option<string[]> WhatIfExcludeChangeTypes { get; } = CreateExcludeChangeTypes("--what-if-exclude-change-types", ["-x", "--exclude-change-types"], "Space-separated change types the what-if of --what-if / -c does not show (Create, Delete, Deploy, Ignore, Modify, NoChange, Unsupported); they are still deployed.");

    public Option<bool> ProceedIfNoChange { get; } = new("--proceed-if-no-change")
    {
        Description = "With --confirm-with-what-if: deploy without asking when the what-if shows no change (only NoChange and Ignore rows, after --what-if-exclude-change-types).",
    };

    public Option<bool> ConfirmWithWhatIf { get; } = new("--confirm-with-what-if", "-c")
    {
        Description = "Show the what-if first and ask for confirmation before applying (use -y to skip the prompt).",
    };

    public Option<bool> Yes { get; } = new("--yes", "-y")
    {
        Description = "Skip the confirmations: the one asked by --confirm-with-what-if, and the one asked before objects are replaced (deleted and created again because an immutable property changed). Without -y, a non-interactive run that would replace objects fails before any write.",
    };

    public Option<bool> ContinueOnError { get; } = new("--continue-on-error")
    {
        Description = "Keep deploying resources that do not depend on a failed one (default: stop at the batch boundary).",
    };

    public Option<bool> RevealSecureOutputs { get; } = new("--reveal-secure-outputs")
    {
        Description = "Print securestring/secureObject outputs in clear text instead of ***.",
    };

    public Option<string?> SecureOutputsFile { get; } = new("--secure-outputs-file")
    {
        Description = "Write the secure outputs (in clear text) to this file with owner-only permissions.",
    };

    public void AddCommon(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Options.Add(TemplateFile);
        command.Options.Add(Parameters);
        command.Options.Add(Name);
        command.Options.Add(Mode);
        command.Options.Add(Parallelism);
        command.Options.Add(AllowCaEnforce);
        command.Options.Add(OnSoftDeletedConflict);
        command.Options.Add(DeviceCode);
        command.Options.Add(NoPrompt);
        command.Options.Add(Timings);
    }

    public void AddWhatIf(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Options.Add(ResultFormat);
        command.Options.Add(ExcludeChangeTypes);
        command.Options.Add(ExitCodeOnChanges);
        command.Options.Add(NoPrettyPrint);
        _resultFormat = ResultFormat;
        _exclude = ExcludeChangeTypes;
    }

    public void AddCreate(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Options.Add(WhatIf);
        command.Options.Add(ConfirmWithWhatIf);
        command.Options.Add(ProceedIfNoChange);
        command.Options.Add(Yes);
        command.Options.Add(ContinueOnError);
        command.Options.Add(RevealSecureOutputs);
        command.Options.Add(SecureOutputsFile);
        command.Options.Add(WhatIfResultFormat);
        command.Options.Add(WhatIfExcludeChangeTypes);
        command.Options.Add(ExitCodeOnChanges);
        command.Options.Add(NoPrettyPrint);
        _resultFormat = WhatIfResultFormat;
        _exclude = WhatIfExcludeChangeTypes;
    }

    public DeploymentInvocation Read(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        int parallelism = Math.Max(1, parseResult.GetValue(Parallelism));
        return new DeploymentInvocation
        {
            TemplateFile = parseResult.GetRequiredValue(TemplateFile),
            ParameterTokens = parseResult.GetValue(Parameters) ?? [],
            Name = parseResult.GetValue(Name),
            Mode = Enum.Parse<DeploymentMode>(parseResult.GetValue(Mode) ?? "Incremental", ignoreCase: true),
            Parallelism = parallelism,
            AllowCaEnforce = parseResult.GetValue(AllowCaEnforce),
            OnSoftDeletedConflict = Enum.Parse<SoftDeletedConflict>(parseResult.GetValue(OnSoftDeletedConflict) ?? "error", ignoreCase: true),
            ResultFormat = Enum.Parse<ResultFormat>((_resultFormat is null ? null : parseResult.GetValue(_resultFormat)) ?? "FullResourcePayloads", ignoreCase: true),
            ExcludeChangeTypes = ParseChangeTypes(_exclude is null ? null : parseResult.GetValue(_exclude)),
            NoPrompt = parseResult.GetValue(NoPrompt),
            ProceedIfNoChange = parseResult.GetResult(ProceedIfNoChange) is not null && parseResult.GetValue(ProceedIfNoChange),
            ExitCodeOnChanges = parseResult.GetValue(ExitCodeOnChanges),
            NoPrettyPrint = parseResult.GetValue(NoPrettyPrint),
            WhatIf = parseResult.GetValue(WhatIf),
            ConfirmWithWhatIf = parseResult.GetValue(ConfirmWithWhatIf),
            Yes = parseResult.GetValue(Yes),
            ContinueOnError = parseResult.GetValue(ContinueOnError),
            RevealSecureOutputs = parseResult.GetValue(RevealSecureOutputs),
            SecureOutputsFile = parseResult.GetValue(SecureOutputsFile),
            Output = GlobalOptions.GetOutputFormat(parseResult),
            Connection = GlobalOptions.BuildConnection(parseResult, parallelism),
            LogLevel = GlobalOptions.GetLogLevel(parseResult),
            DeviceCode = parseResult.GetValue(DeviceCode),
            ShowTimings = parseResult.GetValue(Timings),
        };
    }

    /// <summary>
    /// <c>--no-prompt</c> (as the Azure CLI: a flag, or <c>--no-prompt true|false</c>): never ask on the console for the value of a parameter that has none.
    /// </summary>
    public static Option<bool> CreateNoPrompt()
    {
        return new Option<bool>("--no-prompt")
        {
            Description = "Do not ask for the value of a parameter that has none (an interactive run asks otherwise); the run then fails with MissingParameterValue. Accepts true or false.",
            Arity = ArgumentArity.ZeroOrOne,
        };
    }

    /// <summary><c>--timings</c>: a table of the run's phases (milliseconds, Graph requests) on stderr at the end, whatever the log level.</summary>
    public static Option<bool> CreateTimings()
    {
        return new Option<bool>("--timings")
        {
            Description = "When the run ends, print how long each phase took and how many Graph requests it made (on stderr, whatever the log level and output format).",
        };
    }

    private static Option<string> CreateResultFormat(string name, params string[] aliases)
    {
        var option = new Option<string>(name, aliases)
        {
            Description = "What-if detail: FullResourcePayloads (default) or ResourceIdOnly (resource ids only; an existing declared resource is reported as Deploy).",
            DefaultValueFactory = _ => "FullResourcePayloads",
        };
        option.AcceptOnlyFromAmongIgnoreCase("FullResourcePayloads", "ResourceIdOnly");
        return option;
    }

    private static Option<string[]> CreateExcludeChangeTypes(string name, string[] aliases, string description)
    {
        var option = new Option<string[]>(name, aliases)
        {
            Description = description,
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.OneOrMore,
            DefaultValueFactory = _ => [],
        };
        option.AcceptOnlyFromAmongIgnoreCase(Enum.GetNames<WhatIfChangeType>());
        return option;
    }

    private static HashSet<WhatIfChangeType> ParseChangeTypes(string[]? values)
    {
        var set = new HashSet<WhatIfChangeType>();
        foreach (var value in values ?? [])
        {
            if (WhatIfView.TryParseChangeType(value, out var type))
            {
                set.Add(type);
            }
        }

        return set;
    }

    /// <summary>An integer option with a lower bound: a value below it is a usage error (a negative <c>--parallelism</c> used to be clamped silently).</summary>
    private static Option<int> CreateBounded(string name, string description, int defaultValue, int minimum)
    {
        var option = new Option<int>(name)
        {
            Description = description,
            DefaultValueFactory = _ => defaultValue,
        };
        option.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int>() < minimum)
            {
                result.AddError(minimum == 0 ? $"{name} must be zero or more." : $"{name} must be at least {minimum}.");
            }
        });
        return option;
    }

    private static Option<string> CreateChoice(string name, string description, string defaultValue, params string[] choices)
    {
        var option = new Option<string>(name)
        {
            Description = description,
            DefaultValueFactory = _ => defaultValue,
        };
        option.AcceptOnlyFromAmongIgnoreCase(choices);
        return option;
    }
}
