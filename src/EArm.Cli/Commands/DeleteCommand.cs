using System;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EArm.Az;
using EArm.Cli.Hosting;
using EArm.Engine.State;

namespace EArm.Cli.Commands;

/// <summary>
/// <c>earm deployment delete -n &lt;name&gt; [--delete-resources] [-y] [--force-unlock]</c> (mirrors
/// <c>az deployment group delete</c> plus stack semantics): removes the deployment's state file; with
/// <c>--delete-resources</c> first deletes every state-managed object in reverse dependency order through the Azure
/// CLI session (exit 3 on a tenant mismatch, 4 when locked, 1 when a delete fails: the remaining entries stay).
/// </summary>
internal static class DeleteCommand
{
    public static Command Build()
    {
        var options = new StateOptions();
        var deleteResources = new Option<bool>("--delete-resources")
        {
            Description = "Also delete every object the deployment manages from the tenant (reverse dependency order) before removing the state.",
        };

        var forceUnlock = new Option<bool>("--force-unlock")
        {
            Description = "Break the deployment's state lock first.",
        };

        var continueOnError = new Option<bool>("--continue-on-error")
        {
            Description = "Keep deleting after a failure (default: stop; the failed object and the ones not yet deleted stay in the state).",
        };

        var command = new Command("delete", "Deletes a deployment: its state file, and with --delete-resources the objects it manages.");
        options.Add(command, nameRequired: true, withYes: true);
        command.Options.Add(deleteResources);
        command.Options.Add(forceUnlock);
        command.Options.Add(continueOnError);
        command.SetAction((parseResult, ct) => RunAsync(
            options.Read(parseResult),
            parseResult.GetValue(deleteResources),
            parseResult.GetValue(forceUnlock),
            parseResult.GetValue(continueOnError),
            parseResult.InvocationConfiguration.Output,
            parseResult.InvocationConfiguration.Error,
            ConsolePrompt.DefaultReadLine,
            ct));
        return command;
    }

    internal static async Task<int> RunAsync(StateInvocation invocation, bool deleteResources, bool forceUnlock, bool continueOnError, TextWriter stdout, TextWriter stderr, Func<string?> readLine, CancellationToken cancellationToken)
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

        if (forceUnlock)
        {
            var former = await store.ForceUnlockAsync(name, cancellationToken).ConfigureAwait(false);
            if (former is not null)
            {
                stderr.WriteLine($"Broke the state lock of deployment '{name}' held by {former.Describe()}.");
            }
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
            if (!deleteResources)
            {
                string question = string.Create(CultureInfo.InvariantCulture, $"Remove the state of deployment '{name}' ({state.Resources.Count} resource(s))? The objects stay in the tenant.");
                if (!invocation.Yes && !ConsolePrompt.Confirm(question, stdout, stderr, readLine))
                {
                    stderr.WriteLine("Nothing deleted.");
                    return ExitCodes.Failure;
                }

                await store.DeleteAsync(name, cancellationToken).ConfigureAwait(false);
                stdout.WriteLine($"Removed the state of deployment '{name}' ({store.PathFor(name)}); {state.Resources.Count} object(s) left in the tenant.");
                return ExitCodes.Ok;
            }

            using var services = new EngineServices(invocation.Az, loggerFactory);
            AzPreflightResult preflight;
            try
            {
                preflight = await services.Preflight.RunAsync(cancellationToken).ConfigureAwait(false);
                state.EnsureTenant(preflight.TenantId);
            }
            catch (Exception ex) when (ex is AzCliException or ToolchainException or TenantMismatchException)
            {
                stderr.WriteLine(ex.Message);
                return ExitCodes.AuthOrPreflight;
            }

            string confirm = string.Create(CultureInfo.InvariantCulture, $"Delete the {state.Resources.Count} object(s) managed by deployment '{name}' from tenant {preflight.TenantId} and remove its state?");
            if (!invocation.Yes && !ConsolePrompt.Confirm(confirm, stdout, stderr, readLine))
            {
                stderr.WriteLine("Nothing deleted.");
                return ExitCodes.Failure;
            }

            var result = await DeploymentDeleter.DeleteAllAsync(state, store, services.Providers, services.Graph, null, continueOnError, loggerFactory.CreateLogger("EArm.Cli.Delete"), cancellationToken).ConfigureAwait(false);
            foreach (var outcome in result.Outcomes)
            {
                string line = outcome.Status switch
                {
                    DeleteStatus.Deleted => $"  - {outcome.Entry.Id.Id} (deleted)",
                    DeleteStatus.AlreadyGone => $"  - {outcome.Entry.Id.Id} (already gone)",
                    DeleteStatus.Skipped => $"  ? {outcome.Entry.Id.Id} (skipped: {outcome.Error})",
                    _ => $"  ! {outcome.Entry.Id.Id} ({outcome.Error})",
                };
                (outcome.Succeeded ? stdout : stderr).WriteLine(line);
            }

            if (result.Succeeded)
            {
                stdout.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Deleted {result.DeletedCount} object(s) ({result.AlreadyGoneCount} already gone) and removed the state of deployment '{name}'."));
                return ExitCodes.Ok;
            }

            stderr.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Deployment '{name}': {result.FailedCount} delete(s) failed, {result.SkippedCount} skipped; {state.Resources.Count} entry(ies) remain in the state."));
            return ExitCodes.Failure;
        }
    }
}
