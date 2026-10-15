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
/// <c>--exit-code-on-changes</c>; orphan report rows never count) and/or execute with the state sink (exit 1 on failure). The lock
/// comes BEFORE the state is read, so two runs never plan against the same stale state (review finding E-04). Nothing the template
/// does not declare is ever deleted (Complete mode only reports it). A run that replaces objects (an immutable property changed:
/// delete, then create again) asks for confirmation unless <c>-y</c>; without <c>-y</c> on a non-interactive console it fails
/// (exit 1, <c>RecreateNotConfirmed</c>) before anything is written. To know the replacements up front, a create without <c>-y</c>
/// plans with the read-only what-if pass first (not rendered unless <c>-c</c>).
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

        // Without -y the replacements must be known (and confirmed) before anything is written: the plan reads every resource first (read-only).
        var planMode = needsWhatIf || !invocation.Yes ? PlanMode.WhatIf : PlanMode.Create;

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

            // 7. Plan (identity by live key; orphan report rows in what-if).
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

                // The what-if document is already on stdout (the one document of this run): a refusal is reported on stderr only.
                // Confirming the what-if confirms the replacements it shows.
                if (!invocation.Yes && !ConsolePrompt.Confirm("Are you sure you want to execute the deployment?", stdout, stderr, readLine))
                {
                    stderr.WriteLine("Deployment not started.");
                    return ExitCodes.Failure;
                }
            }
            else if (!invocation.Yes && ConfirmReplacements(invocation, plan, stdout, stderr, readLine) is { } refusedExit)
            {
                return refusedExit;
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
                RevealSecureOutputs = invocation.RevealSecureOutputs,
                SecureOutputsFile = invocation.SecureOutputsFile,

                // -y confirms every replacement; otherwise only the ones the user saw and confirmed (a replacement found later in the run is refused).
                ApprovedRecreates = invocation.Yes ? null : Replacements(plan).Select(change => change.Id.Id).ToHashSet(StringComparer.OrdinalIgnoreCase),
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

    private static int FailValidation(DeploymentInvocation invocation, IReadOnlyList<Diagnostic> diagnostics, TextWriter stdout, TextWriter stderr, int exitCode)
    {
        var report = new ValidationReport { TemplateFile = invocation.TemplateFile };
        ValidationWriter.Fill(report, diagnostics);
        ValidationWriter.Write(report, diagnostics, invocation.TemplateFile, invocation.Output, stdout, stderr);
        return exitCode;
    }
}
