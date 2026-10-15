using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Cli;
using EArm.Cli.Hosting;
using EArm.Core.Json;
using EArm.Graph;
using EArm.Tests.Engine;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// Review 1C, finding I-05 and Q-20: an exception no command handled printed a stack trace (with build paths) and exited 1; usage errors
/// printed the full help on standard output while the message went to standard error; the confirmation prompt wrote to standard output, so
/// <c>-o json</c> output could not be parsed; <c>auth status -o json</c> printed plain text on failure. The process now has one catch-all
/// (<see cref="Program.InvokeAsync"/>) and these streams are separated.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class Review1C_UnexpectedErrorTests
{
    private static async Task<(int Exit, string Out, string Err)> RunThrowingAsync(Func<Exception> make, params string[] extraArgs)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var configuration = new InvocationConfiguration { Output = stdout, Error = stderr };

        int exit = await Program.InvokeAsync(
            ["boom", .. extraArgs],
            configuration,
            TestContext.Current.CancellationToken,
            () =>
            {
                var root = new RootCommand("test");
                root.Options.Add(GlobalOptions.Debug);
                var boom = new Command("boom");
                boom.SetAction((Func<ParseResult, int>)(_ => throw make()));
                root.Subcommands.Add(boom);
                return root;
            });

        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static Exception Thrown(Exception exception)
    {
        try
        {
            throw exception;
        }
        catch (Exception caught)
        {
            return caught;
        }
    }

    // ---- the catch-all ----

    [Fact]
    public async Task AnUnexpectedException_IsOneLine_ExitOne_NoStackTrace()
    {
        var (exit, stdout, stderr) = await RunThrowingAsync(() => new InvalidOperationException("the state machine is confused"));

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, stdout);
        string[] lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal("Unexpected error (InvalidOperationException): the state machine is confused", lines[0]);
        Assert.Equal(2, lines.Length);
        Assert.Contains("--debug", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(".cs:line", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithDebug_TheFullExceptionFollows()
    {
        var (exit, _, stderr) = await RunThrowingAsync(() => Thrown(new InvalidOperationException("detail please")), "--debug");

        Assert.Equal(1, exit);
        Assert.StartsWith("Unexpected error (InvalidOperationException): detail please", stderr, StringComparison.Ordinal);
        Assert.Contains("   at ", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Run again with --debug", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDebugEnvironmentVariable_IsOneOrTrue()
    {
        string? before = Environment.GetEnvironmentVariable(GlobalOptions.DebugVariable);
        try
        {
            foreach (var (value, expected) in new[] { ("1", true), ("true", true), ("TRUE", true), ("0", false), ("", false), ("yes", false) })
            {
                Environment.SetEnvironmentVariable(GlobalOptions.DebugVariable, value);
                Assert.Equal(expected, GlobalOptions.IsDebugEnvironment());
            }

            Environment.SetEnvironmentVariable(GlobalOptions.DebugVariable, null);
            Assert.False(GlobalOptions.IsDebugEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable(GlobalOptions.DebugVariable, before);
        }
    }

    [Fact]
    public async Task TheMessage_IsScrubbedOfSecrets_ControlCharacters_AndIsCapped()
    {
        string secret = "sup3r-s3cret-v@lue-12345";
        var (exit, _, stderr) = await RunThrowingAsync(() =>
        {
            SecretScrubber.Register(secret);
            return new InvalidOperationException("failed with " + secret + "\n##vso[task.setvariable variable=EVIL]1\u001b[2J" + new string('x', 2_000));
        });

        Assert.Equal(1, exit);
        Assert.DoesNotContain(secret, stderr, StringComparison.Ordinal);
        string first = stderr.Split('\n')[0];
        // The writer breaks the log-command token (the second # is written as \u0023, see Review3J_ConsoleSanitizerTests).
        Assert.Contains("failed with ***\\n#\\u0023vso[task.setvariable variable=EVIL]1\\u001b[2J", first, StringComparison.Ordinal);
        Assert.True(first.Length < 700, "the line is " + first.Length + " characters");
        Assert.EndsWith("...", first.TrimEnd('\r'), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileSystemProblem_GetsAFriendlySentence()
    {
        var denied = await RunThrowingAsync(() => new UnauthorizedAccessException("Access to the path 'x' is denied."));
        var io = await RunThrowingAsync(() => new IOException("The process cannot access the file 'x' because it is being used by another process."));
        var missing = await RunThrowingAsync(() => new DirectoryNotFoundException("Could not find a part of the path 'y'."));

        Assert.Equal(1, denied.Exit);
        Assert.StartsWith("Access denied: Access to the path 'x' is denied. Check the permissions", denied.Err, StringComparison.Ordinal);
        Assert.StartsWith("A file operation failed (IOException): The process cannot access the file", io.Err, StringComparison.Ordinal);
        Assert.StartsWith("A file operation failed (DirectoryNotFoundException): ", missing.Err, StringComparison.Ordinal);
        Assert.DoesNotContain("Unexpected error", denied.Err + io.Err + missing.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutOfMemory_IsAOneLineMessage_ExitOne()
    {
        var (exit, stdout, stderr) = await RunThrowingAsync(() => new OutOfMemoryException());

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.Single(stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("ran out of memory", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancelledCommand_SaysSo_AndExitsOne()
    {
        var (exit, _, stderr) = await RunThrowingAsync(() => new OperationCanceledException());

        Assert.Equal(1, exit);
        Assert.Equal("Cancelled.", stderr.Trim());
    }

    [Fact]
    public async Task AValueThatIsTooDeep_IsExplained_NotReportedAsAnInternalError()
    {
        var tooDeep = await RunThrowingAsync(() => new ValueTooDeepException());
        var writer = await RunThrowingAsync(() => new InvalidOperationException("CurrentDepth (64) is equal to or larger than the maximum allowed depth of 64. Cannot write the next JSON object or array."));

        Assert.Contains("nested too deeply", tooDeep.Err, StringComparison.Ordinal);
        Assert.Contains("64 levels", tooDeep.Err, StringComparison.Ordinal);
        Assert.Contains("nested too deeply", writer.Err, StringComparison.Ordinal);
        Assert.DoesNotContain("Unexpected error", tooDeep.Err + writer.Err, StringComparison.Ordinal);
    }

    // ---- usage errors and prompts go to standard error ----

    [Fact]
    public async Task AUsageError_PutsTheMessageAndTheHelpOnStandardError_AndNothingOnStandardOutput()
    {
        var result = await CliRunner.RunAsync("deployment", "validate");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("--template-file", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Usage:", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownCommand_AndABadOptionValue_AreUsageErrorsOnStandardError()
    {
        var unknown = await CliRunner.RunAsync("explode");
        var badOutput = await CliRunner.RunAsync("deployment", "validate", "-f", "x.json", "-o", "yaml");

        foreach (var result in new[] { unknown, badOutput })
        {
            Assert.NotEqual(0, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardOutput);
            Assert.NotEqual(string.Empty, result.StandardError);
        }
    }

    [Fact]
    public async Task RequestedHelp_StaysOnStandardOutput()
    {
        var root = await CliRunner.RunAsync("--help");
        var verb = await CliRunner.RunAsync("deployment", "validate", "--help");
        var bare = await CliRunner.RunAsync();

        foreach (var result in new[] { root, verb, bare })
        {
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Usage:", result.StandardOutput, StringComparison.Ordinal);
            Assert.Equal(string.Empty, result.StandardError);
        }
    }

    [Fact]
    public void TheConfirmationPrompt_IsWrittenToStandardError()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        bool answer = ConsolePrompt.Confirm("Delete everything?", stdout, stderr, () => "y");

        Assert.True(answer);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal("Delete everything? (y/n): ", stderr.ToString());
    }

    [Fact]
    public async Task TheWhatIfJson_AheadOfTheConfirmationPrompt_StaysParseable_BecauseThePromptIsOnStandardError()
    {
        using var sandbox = new CliSandbox(nameof(TheWhatIfJson_AheadOfTheConfirmationPrompt_StaysParseable_BecauseThePromptIsOnStandardError));
        sandbox.UsePreflightOk();
        var invocation = new EArm.Cli.Commands.DeploymentInvocation
        {
            TemplateFile = sandbox.WriteEmptyTemplate(),
            ConfirmWithWhatIf = true,
            Output = OutputFormat.Json,
            Connection = sandbox.Connection,
        };
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exit = await EArm.Cli.Commands.DeploymentRunner.CreateAsync(invocation, stdout, stderr, () => "n", TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
        Assert.NotNull(JsonNode.Parse(stdout.ToString()) as JsonObject);
        Assert.Contains("Are you sure you want to execute the deployment? (y/n): ", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthStatus_Failing_WithJsonOutput_PrintsAJsonDiagnostic()
    {
        using var sandbox = new CliSandbox(nameof(AuthStatus_Failing_WithJsonOutput_PrintsAJsonDiagnostic));
        sandbox.UseNotLoggedIn();

        var json = await CliRunner.RunAsync("auth", "status", "-o", "json");
        var table = await CliRunner.RunAsync("auth", "status");

        Assert.Equal(3, json.ExitCode);
        var report = JsonNode.Parse(json.StandardOutput)!.AsObject();
        Assert.Equal("Failed", report["status"]!.GetValue<string>());
        var diagnostic = Assert.Single(report["diagnostics"]!.AsArray())!.AsObject();
        Assert.Equal("Error", diagnostic["severity"]!.GetValue<string>());
        Assert.Equal("AuthenticationFailed", diagnostic["code"]!.GetValue<string>());
        Assert.Contains("Not signed in", diagnostic["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("Not signed in", json.StandardError, StringComparison.Ordinal);

        // The table format is unchanged: nothing on standard output.
        Assert.Equal(3, table.ExitCode);
        Assert.Equal(string.Empty, table.StandardOutput);
    }

    // ---- --evaluation-timeout ----

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("100000")]
    public async Task TheEvaluationTimeout_RejectsOutOfRangeValues_AsAUsageError(string value)
    {
        var result = await CliRunner.RunAsync("deployment", "validate", "-f", "x.entra.json", "--evaluation-timeout", value);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--evaluation-timeout", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheEvaluationTimeout_IsAcceptedOnAnyVerb_AndZeroMeansNoClock()
    {
        using var directory = new TestDirectory(nameof(TheEvaluationTimeout_IsAcceptedOnAnyVerb_AndZeroMeansNoClock));
        string path = directory.File("ok.entra.json");
        await File.WriteAllTextAsync(path, Review1C.Template("\"resources\": []"), TestContext.Current.CancellationToken);

        var zero = await CliRunner.ValidateJsonAsync(path, "--evaluation-timeout", "0");
        var five = await CliRunner.RunAsync("deployment", "validate", "--evaluation-timeout", "5", "-f", path, "-o", "json");

        Assert.Equal(0, zero.ExitCode);
        Assert.Equal(0, five.ExitCode);
    }

    [Fact]
    public void TheEvaluationTimeoutEnvironmentVariable_IsTheFallback_AndGarbageIsIgnored()
    {
        // The option value is read from the parse result; the environment is the fallback.
        string? before = Environment.GetEnvironmentVariable(EArm.Expressions.Evaluation.EvaluationLimits.TimeoutVariable);
        try
        {
            Environment.SetEnvironmentVariable(EArm.Expressions.Evaluation.EvaluationLimits.TimeoutVariable, "7");
            Assert.Equal(TimeSpan.FromSeconds(7), EArm.Expressions.Evaluation.EvaluationLimits.FromEnvironment().Timeout);
            Environment.SetEnvironmentVariable(EArm.Expressions.Evaluation.EvaluationLimits.TimeoutVariable, "garbage");
            Assert.Equal(TimeSpan.FromSeconds(60), EArm.Expressions.Evaluation.EvaluationLimits.FromEnvironment().Timeout);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EArm.Expressions.Evaluation.EvaluationLimits.TimeoutVariable, before);
        }
    }
}
