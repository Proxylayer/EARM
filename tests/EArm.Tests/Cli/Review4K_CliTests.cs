using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EArm.Tests.Engine;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// Review round 4, K3: <c>-p</c> / <c>--parameters</c> takes ONE value per occurrence on every command, so
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
        string[] arguments = ["deployment", verb, "-f", template, "-p", "@" + parameters, "-p", "b=from-inline", "-n", "named-by-option", "-o", "json", .. verb == "create" ? new[] { "-y" } : []];

        var result = await CliRunner.RunAsync(arguments);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain(result.Diagnostics, d => d!["code"]!.GetValue<string>() is "MissingParameterValue" or "UnknownParameter");
        if (verb == "create")
        {
            Assert.True(result.Json["deploymentName"]?.GetValue<string>() == "named-by-option", "the -n after the -p values was swallowed: " + result.StandardError);
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

        var result = await CliRunner.RunAsync("deployment", verb, "-f", template, "-p", "a=1", "b=2");

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

        var result = await CliRunner.RunAsync("deployment", "what-if", "-f", template, "-p", "@" + parameters, "--no-such-option", "x");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--no-such-option", result.StandardError, StringComparison.Ordinal);
    }
}
