using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Cli.Output;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// <c>-o jsonc|yaml|yamlc|tsv</c> and <c>--query</c>: the renderers (<see cref="YamlText"/>, <see cref="TsvText"/>, <see cref="JsonText"/>,
/// <see cref="JmesPathQuery"/>) and the end-to-end path through <c>Program.InvokeAsync</c>.
/// </summary>
public sealed class DocumentFormatTests
{
    private static string Sample => TestPaths.Sample("orders-api.entra.json");

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public void Yaml_WritesBlockStyle_WithSequencesAtTheKeysIndentation()
    {
        var document = JsonNode.Parse("{ \"status\": \"Succeeded\", \"count\": 2, \"ok\": true, \"none\": null, \"empty\": {}, \"list\": [], \"items\": [ { \"a\": 1, \"b\": [\"x\", \"y\"] }, \"plain\" ], \"nested\": { \"k\": \"v\" } }");

        string yaml = YamlText.Write(document, colors: false);

        Assert.Equal(
            "status: Succeeded\ncount: 2\nok: true\nnone: null\nempty: {}\nlist: []\nitems:\n- a: 1\n  b:\n  - x\n  - y\n- plain\nnested:\n  k: v\n",
            yaml);
    }

    [Theory]
    [InlineData("hello world", "hello world")]
    [InlineData("api://contoso-dev-api", "api://contoso-dev-api")]
    [InlineData("true", "'true'")]
    [InlineData("No", "'No'")]
    [InlineData("2026-10-01-preview", "'2026-10-01-preview'")]
    [InlineData("12", "'12'")]
    [InlineData("a: b", "'a: b'")]
    [InlineData("- item", "'- item'")]
    [InlineData("it's", "'it''s'")]
    [InlineData("", "''")]
    [InlineData("line\nbreak", "\"line\\nbreak\"")]
    [InlineData("@user", "'@user'")]
    public void Yaml_QuotesAStringWheneverItCouldBeReadBackAsSomethingElse(string value, string expected)
    {
        Assert.Equal(expected, YamlText.Quote(value));
    }

    [Fact]
    public void Tsv_FollowsTheAzureCliRules()
    {
        var rows = JsonNode.Parse("[ { \"b\": \"two\", \"a\": 1, \"c\": [1, 2, 3], \"d\": { \"x\": 1 }, \"e\": null, \"f\": true }, [\"x\", false] ]");

        Assert.Equal("1\ttwo\t3\t\tNone\tTrue\nx\tFalse\n", TsvText.Write(rows));
        Assert.Equal("true\n", TsvText.Write(JsonNode.Parse("true")));
        Assert.Equal("hello\n", TsvText.Write(JsonNode.Parse("\"hello\"")));
    }

    [Fact]
    public void JsonText_IndentsWithTwoSpaces_OrWritesOneLine_AndColoursOnlyWhenAsked()
    {
        var document = JsonNode.Parse("{ \"a\": [1, \"x\"], \"b\": {} }");

        Assert.Equal("{\n  \"a\": [\n    1,\n    \"x\"\n  ],\n  \"b\": {}\n}", JsonText.Write(document, indented: true, colors: false));
        Assert.Equal("{\"a\":[1,\"x\"],\"b\":{}}", JsonText.Write(document, indented: false, colors: false));
        string coloured = JsonText.Write(document, indented: false, colors: true);
        Assert.Contains(DocumentColors.Key + "\"a\"" + DocumentColors.Reset, coloured, StringComparison.Ordinal);
        Assert.Contains(DocumentColors.String + "\"x\"" + DocumentColors.Reset, coloured, StringComparison.Ordinal);
    }

    [Fact]
    public void JmesPath_ProjectsAndFilters_AndRejectsABadExpression()
    {
        var document = JsonNode.Parse("{ \"changes\": [ { \"changeType\": \"Create\", \"resourceId\": \"/a\", \"when\": \"2026-10-16T00:00:00+02:00\" }, { \"changeType\": \"NoChange\", \"resourceId\": \"/b\" } ] }");
        Assert.True(JmesPathQuery.TryCreate("changes[?changeType=='Create'].[resourceId, when]", out var query, out _));

        var result = query!.Apply(document);

        Assert.Equal("/a", result![0]![0]!.GetValue<string>());
        Assert.Equal("2026-10-16T00:00:00+02:00", result[0]![1]!.GetValue<string>());
        Assert.True(JmesPathQuery.TryCreate("nothing", out var none, out _));
        Assert.Null(none!.Apply(document));
        Assert.False(JmesPathQuery.TryCreate("changes[?", out _, out string? error));
        Assert.Contains("not a valid JMESPath expression", error, StringComparison.Ordinal);
    }

    [Fact]
    public void StructuredOutput_PassesNonJsonThrough_AndSaysTheQueryWasNotApplied()
    {
        Assert.True(JmesPathQuery.TryCreate("a", out var query, out _));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        new StructuredOutput(DocumentFormat.Yaml, query).Write("Usage: earm\n", stdout, stderr, colors: false);

        Assert.Equal("Usage: earm\n", stdout.ToString());
        Assert.Contains("--query was not applied", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_WithYaml_TsvAndJsonc_RendersTheSameDocument()
    {
        var json = await CliRunner.ValidateJsonAsync(Sample);
        var yaml = await CliRunner.RunAsync("deployment", "validate", "-f", Sample, "-o", "yaml");
        var tsv = await CliRunner.RunAsync("deployment", "validate", "-f", Sample, "-o", "tsv", "--query", "resources[].[type, name]");
        var jsonc = await CliRunner.RunAsync("deployment", "validate", "-f", Sample, "-o", "jsonc");

        Assert.Equal(json.ExitCode, yaml.ExitCode);
        Assert.Contains("\nresources:\n- type: Microsoft.Entra/", Lf(yaml.StandardOutput), StringComparison.Ordinal);
        Assert.Contains("Microsoft.Entra/applications\t", tsv.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", jsonc.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(json.Json.ToJsonString(), JsonNode.Parse(jsonc.StandardOutput)!.ToJsonString());
    }

    [Fact]
    public async Task Query_WithoutAnOutputFormat_SelectsJson_AndWithAnExplicitTable_IsAUsageError()
    {
        var count = await CliRunner.RunAsync("deployment", "validate", "-f", Sample, "--query", "length(resources)");
        var table = await CliRunner.RunAsync("deployment", "validate", "-f", Sample, "--query", "resources", "-o", "table");
        var bad = await CliRunner.RunAsync("deployment", "validate", "-f", Sample, "--query", "resources[?");

        Assert.Equal(0, count.ExitCode);
        Assert.True(int.Parse(count.StandardOutput.Trim(), System.Globalization.CultureInfo.InvariantCulture) > 0);
        Assert.Equal(1, table.ExitCode);
        Assert.Contains("--query needs a document output format", table.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, bad.ExitCode);
        Assert.Contains("not a valid JMESPath expression", bad.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_AppliesToAFailureDocumentToo_AndKeepsTheExitCode()
    {
        var result = await CliRunner.RunAsync("deployment", "validate", "-f", "missing.entra.json", "-o", "json", "--query", "diagnostics[0].code");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("\"FileNotFound\"", result.StandardOutput.Trim());
    }
}
