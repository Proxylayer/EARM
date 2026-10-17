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

/// <summary>A template loaded with its parameters and expanded offline (no token), or why it could not be.</summary>
/// <param name="Expansion">The offline expansion; null on failure.</param>
/// <param name="Errors">The load or validation errors; empty on success.</param>
/// <param name="ExitCode">1 when the template or a parameter file cannot be loaded or an argument is malformed, 2 for validation errors, 0 on success.</param>
/// <param name="ParameterFile">The parameter file named by <c>-p @file</c>, when one was.</param>
internal sealed record TemplateExpansionResult(OfflineTemplateExpansion? Expansion, IReadOnlyList<Diagnostic> Errors, int ExitCode, string? ParameterFile);

/// <summary>
/// Computes <see cref="RunScopes"/> for <c>-f &lt;template&gt; [-p ...] [--verb ...]</c> offline (no token): the scopes <c>auth login -f</c> consents,
/// <c>auth status -f</c> checks and the online verbs request. Loads and expands the template the way <c>earm permissions</c> does (which uses
/// <see cref="ExpandAsync"/> itself), with the same exit codes.
/// </summary>
internal static class TemplateRunScopes
{
    public static async Task<TemplateRunScopeResult> ComputeAsync(string templateFile, IReadOnlyList<string> parameterTokens, RunVerb verb, CancellationToken cancellationToken)
    {
        var expanded = await ExpandAsync(templateFile, parameterTokens, cancellationToken).ConfigureAwait(false);
        return expanded.Expansion is null
            ? Failed(expanded.Errors, expanded.ExitCode, expanded.ParameterFile)
            : new TemplateRunScopeResult(Compute(expanded.Expansion, verb), [], ExitCodes.Ok, expanded.ParameterFile);
    }

    /// <summary>Loads <paramref name="templateFile"/> with the parameters of <paramref name="parameterTokens"/> and expands it offline.</summary>
    public static async Task<TemplateExpansionResult> ExpandAsync(string templateFile, IReadOnlyList<string> parameterTokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(templateFile);
        ArgumentNullException.ThrowIfNull(parameterTokens);
        ParameterSet parameters;
        string? parameterFile;
        try
        {
            (parameters, parameterFile) = await ParameterArguments.BuildAsync(parameterTokens, cancellationToken).ConfigureAwait(false);
        }
        catch (TemplateLoadException ex)
        {
            return new TemplateExpansionResult(null, [ex.ToDiagnostic()], ExitCodes.Failure, null);
        }
        catch (ArgumentException ex)
        {
            return new TemplateExpansionResult(null, [Diagnostic.Error(ValidateCommand.InvalidArgumentCode, ex.Message)], ExitCodes.Failure, null);
        }

        var catalog = ResourceTypeCatalog.Default;
        TemplateDocument document;
        try
        {
            document = await new TemplateLoader(catalog).LoadAsync(templateFile, parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (TemplateLoadException ex)
        {
            return new TemplateExpansionResult(null, [ex.ToDiagnostic()], ExitCodes.Failure, parameterFile);
        }

        var expansion = OfflineTemplateExpansion.Run(document, catalog);
        return expansion.Succeeded
            ? new TemplateExpansionResult(expansion, [], ExitCodes.Ok, parameterFile)
            : new TemplateExpansionResult(null, expansion.Errors, ExitCodes.ValidationFailed, parameterFile);
    }

    /// <summary>The scopes of an already loaded template (the online verbs: parameters bound, Key Vault references not resolved yet); null when it does not expand offline.</summary>
    public static RunScopeSet? Compute(TemplateDocument document, RunVerb verb)
    {
        ArgumentNullException.ThrowIfNull(document);
        var expansion = OfflineTemplateExpansion.Run(document, ResourceTypeCatalog.Default);
        return expansion.Succeeded ? Compute(expansion, verb) : null;
    }

    /// <summary>The scopes of <paramref name="verb"/> for an offline expansion, with the discovered providers.</summary>
    public static RunScopeSet Compute(OfflineTemplateExpansion expansion, RunVerb verb)
    {
        ArgumentNullException.ThrowIfNull(expansion);
        return RunScopes.Compute(expansion, ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default), verb);
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
