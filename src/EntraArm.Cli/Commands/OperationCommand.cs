using System;
using System.CommandLine;
using System.CommandLine.Help;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Cli.Hosting;
using EntraArm.Cli.Output;
using EntraArm.Engine.State;

namespace EntraArm.Cli.Commands;

/// <summary><c>entraarm deployment operation list -n &lt;name&gt;</c> (mirrors <c>az deployment operation group list</c>): the per-resource operations of the last run, from the state.</summary>
internal static class OperationCommand
{
    public static Command Build()
    {
        var options = new StateOptions();
        var list = new Command("list", "Lists the per-resource operations of the deployment's last run.");
        options.Add(list, nameRequired: true);
        list.SetAction((parseResult, ct) => RunListAsync(options.Read(parseResult), parseResult.InvocationConfiguration.Output, parseResult.InvocationConfiguration.Error, ct));

        var command = new Command("operation", "Inspect the operations of a deployment's last run.");
        command.Subcommands.Add(list);
        command.SetAction(parseResult => new HelpAction().Invoke(parseResult));
        return command;
    }

    internal static async Task<int> RunListAsync(StateInvocation invocation, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        string name;
        try
        {
            name = invocation.RequireName();
        }
        catch (ArgumentException ex)
        {
            stderr.WriteLine(ex.Message);
            return ExitCodes.Failure;
        }

        using var loggerFactory = GlobalOptions.CreateLoggerFactory(invocation.LogLevel);
        var store = StateHost.OpenStore(invocation.StateDir, invocation.TemplateFile, loggerFactory);
        StateFile? state;
        try
        {
            state = await store.LoadAsync(name, cancellationToken).ConfigureAwait(false);
        }
        catch (StateException ex)
        {
            stderr.WriteLine(ex.Message);
            return ExitCodes.Failure;
        }

        if (state is null)
        {
            stderr.WriteLine($"No deployment named '{name}' in {store.Directory}.");
            return ExitCodes.Failure;
        }

        StateWriter.WriteOperations(state, invocation.Output, prettyPrint: true, stdout);
        return ExitCodes.Ok;
    }
}
