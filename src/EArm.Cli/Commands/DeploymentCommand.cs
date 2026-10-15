using System.CommandLine;
using System.CommandLine.Help;

namespace EArm.Cli.Commands;

/// <summary>
/// The <c>earm deployment</c> command group (mirrors <c>az deployment group</c>): <c>validate</c> (offline),
/// <c>what-if</c> and <c>create</c> (online, through Microsoft Graph with the signed-in identity), and the state-backed <c>show</c>,
/// <c>list</c>, <c>delete</c> and <c>operation list</c>.
/// </summary>
internal static class DeploymentCommand
{
    public static Command Build()
    {
        var command = new Command("deployment", "Validate, preview, deploy and inspect EARM deployments; mirrors 'az deployment group'.");
        command.Subcommands.Add(ValidateCommand.Build());
        command.Subcommands.Add(WhatIfCommand.Build());
        command.Subcommands.Add(CreateCommand.Build());
        command.Subcommands.Add(ShowCommand.Build());
        command.Subcommands.Add(ListCommand.Build());
        command.Subcommands.Add(DeleteCommand.Build());
        command.Subcommands.Add(OperationCommand.Build());
        command.SetAction(parseResult => new HelpAction().Invoke(parseResult));
        return command;
    }
}
