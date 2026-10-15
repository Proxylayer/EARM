using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EArm.Tests.Engine;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// Review round 4, K3: <c>-p</c> / <c>--parameters</c> takes ONE value per occurrence on every command (and <c>--key</c> of <c>state adopt</c> likewise), so
/// <c>-p @file -n name</c> is no longer swallowed as a second value of <c>-p</c>, while the option can still be repeated and an unknown token after a value is a usage error.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class Review4K_CliTests
{
    private const string TemplateBody = "\"parameters\": { \"a\": { \"type\": \"string\" }, \"b\": { \"type\": \"string\" } }, \"resources\": [], \"outputs\": { \"joined\": { \"type\": \"string\", \"value\": \"[concat(parameters('a'), parameters('b'))]\" } }";

    private static string ParameterFile(CliSandbox sandbox)
    {
        string path = Path.Combine(sandbox.TemplatesDir, "a.parameters.json");
        File.WriteAllText(path, "{ \"parameters\": { \"a\": { \"value\": \"from-file\" } } }", new UTF8Encoding(false));
        return path;
    }

    [Theory]
    [InlineData("what-if")]
    [InlineData("create")]
    public async Task ARepeatedP_WithAFileAndAValue_FollowedByAnOption_IsParsed_OnWhatIfAndCreate(string verb)
    {
        using var sandbox = new CliSandbox(nameof(ARepeatedP_WithAFileAndAValue_FollowedByAnOption_IsParsed_OnWhatIfAndCreate) + verb);
        sandbox.UsePreflightOk();
        string template = sandbox.WriteTemplate("p.entra.json", TemplateBody);
        string parameters = ParameterFile(sandbox);
        string[] arguments = ["deployment", verb, "-f", template, "-p", "@" + parameters, "-p", "b=from-inline", "-n", "named-by-option", "-o", "json", .. verb == "create" ? new[] { "-y" } : [], "--state-dir", sandbox.StateDir];

        var result = await CliRunner.RunAsync(arguments);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain(result.Diagnostics, d => d!["code"]!.GetValue<string>() is "MissingParameterValue" or "UnknownParameter");
        if (verb == "create")
        {
            Assert.True(File.Exists(sandbox.StatePath("named-by-option")), "the -n after the -p values was swallowed: " + result.StandardError);
            Assert.Equal("from-filefrom-inline", result.Json["outputs"]!["joined"]!["value"]!.GetValue<string>());
        }
    }

    [Theory]
    [InlineData("what-if")]
    [InlineData("create")]
    public async Task AStrayTokenAfterAPValue_IsAUsageError_NamingIt_AndNothingIsSent(string verb)
    {
        using var sandbox = new CliSandbox(nameof(AStrayTokenAfterAPValue_IsAUsageError_NamingIt_AndNothingIsSent) + verb);
        sandbox.UsePreflightOk();
        string template = sandbox.WriteTemplate("p.entra.json", TemplateBody);

        var result = await CliRunner.RunAsync("deployment", verb, "-f", template, "-p", "a=1", "b=2", "--state-dir", sandbox.StateDir);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("'b=2'", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(sandbox.RecordedCommands());
    }

    [Fact]
    public async Task AFileFollowedByAnUnknownOption_IsReportedByName_OnWhatIf()
    {
        using var sandbox = new CliSandbox(nameof(AFileFollowedByAnUnknownOption_IsReportedByName_OnWhatIf));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteTemplate("p.entra.json", TemplateBody);
        string parameters = ParameterFile(sandbox);

        var result = await CliRunner.RunAsync("deployment", "what-if", "-f", template, "-p", "@" + parameters, "--no-such-option", "x", "--state-dir", sandbox.StateDir);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--no-such-option", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStrayTokenAfterAKeyValue_IsAUsageError_OnStateAdopt_AndRepeatedKeysStillWork()
    {
        using var sandbox = new CliSandbox(nameof(AStrayTokenAfterAKeyValue_IsAUsageError_OnStateAdopt_AndRepeatedKeysStillWork));
        string assignment = "/providers/Microsoft.Entra/appRoleAssignments/" + Review1A.Gid(4);

        var stray = await CliRunner.RunAsync("state", "adopt", "-n", "dep", "--resource-id", assignment, "--object-id", "AzC8ELHhY0eVMkKAOQtymW1jTUZZ8qNOvDUlRZfSNxQ", "--key", "principalId=" + Review1A.Gid(5), "resourceId=" + Review1A.Gid(6), "--state-dir", sandbox.StateDir);

        Assert.Equal(1, stray.ExitCode);
        Assert.Contains("resourceId=", stray.StandardError, StringComparison.Ordinal);
        Assert.Contains("Unrecognized", stray.StandardError, StringComparison.Ordinal);

        var repeated = await CliRunner.RunAsync("state", "adopt", "-n", "dep", "--resource-id", assignment, "--object-id", "AzC8ELHhY0eVMkKAOQtymW1jTUZZ8qNOvDUlRZfSNxQ",
            "--key", "principalId=" + Review1A.Gid(5), "--key", "resourceId=" + Review1A.Gid(6), "--key", "appRoleId=" + Review1A.Gid(7), "-y", "--state-dir", sandbox.StateDir);

        Assert.DoesNotContain("Unrecognized", repeated.StandardError, StringComparison.Ordinal);
    }
}
