using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EArm.Cli.Output;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Loading;
using EArm.Core.Types;
using EArm.Core.Validation;
using EArm.Expressions.Validation;

namespace EArm.Cli.Commands;

/// <summary>
/// <c>earm deployment validate -f &lt;file&gt; [-p @file|name=value ...] [-o table|json]</c>: offline validation.
/// Exit 0 when there are no errors, 2 on validation errors, 1 when the template or a parameter file cannot be
/// loaded or an argument is malformed.
/// </summary>
internal static class ValidateCommand
{
    /// <summary>Diagnostic code for malformed command-line arguments (CLI-level, not a template finding).</summary>
    public const string InvalidArgumentCode = "InvalidArgument";

    public static Command Build()
    {
        var templateFile = new Option<string>("--template-file", "-f")
        {
            Description = "Path to the template (*.entra.json).",
            Required = true,
        };

        var parameters = new Option<string[]>("--parameters", "-p")
        {
            Description = "Parameter values: @file.json (ARM deploymentParameters shape) or name=value. Repeatable; later values win.",
            AllowMultipleArgumentsPerToken = false,
            DefaultValueFactory = _ => [],
        };

        var command = new Command("validate", "Validates a template offline (JSON Schema, type catalog, semantic checks); no tenant access.");
        command.Options.Add(templateFile);
        command.Options.Add(parameters);
        command.SetAction((parseResult, ct) => RunAsync(
            parseResult.GetRequiredValue(templateFile),
            parseResult.GetValue(parameters) ?? [],
            GlobalOptions.GetOutputFormat(parseResult),
            parseResult.InvocationConfiguration.Output,
            parseResult.InvocationConfiguration.Error,
            ct));
        return command;
    }

    internal static async Task<int> RunAsync(string templateFile, IReadOnlyList<string> parameterTokens, OutputFormat format, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var report = new ValidationReport { TemplateFile = templateFile };

        ParameterSet parameters;
        try
        {
            parameters = BuildParameterSet(parameterTokens, out string? parameterFile);
            report.ParameterFile = parameterFile;
        }
        catch (TemplateLoadException ex)
        {
            return Fail(report, [ex.ToDiagnostic()], format, stdout, stderr);
        }
        catch (ArgumentException ex)
        {
            return Fail(report, [Diagnostic.Error(InvalidArgumentCode, ex.Message)], format, stdout, stderr);
        }

        var catalog = ResourceTypeCatalog.Default;
        var loader = new TemplateLoader(catalog);
        TemplateDocument document;
        try
        {
            document = await loader.LoadAsync(templateFile, parameters, ct).ConfigureAwait(false);
        }
        catch (TemplateLoadException ex)
        {
            return Fail(report, [ex.ToDiagnostic()], format, stdout, stderr);
        }

        // Core checks (schema, catalog, literal semantics) first, then the expression-dependent checks
        // (syntax, functions, static evaluation of names/dependsOn/outputs) in the same offline context.
        var diagnostics = new List<Diagnostic>(new TemplateValidator(catalog).Validate(document));
        var analysis = new ExpressionValidator(catalog).Analyze(document);
        diagnostics.AddRange(analysis.Diagnostics);
        diagnostics.Sort(DiagnosticOrder.Instance);

        foreach (var resource in document.Resources)
        {
            var resolved = analysis.Static.Find(resource);
            report.Resources.Add(new ResourceReport
            {
                Type = resource.Type,
                ApiVersion = resource.ApiVersion,
                Name = resource.Name,
                ResourceId = resource.StaticId?.Id ?? (resolved is { IsStatic: true } ? resolved.Id?.Id : null),
                Nested = resource.IsNested,
                DependsOn = [.. resource.DependsOn],
                InferredDependsOn = [.. resource.InferredDependsOn],
                Path = resource.Path.ToString(),
            });
        }

        ValidationWriter.Fill(report, diagnostics);
        ValidationWriter.Write(report, diagnostics, templateFile, format, stdout, stderr);
        return report.ErrorCount > 0 ? ExitCodes.ValidationFailed : ExitCodes.Ok;
    }

    private static int Fail(ValidationReport report, IReadOnlyList<Diagnostic> diagnostics, OutputFormat format, TextWriter stdout, TextWriter stderr)
    {
        ValidationWriter.Fill(report, diagnostics);
        ValidationWriter.Write(report, diagnostics, report.TemplateFile ?? string.Empty, format, stdout, stderr);
        return ExitCodes.Failure;
    }

    /// <summary>Builds the parameter set from <c>-p</c> tokens in command-line order (<c>@file</c> loads a file, <c>name=value</c> sets one value); a later value wins, as with <c>az deployment group create --parameters</c>.</summary>
    internal static ParameterSet BuildParameterSet(IReadOnlyList<string> tokens, out string? parameterFile)
    {
        parameterFile = null;
        var set = ParameterSet.Empty;
        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            if (token.StartsWith('@'))
            {
                string path = token[1..];
                set = set.Merge(ParameterSet.FromFile(path));
                parameterFile = path;
                continue;
            }

            if (!ParameterSet.TryParseInline(token, out string name, out string rawText))
            {
                throw new ArgumentException($"Parameter argument '{token}' is not valid; expected '@file.json' or 'name=value'.");
            }

            set = set.WithInline(name, rawText);
        }

        return set;
    }
}
