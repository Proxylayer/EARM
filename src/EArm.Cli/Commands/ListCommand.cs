using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EArm.Cli.Hosting;
using EArm.Cli.Output;
using EArm.Engine.State;

namespace EArm.Cli.Commands;

/// <summary><c>earm deployment list [-n name] [-f template] [--state-dir dir] [-o table|json]</c>: every deployment that has a state file in the state directory, or only the one named <c>-n</c> (case-insensitive exact match).</summary>
internal static class ListCommand
{
    public static Command Build()
    {
        var options = new StateOptions();
        var command = new Command("list", "Lists the deployments recorded in the state directory.");
        options.Add(command, nameRequired: false);
        command.SetAction((parseResult, ct) => RunAsync(options.Read(parseResult), parseResult.InvocationConfiguration.Output, parseResult.InvocationConfiguration.Error, ct));
        return command;
    }

    internal static async Task<int> RunAsync(StateInvocation invocation, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        using var loggerFactory = GlobalOptions.CreateLoggerFactory(invocation.LogLevel);
        var store = StateHost.OpenStore(invocation.StateDir, invocation.TemplateFile, loggerFactory);
        try
        {
            var items = new List<DeploymentListItem>();
            string? only = string.IsNullOrWhiteSpace(invocation.Name) ? null : invocation.Name.Trim();
            foreach (var name in await store.ListAsync(cancellationToken).ConfigureAwait(false))
            {
                if (only is not null && !name.Equals(only, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var lockInfo = await store.GetLockAsync(name, cancellationToken).ConfigureAwait(false);
                try
                {
                    items.Add(new DeploymentListItem(name, await store.LoadAsync(name, cancellationToken).ConfigureAwait(false), lockInfo, null));
                }
                catch (StateException ex)
                {
                    items.Add(new DeploymentListItem(name, null, lockInfo, ex.Message));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    items.Add(new DeploymentListItem(name, null, lockInfo, ex.Message));
                }
            }

            StateWriter.WriteList(items, store.Directory, invocation.Output, prettyPrint: true, stdout);
            if (only is not null && items.Count == 0)
            {
                // stderr, so a JSON listing on stdout stays a clean empty list; an unknown name is not a failure.
                stderr.WriteLine($"No deployment named '{only}' in the state directory {store.Directory}.");
            }

            return ExitCodes.Ok;
        }
        catch (StateException ex)
        {
            // A --state-dir that is a file or a link must not read as "No deployments.".
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "StateIoFailed", $"The state directory {store.Directory} could not be read: {ex.Message}");
        }
    }
}
