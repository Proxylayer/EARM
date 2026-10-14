using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Cli.Hosting;
using EntraArm.Cli.Output;
using EntraArm.Engine.State;

namespace EntraArm.Cli.Commands;

/// <summary><c>entraarm deployment list [-f template] [--state-dir dir] [-o table|json]</c>: every deployment that has a state file in the state directory.</summary>
internal static class ListCommand
{
    public static Command Build()
    {
        var options = new StateOptions();
        var command = new Command("list", "Lists the deployments recorded in the state directory.");
        options.Add(command, nameRequired: false);
        command.SetAction((parseResult, ct) => RunAsync(options.Read(parseResult), parseResult.InvocationConfiguration.Output, ct));
        return command;
    }

    internal static async Task<int> RunAsync(StateInvocation invocation, TextWriter stdout, CancellationToken cancellationToken)
    {
        using var loggerFactory = GlobalOptions.CreateLoggerFactory(invocation.LogLevel);
        var store = StateHost.OpenStore(invocation.StateDir, invocation.TemplateFile, loggerFactory);
        var items = new List<DeploymentListItem>();
        foreach (var name in await store.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            var lockInfo = await store.GetLockAsync(name, cancellationToken).ConfigureAwait(false);
            try
            {
                items.Add(new DeploymentListItem(name, await store.LoadAsync(name, cancellationToken).ConfigureAwait(false), lockInfo, null));
            }
            catch (StateException ex)
            {
                items.Add(new DeploymentListItem(name, null, lockInfo, ex.Message));
            }
        }

        StateWriter.WriteList(items, store.Directory, invocation.Output, prettyPrint: true, stdout);
        return ExitCodes.Ok;
    }
}
