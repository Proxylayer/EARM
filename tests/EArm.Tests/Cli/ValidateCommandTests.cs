using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Cli.Commands;
using EArm.Core;
using EArm.Core.Json;
using EArm.Core.Loading;
using Xunit;

namespace EArm.Tests;

/// <summary><c>earm deployment validate</c> and the command-line surface around it, driven in-process.</summary>
[Collection(CliCollection.Name)]
public sealed class ValidateCommandTests
{
    private const string FicType = "Microsoft.Entra/applications/federatedIdentityCredentials";
    private const string ParentDependency = "[resourceId('Microsoft.Entra/applications', variables('apiName'))]";

    private static string NestedSample => TestPaths.Sample("orders-api.entra.json");

    private static string SampleParameters => TestPaths.Sample("orders-api.parameters.json");

    private static string ParameterTypes => TestPaths.ValidFixture("parameter-types.entra.json");

    private static string Code(JsonNode? diagnostic)
    {
        return diagnostic!["code"]!.GetValue<string>();
    }

    // ---- help ----

    [Fact]
    public async Task BareCommand_PrintsHelpAndExitsZero()
    {
        var result = await CliRunner.RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Usage:", result.StandardOutput);
        Assert.Contains("deployment", result.StandardOutput);
        Assert.Contains("Microsoft Entra ID", result.StandardOutput);
        Assert.Contains("--output", result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task DeploymentGroup_PrintsHelpAndExitsZero()
    {
        var result = await CliRunner.RunAsync("deployment");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Usage:", result.StandardOutput);
        Assert.Contains("validate", result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task Validate_WithoutATemplateFile_FailsWithExitCodeOne()
    {
        var result = await CliRunner.RunAsync("deployment", "validate");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--template-file", result.StandardError);
    }

    [Fact]
    public async Task Validate_WithAnUnknownOutputFormat_FailsWithExitCodeOne()
    {
        var result = await CliRunner.RunAsync("deployment", "validate", "-f", NestedSample, "-o", "yaml");

        Assert.Equal(1, result.ExitCode);
        Assert.NotEqual(string.Empty, result.StandardError);
    }

    // ---- samples ----

    // Contract (lambdas, the Entra functions and user-defined functions are
    // implemented, so the sample produces no NotImplementedYet warning): offline validation of the samples without
    // parameter values succeeds (exit 0, no errors) with exactly the two MissingParameterValue warnings and one Info
    // for the output whose reference() target depends only on the missing parameter. Nothing else.
    [Theory]
    [InlineData("orders-api.entra.json")]
    [InlineData("orders-api.flat.entra.json")]
    public async Task Sample_ValidatesWithExitZeroAndOnlyTheExpectedWarnings(string fileName)
    {
        string path = TestPaths.Sample(fileName);

        var result = await CliRunner.ValidateJsonAsync(path);

        Assert.Equal(0, result.ExitCode);
        var report = result.Json;
        Assert.Equal("Succeeded", report["status"]!.GetValue<string>());
        Assert.Equal(0, report["errorCount"]!.GetValue<int>());
        Assert.Equal(2, report["warningCount"]!.GetValue<int>());
        Assert.Equal(path, report["templateFile"]!.GetValue<string>());
        Assert.Null(report["parameterFile"]);

        var diagnostics = result.Diagnostics;
        var missing = diagnostics.Where(d => Code(d) == "MissingParameterValue").ToList();
        Assert.Equal(2, missing.Count);
        Assert.All(missing, d =>
        {
            Assert.Equal("Warning", d!["severity"]!.GetValue<string>());
            Assert.Equal(path, d["source"]!.GetValue<string>());
            Assert.True(d["line"]!.GetValue<int>() > 0);
            Assert.True(d["column"]!.GetValue<int>() > 0);
        });
        Assert.Equal(["parameters.env", "parameters.initialPassword"], missing.Select(d => d!["path"]!.GetValue<string>()).Order().ToList());

        Assert.DoesNotContain(diagnostics, d => Code(d) == "NotImplementedYet");

        var info = Assert.Single(diagnostics, d => Code(d) == "ReferenceDependsOnMissingParameter");
        Assert.Equal("Info", info!["severity"]!.GetValue<string>());
        Assert.Equal("outputs.ciSecret.value", info["path"]!.GetValue<string>());

        Assert.Equal(3, diagnostics.Count);
        Assert.Equal(15, result.Resources.Count);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task Sample_WithItsParameterFile_HasNoDiagnostics()
    {
        var result = await CliRunner.ValidateJsonAsync(NestedSample, "-p", "@" + SampleParameters);

        Assert.Equal(0, result.ExitCode);
        var report = result.Json;
        Assert.Equal("Succeeded", report["status"]!.GetValue<string>());
        Assert.Equal(0, report["errorCount"]!.GetValue<int>());
        Assert.Equal(0, report["warningCount"]!.GetValue<int>());
        Assert.Equal(SampleParameters, report["parameterFile"]!.GetValue<string>());

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task Sample_WithAValueOutsideAllowedValues_ReportsParameterValueNotAllowed()
    {
        var result = await CliRunner.ValidateJsonAsync(NestedSample, "-p", "env=staging");

        Assert.Equal(2, result.ExitCode);
        var report = result.Json;
        Assert.Equal("Failed", report["status"]!.GetValue<string>());
        Assert.Equal(1, report["errorCount"]!.GetValue<int>());
        var error = Assert.Single(result.Diagnostics, d => Code(d) == "ParameterValueNotAllowed");
        Assert.Equal("Error", error!["severity"]!.GetValue<string>());
        Assert.Contains("env", error["message"]!.GetValue<string>());
        Assert.Single(result.Diagnostics, d => Code(d) == "MissingParameterValue" && d!["path"]!.GetValue<string>() == "parameters.initialPassword");
    }

    [Fact]
    public async Task Sample_WithAnUnknownParameter_ReportsUnknownParameter()
    {
        var result = await CliRunner.ValidateJsonAsync(NestedSample, "-p", "@" + SampleParameters, "-p", "bogus=1");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(1, result.Json["errorCount"]!.GetValue<int>());
        var error = Assert.Single(result.Diagnostics, d => d!["severity"]!.GetValue<string>() == "Error");
        Assert.Equal("UnknownParameter", Code(error));
        Assert.Contains("bogus", error!["message"]!.GetValue<string>());
        Assert.Equal(SampleParameters, result.Json["parameterFile"]!.GetValue<string>());
    }

    [Fact]
    public async Task Sample_WithAMalformedParameterToken_FailsWithExitCodeOne()
    {
        var result = await CliRunner.ValidateJsonAsync(NestedSample, "-p", "notatoken");

        Assert.Equal(1, result.ExitCode);
        var report = result.Json;
        Assert.Equal("Failed", report["status"]!.GetValue<string>());
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(ValidateCommand.InvalidArgumentCode, Code(error));
        Assert.Contains("notatoken", error!["message"]!.GetValue<string>());
        Assert.Empty(result.Resources);
    }

    [Fact]
    public async Task Sample_WithAMissingParameterFile_FailsWithExitCodeOne()
    {
        string missing = TestPaths.Sample("missing.parameters.json");

        var result = await CliRunner.ValidateJsonAsync(NestedSample, "-p", "@" + missing);

        Assert.Equal(1, result.ExitCode);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("FileNotFound", Code(error));
        Assert.Equal(missing, error!["source"]!.GetValue<string>());
    }

    [Fact]
    public async Task MissingTemplateFile_FailsWithExitCodeOne()
    {
        string missing = TestPaths.Sample("missing.entra.json");

        var result = await CliRunner.ValidateJsonAsync(missing);

        Assert.Equal(1, result.ExitCode);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("FileNotFound", Code(error));
        Assert.Equal(missing, result.Json["templateFile"]!.GetValue<string>());
    }

    // ---- inline parameter conversion ----

    [Fact]
    public async Task InlineParameters_AreConvertedToTheDeclaredTypes()
    {
        var result = await CliRunner.ValidateJsonAsync(
            ParameterTypes,
            "-p", "count=3",
            "-p", "enabled=true",
            "-p", "settings={\"a\": 1}",
            "-p", "list=[1, 2]",
            "-p", "secret=hunter2",
            "-p", "secretObj={\"pin\": 1234}");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, result.Json["warningCount"]!.GetValue<int>());
        Assert.Empty(result.Diagnostics);
        Assert.DoesNotContain("hunter2", result.StandardOutput);
        Assert.DoesNotContain("1234", result.StandardOutput);
    }

    [Theory]
    [InlineData("count=abc")]
    [InlineData("enabled=yes")]
    [InlineData("settings=[1]")]
    [InlineData("list={\"a\":1}")]
    public async Task InlineParameter_ThatCannotBeConverted_ReportsParameterValueTypeMismatch(string token)
    {
        var result = await CliRunner.ValidateJsonAsync(ParameterTypes, "-p", token);
        string name = token[..token.IndexOf('=')];

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(1, result.Json["errorCount"]!.GetValue<int>());
        var error = Assert.Single(result.Diagnostics, d => Code(d) == "ParameterValueTypeMismatch");
        Assert.Equal("parameters." + name, error!["path"]!.GetValue<string>());
        Assert.True(error["line"]!.GetValue<int>() > 0);
        Assert.True(error["column"]!.GetValue<int>() > 0);
        Assert.DoesNotContain(result.Diagnostics, d => Code(d) == "MissingParameterValue" && d!["path"]!.GetValue<string>() == "parameters." + name);
    }

    [Fact]
    public async Task ParameterTypesFixture_WithoutValues_WarnsPerMissingParameter()
    {
        var result = await CliRunner.ValidateJsonAsync(ParameterTypes);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(6, result.Json["warningCount"]!.GetValue<int>());
        Assert.All(result.Diagnostics, d => Assert.Equal("MissingParameterValue", Code(d)));
    }

    [Fact]
    public void BuildParameterSet_AppliesFilesThenInlineOverrides()
    {
        var set = ValidateCommand.BuildParameterSet(["@" + SampleParameters, "env=dev", "", "extra=1"], out var parameterFile);

        Assert.Equal(SampleParameters, parameterFile);
        Assert.Equal(3, set.Values.Count);
        Assert.Equal("dev", set.Values["env"].RawText);
        Assert.True(set.Values["env"].IsInline);
        Assert.True(set.Values["initialPassword"].IsKeyVaultReference);

        Assert.Throws<ArgumentException>(() => ValidateCommand.BuildParameterSet(["notatoken"], out _));
        Assert.Throws<TemplateLoadException>(() => ValidateCommand.BuildParameterSet(["@" + TestPaths.Sample("missing.json")], out _));
        Assert.Null(ValidateCommand.BuildParameterSet([], out var none).SourcePath);
        Assert.Null(none);
    }

    [Fact]
    public void BuildParameterSet_IsOrderBased_ALaterParameterFileOverridesAnEarlierInlineValue()
    {
        // az deployment group create --parameters: "Parameters are evaluated in order, so when a value is assigned twice, the latter value will be used."
        var fileLast = ValidateCommand.BuildParameterSet(["env=dev", "@" + SampleParameters], out _);
        var inlineLast = ValidateCommand.BuildParameterSet(["@" + SampleParameters, "env=dev"], out _);
        var inlineTwice = ValidateCommand.BuildParameterSet(["env=dev", "env=test"], out _);

        Assert.False(fileLast.Values["env"].IsInline);
        Assert.Equal("prod", fileLast.Values["env"].Value!.GetValue<string>());
        Assert.True(inlineLast.Values["env"].IsInline);
        Assert.Equal("dev", inlineLast.Values["env"].RawText);
        Assert.Equal("test", inlineTwice.Values["env"].RawText);
    }

    // ---- report shape ----

    [Fact]
    public async Task JsonReport_ListsEveryResourceWithIdentityAndDependencies()
    {
        var result = await CliRunner.ValidateJsonAsync(NestedSample);
        var resources = result.Resources;

        Assert.Equal(15, resources.Count);
        Assert.All(resources, r =>
        {
            Assert.NotNull(r!["type"]);
            Assert.Equal("2026-10-01-preview", r["apiVersion"]!.GetValue<string>());
            Assert.NotNull(r["name"]);
            Assert.NotNull(r["nested"]);
            Assert.NotNull(r["dependsOn"]);
            Assert.NotNull(r["inferredDependsOn"]);
            Assert.NotNull(r["path"]);
        });

        var user = Assert.Single(resources, r => r!["type"]!.GetValue<string>() == "Microsoft.Entra/users");
        Assert.Equal("bg-orders@contoso.com", user!["name"]!.GetValue<string>());
        Assert.Equal("/providers/Microsoft.Entra/users/bg-orders@contoso.com", user["resourceId"]!.GetValue<string>());
        Assert.False(user["nested"]!.GetValue<bool>());
        Assert.Empty(user["dependsOn"]!.AsArray());
        Assert.Empty(user["inferredDependsOn"]!.AsArray());
        Assert.Equal("resources[2]", user["path"]!.GetValue<string>());

        var fic = Assert.Single(resources, r => r!["type"]!.GetValue<string>() == FicType);
        Assert.Equal("[format('{0}/{1}', variables('apiName'), format('github-{0}', copyIndex('fics')))]", fic!["name"]!.GetValue<string>());
        Assert.Null(fic["resourceId"]);
        Assert.True(fic["nested"]!.GetValue<bool>());
        Assert.Empty(fic["dependsOn"]!.AsArray());
        Assert.Equal([ParentDependency], fic["inferredDependsOn"]!.AsArray().Select(d => d!.GetValue<string>()).ToList());
        Assert.Equal("resources[0].resources[0]", fic["path"]!.GetValue<string>());

        var policy = Assert.Single(resources, r => r!["type"]!.GetValue<string>() == "Microsoft.Entra/conditionalAccess/policies");
        Assert.Equal(3, policy!["dependsOn"]!.AsArray().Count);
        Assert.Equal("/providers/Microsoft.Entra/conditionalAccess/policies/CA-Orders-Admins-MFA", policy["resourceId"]!.GetValue<string>());

        var application = Assert.Single(resources, r => r!["type"]!.GetValue<string>() == "Microsoft.Entra/applications");
        Assert.Null(application!["resourceId"]);
        Assert.Equal(["[resourceId('Microsoft.Entra/users', 'bg-orders@contoso.com')]"], application["dependsOn"]!.AsArray().Select(d => d!.GetValue<string>()).ToList());

        Assert.Equal(2, resources.Count(r => r!["nested"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task JsonReport_FlatSample_HasNoNestedResources()
    {
        var result = await CliRunner.ValidateJsonAsync(TestPaths.Sample("orders-api.flat.entra.json"));

        Assert.All(result.Resources, r =>
        {
            Assert.False(r!["nested"]!.GetValue<bool>());
            Assert.Empty(r["inferredDependsOn"]!.AsArray());
        });
        var fic = Assert.Single(result.Resources, r => r!["type"]!.GetValue<string>() == FicType);
        Assert.Equal([ParentDependency], fic!["dependsOn"]!.AsArray().Select(d => d!.GetValue<string>()).ToList());
    }

    [Fact]
    public async Task JsonReport_ForAFailedLoad_HasTheDiagnosticAndNoResources()
    {
        var result = await CliRunner.ValidateJsonAsync(TestPaths.InvalidFixture("not-json.entra.json"));

        Assert.Equal(1, result.ExitCode);
        var report = result.Json;
        Assert.Equal("Failed", report["status"]!.GetValue<string>());
        Assert.Equal(1, report["errorCount"]!.GetValue<int>());
        Assert.Equal(0, report["warningCount"]!.GetValue<int>());
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("InvalidJson", Code(error));
        Assert.True(error!["line"]!.GetValue<int>() > 1);
        Assert.Empty(result.Resources);
    }

    [Fact]
    public async Task JsonReport_DiagnosticPathsAreArmStyle()
    {
        var result = await CliRunner.ValidateJsonAsync(TestPaths.InvalidFixture("unknown-resource-key.entra.json"));

        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("SchemaViolation", Code(error));
        Assert.Equal("resources[0].location", error!["path"]!.GetValue<string>());
        Assert.Equal(JsonPath.Parse("resources[0].location"), JsonPath.Parse(error["path"]!.GetValue<string>()));
    }

    [Fact]
    public async Task JsonReport_ForTheRetiredApiVersion_CarriesTheRenameHintAndExitsTwo()
    {
        var result = await CliRunner.ValidateJsonAsync(TestPaths.InvalidFixture("unknown-api-version-retired.entra.json"));

        Assert.Equal(2, result.ExitCode);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("UnknownApiVersion", Code(error));
        Assert.Equal("resources[0].apiVersion", error!["path"]!.GetValue<string>());
        string message = error["message"]!.GetValue<string>();
        Assert.StartsWith("apiVersion '2026-10-01' was renamed '2026-10-01-preview' (the format is in preview);", message, StringComparison.Ordinal);
        Assert.Contains("'2026-10-01-preview.beta' (Graph beta). Valid: 2026-10-01-preview, 2026-10-01-preview.beta", message, StringComparison.Ordinal);
    }

    // ---- table and none output ----

    [Fact]
    public async Task TableOutput_PrintsWarningsToStdoutAndASummaryLine()
    {
        var result = await CliRunner.RunAsync("deployment", "validate", "-f", ParameterTypes, "-p", "count=1", "-p", "enabled=false");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        string[] lines = result.StandardOutput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);
        Assert.StartsWith("warning MissingParameterValue [parameters.settings] (", lines[0]);
        Assert.StartsWith("warning MissingParameterValue [parameters.list] (", lines[1]);
        Assert.StartsWith("warning MissingParameterValue [parameters.secret] (", lines[2]);
        Assert.StartsWith("warning MissingParameterValue [parameters.secretObj] (", lines[3]);
        Assert.Equal("Validation succeeded: 1 resource(s), 4 warning(s).", lines[4]);
    }

    [Fact]
    public async Task TableOutput_PrintsErrorsToStderrWithTheFailureSummary()
    {
        string path = TestPaths.InvalidFixture("missing-api-version.entra.json");

        var result = await CliRunner.RunAsync("deployment", "validate", "-f", path, "-o", "table");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("error MissingApiVersion [resources[0].apiVersion] (", result.StandardError);
        Assert.Contains("Validation failed: 1 error(s), 0 warning(s).", result.StandardError);
        Assert.Equal(string.Empty, result.StandardOutput);
    }

    [Fact]
    public async Task TableOutput_PrefixesParameterFileFindingsWithTheFileName()
    {
        var result = await CliRunner.RunAsync("deployment", "validate", "-f", ParameterTypes, "-p", "@" + SampleParameters);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("orders-api.parameters.json: error UnknownParameter [parameters.env]", result.StandardError);
    }

    [Fact]
    public async Task NoneOutput_PrintsNothingButKeepsTheExitCode()
    {
        var ok = await CliRunner.RunAsync("deployment", "validate", "-f", ParameterTypes, "-o", "none");
        var failed = await CliRunner.RunAsync("deployment", "validate", "-f", TestPaths.InvalidFixture("unknown-type.entra.json"), "--output", "none");

        Assert.Equal(0, ok.ExitCode);
        Assert.Equal(string.Empty, ok.StandardOutput + ok.StandardError);
        Assert.Equal(2, failed.ExitCode);
        Assert.Equal(string.Empty, failed.StandardOutput + failed.StandardError);
    }

    [Fact]
    public async Task LongOptionNames_AreAccepted()
    {
        var result = await CliRunner.RunAsync("deployment", "validate", "--template-file", NestedSample, "--parameters", "@" + SampleParameters, "--output", "json");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, result.Json["errorCount"]!.GetValue<int>());
        Assert.Equal(SampleParameters, result.Json["parameterFile"]!.GetValue<string>());
        Assert.DoesNotContain(result.Diagnostics, d => Code(d) == "MissingParameterValue");
    }

    [Fact]
    public void ResourceIdsInTheReport_ParseBackThroughTheCatalog()
    {
        var catalog = EArm.Core.Types.ResourceTypeCatalog.Default;
        var loader = new TemplateLoader(catalog);
        var document = loader.Parse(System.IO.File.ReadAllText(NestedSample), NestedSample, ParameterSet.Empty);

        foreach (var resource in document.Resources.Where(r => r.StaticId is not null))
        {
            Assert.True(catalog.TryParseResourceId(resource.StaticId!.Id, out var parsed));
            Assert.Equal(resource.StaticId, parsed);
        }
    }
}
