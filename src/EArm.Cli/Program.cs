using System;
using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EArm.Cli.Commands;
using EArm.Cli.Output;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Expressions.Evaluation;
using EArm.Graph;

namespace EArm.Cli;

internal static class Program
{
    /// <summary>The longest text shown for an unexpected error (the message is cut, never the type).</summary>
    private const int MaxUnexpectedMessageLength = 500;

    /// <summary>
    /// How long the process waits, after Ctrl+C / SIGINT / SIGTERM, for the command to finish before System.CommandLine lets it exit (its default is 2 s).
    /// A cancelled deployment does not stop dead: a write already sent to Graph is let finish (bounded by the 120 s request timeout,
    /// <see cref="GraphOptions.Timeout"/>), an object whose create answer was lost is looked up once more (one more request timeout), and the secure
    /// outputs file is saved (30 s bound per save). Killing the process inside that window would orphan the object just written and
    /// its one-time secret, which is exactly what the uncancellable end phase prevents; 5 minutes covers that worst case with margin. Not <c>null</c>:
    /// that would disable the cancellation token altogether, and Ctrl+C would kill the process at once.
    /// </summary>
    internal static readonly TimeSpan ProcessTerminationTimeout = TimeSpan.FromMinutes(5);

    private static Task<int> Main(string[] args)
    {
        return InvokeAsync(args, new InvocationConfiguration { ProcessTerminationTimeout = ProcessTerminationTimeout }, CancellationToken.None);
    }

    /// <summary>
    /// Runs the command line: the one entry point of the process, also used by the tests. Everything the commands write goes through
    /// <see cref="ConsoleSanitizer"/> (control characters and CI log commands from untrusted text are neutralised), the evaluation budget of the
    /// run is installed (<c>--evaluation-timeout</c>), usage errors and the help shown with them go to standard error, and no exception escapes:
    /// anything unexpected becomes one line, <c>Unexpected error (type): message</c>, with exit code 1 (the full exception with <c>--debug</c> or
    /// <c>EARM_DEBUG=1</c>). <paramref name="rootFactory"/> lets a test supply its own command tree.
    /// </summary>
    internal static async Task<int> InvokeAsync(string[] args, InvocationConfiguration configuration, CancellationToken cancellationToken, Func<RootCommand>? rootFactory = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(configuration);

        var rawOutput = configuration.Output;
        var rawError = configuration.Error;
        var output = new ConsoleSanitizer(rawOutput);
        var error = new ConsoleSanitizer(rawError);
        configuration.Output = output;
        configuration.Error = error;
        configuration.EnableDefaultExceptionHandler = false;

        bool debug = GlobalOptions.IsDebugEnvironment();

        // The registry of secret values is created before the try block so a failure reported below still scrubs what the run learned.
        using var secrets = SecretScrubber.Begin();
        try
        {
            var parseResult = (rootFactory?.Invoke() ?? BuildRootCommand()).Parse(args, CreateParserConfiguration());
            debug = GlobalOptions.IsDebug(parseResult);

            if (parseResult.Action is ParseErrorAction)
            {
                if (WantsJson(parseResult))
                {
                    // -o json promises one JSON document on standard output, also for a command line that does not parse (the message stays on standard error).
                    string message = string.Join("\n", parseResult.Errors.Select(e => e.Message));
                    return FailureDocument.Write(OutputFormat.Json, output, error, ValidateCommand.InvalidArgumentCode, message, ExitCodes.Failure);
                }

                // A usage error: the message goes to standard error already; the help that follows it must not pollute standard output.
                configuration.Output = error;
            }

            using var run = EvaluationBudget.BeginRun(GlobalOptions.GetEvaluationLimits(parseResult));
            return await parseResult.InvokeAsync(configuration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            error.WriteLine("Cancelled.");
            return ExitCodes.Failure;
        }
        catch (OutOfMemoryException)
        {
            error.WriteLine("The command ran out of memory. The template is too large or builds values that are too large: lower copy.count, or reduce the size of the strings and arrays it computes.");
            return ExitCodes.Failure;
        }
        catch (Exception ex)
        {
            ReportUnexpected(ex, error, debug);
            return ExitCodes.Failure;
        }
        finally
        {
            configuration.Output = rawOutput;
            configuration.Error = rawError;
            output.Flush();
            error.Flush();
        }
    }

    private static bool WantsJson(ParseResult parseResult)
    {
        try
        {
            return GlobalOptions.GetOutputFormat(parseResult) == OutputFormat.Json;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// The text of an exception that no command handled: a single line, free of secrets (<see cref="SecretScrubber"/> and
    /// <see cref="SecretRedactor"/>) and of control characters, naming the exception type and its message. File and permission problems get a
    /// plain sentence. With <paramref name="debug"/> the full exception follows (stack trace included).
    /// </summary>
    internal static string DescribeUnexpected(Exception exception, bool debug)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string message = Clean(exception.Message);
        bool depth = exception is ValueTooDeepException
            || (exception is InvalidOperationException or System.Text.Json.JsonException && exception.Message.Contains("depth", StringComparison.OrdinalIgnoreCase));
        string line = exception switch
        {
            _ when depth => "A value is nested too deeply to be processed or written as JSON (the limit is 64 levels). Reduce the nesting of the values the template builds.",
            UnauthorizedAccessException => $"Access denied: {message} Check the permissions of the file or directory.",
            IOException => $"A file operation failed ({exception.GetType().Name}): {message}",
            _ => $"Unexpected error ({exception.GetType().Name}): {message}",
        };

        if (!debug)
        {
            return line;
        }

        return line + Environment.NewLine + Clean(exception.ToString(), keepLines: true);
    }

    private static void ReportUnexpected(Exception exception, TextWriter error, bool debug)
    {
        error.WriteLine(DescribeUnexpected(exception, debug));
        if (!debug)
        {
            error.WriteLine("Run again with --debug (or EARM_DEBUG=1) for the details.");
        }
    }

    private static string Clean(string text, bool keepLines = false)
    {
        string scrubbed = SecretRedactor.RedactText(SecretScrubber.Scrub(text));
        if (!keepLines)
        {
            scrubbed = SafeText.Escape(scrubbed);
            return scrubbed.Length <= MaxUnexpectedMessageLength ? scrubbed : scrubbed[..MaxUnexpectedMessageLength] + "...";
        }

        return scrubbed;
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
        var root = new RootCommand("EARM - ARM-style JSON templates for Microsoft Entra ID, deployed through the Microsoft Graph API.");

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
        root.Options.Add(GlobalOptions.Auth);
        root.Options.Add(GlobalOptions.Tenant);
        root.Options.Add(GlobalOptions.ClientId);
        root.Options.Add(GlobalOptions.Verbose);
        root.Options.Add(GlobalOptions.Debug);
        root.Options.Add(GlobalOptions.EvaluationTimeout);
        root.Subcommands.Add(DeploymentCommand.Build());
        root.Subcommands.Add(AuthCommand.Build());
        root.Subcommands.Add(PermissionsCommand.Build());
        root.Subcommands.Add(SchemaCommand.Build());

        // Bare "earm" prints help and exits 0.
        root.SetAction(parseResult => new HelpAction().Invoke(parseResult));

        return root;
    }
}
