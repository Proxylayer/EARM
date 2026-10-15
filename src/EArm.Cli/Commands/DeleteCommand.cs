using System;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Cli.Hosting;
using EArm.Engine.State;

namespace EArm.Cli.Commands;

/// <summary>
/// <c>earm deployment delete -n &lt;name&gt; [--delete-resources] [--delete-adopted] [-y] [--force-unlock]</c> (mirrors
/// <c>az deployment group delete</c> plus stack semantics): removes the deployment's state file; with
/// <c>--delete-resources</c> first deletes every object the deployment created, in reverse dependency order, through the Azure
/// CLI session (exit 3 on a tenant mismatch, 4 when locked, 1 when a delete fails: the remaining entries stay). Objects the
/// deployment only adopted stay unless <c>--delete-adopted</c> is passed. The lock is taken BEFORE the state is read.
/// </summary>
internal static class DeleteCommand
{
    public static Command Build()
    {
        var options = new StateOptions();
        var deleteResources = new Option<bool>("--delete-resources")
        {
            Description = "Also delete every object the deployment created from the tenant (reverse dependency order) before removing the state. Adopted objects stay unless --delete-adopted.",
        };

        var deleteAdopted = new Option<bool>("--delete-adopted")
        {
            Description = "With --delete-resources: also delete the objects the deployment adopted (they existed before it managed them).",
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
        command.Options.Add(deleteAdopted);
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
            ct,
            parseResult.GetValue(deleteAdopted)));
        return command;
    }

    internal static async Task<int> RunAsync(StateInvocation invocation, bool deleteResources, bool forceUnlock, bool continueOnError, TextWriter stdout, TextWriter stderr, Func<string?> readLine, CancellationToken cancellationToken, bool deleteAdopted = false)
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

        using var scrubScope = SecretScrubber.Begin();
        using var loggerFactory = GlobalOptions.CreateLoggerFactory(invocation.LogLevel);
        var store = StateHost.OpenStore(invocation.StateDir, invocation.TemplateFile, loggerFactory);
        try
        {
            // A cheap look first, so a mistyped name does not create a lock file; the state is only trusted once it is read under the lock.
            if (!File.Exists(store.PathFor(name)))
            {
                return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "DeploymentNotFound", $"No deployment named '{name}' in {store.Directory}.");
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
                return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.StateLocked, ex.Code, ex.Message);
            }

            await using (lease.ConfigureAwait(false))
            {
                // E-04: the state is read while the lock is held.
                // Removing only the state file acts on no object: an entry that fails the integrity rules must not keep it from being removed (deleting the objects reads the state strictly).
                StateFile? state = await store.LoadAsync(name, lenient: !deleteResources, cancellationToken).ConfigureAwait(false);
                if (state is null)
                {
                    return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "DeploymentNotFound", $"No deployment named '{name}' in {store.Directory}.");
                }

                if (!deleteResources)
                {
                    string question = string.Create(CultureInfo.InvariantCulture, $"Remove the state of deployment '{name}' ({state.Resources.Count} resource(s))? The objects stay in the tenant.");
                    if (!invocation.Yes && !ConsolePrompt.Confirm(question, stdout, stderr, readLine))
                    {
                        return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "NotConfirmed", "Nothing deleted.");
                    }

                    int left = state.Resources.Count;
                    await store.DeleteAsync(name, cancellationToken).ConfigureAwait(false);
                    return VerbOutput.Succeed(invocation.Output, stdout, $"Removed the state of deployment '{name}' ({store.PathFor(name)}); {left} object(s) left in the tenant.", json =>
                    {
                        json["deployment"] = name;
                        json["stateRemoved"] = true;
                        json["objectsLeftInTenant"] = left;
                    });
                }

                if (!EngineServices.TryCreate(invocation.Connection, loggerFactory, stdout, stderr, invocation.Output, out var created, out int createExit))
                {
                    return createExit;
                }

                using var services = created!;
                GraphPreflightResult preflight;
                try
                {
                    preflight = await services.Preflight.RunAsync(cancellationToken).ConfigureAwait(false);
                    state.EnsureTenant(preflight.TenantId);
                }
                catch (Exception ex) when (ex is GraphTransportException or GraphException or TenantMismatchException)
                {
                    return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.AuthOrPreflight, ex is TenantMismatchException mismatch ? mismatch.Code : "PreflightFailed", SecretScrubber.Scrub(ex.Message));
                }

                int adoptedCount = state.Resources.Values.Count(e => e.Provenance == StateProvenance.Adopted);
                int toDelete = deleteAdopted ? state.Resources.Count : state.Resources.Count - adoptedCount;
                string confirm = string.Create(CultureInfo.InvariantCulture, $"Delete the {toDelete} object(s) managed by deployment '{name}' from tenant {preflight.TenantId} and remove its state?");
                if (!deleteAdopted && adoptedCount > 0)
                {
                    confirm = string.Create(CultureInfo.InvariantCulture, $"Delete the {toDelete} object(s) that deployment '{name}' created in tenant {preflight.TenantId}? The {adoptedCount} adopted object(s) stay (pass --delete-adopted to delete them too), so the state stays as well.");
                }

                if (!invocation.Yes && !ConsolePrompt.Confirm(confirm, stdout, stderr, readLine))
                {
                    return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "NotConfirmed", "Nothing deleted.");
                }

                var guard = new DeleteGuard
                {
                    SignedInPrincipalId = preflight.PrincipalObjectId,
                    TenantId = preflight.TenantId,
                    DeleteAdopted = deleteAdopted,
                };
                var result = await DeploymentDeleter.DeleteAllAsync(state, store, services.Providers, services.Graph, null, continueOnError, guard, loggerFactory.CreateLogger("EArm.Cli.Delete"), cancellationToken).ConfigureAwait(false);
                bool json = invocation.Output == OutputFormat.Json;
                if (!json && invocation.Output != OutputFormat.None)
                {
                    foreach (var outcome in result.Outcomes)
                    {
                        string line = outcome.Status switch
                        {
                            DeleteStatus.Deleted => $"  - {outcome.Entry.Id.Id} (deleted)",
                            DeleteStatus.AlreadyGone => $"  - {outcome.Entry.Id.Id} (already gone)",
                            DeleteStatus.Skipped => $"  ? {outcome.Entry.Id.Id} (skipped: {outcome.Error})",
                            DeleteStatus.Refused => $"  ~ {outcome.Entry.Id.Id} (kept: {outcome.Error})",
                            _ => $"  ! {outcome.Entry.Id.Id} ({outcome.Error})",
                        };
                        (outcome.Succeeded ? stdout : stderr).WriteLine(line);
                    }
                }

                string summary;
                int exit;
                if (result.Succeeded && result.StateRemoved)
                {
                    summary = string.Create(CultureInfo.InvariantCulture, $"Deleted {result.DeletedCount} object(s) ({result.AlreadyGoneCount} already gone) and removed the state of deployment '{name}'.");
                    exit = ExitCodes.Ok;
                }
                else if (result.Succeeded)
                {
                    summary = string.Create(CultureInfo.InvariantCulture, $"Deleted {result.DeletedCount} object(s) ({result.AlreadyGoneCount} already gone). {result.RefusedCount} object(s) were kept (adopted, or owned by another resource) and stay in the state of deployment '{name}'; pass --delete-adopted to delete adopted objects.");
                    exit = ExitCodes.Ok;
                }
                else
                {
                    summary = string.Create(CultureInfo.InvariantCulture, $"Deployment '{name}': {result.FailedCount} delete(s) failed, {result.SkippedCount} skipped; {state.Resources.Count} entry(ies) remain in the state.");
                    exit = ExitCodes.Failure;
                }

                if (json)
                {
                    // ONE document: the per-object outcomes and the totals, on success and on failure (review round 2, S2-20).
                    var outcomes = new JsonArray();
                    foreach (var outcome in result.Outcomes)
                    {
                        outcomes.Add(new JsonObject
                        {
                            ["resourceId"] = outcome.Entry.Id.Id,
                            ["type"] = outcome.Entry.Type,
                            ["graphId"] = outcome.Entry.GraphId,
                            ["status"] = outcome.Status.ToString(),
                            ["error"] = outcome.Error is null ? null : SecretScrubber.Scrub(outcome.Error),
                            ["errorCode"] = outcome.ErrorCode,
                        });
                    }

                    var document = new JsonObject
                    {
                        ["status"] = result.Succeeded ? "Succeeded" : "Failed",
                        ["deployment"] = name,
                        ["message"] = summary,
                        ["deleted"] = result.DeletedCount,
                        ["alreadyGone"] = result.AlreadyGoneCount,
                        ["kept"] = result.RefusedCount,
                        ["failed"] = result.FailedCount,
                        ["skipped"] = result.SkippedCount,
                        ["stateRemoved"] = result.StateRemoved,
                        ["remainingEntries"] = state.Resources.Count,
                        ["outcomes"] = outcomes,
                    };
                    stdout.WriteLine(document.ToJsonString(EArm.Cli.Output.JsonOutput.Options(true)));
                }
                else if (invocation.Output != OutputFormat.None)
                {
                    (exit == ExitCodes.Ok ? stdout : stderr).WriteLine(summary);
                }

                return exit;
            }
        }
        catch (StateException ex)
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "StateIoFailed", $"The state of deployment '{name}' could not be read or written: {SecretScrubber.Scrub(ex.Message)}");
        }
    }
}
