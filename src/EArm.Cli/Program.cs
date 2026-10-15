using System.CommandLine;
using System.CommandLine.Help;
using EArm.Cli.Commands;

namespace EArm.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        return BuildRootCommand().Parse(args, CreateParserConfiguration()).Invoke();
    }

    /// <summary>
    /// Parser settings for the CLI. Response-file expansion is disabled because <c>-p @file.json</c> is EARM's
    /// own parameter-file syntax (as in <c>az deployment group create</c>) and must reach the command verbatim.
    /// </summary>
    internal static ParserConfiguration CreateParserConfiguration()
    {
        return new ParserConfiguration { ResponseFileTokenReplacer = null };
    }

    /// <summary>
    /// Builds the <c>earm</c> root command. Exposed so tests can drive the command line in-process.
    /// </summary>
    internal static RootCommand BuildRootCommand()
    {
        var root = new RootCommand("EARM - ARM-style JSON templates for Microsoft Entra ID, executed via Azure CLI.");

        // Replace the built-in --version (which reports the *entry* assembly) with one pinned to this assembly,
        // so the answer is the same whether the CLI is run as a tool or driven in-process from a test host.
        for (int i = root.Options.Count - 1; i >= 0; i--)
        {
            if (root.Options[i] is VersionOption)
            {
                root.Options.RemoveAt(i);
            }
        }

        root.Options.Add(new Option<bool>("--version")
        {
            Description = "Show version information",
            Arity = ArgumentArity.Zero,
            Action = new VersionAction(),
        });

        root.Options.Add(GlobalOptions.Output);
        root.Options.Add(GlobalOptions.PwshPath);
        root.Options.Add(GlobalOptions.AzCommand);
        root.Options.Add(GlobalOptions.TempDir);
        root.Options.Add(GlobalOptions.StateDir);
        root.Options.Add(GlobalOptions.Verbose);
        root.Options.Add(GlobalOptions.Debug);
        root.Subcommands.Add(DeploymentCommand.Build());
        root.Subcommands.Add(StateCommand.Build());
        root.Subcommands.Add(PermissionsCommand.Build());
        root.Subcommands.Add(SchemaCommand.Build());

        // Bare "earm" prints help and exits 0.
        root.SetAction(parseResult => new HelpAction().Invoke(parseResult));

        return root;
    }
}
