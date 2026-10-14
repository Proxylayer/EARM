using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace EntraArm.Tests;

/// <summary>
/// One case per <c>tests/fixtures/templates/invalid/*.entra.json</c>, driven through the real command line:
/// the <c>.expected.json</c> sidecar lists the codes that must appear (subset match) and the exit code.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class ValidateFixtureTests
{
    private const string TemplateSuffix = ".entra.json";
    private const string ExpectedSuffix = ".expected.json";

    public static TheoryData<string> InvalidFixtures()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(TestPaths.InvalidFixtureDirectory, "*" + TemplateSuffix).Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(InvalidFixtures))]
    public async Task InvalidFixture_ProducesTheExpectedCodesAndExitCode(string fileName)
    {
        string templatePath = TestPaths.InvalidFixture(fileName);
        string expectedPath = templatePath[..^TemplateSuffix.Length] + ExpectedSuffix;
        var expected = JsonNode.Parse(await File.ReadAllTextAsync(expectedPath, TestContext.Current.CancellationToken)) as JsonObject;
        Assert.NotNull(expected);
        int expectedExitCode = expected["exitCode"]!.GetValue<int>();
        var expectedCodes = expected["expectedCodes"]!.AsArray().Select(c => c!.GetValue<string>()).ToList();
        Assert.NotEmpty(expectedCodes);

        var result = await CliRunner.ValidateJsonAsync(templatePath);

        var report = result.Json;
        var codes = result.Diagnostics.Select(d => d!["code"]!.GetValue<string>()).ToList();
        Assert.Equal(expectedExitCode, result.ExitCode);
        foreach (var code in expectedCodes)
        {
            Assert.Contains(code, codes);
        }

        Assert.Equal("Failed", report["status"]!.GetValue<string>());
        Assert.True(report["errorCount"]!.GetValue<int>() >= 1, "at least one error");
        Assert.Equal(templatePath, report["templateFile"]!.GetValue<string>());
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public void EveryFixture_HasASidecarAndViceVersa()
    {
        var templates = Directory.EnumerateFiles(TestPaths.InvalidFixtureDirectory, "*" + TemplateSuffix)
            .Select(f => Path.GetFileName(f)[..^TemplateSuffix.Length])
            .Order(StringComparer.Ordinal)
            .ToList();
        var sidecars = Directory.EnumerateFiles(TestPaths.InvalidFixtureDirectory, "*" + ExpectedSuffix)
            .Select(f => Path.GetFileName(f)[..^ExpectedSuffix.Length])
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(templates, sidecars);
        Assert.True(templates.Count >= 20, $"expected at least 20 invalid fixtures, found {templates.Count}");
    }

    [Fact]
    public void Fixtures_CoverTheAcceptanceCodes()
    {
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sidecar in Directory.EnumerateFiles(TestPaths.InvalidFixtureDirectory, "*" + ExpectedSuffix))
        {
            var expected = JsonNode.Parse(File.ReadAllText(sidecar))!.AsObject();
            foreach (var code in expected["expectedCodes"]!.AsArray())
            {
                covered.Add(code!.GetValue<string>());
            }
        }

        string[] required =
        [
            "MissingApiVersion", "UnknownResourceType", "UnknownApiVersion", "InvalidResourceName", "DuplicateResourceId",
            "DynamicMembershipConflict", "ParentChildTypeMismatch", "NestingTooDeep", "InvalidJson", "SchemaViolation",
            "UnrecognizedSchema", "InvalidContentVersion", "NameMappedPropertyConflict", "DuplicateDefinition",
        ];
        foreach (var code in required)
        {
            Assert.Contains(code, covered);
        }
    }
}
