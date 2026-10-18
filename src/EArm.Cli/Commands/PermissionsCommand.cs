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
using EArm.Core.Diagnostics;
using EArm.Core.Types;
using EArm.Engine.Permissions;
using EArm.Providers;

namespace EArm.Cli.Commands;

/// <summary>
/// <c>earm permissions -f &lt;template&gt; [-p ...] [--mode app|delegated] [--verb whatif|create|delete] [-o table|json]</c>:
/// the Microsoft Graph permissions (and, for delegated use, the Entra roles) a template needs, computed
/// offline from the expanded resources. They cover both deployment modes: Complete mode never deletes, it only reads the
/// declared types to report undeclared objects. With <c>--verb</c>, the delegated scopes one run of that verb requests
/// (<see cref="RunScopes"/>).
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

        var parameters = ParameterArguments.CreateOption();

        var mode = new Option<string>("--mode")
        {
            Description = "app: Microsoft Graph application permissions of a service principal login. delegated: delegated scopes plus the Entra roles of a user login.",
            DefaultValueFactory = _ => "app",
        };
        mode.AcceptOnlyFromAmongIgnoreCase("app", "delegated");

        var verb = new Option<string?>("--verb")
        {
            Description = "whatif|create|delete: the delegated scopes one run of that verb requests for a signed-in user (implies --mode delegated).",
        };
        verb.AcceptOnlyFromAmongIgnoreCase("whatif", "create", "delete");

        var command = new Command("permissions", "Lists the Microsoft Graph permissions and Entra roles a template needs, offline (no tenant access).");
        command.Options.Add(templateFile);
        command.Options.Add(parameters);
        command.Options.Add(mode);
        command.Options.Add(verb);
        command.SetAction((parseResult, ct) =>
        {
            bool explicitApp = parseResult.GetResult(mode) is { Implicit: false } && string.Equals(parseResult.GetValue(mode), "app", StringComparison.OrdinalIgnoreCase);
            string? verbText = parseResult.GetValue(verb);
            return RunAsync(
                parseResult.GetRequiredValue(templateFile),
                parseResult.GetValue(parameters) ?? [],
                string.Equals(parseResult.GetValue(mode), "delegated", StringComparison.OrdinalIgnoreCase) ? PermissionMode.Delegated : PermissionMode.App,
                GlobalOptions.GetOutputFormat(parseResult),
                parseResult.InvocationConfiguration.Output,
                parseResult.InvocationConfiguration.Error,
                ct,
                verbText is null ? null : ParseVerb(verbText),
                explicitApp);
        });
        return command;
    }

    private static RunVerb ParseVerb(string text)
    {
        return text.ToLowerInvariant() switch
        {
            "create" => RunVerb.Create,
            "delete" => RunVerb.Delete,
            _ => RunVerb.WhatIf,
        };
    }

    internal static async Task<int> RunAsync(string templateFile, IReadOnlyList<string> parameterTokens, PermissionMode mode, OutputFormat format, TextWriter stdout, TextWriter stderr, CancellationToken ct, RunVerb? verb = null, bool explicitAppMode = false)
    {
        var report = new ValidationReport { TemplateFile = templateFile };
        if (verb is not null && explicitAppMode)
        {
            return Fail(report, [Diagnostic.Error(ValidateCommand.InvalidArgumentCode, "--verb lists the delegated scopes of a user sign-in; it cannot be combined with --mode app.")], format, stdout, stderr, ExitCodes.Failure);
        }

        var expanded = await TemplateRunScopes.ExpandAsync(templateFile, parameterTokens, ct).ConfigureAwait(false);
        report.ParameterFile = expanded.ParameterFile;
        if (expanded.Expansion is not { } expansion)
        {
            return Fail(report, expanded.Errors, format, stdout, stderr, expanded.ExitCode);
        }

        if (verb is { } runVerb)
        {
            WriteScopes(templateFile, TemplateRunScopes.Compute(expansion, runVerb), format, stdout);
            return ExitCodes.Ok;
        }

        Write(PermissionManifest.Build(templateFile, mode, expansion, ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default)), format, stdout);
        return ExitCodes.Ok;
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

    internal static void WriteScopes(string template, RunScopeSet scopes, OutputFormat format, TextWriter stdout)
    {
        switch (format)
        {
            case OutputFormat.None:
                return;
            case OutputFormat.Json:
                stdout.WriteLine(ToJson(template, scopes).ToJsonString(new JsonSerializerOptions { WriteIndented = true, MaxDepth = JsonOutput.MaxDepth, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                return;
            default:
                stdout.WriteLine($"Delegated scopes a {VerbName(scopes.Verb)} run of {template} requests ({scopes.Scopes.Count} scope(s))");
                stdout.WriteLine();
                stdout.WriteLine("Microsoft Graph delegated scopes");
                foreach (var scope in scopes.Scopes)
                {
                    stdout.WriteLine("  " + scope.Name);
                    WriteResources(stdout, scope.Resources, scope.Reasons);
                }

                stdout.WriteLine();
                stdout.WriteLine(scopes.NeedsKeyVault
                    ? "Azure Key Vault: https://vault.azure.net/user_impersonation (the template has Key Vault parameter references)"
                    : "Azure Key Vault: not needed");
                return;
        }
    }

    internal static JsonObject ToJson(string template, RunScopeSet scopes)
    {
        var details = new JsonArray();
        foreach (var scope in scopes.Scopes)
        {
            details.Add(new JsonObject
            {
                ["name"] = scope.Name,
                ["resources"] = new JsonArray(scope.Resources.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
                ["reasons"] = new JsonArray(scope.Reasons.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
            });
        }

        return new JsonObject
        {
            ["template"] = template,
            ["verb"] = VerbName(scopes.Verb),
            ["graphScopes"] = new JsonArray(scopes.GraphScopes.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            ["needsKeyVault"] = scopes.NeedsKeyVault,
            ["scopes"] = details,
        };
    }

    private static string VerbName(RunVerb verb)
    {
        return verb switch
        {
            RunVerb.WhatIf => "whatif",
            RunVerb.Create => "create",
            RunVerb.Delete => "delete",
            _ => "validate",
        };
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
            var entry = new JsonObject
            {
                ["anyOf"] = new JsonArray(role.AnyOf.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
            };

            // Roles needed together: anyOf is empty and allOf lists the parts (each part an array of alternatives); plain groups carry no allOf.
            if (role.IsTogether)
            {
                entry["allOf"] = new JsonArray(role.AllOf.Select(p => (JsonNode?)new JsonArray(p.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray())).ToArray());
            }

            entry["resources"] = new JsonArray(role.Resources.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray());
            entry["reasons"] = new JsonArray(role.Reasons.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray());
            roles.Add(entry);
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
                writer.WriteLine("  " + RoleLine(role));
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

    /// <summary><c>A | B</c> for alternatives; <c>together: A + (B | C)</c> for roles needed together.</summary>
    internal static string RoleLine(RoleRequirement role)
    {
        return role.IsTogether
            ? "together: " + string.Join(" + ", role.AllOf.Select(p => p.Count == 1 ? p[0] : "(" + string.Join(" | ", p) + ")"))
            : string.Join(" | ", role.AnyOf);
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
