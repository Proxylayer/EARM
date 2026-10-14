using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Az;
using EntraArm.Cli.Hosting;
using EntraArm.Cli.Output;
using EntraArm.Core;
using EntraArm.Core.Diagnostics;
using EntraArm.Core.Loading;
using EntraArm.Core.Types;
using EntraArm.Engine.Execution;
using EntraArm.Engine.KeyVault;
using EntraArm.Engine.Planning;
using EntraArm.Engine.Providers;
using EntraArm.Engine.State;
using Microsoft.Extensions.Logging;

namespace EntraArm.Cli.Commands;

/// <summary>
/// The shared pipeline of <c>deployment what-if</c> and <c>deployment create</c>: load → offline validation (exit 2)
/// → state load (exit 1 on an unreadable state) → preflight (exit 3 on a missing az session or toolchain) → tenant
/// pin (exit 3) → Key Vault parameter references (exit 3) → state lock for create (exit 4) → plan (exit 2 on
/// validation errors, exit 1 when a type has no provider) → render the what-if (exit 5 with
/// <c>--exit-code-on-changes</c>) and/or execute with the state sink (exit 1 on failure).
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
            return FailValidation(invocation, [Diagnostic.Error(ValidateCommand.InvalidArgumentCode, $"Deployment name '{name}' is not valid: use 1-{StateNames.MaxLength} letters, digits, '.', '_' or '-' (pass -n to choose one).")], stdout, stderr, ExitCodes.Failure);
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

        using var loggerFactory = GlobalOptions.CreateLoggerFactory(invocation.LogLevel);
        using var services = new EngineServices(invocation.Az, loggerFactory);
        bool needsWhatIf = invocation.WhatIf || invocation.ConfirmWithWhatIf;
        var planMode = needsWhatIf ? PlanMode.WhatIf : PlanMode.Create;

        // 2. Offline validation first (plan section 2.4: validate before preflight), so a broken template is
        //    reported without an Azure CLI session. Key Vault references are resolved after preflight (step 6), so
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

        // 3. The deployment's state (before preflight: an unreadable state file needs no session to be reported).
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

        // 4. Preflight: az session, tenant, Graph token.
        AzPreflightResult preflight;
        try
        {
            preflight = await services.Preflight.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AzCliException ex)
        {
            stderr.WriteLine("Preflight failed: " + ex.Message);
            return ExitCodes.AuthOrPreflight;
        }
        catch (ToolchainException ex)
        {
            stderr.WriteLine("Preflight failed: " + ex.Message);
            return ExitCodes.AuthOrPreflight;
        }

        // 5. Tenant pin: a state written for another tenant is never touched.
        try
        {
            state?.EnsureTenant(preflight.TenantId);
        }
        catch (TenantMismatchException ex)
        {
            stderr.WriteLine(ex.Message);
            return ExitCodes.AuthOrPreflight;
        }

        // 5b. What tenant() and environment() return (plan section 1.5); an unreadable organization keeps tenant() unknown.
        var tenant = await DeploymentEnvironment.ReadTenantAsync(services.Graph, preflight.TenantId, loggerFactory.CreateLogger<DeploymentPlanner>(), cancellationToken).ConfigureAwait(false);
        var environment = DeploymentEnvironment.Environment(preflight.CloudName);

        // 6. Key Vault parameter references (plan section 1.6), through the same az session.
        if (KeyVaultSecretResolver.HasPendingReferences(document))
        {
            try
            {
                document = await services.CreateKeyVaultResolver().ResolveAsync(document, cancellationToken).ConfigureAwait(false);
            }
            catch (KeyVaultResolutionException ex)
            {
                var definition = document.Parameters.TryGetValue(ex.ParameterName, out var found) ? found : null;
                return FailValidation(invocation, [Diagnostic.Error(ex.Code, ex.Message, definition?.Path, document.LocationOf(definition?.Path), document.SourcePath)], stdout, stderr, ExitCodes.AuthOrPreflight);
            }
        }

        // 7. The state lock (create only; what-if never writes).
        IAsyncDisposable? lease = null;
        try
        {
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
                    stderr.WriteLine(ex.Message);
                    return ExitCodes.StateLocked;
                }
            }

            // 8. Plan (pinned ids from the state; mode-pass rows in what-if).
            var planner = services.CreatePlanner(new PlannerOptions
            {
                DeploymentName = name,
                TenantId = preflight.TenantId,
                Tenant = tenant,
                Environment = environment,
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
                stderr.WriteLine(ex.Message);
                return ExitCodes.Failure;
            }

            // 9. What-if output (plan section 1.8). With -c the same plan is executed below: the expansion, the
            //    dependency graph and the batches are not computed again; the executor re-reads each resource right
            //    before applying it (docs/engine.md step 6), so the confirmed what-if cannot go stale.
            if (needsWhatIf)
            {
                var report = WhatIfWriter.Write(plan, invocation, stdout);
                if (invocation.WhatIf)
                {
                    return invocation.ExitCodeOnChanges && report.HasChanges ? ExitCodes.WhatIfChangesDetected : ExitCodes.Ok;
                }

                if (!invocation.Yes && !ConsolePrompt.Confirm("Are you sure you want to execute the deployment?", stdout, stderr, readLine))
                {
                    stderr.WriteLine("Deployment not started.");
                    return ExitCodes.Failure;
                }
            }

            // 10. Execute with the state sink (saved after every resource).
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
                StateSink = sink,
                State = state,
            }, cancellationToken).ConfigureAwait(false);

            DeploymentResultWriter.Write(result, invocation.Output, !invocation.NoPrettyPrint, stdout, stderr);
            return result.Succeeded ? ExitCodes.Ok : ExitCodes.Failure;
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
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
