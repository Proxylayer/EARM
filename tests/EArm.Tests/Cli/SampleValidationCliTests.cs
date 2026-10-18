using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>The four samples through <c>deployment validate</c>: schema, semantic and expression validation together report nothing.</summary>
public sealed class SampleValidationCliTests
{
    [Theory]
    [InlineData("orders-api.entra.json", "orders-api.parameters.json")]
    [InlineData("orders-api.flat.entra.json", "orders-api.parameters.json")]
    [InlineData("app-with-sp-and-secret.entra.json", "app-with-sp-and-secret.parameters.json")]
    [InlineData("app-certificates-extensions.entra.json", "app-certificates-extensions.parameters.json")]
    [InlineData("tenant-security.entra.json", "tenant-security.parameters.json")]
    [InlineData("authentication-methods.entra.json", "authentication-methods.parameters.json")]
    [InlineData("pim-eligibility.entra.json", "pim-eligibility.parameters.json")]
    [InlineData("pim-active.entra.json", "pim-active.parameters.json")]
    [InlineData("pim-policies.entra.json", "pim-policies.parameters.json")]
    [InlineData("pim-group-onboarding.entra.json", "pim-group-onboarding.parameters.json")]
    [InlineData("privileged-access.entra.json", "privileged-access.parameters.json")]
    [InlineData("scoped-access-groups.entra.json", "scoped-access-groups.parameters.json")]
    [InlineData("symbolic-names.entra.json", "symbolic-names.parameters.json")]
    [InlineData("nested-deployments.entra.json", "nested-deployments.parameters.json")]
    public async Task EverySample_WithItsParameterFile_PassesWithNoDiagnosticsAtAll(string template, string parameters)
    {
        var result = await CliRunner.ValidateJsonAsync(TestPaths.Sample(template), "-p", "@" + TestPaths.Sample(parameters));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Succeeded", result.Json["status"]!.GetValue<string>());
        Assert.Empty(result.Diagnostics);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task ValidFixtures_ThroughTheCommandLine_HaveNoErrors()
    {
        var fixtures = System.IO.Directory.EnumerateFiles(TestPaths.ValidFixtureDirectory, "*.entra.json").ToList();
        Assert.NotEmpty(fixtures);

        foreach (string fixture in fixtures)
        {
            var result = await CliRunner.ValidateJsonAsync(fixture);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(0, result.Json["errorCount"]!.GetValue<int>());
            Assert.DoesNotContain(result.Diagnostics, d => d!["severity"]!.GetValue<string>() == "Error");
        }
    }
}
