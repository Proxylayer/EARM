using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using EntraArm.Az;
using EntraArm.Engine.Planning;
using EntraArm.Engine.Providers;
using EntraArm.Engine.WhatIf;
using Microsoft.Extensions.Logging;

namespace EntraArm.Cli.Commands;

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

    public bool ExitCodeOnChanges { get; init; }

    public bool NoPrettyPrint { get; init; }

    // create
    public bool WhatIf { get; init; }

    public bool ConfirmWithWhatIf { get; init; }

    public bool Yes { get; init; }

    public bool ContinueOnError { get; init; }

    public bool RevealSecureOutputs { get; init; }

    public string? SecureOutputsFile { get; init; }

    /// <summary><c>--force-unlock</c>: break an existing state lock before acquiring it.</summary>
    public bool ForceUnlock { get; init; }

    /// <summary><c>--lock-timeout</c>: seconds to wait for the state lock before giving up (exit 4).</summary>
    public int LockTimeoutSeconds { get; init; } = 10;

    /// <summary><c>--state-dir</c> (global); <c>null</c> resolves to <c>ENTRAARM_STATE_DIR</c> or <c>.entraarm/state</c> next to the template.</summary>
    public string? StateDir { get; init; }

    public required OutputFormat Output { get; init; }

    public required AzOptions Az { get; init; }

    public LogLevel LogLevel { get; init; } = LogLevel.Information;

    /// <summary><c>-n</c>, or the template file stem without <c>.entra</c> (<c>orders-api.entra.json</c> → <c>orders-api</c>).</summary>
    public string EffectiveName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name))
            {
                return Name;
            }

            string stem = Path.GetFileNameWithoutExtension(TemplateFile);
            return stem.EndsWith(".entra", StringComparison.OrdinalIgnoreCase) ? stem[..^".entra".Length] : stem;
        }
    }
}

/// <summary>
/// The options shared by <c>deployment create</c> and <c>deployment what-if</c> (mirroring <c>az deployment group</c>),
/// plus the verb-specific ones. One instance per command build; <see cref="Read"/> turns a parse result into a
/// <see cref="DeploymentInvocation"/>.
/// </summary>
internal sealed class DeploymentOptions
{
    public Option<string> TemplateFile { get; } = new("--template-file", "-f")
    {
        Description = "Path to the template (*.entra.json).",
        Required = true,
    };

    public Option<string[]> Parameters { get; } = new("--parameters", "-p")
    {
        Description = "Parameter values: @file.json (ARM deploymentParameters shape) or name=value. Repeatable; later values win.",
        AllowMultipleArgumentsPerToken = true,
        DefaultValueFactory = _ => [],
    };

    public Option<string?> Name { get; } = new("--name", "-n")
    {
        Description = "Deployment name (state file name). Default: the template file stem.",
    };

    public Option<string> Mode { get; } = CreateChoice("--mode", "Incremental: create/update declared resources. Complete: additionally delete state-managed resources missing from the template (WI-06).", "Incremental", "Incremental", "Complete");

    public Option<int> Parallelism { get; } = new("--parallelism")
    {
        Description = "Maximum number of resources read/applied concurrently inside a dependency batch.",
        DefaultValueFactory = _ => 4,
    };

    public Option<bool> AllowCaEnforce { get; } = new("--allow-ca-enforce")
    {
        Description = "Allow Conditional Access policies with state 'enabled' (default: report-only or disabled only).",
    };

    public Option<string> OnSoftDeletedConflict { get; } = CreateChoice("--on-soft-deleted-conflict", "What to do when the key of a resource to create is held by a soft-deleted object.", "error", "error", "restore");

    // what-if
    public Option<string> ResultFormat { get; } = CreateChoice("--result-format", "What-if detail: FullResourcePayloads (default) or ResourceIdOnly.", "FullResourcePayloads", "FullResourcePayloads", "ResourceIdOnly");

    public Option<bool> ExitCodeOnChanges { get; } = new("--exit-code-on-changes")
    {
        Description = "Exit with code 5 when the what-if reports any Create, Modify or Delete.",
    };

    public Option<bool> NoPrettyPrint { get; } = new("--no-pretty-print")
    {
        Description = "Compact JSON output (-o json).",
    };

    // create
    public Option<bool> WhatIf { get; } = new("--what-if")
    {
        Description = "Show what the deployment would do and exit without applying anything.",
    };

    public Option<bool> ConfirmWithWhatIf { get; } = new("--confirm-with-what-if", "-c")
    {
        Description = "Show the what-if first and ask for confirmation before applying (use -y to skip the prompt).",
    };

    public Option<bool> Yes { get; } = new("--yes", "-y")
    {
        Description = "Do not prompt for confirmation.",
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

    public Option<bool> ForceUnlock { get; } = new("--force-unlock")
    {
        Description = "Break the deployment's state lock before starting (when a previous run died without releasing it).",
    };

    public Option<int> LockTimeout { get; } = new("--lock-timeout")
    {
        Description = "Seconds to wait for the deployment's state lock held by another run before giving up (exit code 4).",
        DefaultValueFactory = _ => 10,
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
    }

    public void AddWhatIf(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Options.Add(ResultFormat);
        command.Options.Add(ExitCodeOnChanges);
        command.Options.Add(NoPrettyPrint);
    }

    public void AddCreate(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Options.Add(WhatIf);
        command.Options.Add(ConfirmWithWhatIf);
        command.Options.Add(Yes);
        command.Options.Add(ContinueOnError);
        command.Options.Add(RevealSecureOutputs);
        command.Options.Add(SecureOutputsFile);
        command.Options.Add(ForceUnlock);
        command.Options.Add(LockTimeout);
        command.Options.Add(ResultFormat);
        command.Options.Add(ExitCodeOnChanges);
        command.Options.Add(NoPrettyPrint);
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
            ResultFormat = Enum.Parse<ResultFormat>(parseResult.GetValue(ResultFormat) ?? "FullResourcePayloads", ignoreCase: true),
            ExitCodeOnChanges = parseResult.GetValue(ExitCodeOnChanges),
            NoPrettyPrint = parseResult.GetValue(NoPrettyPrint),
            WhatIf = parseResult.GetValue(WhatIf),
            ConfirmWithWhatIf = parseResult.GetValue(ConfirmWithWhatIf),
            Yes = parseResult.GetValue(Yes),
            ContinueOnError = parseResult.GetValue(ContinueOnError),
            RevealSecureOutputs = parseResult.GetValue(RevealSecureOutputs),
            SecureOutputsFile = parseResult.GetValue(SecureOutputsFile),
            ForceUnlock = parseResult.GetValue(ForceUnlock),
            LockTimeoutSeconds = Math.Max(0, parseResult.GetValue(LockTimeout)),
            StateDir = parseResult.GetValue(GlobalOptions.StateDir),
            Output = GlobalOptions.GetOutputFormat(parseResult),
            Az = GlobalOptions.BuildAzOptions(parseResult, parallelism),
            LogLevel = GlobalOptions.GetLogLevel(parseResult),
        };
    }

    private static Option<string> CreateChoice(string name, string description, string defaultValue, params string[] choices)
    {
        var option = new Option<string>(name)
        {
            Description = description,
            DefaultValueFactory = _ => defaultValue,
        };
        option.AcceptOnlyFromAmong(choices);
        return option;
    }
}
