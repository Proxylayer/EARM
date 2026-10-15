using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Cli.Output;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Loading;
using EArm.Core.Types;
using EArm.Core.Validation;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Expressions;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Validation;
using EArm.Providers;

namespace EArm.Cli.Commands;

/// <summary>
/// <c>earm permissions -f &lt;template&gt; [-p ...] [--mode app|delegated] [-o table|json]</c>:
/// the Microsoft Graph permissions (and, for delegated use, the Entra roles) a template needs, computed
/// offline from the expanded resources. They cover both deployment modes: Complete mode never deletes, it only reads the
/// declared types to report undeclared objects.
/// Exit 0; 2 when the template has validation errors; 1 when it or a parameter
/// file cannot be loaded or an argument is malformed.
/// </summary>
internal static class PermissionsCommand
{
    public static Command Build()
    {
        var templateFile = new Option<string>("--template-file", "-f")
        {
            Description = "Path to the template (*.entra.json).",
            Required = true,
        };

        var parameters = new Option<string[]>("--parameters", "-p")
        {
            Description = "Parameter values: @file.json (ARM deploymentParameters shape) or name=value. Repeatable; later values win. Values decide copy counts and conditions.",
            AllowMultipleArgumentsPerToken = false,
            DefaultValueFactory = _ => [],
        };

        var mode = new Option<string>("--mode")
        {
            Description = "app: Microsoft Graph application permissions of a service principal login. delegated: delegated scopes plus the Entra roles of a user login.",
            DefaultValueFactory = _ => "app",
        };
        mode.AcceptOnlyFromAmong("app", "delegated");

        var command = new Command("permissions", "Lists the Microsoft Graph permissions and Entra roles a template needs, offline (no tenant access).");
        command.Options.Add(templateFile);
        command.Options.Add(parameters);
        command.Options.Add(mode);
        command.SetAction((parseResult, ct) => RunAsync(
            parseResult.GetRequiredValue(templateFile),
            parseResult.GetValue(parameters) ?? [],
            string.Equals(parseResult.GetValue(mode), "delegated", StringComparison.OrdinalIgnoreCase) ? PermissionMode.Delegated : PermissionMode.App,
            GlobalOptions.GetOutputFormat(parseResult),
            parseResult.InvocationConfiguration.Output,
            parseResult.InvocationConfiguration.Error,
            ct));
        return command;
    }

    internal static async Task<int> RunAsync(string templateFile, IReadOnlyList<string> parameterTokens, PermissionMode mode, OutputFormat format, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var report = new ValidationReport { TemplateFile = templateFile };

        ParameterSet parameters;
        try
        {
            parameters = ValidateCommand.BuildParameterSet(parameterTokens, out string? parameterFile);
            report.ParameterFile = parameterFile;
        }
        catch (TemplateLoadException ex)
        {
            return Fail(report, [ex.ToDiagnostic()], format, stdout, stderr, ExitCodes.Failure);
        }
        catch (ArgumentException ex)
        {
            return Fail(report, [Diagnostic.Error(ValidateCommand.InvalidArgumentCode, ex.Message)], format, stdout, stderr, ExitCodes.Failure);
        }

        var catalog = ResourceTypeCatalog.Default;
        TemplateDocument document;
        try
        {
            document = await new TemplateLoader(catalog).LoadAsync(templateFile, parameters, ct).ConfigureAwait(false);
        }
        catch (TemplateLoadException ex)
        {
            return Fail(report, [ex.ToDiagnostic()], format, stdout, stderr, ExitCodes.Failure);
        }

        // Offline validation first: the permissions of a broken template are not meaningful.
        var diagnostics = new List<Diagnostic>(new TemplateValidator(catalog).Validate(document));
        var analysis = new ExpressionValidator(catalog).Analyze(document);
        diagnostics.AddRange(analysis.Diagnostics);
        if (diagnostics.Any(d => d.IsError))
        {
            diagnostics.Sort(DiagnosticOrder.Instance);
            return Fail(report, diagnostics, format, stdout, stderr, ExitCodes.ValidationFailed);
        }

        // Expand copy loops and conditions with the offline context, then wire the dependency graph so the providers
        // can see what each instance references (the planner does the same before it asks for permissions).
        var evaluator = ExpressionEvaluator.Default;
        var expansionDiagnostics = new List<Diagnostic>();
        var instances = new ResourceExpander(evaluator, catalog).Expand(analysis.Static, analysis.Static.Context, expansionDiagnostics);
        if (!expansionDiagnostics.Any(d => d.IsError))
        {
            DependencyGraph.Build(instances, analysis.Static, evaluator, catalog, expansionDiagnostics);
        }

        if (expansionDiagnostics.Any(d => d.IsError))
        {
            expansionDiagnostics.Sort(DiagnosticOrder.Instance);
            return Fail(report, expansionDiagnostics, format, stdout, stderr, ExitCodes.ValidationFailed);
        }

        int ignored = instances.Count(i => i.IsIgnored);
        var active = instances.Where(i => !i.IsIgnored).ToList();
        foreach (var instance in active)
        {
            try
            {
                InstanceEvaluator.EvaluateProperties(evaluator, instance);
            }
            catch (ExpressionException)
            {
                // Deploy-time values: the providers fall back to the declaration.
            }
        }

        var registry = ProviderDiscovery.CreateRegistry(catalog);
        var result = PermissionManifest.Build(
            templateFile,
            mode,
            active,
            registry,
            ExpressionStrings(document.Root),
            document.ParameterValues.Values
                .Where(b => b.PendingReference is not null)
                .Select(b => (b.Name, b.PendingReference!)),
            ignored);

        Write(result, format, stdout);
        return ExitCodes.Ok;
    }

