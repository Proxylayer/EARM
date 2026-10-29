using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Cli.Hosting;
using EArm.Cli.Output;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Permissions;
using EArm.Engine.Planning;
using EArm.Graph;

namespace EArm.Cli.Commands;

/// <summary>
/// <c>earm deployment delete -f &lt;template&gt; [-p ...] [-n name] [-y] [--continue-on-error]</c>: deletes the objects the template declares, found by
/// their live keys exactly like <c>what-if</c> finds them (nothing is recorded locally), in reverse dependency order, after listing them and asking for
/// confirmation (<c>-y</c> skips it; a non-interactive console without <c>-y</c> deletes nothing). The signed-in principal and service principals owned by
/// another organization are never deleted (they are listed as kept). Exit 0 when every listed object was deleted (or kept); 1 when a delete failed (the
/// rest stops unless <c>--continue-on-error</c>), when it was not confirmed, or for a template that cannot be loaded; 2 on validation errors; 3 when no
/// credential signs in. <c>-n</c> is only a label.
/// </summary>
internal static class DeleteCommand
{
    public static Command Build()
    {
        var options = new DeploymentOptions();
        var yes = new Option<bool>("--yes", "-y")
        {
            Description = "Do not prompt for confirmation before deleting.",
        };

        var continueOnError = new Option<bool>("--continue-on-error")
        {
            Description = "Keep deleting after a failure (default: stop; the objects not yet deleted stay).",
        };

        var command = new Command("delete", "Deletes the objects a template declares (found by their keys, reverse dependency order) after confirmation.");
        command.Options.Add(options.TemplateFile);
        command.Options.Add(options.Parameters);
        command.Options.Add(options.Name);
        command.Options.Add(options.Parallelism);
        command.Options.Add(options.DeviceCode);
        command.Options.Add(yes);
        command.Options.Add(continueOnError);
        command.Options.Add(options.Timings);
        command.SetAction((parseResult, ct) => RunAsync(
            options.Read(parseResult) with { Yes = parseResult.GetValue(yes), ContinueOnError = parseResult.GetValue(continueOnError) },
            parseResult.InvocationConfiguration.Output,
            parseResult.InvocationConfiguration.Error,
            ConsolePrompt.DefaultReadLine,
            ct));
        return command;
    }

