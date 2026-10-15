using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EArm.Cli.Hosting;
using EArm.Cli.Output;
using EArm.Engine;
using EArm.Graph;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Engine.WhatIf;
using Microsoft.Extensions.Logging;

namespace EArm.Cli.Commands;

/// <summary>
/// The shared pipeline of <c>deployment what-if</c> and <c>deployment create</c>: <c>--secure-outputs-file</c> check (exit 1) → the setup of
/// <see cref="PlannedDeployment"/> (load, offline validation, preflight, Key Vault references, plan; exits 1/2/3) → render the what-if and/or execute (exit 1 on failure). <c>--exit-code-on-changes</c> exits 5 when the what-if plans a
/// change, or when a successful <c>create</c> wrote one; orphan report rows never count. With <c>-o json</c> and <c>-c</c> the what-if is embedded in the
/// run's one document (<c>whatIf</c>) instead of being a document of its own. Identity is resolved by live key on every run;
/// nothing is recorded locally (only the secure outputs file is written, when asked for). Nothing the template does not declare is ever deleted
/// (Complete mode only reports it). A run that replaces objects (an immutable property changed: delete, then create again) asks for confirmation
/// unless <c>-y</c>; without <c>-y</c> on a non-interactive console it fails (exit 1, <c>RecreateNotConfirmed</c>) before anything is written. To
/// know the replacements up front, a create without <c>-y</c> plans with the read-only what-if pass first (not rendered unless <c>-c</c>).
/// </summary>
internal static class DeploymentRunner
{
    /// <summary>Reads a confirmation line from the console.</summary>
    public static readonly Func<string?> DefaultReadLine = ConsolePrompt.DefaultReadLine;

    public static Task<int> WhatIfAsync(DeploymentInvocation invocation, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        return RunAsync(invocation with { WhatIf = true }, stdout, stderr, static () => null, cancellationToken);
    }

    public static Task<int> CreateAsync(DeploymentInvocation invocation, TextWriter stdout, TextWriter stderr, Func<string?> readLine, CancellationToken cancellationToken)
    {
        return RunAsync(invocation, stdout, stderr, readLine, cancellationToken);
    }

