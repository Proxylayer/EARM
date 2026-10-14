using System;
using System.CommandLine;
using System.CommandLine.Help;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Core;
using EntraArm.Core.Types;

namespace EntraArm.Cli.Commands;

/// <summary>
/// The <c>entraarm schema</c> command group (plan sections 1.9 and 1.10): <c>print [template|parameters|&lt;type&gt;]</c>
/// writes an embedded JSON Schema to standard output; <c>install</c> extracts the schemas next to the user and maps them
/// to <c>*.entra.json</c> in VS Code (<see cref="SchemaInstaller"/>).
/// </summary>
internal static class SchemaCommand
{
    /// <summary>The names <c>schema print</c> accepts besides a resource type.</summary>
    public const string TemplateName = "template";

    public const string ParametersName = "parameters";

    public static Command Build()
    {
        var command = new Command("schema", "Print or install the JSON Schemas of the template format.");
        command.Subcommands.Add(BuildPrint());
        command.Subcommands.Add(BuildInstall());
        command.SetAction(parseResult => new HelpAction().Invoke(parseResult));
        return command;
    }

    private static Command BuildPrint()
    {
        var what = new Argument<string>("what")
        {
            Description = "template (default), parameters, or a resource type: Microsoft.Entra/applications, applications, conditionalAccess/policies, conditionalAccess.policies, ...",
            Arity = ArgumentArity.ZeroOrOne,
            DefaultValueFactory = _ => TemplateName,
        };

        var command = new Command("print", "Writes a JSON Schema to standard output: the template envelope, the parameter file, or the schema of one resource type (with its x-entraarm overlay).");
        command.Arguments.Add(what);
        command.SetAction(parseResult => RunPrint(parseResult.GetValue(what) ?? TemplateName, parseResult.InvocationConfiguration.Output, parseResult.InvocationConfiguration.Error));
        return command;
    }

    private static Command BuildInstall()
    {
        var global = new Option<bool>("--global")
        {
            Description = "Write the mapping into the VS Code user settings instead of the workspace's .vscode/settings.json.",
        };

        var workspace = new Option<string?>("--workspace")
        {
            Description = "Workspace directory whose .vscode/settings.json receives the mapping. Default: the current directory.",
        };

        var settingsFile = new Option<string?>("--settings-file")
        {
            Description = "Settings file to merge into (overrides --workspace and --global), for editors that read the VS Code format.",
        };

        var schemaDir = new Option<string?>("--schema-dir")
        {
            Description = "Directory to extract the schemas to. Default: %LOCALAPPDATA%\\EntraArm\\schemas\\2026-10-01 (~/.local/share/EntraArm/schemas/2026-10-01 elsewhere).",
        };

        var command = new Command("install", "Extracts the schemas and maps them to **/*.entra.json and **/*.entra.jsonc in VS Code (json.schemas), preserving the other settings.");
        command.Options.Add(global);
        command.Options.Add(workspace);
        command.Options.Add(settingsFile);
        command.Options.Add(schemaDir);
        command.SetAction(parseResult => SchemaInstaller.Run(
            new SchemaInstallRequest
            {
                Global = parseResult.GetValue(global),
                Workspace = parseResult.GetValue(workspace),
                SettingsFile = parseResult.GetValue(settingsFile),
                SchemaDirectory = parseResult.GetValue(schemaDir),
                Output = GlobalOptions.GetOutputFormat(parseResult),
            },
            parseResult.InvocationConfiguration.Output,
            parseResult.InvocationConfiguration.Error));
        return command;
    }

    /// <summary>Writes the requested embedded schema; exit 1 (with the known names on stderr) for an unknown one.</summary>
    internal static int RunPrint(string what, TextWriter stdout, TextWriter stderr)
    {
        if (!TryResolve(what, out string? resource, out string? error))
        {
            stderr.WriteLine(error);
            return ExitCodes.Failure;
        }

        string text = EmbeddedSchemas.ReadText(resource!);
        stdout.Write(text);
        if (!text.EndsWith('\n'))
        {
            stdout.WriteLine();
        }

        return ExitCodes.Ok;
    }

    /// <summary>Maps <c>template</c>, <c>parameters</c> or a type name to the embedded resource name.</summary>
    internal static bool TryResolve(string what, out string? resource, out string? error)
    {
        resource = null;
        error = null;
        string name = what.Trim();
        if (name.Equals(TemplateName, StringComparison.OrdinalIgnoreCase))
        {
            resource = EmbeddedSchemas.TemplateSchemaResource;
            return true;
        }

        if (name.Equals(ParametersName, StringComparison.OrdinalIgnoreCase))
        {
            resource = EmbeddedSchemas.ParametersSchemaResource;
            return true;
        }

        var catalog = ResourceTypeCatalog.Default;
        string candidate = name;
        string prefix = ResourceIdentifier.ProviderNamespace + "/";
        bool prefixed = candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        string rest = prefixed ? candidate[prefix.Length..] : candidate;

        // "conditionalAccess.policies" is the file-name spelling of "conditionalAccess/policies".
        var definition = catalog.Find(prefix + rest) ?? catalog.Find(prefix + rest.Replace('.', '/'));
        if (definition is null)
        {
            error = $"Unknown schema '{what}'. Use 'template', 'parameters' or one of: {string.Join(", ", catalog.Types.Select(t => t.Type))}.";
            return false;
        }

        resource = definition.SchemaResourceName;
        return true;
    }
}
