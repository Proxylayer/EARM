using System;
using System.CommandLine;
using System.CommandLine.Help;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EArm.Cli.Hosting;
using EArm.Engine.State;

namespace EArm.Cli.Commands;

/// <summary>
/// The <c>earm state</c> command group: <c>unlock -n &lt;name&gt;</c> breaks a stale lock;
/// <c>forget -n &lt;name&gt; --resource-id &lt;id&gt;</c> drops one entry from the state without touching the tenant;
/// <c>set-provenance</c> marks an entry <c>created</c> or <c>adopted</c> (the way to fix a state file written before provenance was recorded);
/// <c>adopt</c> pins an existing object's id into the state or sets <c>uniqueName</c> on it (<see cref="AdoptCommand"/>).
/// With <c>-o json</c> every verb writes exactly one JSON document, on success and on failure (<see cref="VerbOutput"/>).
/// </summary>
internal static class StateCommand
{
    public static Command Build()
    {
        var command = new Command("state", "Maintain deployment state files: break locks, forget resources, adopt existing objects, mark provenance.");
        command.Subcommands.Add(BuildUnlock());
        command.Subcommands.Add(BuildForget());
        command.Subcommands.Add(BuildSetProvenance());
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

    private static Command BuildSetProvenance()
    {
        var options = new StateOptions();
        var resourceId = new Option<string>("--resource-id")
        {
            Description = "The ARM-style resource id of the entry (/providers/Microsoft.Entra/<type>/<name>); it must be in the deployment's state.",
            Required = true,
        };

        var provenance = new Option<string>("--provenance")
        {
            Description = "created: this deployment created the object, so Complete mode and 'deployment delete --delete-resources' may delete it. adopted: it existed before (never deleted without --delete-adopted).",
            Required = true,
        };
        provenance.AcceptOnlyFromAmong(StateProvenances.CreatedText, StateProvenances.AdoptedText);

        var command = new Command("set-provenance", "Marks one state entry as created or adopted (the fix for a state file written before provenance was recorded: its entries read as adopted).");
        options.Add(command, nameRequired: true, withYes: true);
        command.Options.Add(resourceId);
        command.Options.Add(provenance);
        command.SetAction((parseResult, ct) => RunSetProvenanceAsync(options.Read(parseResult), parseResult.GetRequiredValue(resourceId), parseResult.GetRequiredValue(provenance), parseResult.InvocationConfiguration.Output, parseResult.InvocationConfiguration.Error, ConsolePrompt.DefaultReadLine, ct));
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
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "InvalidArgument", ex.Message);
        }