    private static async Task<int> RunAsync(DeploymentInvocation invocation, TextWriter stdout, TextWriter stderr, Func<string?> readLine, CancellationToken cancellationToken)
    {
        // Every secret value the run learns is masked from here on, wherever an error message echoes it (S-02).
        using var scrubScope = SecretScrubber.Begin();

        // 0. The secure outputs file must be usable BEFORE anything is written: a secret that cannot be saved is lost for good (E-03a).
        if (!invocation.WhatIf && !string.IsNullOrWhiteSpace(invocation.SecureOutputsFile) && SecureOutputsFile.Validate(invocation.SecureOutputsFile) is { } fileProblem)
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "SecureOutputsFileInvalid", $"--secure-outputs-file: {fileProblem}\nDeployment not started.");
        }

        bool needsWhatIf = invocation.WhatIf || invocation.ConfirmWithWhatIf;

        // Without -y the replacements must be known (and confirmed) before anything is written: the plan reads every resource first (read-only).
        var planMode = needsWhatIf || !invocation.Yes ? PlanMode.WhatIf : PlanMode.Create;

        // 1-5. Load, validate, sign in, resolve Key Vault references and plan.
        var (opened, setupExit) = await PlannedDeployment.OpenAsync(invocation, invocation.Mode, planMode, stdout, stderr, cancellationToken).ConfigureAwait(false);
        if (opened is null)
        {
            return setupExit;
        }

        using var deployment = opened;
        var plan = deployment.Plan;

        // 6. What-if output. With -c the same plan is executed below: the expansion, the
        //    dependency graph and the batches are not computed again; the executor re-reads each resource right
        //    before applying it (docs/engine.md step 6), so the confirmed what-if cannot go stale.
        WhatIfReport? confirmedWhatIf = null;
        if (needsWhatIf)
        {
            if (invocation.WhatIf)
            {
                var report = WhatIfWriter.Write(plan, invocation, stdout);
                return invocation.ExitCodeOnChanges && report.HasChanges ? ExitCodes.WhatIfChangesDetected : ExitCodes.Ok;
            }

            // -c: with -o json stdout carries exactly ONE document, so the what-if is not written on its own: it is embedded in the result
            // document ("whatIf"), or in the refusal document when the run does not start. The prompt then shows the what-if as text on stderr.
            bool json = invocation.Output == OutputFormat.Json;
            confirmedWhatIf = json ? WhatIfReport.From(plan) : WhatIfWriter.Write(plan, invocation, stdout);
            if (!invocation.Yes)
            {
                if (json)
                {
                    WhatIfText.Render(confirmedWhatIf, stderr, new WhatIfTextOptions(invocation.ResultFormat, Verbose: invocation.LogLevel <= LogLevel.Debug, Colors: false));
                }

                // Confirming the what-if confirms the replacements it shows.
                if (!ConsolePrompt.Confirm("Are you sure you want to execute the deployment?", stdout, stderr, readLine))
                {
                    if (json)
                    {
                        // The human line on stderr (next to the prompt), the one document on stdout.
                        stderr.WriteLine("Deployment not started.");
                        var declined = WhatIfJson.Build(confirmedWhatIf, invocation.ResultFormat);
                        return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "NotConfirmed", "Deployment not started.", document => document["whatIf"] = declined);
                    }

                    // The what-if text is already on stdout: a refusal is reported on stderr only.
                    stderr.WriteLine("Deployment not started.");
                    return ExitCodes.Failure;
                }
            }
        }
        else if (!invocation.Yes && ConfirmReplacements(invocation, plan, stdout, stderr, readLine) is { } refusedExit)
        {
            return refusedExit;
        }

        // 7. Execute.
        var executor = deployment.Services.CreateExecutor();
        var result = await executor.ExecuteAsync(plan, new ExecuteOptions
        {
            Parallelism = invocation.Parallelism,
            ContinueOnError = invocation.ContinueOnError,
            RevealSecureOutputs = invocation.RevealSecureOutputs,
            SecureOutputsFile = invocation.SecureOutputsFile,

            // -y confirms every replacement; otherwise only the ones the user saw and confirmed (a replacement found later in the run is refused).
            ApprovedRecreates = invocation.Yes ? null : Replacements(plan).Select(change => change.Id.Id).ToHashSet(StringComparer.OrdinalIgnoreCase),
        }, cancellationToken).ConfigureAwait(false);

        var embeddedWhatIf = invocation.Output == OutputFormat.Json && confirmedWhatIf is not null ? WhatIfJson.Build(confirmedWhatIf, invocation.ResultFormat) : null;
        DeploymentResultWriter.Write(result, invocation.Output, !invocation.NoPrettyPrint, stdout, stderr, embeddedWhatIf);
        if (!result.Succeeded)
        {
            return ExitCodes.Failure;
        }

        // --exit-code-on-changes on create: exit 5 when the (successful) run wrote something; the orphan report never counts.
        return invocation.ExitCodeOnChanges && result.HasChanges ? ExitCodes.WhatIfChangesDetected : ExitCodes.Ok;
    }

    // ---- replacements (delete and create again) ----

    /// <summary>The planned replacements: an immutable property changed on a type that is deleted and created again (what-if plans only).</summary>
    private static IReadOnlyList<ResourceChange> Replacements(DeploymentPlan plan)
    {
        return plan.Changes.Where(change => change is { Type: ChangeType.Modify, Mode: ChangeMode.Recreate, HasError: false }).ToList();
    }

    /// <summary>
    /// Lists the objects a run replaces (on stderr: stdout stays the run's one document) and asks for confirmation; returns the exit code when the run must
    /// not start: not confirmed, or standard input is not interactive (then <c>-y</c> is required). <c>null</c> when nothing is replaced or the user agreed.
    /// </summary>
    private static int? ConfirmReplacements(DeploymentInvocation invocation, DeploymentPlan plan, TextWriter stdout, TextWriter stderr, Func<string?> readLine)
    {
        var replacements = Replacements(plan);
        if (replacements.Count == 0)
        {
            return null;
        }

        const int Shown = 50;
        stderr.WriteLine(string.Create(CultureInfo.InvariantCulture, $"The deployment replaces {replacements.Count} object(s): an immutable property changed, so each one is DELETED and created again (with a new object id):"));
        foreach (var change in replacements.Take(Shown))
        {
            stderr.WriteLine($"  - {change.Id.Id} ({change.ResourceType}){(change.GraphId is null ? string.Empty : $" Graph id {change.GraphId}")}");
        }

        if (replacements.Count > Shown)
        {
            stderr.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  ... and {replacements.Count - Shown} more"));
        }

        if (ConsolePrompt.Confirm("Replace them?", stdout, stderr, readLine))
        {
            return null;
        }

        string message = string.Create(CultureInfo.InvariantCulture, $"The deployment would replace {replacements.Count} object(s) (delete and create again) and that was not confirmed; pass -y to confirm (required when standard input is not interactive).\nDeployment not started.");
        return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, EngineDiagnosticCodes.RecreateNotConfirmed, message);
    }
}
