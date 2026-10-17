using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Cli;
using EArm.Cli.Commands;
using EArm.Cli.Hosting;
using EArm.Expressions.Evaluation;
using EArm.Graph;
using EArm.Graph.Auth;
using EArm.Tests.Engine;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// Review 3J, findings 9 and 11: <c>--evaluation-timeout abc</c> was an "Unexpected error" (and <c>NaN</c> another), <c>EARM_EVALUATION_TIMEOUT</c> was
/// capped at half of <c>TimeSpan.MaxValue</c> instead of 24 hours; <c>schema print</c>, <c>schema install</c>, <c>auth</c> and a command line that does
/// not parse printed plain text under <c>-o json</c>; a multi-line sign-in failure was flattened with literal <c>\r\n</c>; <c>-p @file -n x</c> was
/// swallowed into <c>-p</c>; <c>auth login</c> waited forever for a browser nobody could use.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class Review3J_CliTests
{
    private static async Task<string> TemplateFileAsync(string testName)
    {
        var directory = new TestDirectory(testName);
        string path = directory.File("ok.entra.json");
        await File.WriteAllTextAsync(path, Review1C.Template("\"resources\": []"), new UTF8Encoding(false), TestContext.Current.CancellationToken);
        return path;
    }

    // ---- --evaluation-timeout ----

    [Theory]
    [InlineData("abc")]
    [InlineData("NaN")]
    [InlineData("nan")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("-1")]
    [InlineData("86401")]
    [InlineData("1e18")]
    [InlineData("1e400")]
    [InlineData("")]
    public async Task AnInvalidEvaluationTimeout_IsAOneLineUsageError_NeverAnUnexpectedError(string value)
    {
        string path = await TemplateFileAsync(nameof(AnInvalidEvaluationTimeout_IsAOneLineUsageError_NeverAnUnexpectedError));

        var result = await CliRunner.RunAsync("deployment", "validate", "-f", path, "--evaluation-timeout", value);

        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain("Unexpected error", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Unexpected error", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--evaluation-timeout must be a number of seconds between 0 and 86400.", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardOutput);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.5")]
    [InlineData("60")]
    [InlineData("86400")]
    [InlineData("1e2")]
    public async Task AValidEvaluationTimeout_IsAccepted(string value)
    {
        string path = await TemplateFileAsync(nameof(AValidEvaluationTimeout_IsAccepted));

        var result = await CliRunner.RunAsync("deployment", "validate", "-f", path, "--evaluation-timeout", value, "-o", "json");

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task AnInvalidEvaluationTimeout_WithJsonOutput_IsAFailureDocument()
    {
        string path = await TemplateFileAsync(nameof(AnInvalidEvaluationTimeout_WithJsonOutput_IsAFailureDocument));

        var result = await CliRunner.RunAsync("deployment", "validate", "-f", path, "--evaluation-timeout", "abc", "-o", "json");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("Failed", result.Json["status"]!.GetValue<string>());
        Assert.Contains("--evaluation-timeout", result.Diagnostics[0]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("86400", true, 86400)]
    [InlineData("86400.5", false, 0)]
    [InlineData("1e18", false, 0)]
    [InlineData("NaN", false, 0)]
    [InlineData("Infinity", false, 0)]
    [InlineData("-0.5", false, 0)]
    [InlineData(" 5 ", true, 5)]
    [InlineData("", false, 0)]
    [InlineData(null, false, 0)]
    [InlineData("0", true, 0)]
    public void TheTimeoutParser_AcceptsZeroTo86400_AndNothingElse(string? text, bool accepted, double seconds)
    {
        Assert.Equal(accepted, EvaluationLimits.TryParseTimeout(text, out var timeout));
        if (accepted)
        {
            Assert.Equal(TimeSpan.FromSeconds(seconds), timeout);
        }
    }

    [Fact]
    public void TheEnvironmentVariable_IsCappedAtTheSame86400Seconds()
    {
        string? before = Environment.GetEnvironmentVariable(EvaluationLimits.TimeoutVariable);
        try
        {
            Environment.SetEnvironmentVariable(EvaluationLimits.TimeoutVariable, "86400");
            Assert.Equal(TimeSpan.FromSeconds(86400), EvaluationLimits.FromEnvironment().Timeout);
            Environment.SetEnvironmentVariable(EvaluationLimits.TimeoutVariable, "86401");
            Assert.Equal(TimeSpan.FromSeconds(60), EvaluationLimits.FromEnvironment().Timeout);
            Environment.SetEnvironmentVariable(EvaluationLimits.TimeoutVariable, "1e11");
            Assert.Equal(TimeSpan.FromSeconds(60), EvaluationLimits.FromEnvironment().Timeout);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EvaluationLimits.TimeoutVariable, before);
        }
    }

    // ---- -o json: one document on success and on failure ----

    [Fact]
    public async Task SchemaPrint_OfAnUnknownName_WithJsonOutput_IsAFailureDocument_AndTheMessageIsOnStandardError()
    {
        var result = await CliRunner.RunAsync("schema", "print", "nope", "-o", "json");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("Failed", result.Json["status"]!.GetValue<string>());
        Assert.Equal("InvalidArgument", result.Diagnostics[0]!["code"]!.GetValue<string>());
        Assert.Contains("Unknown schema 'nope'", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SchemaPrint_OfAnUnknownName_InTableMode_StaysPlainText()
    {
        var result = await CliRunner.RunAsync("schema", "print", "nope");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("Unknown schema 'nope'", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SchemaPrint_WithJsonOutput_IsTheSchemaItself_OneDocument()
    {
        var result = await CliRunner.RunAsync("schema", "print", "template", "-o", "json");

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.Json["$schema"]);
    }

    [Fact]
    public async Task SchemaInstall_WithJsonOutput_IsOneDocument_OnSuccess_AndOnFailure()
    {
        using var directory = new TestDirectory(nameof(SchemaInstall_WithJsonOutput_IsOneDocument_OnSuccess_AndOnFailure));
        string settings = directory.File("settings.json");
        string schemas = directory.Sub("schemas");

        var ok = await CliRunner.RunAsync("schema", "install", "--settings-file", settings, "--schema-dir", schemas, "-o", "json");
        var again = await CliRunner.RunAsync("schema", "install", "--settings-file", settings, "--schema-dir", schemas, "-o", "json");

        Assert.Equal(0, ok.ExitCode);
        Assert.Equal("Succeeded", ok.Json["status"]!.GetValue<string>());
        Assert.Equal("created", ok.Json["settings"]!.GetValue<string>());
        Assert.True(ok.Json["written"]!.GetValue<int>() > 0);
        Assert.Equal("unchanged", again.Json["settings"]!.GetValue<string>());
        Assert.Equal(0, again.Json["written"]!.GetValue<int>());

        // A settings file that is not JSON: a failure document, exit 1.
        await File.WriteAllTextAsync(settings, "this is not json", TestContext.Current.CancellationToken);
        var failed = await CliRunner.RunAsync("schema", "install", "--settings-file", settings, "--schema-dir", schemas, "-o", "json");
        Assert.Equal(1, failed.ExitCode);
        Assert.Equal("Failed", failed.Json["status"]!.GetValue<string>());
        Assert.Contains("is not valid JSON", failed.Diagnostics[0]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SchemaInstall_InTableMode_PrintsTheSameTextAsBefore()
    {
        using var directory = new TestDirectory(nameof(SchemaInstall_InTableMode_PrintsTheSameTextAsBefore));

        var result = await CliRunner.RunAsync("schema", "install", "--settings-file", directory.File("s.json"), "--schema-dir", directory.Sub("schemas"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Schemas (", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Created ", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACommandLineThatDoesNotParse_WithJsonOutput_IsAFailureDocument_NamingTheUnknownOption()
    {
        string path = await TemplateFileAsync(nameof(ACommandLineThatDoesNotParse_WithJsonOutput_IsAFailureDocument_NamingTheUnknownOption));

        var result = await CliRunner.RunAsync("deployment", "validate", "-f", path, "--bogus", "-o", "json");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("Failed", result.Json["status"]!.GetValue<string>());
        Assert.Contains("--bogus", result.Diagnostics[0]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("--bogus", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Usage:", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AUsageError_WithoutJsonOutput_KeepsStandardOutputEmpty_AndTheHelpOnStandardError()
    {
        var result = await CliRunner.RunAsync("deployment", "validate", "--bogus");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("Usage:", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthStatus_Failure_KeepsItsLines_AsRealLineBreaks_NotLiteralBackslashes()
    {
        using var sandbox = new CliSandbox(nameof(AuthStatus_Failure_KeepsItsLines_AsRealLineBreaks_NotLiteralBackslashes));
        sandbox.UseNotLoggedIn();

        var result = await CliRunner.RunAsync("auth", "status", "-o", "json");

        Assert.Equal(3, result.ExitCode);
        var diagnostic = result.Diagnostics.Single()!.AsObject();
        string message = diagnostic["message"]!.GetValue<string>();
        var lines = diagnostic["lines"]!.AsArray().Select(l => l!.GetValue<string>()).ToArray();
        Assert.DoesNotContain("\\r", message, StringComparison.Ordinal);
        Assert.DoesNotContain("\\n", message, StringComparison.Ordinal);
        Assert.Contains('\n', message);
        Assert.DoesNotContain('\r', message);
        Assert.True(lines.Length > 1);
        Assert.Equal(message, string.Join('\n', lines));
        Assert.StartsWith("Not signed in", lines[0], StringComparison.Ordinal);
    }

    // ---- -p: several values per option (as the Azure CLI), ended by the next option ----

    [Fact]
    public async Task AStrayOption_AfterAParameterFile_IsAUsageError_NamingIt_OnValidate()
    {
        using var directory = new TestDirectory(nameof(AStrayOption_AfterAParameterFile_IsAUsageError_NamingIt_OnValidate));
        string path = await TemplateFileAsync(nameof(AStrayOption_AfterAParameterFile_IsAUsageError_NamingIt_OnValidate) + "-t");
        string parameters = directory.File("p.json");
        await File.WriteAllTextAsync(parameters, "{ \"parameters\": {} }", TestContext.Current.CancellationToken);

        var swallowed = await CliRunner.RunAsync("deployment", "validate", "-f", path, "-p", "@" + parameters, "-n", "x");
        var two = await CliRunner.RunAsync("deployment", "validate", "-f", path, "-p", "@" + parameters, "-p", "z=1");
        var spaced = await CliRunner.RunAsync("deployment", "validate", "-f", path, "-p", "@" + parameters, "z=1");

        // validate has no -n: the unknown option is reported by name, never taken as a parameter value.
        Assert.Equal(1, swallowed.ExitCode);
        Assert.Contains("-n", swallowed.StandardError, StringComparison.Ordinal);
        // Several values after one -p, or the option repeated: both reach the template (z is not one of its parameters).
        Assert.Contains("UnknownParameter", spaced.StandardError, StringComparison.Ordinal);
        Assert.Contains("UnknownParameter", two.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APermissionsRun_TakesSeveralValuesPerP_AndRefusesAValueInNoAcceptedForm()
    {
        string path = await TemplateFileAsync(nameof(APermissionsRun_TakesSeveralValuesPerP_AndRefusesAValueInNoAcceptedForm));

        var ok = await CliRunner.RunAsync("permissions", "-f", path, "-o", "json");
        var stray = await CliRunner.RunAsync("permissions", "-f", path, "-p", "a=1", "stray", "-o", "json");

        Assert.Equal(0, ok.ExitCode);
        Assert.Equal(1, stray.ExitCode);
        Assert.Contains("InvalidArgument", stray.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("stray", stray.StandardOutput, StringComparison.Ordinal);
    }

    // ---- auth login ----

    private static AuthInvocation Invocation(CliSandbox sandbox, bool deviceCode = false)
    {
        return new AuthInvocation { Connection = sandbox.Connection, Output = OutputFormat.Table, DeviceCode = deviceCode };
    }

    [Fact]
    public async Task Login_WithAuthDefault_IsRefusedAtOnce_WithAClearMessage()
    {
        using var sandbox = new CliSandbox(nameof(Login_WithAuthDefault_IsRefusedAtOnce_WithAClearMessage));

        var table = await CliRunner.RunAsync("auth", "login", "--auth", "default");
        var json = await CliRunner.RunAsync("auth", "login", "--auth", "default", "-o", "json");

        Assert.Equal(1, table.ExitCode);
        Assert.Contains("only for interactive sign-in", table.StandardError, StringComparison.Ordinal);
        Assert.Equal(string.Empty, table.StandardOutput);
        Assert.Equal(1, json.ExitCode);
        Assert.Equal("Failed", json.Json["status"]!.GetValue<string>());
        Assert.Contains("only for interactive sign-in", json.Diagnostics[0]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_WithRedirectedStreams_AndNoDeviceCode_FailsFast_NamingTheDeviceCodeFlag()
    {
        using var sandbox = new CliSandbox(nameof(Login_WithRedirectedStreams_AndNoDeviceCode_FailsFast_NamingTheDeviceCodeFlag));
        var before = AuthCommand.StandardStreamsRedirected;
        AuthCommand.StandardStreamsRedirected = () => true;
        try
        {
            var result = await CliRunner.RunAsync("auth", "login");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("--device-code", result.StandardError, StringComparison.Ordinal);
            Assert.Contains("redirected", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            AuthCommand.StandardStreamsRedirected = before;
        }
    }

    [Fact]
    public void TheLoginGuard_IsExactlyAboutDefaultMode_AndRedirectedStreamsWithoutADeviceCode()
    {
        using var sandbox = new CliSandbox(nameof(TheLoginGuard_IsExactlyAboutDefaultMode_AndRedirectedStreamsWithoutADeviceCode));
        var before = AuthCommand.StandardStreamsRedirected;
        try
        {
            var interactive = new GraphAuthOptions { Mode = GraphAuthMode.Interactive };
            var auto = new GraphAuthOptions { Mode = GraphAuthMode.Auto };
            var chain = new GraphAuthOptions { Mode = GraphAuthMode.Default };

            AuthCommand.StandardStreamsRedirected = () => true;
            Assert.NotNull(AuthCommand.LoginRefusal(Invocation(sandbox), interactive, realSignIn: true));
            Assert.NotNull(AuthCommand.LoginRefusal(Invocation(sandbox), auto, realSignIn: true));
            Assert.Null(AuthCommand.LoginRefusal(Invocation(sandbox, deviceCode: true), interactive, realSignIn: true));
            Assert.Null(AuthCommand.LoginRefusal(Invocation(sandbox), interactive, realSignIn: false));
            Assert.NotNull(AuthCommand.LoginRefusal(Invocation(sandbox, deviceCode: true), chain, realSignIn: true));
            Assert.NotNull(AuthCommand.LoginRefusal(Invocation(sandbox), chain, realSignIn: false));

            AuthCommand.StandardStreamsRedirected = () => false;
            Assert.Null(AuthCommand.LoginRefusal(Invocation(sandbox), interactive, realSignIn: true));
        }
        finally
        {
            AuthCommand.StandardStreamsRedirected = before;
        }
    }

    [Fact]
    public async Task Logout_WithAnInvalidTenant_WithJsonOutput_IsAFailureDocument()
    {
        using var sandbox = new CliSandbox(nameof(Logout_WithAnInvalidTenant_WithJsonOutput_IsAFailureDocument));

        var result = await CliRunner.RunAsync("auth", "logout", "--client-id", "not-a-guid", "-o", "json");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("Failed", result.Json["status"]!.GetValue<string>());
        Assert.Contains("not a GUID", result.Diagnostics[0]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryVerbOfTheGroup_PrintsOneJsonDocument_OnSuccess()
    {
        using var directory = new TestDirectory(nameof(EveryVerbOfTheGroup_PrintsOneJsonDocument_OnSuccess));
        string path = await TemplateFileAsync(nameof(EveryVerbOfTheGroup_PrintsOneJsonDocument_OnSuccess));
        using var sandbox = new CliSandbox(nameof(EveryVerbOfTheGroup_PrintsOneJsonDocument_OnSuccess));

        foreach (string[] args in new[]
        {
            new[] { "deployment", "validate", "-f", path, "-o", "json" },
            new[] { "permissions", "-f", path, "-o", "json" },
            new[] { "schema", "print", "parameters", "-o", "json" },
            new[] { "auth", "logout", "-o", "json" },
        })
        {
            var result = await CliRunner.RunAsync(args);

            Assert.Equal(0, result.ExitCode);
            Assert.NotNull(JsonNode.Parse(result.StandardOutput));
        }
    }

    [Fact]
    public async Task Logout_InASandbox_DeletesOnlyTheSandboxCache_NeverAnotherDirectory()
    {
        // The safety net of tests/Directory.Build.props is on in this test executable too.
        Assert.True(AppContext.TryGetSwitch(GraphAuthFactory.ForbidPlatformCacheSwitch, out var forbidden) && forbidden);
        using var sandbox = new CliSandbox(nameof(Logout_InASandbox_DeletesOnlyTheSandboxCache_NeverAnotherDirectory));
        // Stands in for the real cache directory: same file names, outside the sandbox's cache directory.
        string elsewhere = Path.Combine(sandbox.Root, "elsewhere", ".IdentityService");
        Directory.CreateDirectory(elsewhere);
        foreach (string name in new[] { "earm.nocae", "earm.cae" })
        {
            await File.WriteAllTextAsync(Path.Combine(elsewhere, name), "real", TestContext.Current.CancellationToken);
        }

        foreach (string name in new[] { "earm.nocae", "earm.cae", "earm-spike.nocae" })
        {
            await File.WriteAllTextAsync(Path.Combine(sandbox.TokenCacheDir, name), "x", TestContext.Current.CancellationToken);
        }

        var result = await CliRunner.RunAsync("auth", "logout", "-o", "json");

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(elsewhere, "earm.nocae")));
        Assert.True(File.Exists(Path.Combine(elsewhere, "earm.cae")));
        Assert.Equal(["earm-spike.nocae"], Directory.GetFiles(sandbox.TokenCacheDir).Select(Path.GetFileName));
    }
}
