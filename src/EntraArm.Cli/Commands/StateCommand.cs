using System;
using System.CommandLine;
using System.CommandLine.Help;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Cli.Hosting;
using EntraArm.Engine.State;

namespace EntraArm.Cli.Commands;

/// <summary>
/// The <c>entraarm state</c> command group: <c>unlock -n &lt;name&gt;</c> breaks a stale lock;
/// <c>forget -n &lt;name&gt; --resource-id &lt;id&gt;</c> drops one entry from the state without touching the tenant;
/// <c>adopt</c> pins an existing object's id into the state or sets <c>uniqueName</c> on it (<see cref="AdoptCommand"/>).
/// </summary>
internal static class StateCommand
{
    public static Command Build()
    {
        var command = new Command("state", "Maintain deployment state files: break locks, forget resources, adopt existing objects.");
        command.Subcommands.Add(BuildUnlock());
        command.Subcommands.Add(BuildForget());
        command.Subcommands.Add(AdoptCommand.Build());
        command.SetAction(parseResult => new HelpAction().Invoke(parseResult));
        return command;
    }

    private static Command BuildUnlock()
    {
        var options = new StateOptions();
        var command = new Command("unlock", "Breaks the state lock of a deployment left behind by a run that died.");
        options.Add(command, nameRequired: true);
        command.SetAction((parseResult, ct) => RunUnlockAsync(options.Read(parseResult), parseResult.InvocationConfiguration.Output, parseResult.InvocationConfiguration.Error, ct));
        return command;
    }

    private static Command BuildForget()
    {
        var options = new StateOptions();
        var resourceId = new Option<string>("--resource-id")
        {
            Description = "The ARM-style resource id to drop from the state (/providers/Microsoft.Entra/<type>/<name>). The object stays in the tenant.",
            Required = true,
        };

        var command = new Command("forget", "Removes one resource from a deployment's state without touching the tenant.");
        options.Add(command, nameRequired: true, withYes: true);
        command.Options.Add(resourceId);
        command.SetAction((parseResult, ct) => RunForgetAsync(options.Read(parseResult), parseResult.GetRequiredValue(resourceId), parseResult.InvocationConfiguration.Output, parseResult.InvocationConfiguration.Error, ConsolePrompt.DefaultReadLine, ct));
        return command;
    }

    internal static async Task<int> RunUnlockAsync(StateInvocation invocation, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
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
        var former = await store.ForceUnlockAsync(name, cancellationToken).ConfigureAwait(false);
        if (former is null)
        {
            stdout.WriteLine($"Deployment '{name}' was not locked ({store.LockPathFor(name)}).");
        }
        else
        {
            stdout.WriteLine($"Unlocked deployment '{name}'; the lock was held by {former.Describe()}.");
        }

        return ExitCodes.Ok;
    }

    internal static async Task<int> RunForgetAsync(StateInvocation invocation, string resourceId, TextWriter stdout, TextWriter stderr, Func<string?> readLine, CancellationToken cancellationToken)
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

        var entry = state.Find(resourceId.Trim());
        if (entry is null)
        {
            stderr.WriteLine($"Deployment '{name}' has no state entry '{resourceId}'. Use 'entraarm deployment show -n {name}' to list them.");
            return ExitCodes.Failure;
        }

        IAsyncDisposable lease;
        try
        {
            lease = await store.LockAsync(name, TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        }
        catch (StateLockedException ex)
        {
            stderr.WriteLine(ex.Message);
            return ExitCodes.StateLocked;
        }

        await using (lease.ConfigureAwait(false))
        {
            if (!invocation.Yes && !ConsolePrompt.Confirm($"Forget '{entry.Id.Id}' (Graph id {entry.GraphId})? The object stays in the tenant and will be adopted again by the next deployment.", stdout, stderr, readLine))
            {
                stderr.WriteLine("Nothing forgotten.");
                return ExitCodes.Failure;
            }

            state.Remove(entry.Id.Id);
            await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            stdout.WriteLine($"Forgot '{entry.Id.Id}' from deployment '{name}'; {state.Resources.Count} resource(s) remain in the state.");
            return ExitCodes.Ok;
        }
    }
}
