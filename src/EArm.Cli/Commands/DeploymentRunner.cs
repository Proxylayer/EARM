using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Cli.Hosting;
using EArm.Cli.Output;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Loading;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.KeyVault;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Engine.State;
using Microsoft.Extensions.Logging;

namespace EArm.Cli.Commands;

/// <summary>
/// The shared pipeline of <c>deployment what-if</c> and <c>deployment create</c>: load → offline validation (exit 2)
/// → <c>--secure-outputs-file</c> check (exit 1) → state lock for create (exit 4; what-if reads an unlocked snapshot) →
/// state load (exit 1 on an unreadable state) → preflight (exit 3 when no credential signs in) → tenant
/// pin (exit 3) → Key Vault parameter references (exit 3) → plan (exit 2 on
/// validation errors, exit 1 when a type has no provider) → render the what-if (exit 5 with
/// <c>--exit-code-on-changes</c>) and/or execute with the state sink (exit 1 on failure). The lock comes BEFORE the state is
/// read, so two runs never plan against the same stale state (review finding E-04). A Complete-mode run that would delete
/// objects asks for confirmation (without <c>-y</c>), and one that deletes everything or more than <c>--max-deletes</c> objects
/// needs <c>--allow-delete-all</c> (review finding I-06).
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

        // 1. Parameters and template (offline).
        ParameterSet parameters;
        try
        {
            parameters = ValidateCommand.BuildParameterSet(invocation.ParameterTokens, out _);
        }
        catch (TemplateLoadException ex)
        {
            return FailValidation(invocation, [ex.ToDiagnostic()], stdout, stderr, ExitCodes.Failure);
        }
        catch (ArgumentException ex)
        {
            return FailValidation(invocation, [Diagnostic.Error(ValidateCommand.InvalidArgumentCode, ex.Message)], stdout, stderr, ExitCodes.Failure);
        }

        string name = invocation.EffectiveName;
        if (!StateNames.IsValid(name))
        {
            return FailValidation(invocation, [Diagnostic.Error(ValidateCommand.InvalidArgumentCode, $"Deployment name '{name}' is not valid: use 1-{StateNames.MaxLength} letters, digits, '.', '_' or '-' (not a reserved device name such as CON or NUL; pass -n to choose one).")], stdout, stderr, ExitCodes.Failure);
        }

        var catalog = ResourceTypeCatalog.Default;
        TemplateDocument document;
        try
        {
            document = await new TemplateLoader(catalog).LoadAsync(invocation.TemplateFile, parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (TemplateLoadException ex)
        {
            return FailValidation(invocation, [ex.ToDiagnostic()], stdout, stderr, ExitCodes.Failure);
        }

        SecretRegistration.RegisterDocument(document);

        using var loggerFactory = GlobalOptions.CreateLoggerFactory(invocation.LogLevel);
        if (!EngineServices.TryCreate(invocation.Connection, loggerFactory, stdout, stderr, invocation.Output, out var created, out int createExit))
        {
            return createExit;
        }

        using var services = created!;
        bool needsWhatIf = invocation.WhatIf || invocation.ConfirmWithWhatIf;
        var planMode = needsWhatIf ? PlanMode.WhatIf : PlanMode.Create;

        // 2. Offline validation first (validate before preflight), so a broken template is
        //    reported without a sign-in. Key Vault references are resolved after preflight (step 6), so
        //    the what-if promotion rules apply here; PlanAsync repeats the validation with the real mode.
        var offlinePlanner = services.CreatePlanner(new PlannerOptions { DeploymentName = name, Catalog = catalog });
        try
        {
            offlinePlanner.Validate(document, PlanMode.WhatIf);
        }
        catch (ValidationFailedException ex)
        {
            return FailValidation(invocation, ex.Diagnostics, stdout, stderr, ExitCodes.ValidationFailed);
        }

        var store = StateHost.OpenStore(invocation.StateDir, invocation.TemplateFile, loggerFactory);
        IAsyncDisposable? lease = null;
        try
        {
            // 3. The state lock (create only; what-if never writes): taken BEFORE the state is read (E-04).
            if (!invocation.WhatIf)
            {
                if (invocation.ForceUnlock)
                {
                    var former = await store.ForceUnlockAsync(name, cancellationToken).ConfigureAwait(false);
                    if (former is not null)
                    {
                        stderr.WriteLine($"Broke the state lock of deployment '{name}' held by {former.Describe()}.");
                    }
                }

                try
                {
                    lease = await store.LockAsync(name, TimeSpan.FromSeconds(invocation.LockTimeoutSeconds), cancellationToken).ConfigureAwait(false);
                }
                catch (StateLockedException ex)
                {
                    return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.StateLocked, ex.Code, ex.Message);
                }
            }

            // 4. The deployment's state (before preflight: an unreadable state file needs no session to be reported).
            StateFile? state;
            try
            {
                state = await store.LoadAsync(name, cancellationToken).ConfigureAwait(false);
            }
            catch (StateException ex)
            {
                return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, ex.Code, ex.Message);
            }

            if (invocation.WhatIf && state is not null)
            {
                stderr.WriteLine($"Note: what-if read the state of deployment '{name}' without taking its lock; it is a snapshot, and a deployment running now may change it.");
            }

            // 5. Preflight: sign-in, tenant, signed-in principal and the one /organization read (tenant info for tenant()).
            GraphPreflightResult preflight;
            try
            {
                preflight = await services.Preflight.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is GraphTransportException or GraphException)
            {
                return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.AuthOrPreflight, "PreflightFailed", "Preflight failed: " + SecretScrubber.Scrub(ex.Message));
            }

            // 5a. Tenant pin: a state written for another tenant is never touched.
            try
            {
                state?.EnsureTenant(preflight.TenantId);
            }
            catch (TenantMismatchException ex)
            {
                return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.AuthOrPreflight, ex.Code, ex.Message);
            }

            // 5b. What tenant() and environment() return; an unreadable organization keeps tenant() unknown.
            var tenant = DeploymentEnvironment.TenantFromOrganization(preflight.Organization, preflight.TenantId);
            var environment = DeploymentEnvironment.Environment(preflight.Cloud);

            // 6. Key Vault parameter references, with a Key Vault token of the same identity.
            if (KeyVaultSecretResolver.HasPendingReferences(document))
            {
                try
                {
                    document = await services.CreateKeyVaultResolver().ResolveAsync(document, cancellationToken).ConfigureAwait(false);
                    SecretRegistration.RegisterDocument(document);
                }
                catch (KeyVaultResolutionException ex)
                {
                    var definition = document.Parameters.TryGetValue(ex.ParameterName, out var found) ? found : null;
                    return FailValidation(invocation, [Diagnostic.Error(ex.Code, ex.Message, definition?.Path, document.LocationOf(definition?.Path), document.SourcePath)], stdout, stderr, ExitCodes.AuthOrPreflight);
                }
            }

            // 7. Plan (pinned ids from the state; mode-pass rows in what-if).
            var planner = services.CreatePlanner(new PlannerOptions
            {
                DeploymentName = name,
                TenantId = preflight.TenantId,
                Tenant = tenant,
                Environment = environment,
                SignedInPrincipalId = preflight.PrincipalObjectId,
                AppOnlyToken = string.Equals(preflight.PrincipalType, "application", StringComparison.OrdinalIgnoreCase),
                Parallelism = invocation.Parallelism,
                AllowCaEnforce = invocation.AllowCaEnforce,
                DeleteAdopted = invocation.DeleteAdopted,
                OnSoftDeletedConflict = invocation.OnSoftDeletedConflict,
                Catalog = catalog,
            });

            DeploymentPlan plan;
            try
            {
                plan = await planner.PlanAsync(document, state, invocation.Mode, planMode, cancellationToken).ConfigureAwait(false);
            }
            catch (ValidationFailedException ex)
            {
                return FailValidation(invocation, ex.Diagnostics, stdout, stderr, ExitCodes.ValidationFailed);
            }
            catch (UnknownProviderException ex)
            {
                return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "UnknownProvider", ex.Message);
            }

            // 8. What-if output. With -c the same plan is executed below: the expansion, the
            //    dependency graph and the batches are not computed again; the executor re-reads each resource right
            //    before applying it (docs/engine.md step 6), so the confirmed what-if cannot go stale.
            if (needsWhatIf)
            {
                var report = WhatIfWriter.Write(plan, invocation, stdout);
                if (invocation.WhatIf)
                {
                    return invocation.ExitCodeOnChanges && report.HasChanges ? ExitCodes.WhatIfChangesDetected : ExitCodes.Ok;
                }

                // The blast-radius limits apply even to -y, so they come before the confirmation prompt.
                // The what-if document is already on stdout (the one document of this run): a refusal from here on is reported on stderr only.
                if (CheckDeleteLimits(invocation, plan, state, stdout, stderr, documentWritten: true) is { } limitExit)
                {
                    return limitExit;
                }

                if (!invocation.Yes && !ConsolePrompt.Confirm("Are you sure you want to execute the deployment?", stdout, stderr, readLine))
                {
                    stderr.WriteLine("Deployment not started.");
                    return ExitCodes.Failure;
                }
            }
            else
            {
                if (CheckDeleteLimits(invocation, plan, state, stdout, stderr, documentWritten: false) is { } limitExit)
                {
                    return limitExit;
                }

                if (!await ConfirmDeletionsAsync(invocation, plan, state, services, preflight.TenantId, stdout, stderr, readLine, cancellationToken).ConfigureAwait(false))
                {
                    // The listing of what would be deleted is already on stdout: the refusal stays on stderr.
                    stderr.WriteLine("Deployment not started.");
                    return ExitCodes.Failure;
                }
            }

            // 9. Execute with the state sink (saved after every resource).
            state ??= StateFile.CreateNew(name, preflight.TenantId, DateTimeOffset.UtcNow);
            var sink = new FileStateSink(store, state, plan, loggerFactory.CreateLogger<FileStateSink>());
            await sink.BeginAsync(cancellationToken).ConfigureAwait(false);

            var executor = services.CreateExecutor();
            var result = await executor.ExecuteAsync(plan, new ExecuteOptions
            {
                Parallelism = invocation.Parallelism,
                ContinueOnError = invocation.ContinueOnError,
                DeleteAdopted = invocation.DeleteAdopted,
                RevealSecureOutputs = invocation.RevealSecureOutputs,
                SecureOutputsFile = invocation.SecureOutputsFile,
                MaxDeletes = invocation.AllowDeleteAll ? null : invocation.MaxDeletes,
                StateSink = sink,
                State = state,
            }, cancellationToken).ConfigureAwait(false);

            DeploymentResultWriter.Write(result, invocation.Output, !invocation.NoPrettyPrint, stdout, stderr);
            return result.Succeeded ? ExitCodes.Ok : ExitCodes.Failure;
        }
        catch (StateException ex)
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "StateIoFailed", $"The state of deployment '{name}' could not be read or written: {SecretScrubber.Scrub(ex.Message)}");
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // ---- delete blast radius (I-06; review round 2 S2-03, S2-12) ----

    /// <summary>
    /// What a run would delete: the orphan entries the plan can judge now, the entries of types it cannot judge yet (a resource of that type has a deploy-time name, so the
    /// mode pass may delete some of them once the names resolve: an upper bound), and the objects it replaces (a recreate deletes the old object). Recreates are only known
    /// from a what-if plan (<c>-c</c>); the executor enforces <c>--max-deletes</c> as a hard limit whatever this estimate says.
    /// </summary>
    private sealed record DeletionEstimate(IReadOnlyList<StateEntry> Orphans, IReadOnlyList<StateEntry> Maybe, IReadOnlyList<ResourceChange> Recreates)
    {
        public int Total => Orphans.Count + Maybe.Count + Recreates.Count;
    }

    private static DeletionEstimate EstimateDeletions(DeploymentInvocation invocation, DeploymentPlan plan, StateFile? state)
    {
        IReadOnlyList<StateEntry> orphans = [];
        IReadOnlyList<StateEntry> maybe = [];
        if (invocation.Mode == DeploymentMode.Complete && state is { Resources.Count: > 0 })
        {
            bool Eligible(StateEntry entry)
            {
                return invocation.DeleteAdopted || entry.Provenance == StateProvenance.Created;
            }

            orphans = ModePass.FindOrphans(state, plan, out _).Where(Eligible).ToList();
            maybe = ModePass.FindUndecided(state, plan).Where(Eligible).ToList();
        }

        var recreates = plan.Changes.Where(change => change is { Type: ChangeType.Modify, Mode: ChangeMode.Recreate, HasError: false }).ToList();
        return new DeletionEstimate(orphans, maybe, recreates);
    }

    /// <summary>
    /// A Complete-mode template that declares no resources while the state has some, or a run that would delete more than <c>--max-deletes</c> objects (orphans, entries of
    /// types whose names are still deploy-time, and objects replaced by a recreate), needs <c>--allow-delete-all</c> (a mistyped template path or an emptied file must not wipe
    /// a deployment, even with <c>-y</c>). Returns the exit code when the run must not start.
    /// </summary>
    private static int? CheckDeleteLimits(DeploymentInvocation invocation, DeploymentPlan plan, StateFile? state, TextWriter stdout, TextWriter stderr, bool documentWritten)
    {
        if (invocation.AllowDeleteAll)
        {
            return null;
        }

        var estimate = EstimateDeletions(invocation, plan, state);
        bool declaresNothing = invocation.Mode == DeploymentMode.Complete && state is { Resources.Count: > 0 } && plan.Instances.Count == 0;
        if (!declaresNothing && estimate.Total <= invocation.MaxDeletes)
        {
            return null;
        }

        string replaced = estimate.Recreates.Count == 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" ({estimate.Recreates.Count} of them replaced: deleted and created again)");
        string reason;
        if (declaresNothing)
        {
            reason = $"the template declares no resources, but deployment '{plan.DeploymentName}' manages {state!.Resources.Count} object(s)";
        }
        else if (estimate.Maybe.Count > 0)
        {
            reason = string.Create(CultureInfo.InvariantCulture, $"it would delete up to {estimate.Total} object(s){replaced} (this may include {estimate.Maybe.Count} entry(ies) of types whose resource names are only known at deployment time), more than --max-deletes {invocation.MaxDeletes}");
        }
        else
        {
            reason = string.Create(CultureInfo.InvariantCulture, $"it would delete {estimate.Total} object(s){replaced}, more than --max-deletes {invocation.MaxDeletes}");
        }

        string what = invocation.Mode == DeploymentMode.Complete ? "Complete-mode deployment" : "deployment";
        string message = $"Refusing the {what}: {reason}. Check the template and the deployment name; pass --allow-delete-all to confirm that this is intended.\nDeployment not started.";
        return VerbOutput.Fail(documentWritten ? OutputFormat.Table : invocation.Output, stdout, stderr, ExitCodes.Failure, EngineDiagnosticCodes.DeleteLimitExceeded, message);
    }

    /// <summary>Lists what a run deletes (Complete-mode orphans, entries that may be deleted, objects that are replaced) and asks (unless <c>-y</c>); true when the run may start.</summary>
    private static async Task<bool> ConfirmDeletionsAsync(DeploymentInvocation invocation, DeploymentPlan plan, StateFile? state, EngineServices services, string tenantId, TextWriter stdout, TextWriter stderr, Func<string?> readLine, CancellationToken cancellationToken)
    {
        var estimate = EstimateDeletions(invocation, plan, state);
        if (estimate.Total == 0 || invocation.Yes)
        {
            return true;
        }

        const int Shown = 50;
        string headline = estimate.Maybe.Count == 0
            ? string.Create(CultureInfo.InvariantCulture, $"Complete mode will DELETE {estimate.Orphans.Count + estimate.Recreates.Count} object(s) from tenant {tenantId}, because the template no longer declares them:")
            : string.Create(CultureInfo.InvariantCulture, $"Complete mode will DELETE {estimate.Orphans.Count + estimate.Recreates.Count} object(s) from tenant {tenantId}, and may delete up to {estimate.Maybe.Count} more (entries of types whose resource names are only known at deployment time), because the template no longer declares them:");
        stdout.WriteLine(headline);
        var listed = estimate.Orphans.Select(entry => (entry, may: false)).Concat(estimate.Maybe.Select(entry => (entry, may: true))).ToList();
        foreach (var (entry, may) in listed.Take(Shown))
        {
            string? displayName = await LiveDisplayNameAsync(services, entry, cancellationToken).ConfigureAwait(false);
            string adopted = entry.Provenance == StateProvenance.Adopted ? " [adopted]" : string.Empty;
            string maybeNote = may ? " [may be deleted]" : string.Empty;
            stdout.WriteLine($"  - {entry.Id.Id} ({entry.Type}){(displayName is null ? string.Empty : $" \"{displayName}\"")}{adopted}{maybeNote}");
        }

        if (listed.Count > Shown)
        {
            stdout.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  ... and {listed.Count - Shown} more"));
        }

        foreach (var change in estimate.Recreates.Take(Shown))
        {
            stdout.WriteLine($"  - {change.Id.Id} ({change.ResourceType}) [replaced: deleted and created again]");
        }

        return ConsolePrompt.Confirm("Delete them as part of this deployment?", stdout, stderr, readLine);
    }

    /// <summary>The live <c>displayName</c> of the object a state entry points at, when it can be read cheaply (best effort).</summary>
    private static async Task<string?> LiveDisplayNameAsync(EngineServices services, StateEntry entry, CancellationToken cancellationToken)
    {
        if (!services.Providers.TryGet(entry.Type, out var provider))
        {
            return null;
        }

        try
        {
            var live = await provider.ReadAsync(entry.ToResourceKey(ResourceTypeCatalog.Default), null, services.Graph, cancellationToken).ConfigureAwait(false);
            return live is not null && string.Equals(live.GraphId, entry.GraphId, StringComparison.OrdinalIgnoreCase)
                ? EArm.Engine.Providers.JsonObjects.GetString(live.Raw, "displayName")
                : null;
        }
        catch (Exception ex) when (ex is ProviderException or GraphException or GraphTransportException)
        {
            return null;
        }
    }

    private static int FailValidation(DeploymentInvocation invocation, IReadOnlyList<Diagnostic> diagnostics, TextWriter stdout, TextWriter stderr, int exitCode)
    {
        var report = new ValidationReport { TemplateFile = invocation.TemplateFile };
        ValidationWriter.Fill(report, diagnostics);
        ValidationWriter.Write(report, diagnostics, invocation.TemplateFile, invocation.Output, stdout, stderr);
        return exitCode;
    }
}