    internal static async Task<int> RunAsync(DeploymentInvocation invocation, TextWriter stdout, TextWriter stderr, Func<string?> readLine, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        using var scrubScope = SecretScrubber.Begin();

        // --timings: the phase table on stderr when the run ends, whatever the exit (declared before the deployment, so written after it is disposed).
        using var timingsReport = RunTimingsWriter.Begin(invocation, stderr);

        // The declared objects are found exactly as what-if finds them: by key, read-only.
        var (opened, setupExit) = await PlannedDeployment.OpenAsync(invocation, RunVerb.Delete, DeploymentMode.Incremental, PlanMode.WhatIf, stdout, stderr, cancellationToken).ConfigureAwait(false);
        if (opened is null)
        {
            return setupExit;
        }

        using var deployment = opened;
        var plan = deployment.Plan;
        var preflight = deployment.Preflight;
        string name = invocation.EffectiveName;
        var deleter = deployment.Services.CreateTemplateDeleter();
        var findPhase = invocation.Timings.Measure("find declared objects");
        var targets = await deleter.FindDeclaredObjectsAsync(plan, cancellationToken).ConfigureAwait(false);
        findPhase.Dispose();
        if (targets.Count == 0)
        {
            return VerbOutput.Succeed(invocation.Output, stdout, $"Nothing to delete: none of the objects template '{invocation.TemplateFile}' declares exists in tenant {preflight.TenantId}.", json =>
            {
                json["deployment"] = name;
                json["deleted"] = 0;
            });
        }

        if (!invocation.Yes && !Confirm(invocation, targets, preflight.TenantId, stdout, stderr, readLine))
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, CliDiagnosticCodes.NotConfirmed, "Nothing deleted.");
        }

        var deletePhase = invocation.Timings.Measure("delete");
        var result = await deleter.DeleteAsync(plan, targets, invocation.ContinueOnError, cancellationToken).ConfigureAwait(false);
        deletePhase.Dispose();
        return Report(invocation, name, result, stdout, stderr);
    }

    /// <summary>Lists what will be deleted (and kept) on stderr, so stdout stays the command's one document, and asks.</summary>
    private static bool Confirm(DeploymentInvocation invocation, IReadOnlyList<DeclaredObject> targets, string tenantId, TextWriter stdout, TextWriter stderr, Func<string?> readLine)
    {
        int deletable = targets.Count(t => t.Refusal is null && t.Problem is null);
        stderr.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Template '{invocation.TemplateFile}' declares {targets.Count} object(s) found in tenant {tenantId}; {deletable} will be DELETED, in this order:"));
        foreach (var target in targets)
        {
            stderr.WriteLine("  " + Describe(target));
        }

        return ConsolePrompt.Confirm(string.Create(CultureInfo.InvariantCulture, $"Delete these {deletable} object(s)?"), stdout, stderr, readLine);
    }

    private static string Describe(DeclaredObject target)
    {
        string displayName = target.DisplayName is null ? string.Empty : $" \"{target.DisplayName}\"";
        string graphId = target.GraphId is null ? string.Empty : $" Graph id {target.GraphId}";
        string note = target switch
        {
            { Problem: { } problem } => $" [cannot be checked: {problem}]",
            { Refusal: not null, RefusalCode: EngineDiagnosticCodes.NotDeletable } => " [kept: not deletable]",
            { Refusal: not null } => " [kept: protected]",
            { Risk: { } risk } => $" [{risk}]",
            _ => string.Empty,
        };
        return $"- {target.Instance.Id.Id} ({target.Instance.Type}){displayName}{graphId}{note}";
    }

    private static int Report(DeploymentInvocation invocation, string name, TemplateDeleteResult result, TextWriter stdout, TextWriter stderr)
    {
        string summary = result.Succeeded
            ? string.Create(CultureInfo.InvariantCulture, $"Deployment '{name}': deleted {result.DeletedCount} object(s) ({result.AlreadyGoneCount} already gone, {result.RefusedCount} kept).")
            : string.Create(CultureInfo.InvariantCulture, $"Deployment '{name}': {result.FailedCount} delete(s) failed, {result.SkippedCount} skipped; deleted {result.DeletedCount} object(s) ({result.AlreadyGoneCount} already gone, {result.RefusedCount} kept).");
        int exit = result.Succeeded ? ExitCodes.Ok : ExitCodes.Failure;

        if (invocation.Output == OutputFormat.Json)
        {
            // ONE document: the per-object outcomes and the totals, on success and on failure.
            var outcomes = new JsonArray();
            foreach (var outcome in result.Outcomes)
            {
                outcomes.Add(new JsonObject
                {
                    ["resourceId"] = outcome.Target.Instance.Id.Id,
                    ["type"] = outcome.Target.Instance.Type,
                    ["graphId"] = outcome.Target.GraphId,
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
                ["outcomes"] = outcomes,
            };
            stdout.WriteLine(document.ToJsonString(JsonOutput.Options(!invocation.NoPrettyPrint)));
            return exit;
        }

        if (invocation.Output == OutputFormat.None)
        {
            return exit;
        }

        foreach (var outcome in result.Outcomes)
        {
            string id = outcome.Target.Instance.Id.Id;
            string line = outcome.Status switch
            {
                DeletionStatus.Deleted => $"  - {id} (deleted)",
                DeletionStatus.AlreadyGone => $"  - {id} (already gone)",
                DeletionStatus.Skipped => $"  ? {id} (skipped: {outcome.Error})",
                DeletionStatus.Refused => $"  * {id} (kept: {outcome.Error})",
                _ => $"  ! {id} ({SecretScrubber.Scrub(outcome.Error ?? "failed")})",
            };
            (outcome.Status == DeletionStatus.Failed ? stderr : stdout).WriteLine(line);
        }

        (exit == ExitCodes.Ok ? stdout : stderr).WriteLine(summary);
        return exit;
    }
}
