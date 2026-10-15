using System;
using System.Collections.Generic;
using System.IO;
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
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Graph;
using Microsoft.Extensions.Logging;

namespace EArm.Cli.Commands;

/// <summary>
/// The setup shared by the online verbs (<c>what-if</c>, <c>create</c>, <c>delete</c>): parameters and template (exit 1 when they cannot be loaded) →
/// engine services (exit 3 when no credential can be built) → offline validation (exit 2; before preflight, so a broken template is reported
/// without a sign-in) → preflight (exit 3 when no credential signs in) → Key Vault parameter references (exit 3) → plan (exit 2 on validation
/// errors, exit 1 when a type has no provider). Identity is resolved by live key; nothing is read from or written to the local disk. Owns the
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
    /// Runs the setup and plans the template with <paramref name="mode"/> and <paramref name="planMode"/>. Returns the planned deployment, or
    /// <c>null</c> with the exit code of the step that failed (its message is already written).
    /// </summary>
    public static async Task<(PlannedDeployment? Deployment, int ExitCode)> OpenAsync(DeploymentInvocation invocation, DeploymentMode mode, PlanMode planMode, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        // 1. Parameters and template (offline).
        ParameterSet parameters;
        try
        {
            parameters = ValidateCommand.BuildParameterSet(invocation.ParameterTokens, out _);
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

        SecretRegistration.RegisterDocument(document);

        ILoggerFactory? loggerFactory = GlobalOptions.CreateLoggerFactory(invocation.LogLevel);
        EngineServices? services = null;
        try
        {
            if (!EngineServices.TryCreate(invocation.Connection, loggerFactory, stdout, stderr, invocation.Output, out services, out int createExit))
            {
                return (null, createExit);
            }

            var (exit, preflight, plan) = await PlanCoreAsync(invocation, services!, document, name, catalog, mode, planMode, stdout, stderr, cancellationToken).ConfigureAwait(false);
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

        // 3. Preflight: sign-in, tenant, signed-in principal and the one /organization read (tenant info for tenant()).
        GraphPreflightResult preflight;
        try
        {
            preflight = await services.Preflight.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GraphTransportException or GraphException)
        {
            return (VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.AuthOrPreflight, "PreflightFailed", "Preflight failed: " + SecretScrubber.Scrub(ex.Message)), null, null);
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

        // 5. Plan (identity by live key). tenant() and environment() come from the preflight; an unreadable organization keeps tenant() unknown.
        var planner = services.CreatePlanner(new PlannerOptions
        {
            DeploymentName = name,
            TenantId = preflight.TenantId,
            Tenant = DeploymentEnvironment.TenantFromOrganization(preflight.Organization, preflight.TenantId),
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
