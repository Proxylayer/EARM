using System;
using System.CommandLine;
using EntraArm.Az;
using EntraArm.Engine.State;
using Microsoft.Extensions.Logging;

namespace EntraArm.Cli.Commands;

/// <summary>What the state-backed verbs read from the command line.</summary>
internal sealed record StateInvocation
{
    public string? Name { get; init; }

    public string? TemplateFile { get; init; }

    public string? StateDir { get; init; }

    public required OutputFormat Output { get; init; }

    public required AzOptions Az { get; init; }

    public LogLevel LogLevel { get; init; } = LogLevel.Information;

    public bool Yes { get; init; }

    /// <summary>The deployment name, validated; throws <see cref="ArgumentException"/> when missing or unusable.</summary>
    public string RequireName()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("A deployment name is required (-n/--name).");
        }

        StateNames.Validate(Name);
        return Name;
    }
}

/// <summary>
/// The options shared by <c>deployment show / list / delete / operation list</c> and the <c>state</c> verbs:
/// <c>-n</c>, an optional <c>-f</c> (only to locate the state directory next to the template) and <c>-y</c>.
/// </summary>
internal sealed class StateOptions
{
    /// <param name="nameOption">The long name of the deployment-name option: <c>--name</c> (default) or, where <c>--name</c> means something else (<c>state adopt</c>: the uniqueName), <c>--deployment</c>.</param>
    public StateOptions(string nameOption = "--name")
    {
        Name = new Option<string?>(nameOption, "-n")
        {
            Description = "Deployment name (the state file name).",
        };
    }

    public Option<string?> Name { get; }

    public Option<string?> TemplateFile { get; } = new("--template-file", "-f")
    {
        Description = "Template whose directory holds the state (.entraarm/state next to it). Default: the current directory. --state-dir overrides both.",
    };

    public Option<bool> Yes { get; } = new("--yes", "-y")
    {
        Description = "Do not prompt for confirmation.",
    };

    public void Add(Command command, bool nameRequired, bool withYes = false)
    {
        ArgumentNullException.ThrowIfNull(command);
        Name.Required = nameRequired;
        command.Options.Add(Name);
        command.Options.Add(TemplateFile);
        if (withYes)
        {
            command.Options.Add(Yes);
        }
    }

    public StateInvocation Read(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        return new StateInvocation
        {
            Name = parseResult.GetValue(Name),
            TemplateFile = parseResult.GetValue(TemplateFile),
            StateDir = parseResult.GetValue(GlobalOptions.StateDir),
            Output = GlobalOptions.GetOutputFormat(parseResult),
            Az = GlobalOptions.BuildAzOptions(parseResult, 4),
            LogLevel = GlobalOptions.GetLogLevel(parseResult),
            Yes = parseResult.GetValue(Yes),
        };
    }
}