    private static IEnumerable<string> ExpressionStrings(JsonNode? root)
    {
        var stack = new Stack<JsonNode?>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            switch (stack.Pop())
            {
                case JsonObject obj:
                    foreach (var pair in obj)
                    {
                        stack.Push(pair.Value);
                    }

                    break;
                case JsonArray array:
                    foreach (var item in array)
                    {
                        stack.Push(item);
                    }

                    break;
                case JsonValue value when value.TryGetValue<string>(out string? text) && text.StartsWith('['):
                    yield return text;
                    break;
            }
        }
    }

    private static int Fail(ValidationReport report, IReadOnlyList<Diagnostic> diagnostics, OutputFormat format, TextWriter stdout, TextWriter stderr, int exitCode)
    {
        ValidationWriter.Fill(report, diagnostics);
        ValidationWriter.Write(report, diagnostics, report.TemplateFile ?? string.Empty, format, stdout, stderr);
        return exitCode;
    }

    // ---- output ----

    internal static void Write(PermissionReport report, OutputFormat format, TextWriter stdout)
    {
        switch (format)
        {
            case OutputFormat.None:
                return;
            case OutputFormat.Json:
                stdout.WriteLine(ToJson(report).ToJsonString(new JsonSerializerOptions { WriteIndented = true, MaxDepth = JsonOutput.MaxDepth, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                return;
            default:
                WriteTable(report, stdout);
                return;
        }
    }

    internal static JsonObject ToJson(PermissionReport report)
    {
        var permissions = new JsonArray();
        foreach (var entry in report.Permissions)
        {
            permissions.Add(new JsonObject
            {
                ["name"] = entry.Name,
                ["kind"] = KindName(entry.Kind),
                ["resources"] = new JsonArray(entry.Resources.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
                ["reasons"] = new JsonArray(entry.Reasons.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
            });
        }

        var roles = new JsonArray();
        foreach (var role in report.Roles)
        {
            roles.Add(new JsonObject
            {
                ["anyOf"] = new JsonArray(role.AnyOf.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
                ["resources"] = new JsonArray(role.Resources.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
                ["reasons"] = new JsonArray(role.Reasons.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
            });
        }

        return new JsonObject
        {
            ["template"] = report.Template,
            ["mode"] = report.Mode == PermissionMode.App ? "app" : "delegated",
            ["resourceCount"] = report.ResourceCount,
            ["permissions"] = permissions,
            ["entraRoles"] = roles,
            ["notes"] = new JsonArray(report.Notes.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
        };
    }

    private static string KindName(PermissionKind kind)
    {
        return kind switch
        {
            PermissionKind.GraphApplication => "graphApplication",
            PermissionKind.GraphDelegated => "graphDelegated",
            _ => "azureRbac",
        };
    }

    private static void WriteTable(PermissionReport report, TextWriter writer)
    {
        string mode = report.Mode == PermissionMode.App ? "app" : "delegated";
        writer.WriteLine($"Permissions required by {report.Template} (mode: {mode}, {report.ResourceCount} resource(s))");

        WriteGroup(writer, report.Permissions.Where(p => p.Kind != PermissionKind.AzureRbac).ToList(),
            report.Mode == PermissionMode.App ? "Microsoft Graph application permissions" : "Microsoft Graph delegated permissions");

        if (report.Mode == PermissionMode.Delegated)
        {
            writer.WriteLine();
            writer.WriteLine("Entra roles (the signed-in user needs at least one role of each group)");
            if (report.Roles.Count == 0)
            {
                writer.WriteLine("  none");
            }

            foreach (var role in report.Roles)
            {
                writer.WriteLine("  " + string.Join(" | ", role.AnyOf));
                WriteResources(writer, role.Resources, role.Reasons);
            }
        }

        var azure = report.Permissions.Where(p => p.Kind == PermissionKind.AzureRbac).ToList();
        if (azure.Count > 0)
        {
            WriteGroup(writer, azure, "Azure (signed-in identity)");
        }

        writer.WriteLine();
        foreach (string note in report.Notes)
        {
            writer.WriteLine("note: " + note);
        }
    }

    private static void WriteGroup(TextWriter writer, IReadOnlyList<PermissionEntry> entries, string title)
    {
        writer.WriteLine();
        writer.WriteLine(title);
        if (entries.Count == 0)
        {
            writer.WriteLine("  none");
            return;
        }

        foreach (var entry in entries)
        {
            writer.WriteLine("  " + entry.Name);
            WriteResources(writer, entry.Resources, entry.Reasons);
        }
    }

    private static void WriteResources(TextWriter writer, IReadOnlyList<string> resources, IReadOnlyList<string> reasons)
    {
        foreach (string resource in resources)
        {
            writer.WriteLine("      " + resource);
        }

        writer.WriteLine("      (" + string.Join("; ", reasons) + ")");
    }
}
