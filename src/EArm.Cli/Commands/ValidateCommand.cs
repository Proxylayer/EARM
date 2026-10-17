using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EArm.Cli.Output;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Loading;
using EArm.Core.Types;
using EArm.Core.Validation;
using EArm.Engine;
using EArm.Engine.Permissions;
using EArm.Engine.Planning;
using EArm.Expressions.Validation;

namespace EArm.Cli.Commands;

/// <summary>
/// <c>--validation-level</c> of <c>deployment validate</c>, with the Azure CLI's names: <see cref="Template"/> (EARM's default) is the offline validation;
/// <see cref="Provider"/> and <see cref="ProviderNoRbac"/> also sign in and plan the template against the tenant (read only), with the delegated scopes of
/// <c>create</c> (write) or of <c>what-if</c> (read) respectively.
/// </summary>
internal enum ValidationLevel
{
    Template,
    Provider,
    ProviderNoRbac,
}

/// <summary>
/// <c>earm deployment validate -f &lt;file&gt; [-p ...] [--validation-level Template|Provider|ProviderNoRbac] [--no-prompt] [-o ...]</c>: offline validation,
/// plus, with <c>--validation-level Provider</c> / <c>ProviderNoRbac</c>, a read-only plan against the tenant (the steps of <c>what-if</c> up to the plan:
/// sign-in, preflight with its consent check, Key Vault references, live reads) whose findings are added to the report; nothing is written.
/// Exit 0 when there are no errors, 2 on validation errors, 1 when the template or a parameter file cannot be loaded or an argument is malformed,
/// 3 when the online level cannot sign in.
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

        var parameters = ParameterArguments.CreateOption();

        var validationLevel = new Option<string>("--validation-level")
        {
            Description = "Template (default): offline validation, no tenant access. Provider: also sign in and plan against the tenant (read only), checking consent for the write scopes create needs. ProviderNoRbac: the same with the read-only scopes of what-if.",
            DefaultValueFactory = _ => nameof(ValidationLevel.Template),
        };
        validationLevel.AcceptOnlyFromAmongIgnoreCase(nameof(ValidationLevel.Provider), nameof(ValidationLevel.ProviderNoRbac), nameof(ValidationLevel.Template));

        var noPrompt = DeploymentOptions.CreateNoPrompt();

        var command = new Command("validate", "Validates a template offline (JSON Schema, type catalog, semantic checks); with --validation-level Provider also against the tenant (read only).");
        command.Options.Add(templateFile);
        command.Options.Add(parameters);
        command.Options.Add(validationLevel);
        command.Options.Add(noPrompt);
        command.SetAction((parseResult, ct) =>
        {
            string file = parseResult.GetRequiredValue(templateFile);
            var tokens = parseResult.GetValue(parameters) ?? [];
            var level = Enum.Parse<ValidationLevel>(parseResult.GetValue(validationLevel) ?? nameof(ValidationLevel.Template), ignoreCase: true);
            var format = GlobalOptions.GetOutputFormat(parseResult);
            DeploymentInvocation? online = level == ValidationLevel.Template ? null : new DeploymentInvocation
            {
                TemplateFile = file,
                ParameterTokens = tokens,
                NoPrompt = true,
                Output = format,
                Connection = GlobalOptions.BuildConnection(parseResult, 4),
                LogLevel = GlobalOptions.GetLogLevel(parseResult),
            };
            return RunAsync(file, tokens, format, parseResult.InvocationConfiguration.Output, parseResult.InvocationConfiguration.Error, ct, level, online);
        });
        return command;
    }

    internal static async Task<int> RunAsync(
        string templateFile,
        IReadOnlyList<string> parameterTokens,
        OutputFormat format,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken ct,
        ValidationLevel level = ValidationLevel.Template,
        DeploymentInvocation? online = null)
    {
        var report = new ValidationReport { TemplateFile = templateFile };

        ParameterSet parameters;
        try
        {
            (parameters, report.ParameterFile) = await ParameterArguments.BuildAsync(parameterTokens, ct).ConfigureAwait(false);
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

        // --validation-level Provider / ProviderNoRbac: the read-only plan against the tenant, once the offline checks pass.
        if (level != ValidationLevel.Template && online is not null && !diagnostics.Any(d => d.IsError))
        {
            var (planned, exit) = await PlannedDeployment.OpenAsync(online, level == ValidationLevel.Provider ? RunVerb.Create : RunVerb.WhatIf, online.Mode, PlanMode.WhatIf, stdout, stderr, ct).ConfigureAwait(false);
            if (planned is null)
            {
                // The failure (validation, sign-in, Key Vault) is already reported in the output format asked for.
                return exit;
            }

            using (planned)
            {
                diagnostics.AddRange(OnlineFindings(planned.Plan, diagnostics));
            }
        }

        diagnostics.Sort(DiagnosticOrder.Instance);
        ValidationWriter.Fill(report, diagnostics);
        ValidationWriter.Write(report, diagnostics, templateFile, format, stdout, stderr);
        return report.ErrorCount > 0 ? ExitCodes.ValidationFailed : ExitCodes.Ok;
    }

    /// <summary>The plan's own diagnostics (those the offline pass did not report) and one error per change that cannot be applied as planned.</summary>
    private static IEnumerable<Diagnostic> OnlineFindings(DeploymentPlan plan, IReadOnlyList<Diagnostic> offline)
    {
        var known = new HashSet<(string, string)>(offline.Select(d => (d.Code, d.Message)));
        foreach (var diagnostic in plan.Diagnostics)
        {
            if (known.Add((diagnostic.Code, diagnostic.Message)))
            {
                yield return diagnostic;
            }
        }

        foreach (var change in plan.Changes.Where(c => c.HasError))
        {
            yield return Diagnostic.Error(change.UnsupportedCode ?? EngineDiagnosticCodes.ImmutablePropertyChanged, change.UnsupportedReason!);
        }
    }

    private static int Fail(ValidationReport report, IReadOnlyList<Diagnostic> diagnostics, OutputFormat format, TextWriter stdout, TextWriter stderr)
    {
        ValidationWriter.Fill(report, diagnostics);
        ValidationWriter.Write(report, diagnostics, report.TemplateFile ?? string.Empty, format, stdout, stderr);
        return ExitCodes.Failure;
    }
}