        using var loggerFactory = GlobalOptions.CreateLoggerFactory(invocation.LogLevel);
        var store = StateHost.OpenStore(invocation.StateDir, invocation.TemplateFile, loggerFactory);
        try
        {
            var former = await store.ForceUnlockAsync(name, cancellationToken).ConfigureAwait(false);
            if (former is null)
            {
                return VerbOutput.Succeed(invocation.Output, stdout, $"Deployment '{name}' was not locked ({store.LockPathFor(name)}).", json =>
                {
                    json["deployment"] = name;
                    json["unlocked"] = false;
                });
            }

            return VerbOutput.Succeed(invocation.Output, stdout, $"Unlocked deployment '{name}'; the lock was held by {former.Describe()}.", json =>
            {
                json["deployment"] = name;
                json["unlocked"] = true;
                json["formerHolder"] = former.Describe();
            });
        }
        catch (StateException ex)
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "StateIoFailed", $"The lock of deployment '{name}' could not be removed: {ex.Message}");
        }
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
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "InvalidArgument", ex.Message);
        }

        using var loggerFactory = GlobalOptions.CreateLoggerFactory(invocation.LogLevel);
        var store = StateHost.OpenStore(invocation.StateDir, invocation.TemplateFile, loggerFactory);
        try
        {
            // A cheap look first, so a mistyped name does not create a lock file; the state is only trusted once it is read under the lock.
            if (!File.Exists(store.PathFor(name)))
            {
                return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "DeploymentNotFound", $"No deployment named '{name}' in {store.Directory}.");
            }

            // E-04: lock first, then read and change the state.
            IAsyncDisposable lease;
            try
            {
                lease = await store.LockAsync(name, TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
            }
            catch (StateLockedException ex)
            {
                return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.StateLocked, ex.Code, ex.Message);
            }

            await using (lease.ConfigureAwait(false))
            {
                // Lenient: an entry that fails the integrity rules (a hand-edited key, a malformed id) must still be removable - this verb is the repair the load error points to.
                StateFile? state = await store.LoadAsync(name, lenient: true, cancellationToken).ConfigureAwait(false);
                if (state is null)
                {
                    return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "DeploymentNotFound", $"No deployment named '{name}' in {store.Directory}.");
                }

                var entry = state.Find(resourceId.Trim());
                if (entry is null)
                {
                    return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "ResourceNotInState", $"Deployment '{name}' has no state entry '{resourceId}'. Use 'earm deployment show -n {name}' to list them.");
                }

                if (!invocation.Yes && !ConsolePrompt.Confirm($"Forget '{entry.Id.Id}' (Graph id {entry.GraphId})? The object stays in the tenant and will be adopted again by the next deployment.", stdout, stderr, readLine))
                {
                    return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "NotConfirmed", "Nothing forgotten.");
                }

                state.Remove(entry.Id.Id);
                await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
                return VerbOutput.Succeed(invocation.Output, stdout, $"Forgot '{entry.Id.Id}' from deployment '{name}'; {state.Resources.Count} resource(s) remain in the state.", json =>
                {
                    json["deployment"] = name;
                    json["resourceId"] = entry.Id.Id;
                    json["graphId"] = entry.GraphId;
                    json["remaining"] = state.Resources.Count;
                });
            }
        }
        catch (StateException ex)
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "StateIoFailed", $"The state of deployment '{name}' could not be read or written: {ex.Message}");
        }
    }

    /// <summary>
    /// <c>state set-provenance</c>: changes the provenance of one entry that is in the state (an unknown resource id is refused, nothing is invented). Taken under the
    /// deployment's lock, with a confirmation unless <c>-y</c>: <c>created</c> allows Complete mode to delete the object.
    /// </summary>
    internal static async Task<int> RunSetProvenanceAsync(StateInvocation invocation, string resourceId, string provenanceText, TextWriter stdout, TextWriter stderr, Func<string?> readLine, CancellationToken cancellationToken)
    {
        string name;
        try
        {
            name = invocation.RequireName();
        }
        catch (ArgumentException ex)
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "InvalidArgument", ex.Message);
        }

        string normalized = provenanceText.Trim().ToLowerInvariant();
        if (normalized is not (StateProvenances.CreatedText or StateProvenances.AdoptedText))
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "InvalidArgument", $"--provenance must be '{StateProvenances.CreatedText}' or '{StateProvenances.AdoptedText}', not '{provenanceText}'.");
        }

        var wanted = StateProvenances.Parse(normalized);
        using var loggerFactory = GlobalOptions.CreateLoggerFactory(invocation.LogLevel);
        var store = StateHost.OpenStore(invocation.StateDir, invocation.TemplateFile, loggerFactory);
        try
        {
            if (!File.Exists(store.PathFor(name)))
            {
                return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "DeploymentNotFound", $"No deployment named '{name}' in {store.Directory}.");
            }

            IAsyncDisposable lease;
            try
            {
                lease = await store.LockAsync(name, TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
            }
            catch (StateLockedException ex)
            {
                return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.StateLocked, ex.Code, ex.Message);
            }

            await using (lease.ConfigureAwait(false))
            {
                StateFile? state = await store.LoadAsync(name, cancellationToken).ConfigureAwait(false);
                if (state is null)
                {
                    return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "DeploymentNotFound", $"No deployment named '{name}' in {store.Directory}.");
                }

                var entry = state.Find(resourceId.Trim());
                if (entry is null)
                {
                    return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "ResourceNotInState", $"Deployment '{name}' has no state entry '{resourceId}'. Use 'earm deployment show -n {name}' to list them; this verb never creates an entry (pin an object with 'earm state adopt').");
                }

                if (entry.Provenance == wanted && entry.ProvenanceRecorded)
                {
                    return VerbOutput.Succeed(invocation.Output, stdout, $"'{entry.Id.Id}' is already marked {normalized} in deployment '{name}'; nothing to do.", json =>
                    {
                        json["deployment"] = name;
                        json["resourceId"] = entry.Id.Id;
                        json["provenance"] = normalized;
                        json["changed"] = false;
                    });
                }

                string consequence = wanted == StateProvenance.Created
                    ? "Complete mode and 'deployment delete --delete-resources' will delete the object (Graph id " + entry.GraphId + ") without --delete-adopted."
                    : "Complete mode and 'deployment delete --delete-resources' will keep the object (Graph id " + entry.GraphId + ") unless --delete-adopted is passed.";
                if (!invocation.Yes && !ConsolePrompt.Confirm($"Mark '{entry.Id.Id}' as {normalized}? {consequence}", stdout, stderr, readLine))
                {
                    return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "NotConfirmed", "Nothing changed.");
                }

                state.Upsert(entry with { Provenance = wanted, ProvenanceRecorded = true });
                await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
                return VerbOutput.Succeed(invocation.Output, stdout, string.Create(CultureInfo.InvariantCulture, $"Marked '{entry.Id.Id}' as {normalized} in deployment '{name}'."), json =>
                {
                    json["deployment"] = name;
                    json["resourceId"] = entry.Id.Id;
                    json["provenance"] = normalized;
                    json["previousProvenance"] = StateProvenances.ToText(entry.Provenance);
                    json["changed"] = true;
                });
            }
        }
        catch (StateException ex)
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "StateIoFailed", $"The state of deployment '{name}' could not be read or written: {ex.Message}");
        }
    }
}
