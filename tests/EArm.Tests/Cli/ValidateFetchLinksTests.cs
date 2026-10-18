using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Cli;
using EArm.Cli.Commands;
using EArm.Core.Diagnostics;
using EArm.Core.Loading;
using EArm.Engine;
using EArm.Engine.Planning.Nested;
using EArm.Tests.Engine;
using Xunit;
using static EArm.Tests.Engine.NestedDeploymentTests;

namespace EArm.Tests.Cli;

/// <summary>
/// <c>deployment validate --fetch-links</c>: the offline validation downloads the https linked templates and parameter files of nested deployments
/// (through the per-run reader of what-if and create, here over a fake download) and validates them; without the option they are not fetched
/// (<c>LinkedTemplateNotFetched</c>, a warning).
/// </summary>
public sealed class ValidateFetchLinksTests
{
    private const string Schema = "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#";
    private const string ChildUrl = "https://templates.contoso.example/child.json?sv=2024&sig=secret";
    private const string ParametersUrl = "https://templates.contoso.example/child.parameters.json";

    private static string MainTemplate(string resources)
    {
        return $$"""{ "$schema": "{{Templates.SchemaUri}}", "contentVersion": "1.0.0.0", "resources": [ {{resources}} ] }""";
    }

    private static string Child(string body)
    {
        return $$"""{ "$schema": "{{Schema}}", "contentVersion": "1.0.0.0", {{body}} }""";
    }

    private static string Write(TestDirectory directory, string name, string text)
    {
        string path = directory.File(name);
        File.WriteAllText(path, text);
        return path;
    }

    private static string LinkedMain(TestDirectory directory, string? parametersLink = null)
    {
        string parameters = parametersLink is null ? string.Empty : $", \"parametersLink\": {{ \"uri\": \"{parametersLink}\" }}";
        return Write(directory, "main.entra.json", MainTemplate(Deployment("child", $$"""{ "mode": "Incremental", "templateLink": { "uri": "{{ChildUrl}}" }{{parameters}} }""")));
    }

    private static async Task<(int Exit, JsonArray Diagnostics, string Output)> ValidateAsync(string template, LinkedTemplateReader? linkedTemplates)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int exit = await ValidateCommand.RunAsync(template, [], OutputFormat.Json, stdout, stderr, TestContext.Current.CancellationToken, linkedTemplates: linkedTemplates);
        string output = stdout.ToString();
        var diagnostics = (JsonNode.Parse(output) as JsonObject)!["diagnostics"]!.AsArray();
        return (exit, diagnostics, output);
    }

    private static IEnumerable<string> Codes(JsonArray diagnostics)
    {
        return diagnostics.Select(d => d!["code"]!.GetValue<string>());
    }

    private static LinkedTemplateReader Serving(Dictionary<string, string> files, List<string>? calls = null)
    {
        return LinkedTemplateDownloads.ForRun((uri, _) =>
        {
            calls?.Add(uri.AbsoluteUri);
            return files.TryGetValue(uri.AbsoluteUri, out string? text)
                ? Task.FromResult(Encoding.UTF8.GetBytes(text))
                : Task.FromException<byte[]>(new TemplateLoadException("ParameterDownloadFailed", "The linked file could not be downloaded: the server answered 404 (Not Found).", LinkedTemplateReader.DisplayUrl(uri.AbsoluteUri)));
        });
    }

    [Fact]
    public async Task Without_the_option_an_https_link_is_not_fetched_and_is_a_warning()
    {
        using var directory = new TestDirectory(nameof(Without_the_option_an_https_link_is_not_fetched_and_is_a_warning));
        string main = LinkedMain(directory);

        var (exit, diagnostics, output) = await ValidateAsync(main, null);

        Assert.Equal(ExitCodes.Ok, exit);
        Assert.Contains(EngineDiagnosticCodes.LinkedTemplateNotFetched, Codes(diagnostics));
        Assert.Contains("--fetch-links", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_the_option_the_linked_template_is_downloaded_once_and_validated()
    {
        using var directory = new TestDirectory(nameof(With_the_option_the_linked_template_is_downloaded_once_and_validated));
        string main = LinkedMain(directory);
        var calls = new List<string>();
        var reader = Serving(new() { [ChildUrl] = Child($"\"resources\": [ {Group("[parameters('missing')]")} ]") }, calls);

        var (exit, diagnostics, output) = await ValidateAsync(main, reader);

        Assert.Equal(ExitCodes.ValidationFailed, exit);
        Assert.Contains(DiagnosticCodes.UndefinedParameter, Codes(diagnostics));
        Assert.DoesNotContain(EngineDiagnosticCodes.LinkedTemplateNotFetched, Codes(diagnostics));
        Assert.Equal([ChildUrl], calls);

        // The SAS token of the link is sent, never shown.
        Assert.DoesNotContain("sig=secret", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_the_option_a_linked_parameter_file_is_downloaded_too()
    {
        using var directory = new TestDirectory(nameof(With_the_option_a_linked_parameter_file_is_downloaded_too));
        string main = LinkedMain(directory, ParametersUrl);
        var reader = Serving(new()
        {
            [ChildUrl] = Child($"\"parameters\": {{ \"name\": {{ \"type\": \"string\", \"maxLength\": 5 }} }}, \"resources\": [ {Group("[parameters('name')]")} ]"),
            [ParametersUrl] = """{ "contentVersion": "1.0.0.0", "parameters": { "name": { "value": "far-too-long" } } }""",
        });

        var (exit, diagnostics, _) = await ValidateAsync(main, reader);

        // The value of the downloaded parameter file is checked against the downloaded template.
        Assert.Equal(ExitCodes.ValidationFailed, exit);
        Assert.Contains(diagnostics, d => d!["severity"]!.GetValue<string>().Equals("error", StringComparison.OrdinalIgnoreCase) && d["message"]!.GetValue<string>().Contains("name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_the_option_a_failed_download_is_an_error()
    {
        using var directory = new TestDirectory(nameof(With_the_option_a_failed_download_is_an_error));
        string main = LinkedMain(directory);

        var (exit, diagnostics, _) = await ValidateAsync(main, Serving([]));

        Assert.Equal(ExitCodes.ValidationFailed, exit);
        Assert.Contains(EngineDiagnosticCodes.LinkedTemplateUnavailable, Codes(diagnostics));
    }

    [Fact]
    public async Task The_command_line_accepts_the_option_and_still_reads_local_links()
    {
        using var directory = new TestDirectory(nameof(The_command_line_accepts_the_option_and_still_reads_local_links));
        Write(directory, "child.json", Child($"\"resources\": [ {Group("local-g")} ]"));
        string main = Write(directory, "main.entra.json", MainTemplate(Deployment("child", """{ "mode": "Incremental", "templateLink": { "relativePath": "child.json" } }""")));

        var result = await CliRunner.ValidateJsonAsync(main, "--fetch-links");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.DoesNotContain(result.Diagnostics, d => d!["severity"]!.GetValue<string>().Equals("error", StringComparison.OrdinalIgnoreCase));
    }
}
