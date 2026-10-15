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
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Loading;
using EArm.Core.Types;
using EArm.Engine.Execution;
using EArm.Engine.KeyVault;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Graph;

namespace EArm.Cli.Commands;

/// <summary>
/// <c>earm deployment delete -f &lt;template&gt; [-p ...] [-n name] [-y] [--continue-on-error]</c>: deletes the objects the template declares, found by
/// their live keys exactly like <c>what-if</c> finds them (no state is involved), in reverse dependency order, after listing them and asking for
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
        command.Options.Add(yes);
        command.Options.Add(continueOnError);
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
        try
        {
            services.CreatePlanner(new PlannerOptions { DeploymentName = name, Catalog = catalog }).Validate(document, PlanMode.WhatIf);
        }
        catch (ValidationFailedException ex)
        {
            return FailValidation(invocation, ex.Diagnostics, stdout, stderr, ExitCodes.ValidationFailed);
        }

        GraphPreflightResult preflight;
        try
        {
            preflight = await services.Preflight.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GraphTransportException or GraphException)
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.AuthOrPreflight, "PreflightFailed", "Preflight failed: " + SecretScrubber.Scrub(ex.Message));
        }

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

        // The declared objects are found exactly as what-if finds them: by key, read-only.
        var planner = services.CreatePlanner(new PlannerOptions
        {
            DeploymentName = name,
            TenantId = preflight.TenantId,
            Tenant = DeploymentEnvironment.TenantFromOrganization(preflight.Organization, preflight.TenantId),
            Environment = DeploymentEnvironment.Environment(preflight.Cloud),
            SignedInPrincipalId = preflight.PrincipalObjectId,
            AppOnlyToken = string.Equals(preflight.PrincipalType, "application", StringComparison.OrdinalIgnoreCase),
            Parallelism = invocation.Parallelism,
            Catalog = catalog,
        });

        DeploymentPlan plan;
        try
        {
            plan = await planner.PlanAsync(document, null, DeploymentMode.Incremental, PlanMode.WhatIf, cancellationToken).ConfigureAwait(false);
        }
        catch (ValidationFailedException ex)
        {
            return FailValidation(invocation, ex.Diagnostics, stdout, stderr, ExitCodes.ValidationFailed);
        }
        catch (UnknownProviderException ex)
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "UnknownProvider", ex.Message);
        }

        var targets = TemplateDeleter.FindDeclaredObjects(plan);
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
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "NotConfirmed", "Nothing deleted.");
        }

        var result = await services.CreateTemplateDeleter().DeleteAsync(plan, targets, invocation.ContinueOnError, cancellationToken).ConfigureAwait(false);
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

    private static int FailValidation(DeploymentInvocation invocation, IReadOnlyList<Diagnostic> diagnostics, TextWriter stdout, TextWriter stderr, int exitCode)
    {
        var report = new ValidationReport { TemplateFile = invocation.TemplateFile };
        ValidationWriter.Fill(report, diagnostics);
        ValidationWriter.Write(report, diagnostics, invocation.TemplateFile, invocation.Output, stdout, stderr);
        return exitCode;
    }
}
