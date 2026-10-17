using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EArm.Cli.Hosting;
using EArm.Cli.Output;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Loading;
using EArm.Core.Types;
using EArm.Engine.Execution;
using EArm.Engine.KeyVault;
using EArm.Engine.Permissions;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Graph;
using EArm.Graph.Auth;
using Microsoft.Extensions.Logging;

namespace EArm.Cli.Commands;

/// <summary>
/// The setup shared by the online verbs (<c>what-if</c>, <c>create</c>, <c>delete</c>): parameters and template (exit 1 when they cannot be loaded) →
/// the delegated scopes of the verb (<see cref="RunScopes"/>, offline; requested only by a user sign-in, app-only identities keep <c>.default</c>) →
/// engine services (exit 3 when no credential can be built) → offline validation (exit 2; before preflight, so a broken template is reported
/// without a sign-in) → preflight (exit 3 when no credential signs in) → Key Vault parameter references (exit 3) → plan (exit 2 on validation
/// errors, exit 1 when a type has no provider). A missing delegated consent stops the run at preflight or at the Key Vault step, before any write (exit 3),
/// unless an interactive terminal lets the user consent on the spot. Identity is resolved by live key; nothing is read from or written to the local disk. Owns the
/// services and the logger factory of the run: dispose it when the verb is done.
/// </summary>
internal sealed class PlannedDeployment : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;

    private PlannedDeployment(ILoggerFactory loggerFactory, EngineServices services, GraphPreflightResult preflight, DeploymentPlan plan)
    {
        _loggerFactory = loggerFactory;
        Services = services;
        Preflight = preflight;
        Plan = plan;
    }

    public EngineServices Services { get; }

    public GraphPreflightResult Preflight { get; }

    public DeploymentPlan Plan { get; }

    /// <summary>
    /// Runs the setup and plans the template with <paramref name="mode"/> and <paramref name="planMode"/>; a user sign-in requests the delegated scopes of
    /// <paramref name="verb"/>. Returns the planned deployment, or <c>null</c> with the exit code of the step that failed (its message is already written).
    /// </summary>
    public static async Task<(PlannedDeployment? Deployment, int ExitCode)> OpenAsync(DeploymentInvocation invocation, RunVerb verb, DeploymentMode mode, PlanMode planMode, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        // 1. Parameters and template (offline).
        ParameterSet parameters;
        try
        {
            parameters = (await ParameterArguments.BuildAsync(invocation.ParameterTokens, cancellationToken).ConfigureAwait(false)).Parameters;
        }
        catch (TemplateLoadException ex)
        {
            return (null, FailValidation(invocation, [ex.ToDiagnostic()], stdout, stderr, ExitCodes.Failure));
        }
        catch (ArgumentException ex)
        {
            return (null, FailValidation(invocation, [Diagnostic.Error(ValidateCommand.InvalidArgumentCode, ex.Message)], stdout, stderr, ExitCodes.Failure));
        }

        string name = invocation.EffectiveName;
        var catalog = ResourceTypeCatalog.Default;
        TemplateDocument document;
        try
        {
            document = await new TemplateLoader(catalog).LoadAsync(invocation.TemplateFile, parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (TemplateLoadException ex)
        {
            return (null, FailValidation(invocation, [ex.ToDiagnostic()], stdout, stderr, ExitCodes.Failure));
        }

        // A parameter without a value or default: an interactive what-if / create asks for it (unless --no-prompt), as the Azure CLI does.
        if (!invocation.NoPrompt && verb != RunVerb.Delete && document.ParameterValues.Values.Any(bound => bound.IsMissing) && ParameterPrompt.ForConsole(stderr) is { } prompt)
        {
            var answers = prompt.Ask(document.ParameterValues.Values.Where(bound => bound.IsMissing).Select(bound => bound.Definition));
            if (answers is not null)
            {
                parameters = answers.Aggregate(parameters, (set, answer) => set.WithInline(answer.Key, answer.Value));
                try
                {
                    document = await new TemplateLoader(catalog).LoadAsync(invocation.TemplateFile, parameters, cancellationToken).ConfigureAwait(false);
                }
                catch (TemplateLoadException ex)
                {
                    return (null, FailValidation(invocation, [ex.ToDiagnostic()], stdout, stderr, ExitCodes.Failure));
                }
            }
        }

        SecretRegistration.RegisterDocument(document);

        // The delegated scopes of this run, computed offline from the loaded template before the services (and their token provider) exist. Null when the
        // template does not expand offline: the validation below then reports why.
        var runScopes = TemplateRunScopes.Compute(document, verb);

        ILoggerFactory? loggerFactory = GlobalOptions.CreateLoggerFactory(invocation.LogLevel);
        EngineServices? services = null;
        try
        {
            if (!EngineServices.TryCreate(invocation.Connection, loggerFactory, stdout, stderr, invocation.Output, new RunAuthentication(runScopes, invocation.DeviceCode), out services, out int createExit))
            {
                return (null, createExit);
            }

            var (exit, preflight, plan) = await PlanCoreAsync(invocation, services!, document, name, catalog, verb, mode, planMode, stdout, stderr, cancellationToken).ConfigureAwait(false);
            if (exit is { } failed)
            {
                return (null, failed);
            }

            var deployment = new PlannedDeployment(loggerFactory, services!, preflight!, plan!);
            services = null;
            loggerFactory = null;
            return (deployment, ExitCodes.Ok);
        }
        finally
        {
            services?.Dispose();
            loggerFactory?.Dispose();
        }
    }

    public void Dispose()
    {
        Services.Dispose();
        _loggerFactory.Dispose();
    }

    private static async Task<(int? ExitCode, GraphPreflightResult? Preflight, DeploymentPlan? Plan)> PlanCoreAsync(
        DeploymentInvocation invocation,
        EngineServices services,
        TemplateDocument document,
        string name,
        ResourceTypeCatalog catalog,
        RunVerb verb,
        DeploymentMode mode,
        PlanMode planMode,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        // 2. Offline validation. Key Vault references are resolved after preflight, so the what-if promotion rules apply here;
        //    PlanAsync repeats the validation with the real mode.
        try
        {
            services.CreatePlanner(new PlannerOptions { DeploymentName = name, Catalog = catalog }).Validate(document, PlanMode.WhatIf);
        }
        catch (ValidationFailedException ex)
        {
            return (FailValidation(invocation, ex.Diagnostics, stdout, stderr, ExitCodes.ValidationFailed), null, null);
        }

        // A user sign-in never falls back to .default: without the run's scopes (the template did not expand offline) it stops here, before any sign-in.
        if (services.UsesDelegatedSignIn && !services.HasRunScopes)
        {
            var expansion = OfflineTemplateExpansion.Run(document, catalog, LinkedTemplateDownloads.Reader);
            return (FailValidation(invocation, expansion.Errors, stdout, stderr, ExitCodes.ValidationFailed), null, null);
        }

        // 3. Preflight: sign-in, tenant, signed-in principal and the one /organization read (tenant info for tenant()).
        GraphPreflightResult preflight;
        try
        {
            preflight = await services.Preflight.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GraphTransportException or GraphException)
        {
            // A missing delegated consent (non-interactive run, declined prompt, scp check) has its own code; nothing has been written.
            string code = ConsentRequiredException.FindIn(ex) is null ? "PreflightFailed" : "ConsentRequired";
            return (VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.AuthOrPreflight, code, "Preflight failed: " + SecretScrubber.Scrub(ex.Message)), null, null);
        }

        // 4. Key Vault parameter references, with a Key Vault token of the same identity.
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
                return (FailValidation(invocation, [Diagnostic.Error(ex.Code, ex.Message, definition?.Path, document.LocationOf(definition?.Path), document.SourcePath)], stdout, stderr, ExitCodes.AuthOrPreflight), null, null);
            }
        }

        // 5. Plan (identity by live key). tenant(), deployer() and environment() come from the preflight; an unreadable organization keeps tenant() unknown.
        //    Only delete confirms an empty key read with extra reads: it deletes what this plan finds. what-if and create plan on the first read; the
        //    executor of create repeats the full check right before every POST, so a create planned on a false "absent" is adopted, never duplicated.
        var planner = services.CreatePlanner(new PlannerOptions
        {
            ConfirmKeyAbsence = verb == RunVerb.Delete,
            DeploymentName = name,
            TenantId = preflight.TenantId,
            Tenant = DeploymentEnvironment.TenantFromOrganization(preflight.Organization, preflight.TenantId),
            Deployer = DeploymentEnvironment.Deployer(preflight),
            Environment = DeploymentEnvironment.Environment(preflight.Cloud),
            SignedInPrincipalId = preflight.PrincipalObjectId,
            AppOnlyToken = string.Equals(preflight.PrincipalType, "application", StringComparison.OrdinalIgnoreCase),
            Parallelism = invocation.Parallelism,
            AllowCaEnforce = invocation.AllowCaEnforce,
            OnSoftDeletedConflict = invocation.OnSoftDeletedConflict,
            Catalog = catalog,
        });

        try
        {
            var plan = await planner.PlanAsync(document, mode, planMode, cancellationToken).ConfigureAwait(false);
            return (null, preflight, plan);
        }
        catch (ValidationFailedException ex)
        {
            return (FailValidation(invocation, ex.Diagnostics, stdout, stderr, ExitCodes.ValidationFailed), null, null);
        }
        catch (UnknownProviderException ex)
        {
            return (VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "UnknownProvider", ex.Message), null, null);
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
