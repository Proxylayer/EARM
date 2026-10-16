using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EArm.Cli.Output;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Loading;
using EArm.Core.Types;
using EArm.Engine.Permissions;
using EArm.Providers;

namespace EArm.Cli.Commands;

/// <summary>The delegated scopes of one template run, or why they could not be computed.</summary>
/// <param name="Scopes">The run's scope set; null on failure.</param>
/// <param name="Errors">The load or validation errors; empty on success.</param>
/// <param name="ExitCode">1 when the template or a parameter file cannot be loaded or an argument is malformed, 2 for validation errors, 0 on success.</param>
/// <param name="ParameterFile">The parameter file named by <c>-p @file</c>, when one was.</param>
internal sealed record TemplateRunScopeResult(RunScopeSet? Scopes, IReadOnlyList<Diagnostic> Errors, int ExitCode, string? ParameterFile);

/// <summary>
/// Computes <see cref="RunScopes"/> for <c>-f &lt;template&gt; [-p ...] [--verb ...]</c> offline (no token): the scopes <c>auth login -f</c> consents and
/// <c>auth status -f</c> checks. Loads and expands the template the way <c>earm permissions --verb</c> does, with the same exit codes.
/// </summary>
internal static class TemplateRunScopes
{
    public static async Task<TemplateRunScopeResult> ComputeAsync(string templateFile, IReadOnlyList<string> parameterTokens, RunVerb verb, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(templateFile);
        ArgumentNullException.ThrowIfNull(parameterTokens);
        ParameterSet parameters;
        string? parameterFile;
        try
        {
            parameters = ValidateCommand.BuildParameterSet(parameterTokens, out parameterFile);
        }
        catch (TemplateLoadException ex)
        {
            return Failed([ex.ToDiagnostic()], ExitCodes.Failure, null);
        }
        catch (ArgumentException ex)
        {
            return Failed([Diagnostic.Error(ValidateCommand.InvalidArgumentCode, ex.Message)], ExitCodes.Failure, null);
        }

        var catalog = ResourceTypeCatalog.Default;
        TemplateDocument document;
        try
        {
            document = await new TemplateLoader(catalog).LoadAsync(templateFile, parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (TemplateLoadException ex)
        {
            return Failed([ex.ToDiagnostic()], ExitCodes.Failure, parameterFile);
        }

        var expansion = OfflineTemplateExpansion.Run(document, catalog);
        if (!expansion.Succeeded)
        {
            return Failed(expansion.Errors, ExitCodes.ValidationFailed, parameterFile);
        }

        var scopes = RunScopes.Compute(expansion, ProviderDiscovery.CreateRegistry(catalog), verb);
        return new TemplateRunScopeResult(scopes, [], ExitCodes.Ok, parameterFile);
    }

    /// <summary>Writes the errors of a failed computation the way <c>earm permissions</c> does (a validation report) and returns its exit code.</summary>
    public static int WriteFailure(string templateFile, TemplateRunScopeResult result, OutputFormat format, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(result);
        var report = new ValidationReport { TemplateFile = templateFile, ParameterFile = result.ParameterFile };
        ValidationWriter.Fill(report, result.Errors);
        ValidationWriter.Write(report, result.Errors, templateFile, format, stdout, stderr);
        return result.ExitCode;
    }

    /// <summary>The <c>--verb</c> value (<c>create</c>, <c>whatif</c>, <c>delete</c>; case-insensitive) as a <see cref="RunVerb"/>.</summary>
    public static RunVerb ParseVerb(string? text)
    {
        return text?.ToLowerInvariant() switch
        {
            "whatif" => RunVerb.WhatIf,
            "delete" => RunVerb.Delete,
            _ => RunVerb.Create,
        };
    }

    private static TemplateRunScopeResult Failed(IReadOnlyList<Diagnostic> errors, int exitCode, string? parameterFile)
    {
        return new TemplateRunScopeResult(null, errors, exitCode, parameterFile);
    }
}
