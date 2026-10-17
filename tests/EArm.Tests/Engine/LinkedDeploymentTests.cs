using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Loading;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Permissions;
using EArm.Engine.Planning;
using EArm.Engine.Planning.Nested;
using Xunit;
using static EArm.Tests.Engine.NestedDeploymentTests;

namespace EArm.Tests.Engine;

/// <summary>
/// Linked deployments (<c>templateLink</c>): <c>relativePath</c> next to the template, <c>parametersLink</c>, https links through a fake download,
/// cycles and <c>contentVersion</c>.
/// </summary>
public sealed class LinkedDeploymentTests
{
    private const string Schema = "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#";

    private static string Template(string body, string contentVersion = "1.0.0.0")
    {
        return $$"""{ "$schema": "{{Schema}}", "contentVersion": "{{contentVersion}}", {{body}} }""";
    }

    private static string MainTemplate(string resources)
    {
        return $$"""{ "$schema": "{{Templates.SchemaUri}}", "contentVersion": "1.0.0.0", "resources": [ {{resources}} ] }""";
    }

    private static string Link(string link, string? parameters = null)
    {
        string passed = parameters is null ? string.Empty : $", {parameters}";
        return $$"""{ "mode": "Incremental", "templateLink": {{link}}{{passed}} }""";
    }

    private static readonly string ChildTemplate = Template($$"""
        "parameters": { "name": { "type": "string" } },
        "resources": [ {{Group("[parameters('name')]")}} ],
        "outputs": { "made": { "type": "string", "value": "[concat('made-', parameters('name'))]" } }
        """);

    private static async Task<TemplateDocument> LoadAsync(string path)
    {
        return await new TemplateLoader().LoadAsync(path, ParameterSet.Empty, TestContext.Current.CancellationToken);
    }

    private static string Write(TestDirectory directory, string relative, string text)
    {
        string path = Path.Combine(directory.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>A download function serving fixed bodies by URL (query string included) and recording what was asked.</summary>
    private sealed class FakeDownloads
    {
        private readonly Dictionary<string, string> _bodies = new(StringComparer.Ordinal);

        public ConcurrentQueue<Uri> Requested { get; } = new();

        public FakeDownloads Serve(string url, string body)
        {
            _bodies[url] = body;
            return this;
        }

        public LinkedTemplateReader Reader => new(DownloadAsync);

        private Task<byte[]> DownloadAsync(Uri uri, CancellationToken cancellationToken)
        {
            Requested.Enqueue(uri);
            return _bodies.TryGetValue(uri.AbsoluteUri, out var body)
                ? Task.FromResult(Encoding.UTF8.GetBytes(body))
                : throw new TemplateLoadException("ParameterDownloadFailed", $"The linked file {LinkedTemplateReader.DisplayUrl(uri.AbsoluteUri)} could not be downloaded: the server answered 404 (Not Found).");
        }
    }

    [Fact]
    public async Task RelativePath_reads_the_file_next_to_the_template_and_passes_outputs_back()
    {
        using var directory = new TestDirectory(nameof(RelativePath_reads_the_file_next_to_the_template_and_passes_outputs_back));
        Write(directory, "children/child.json", ChildTemplate);
        string main = Write(directory, "main.entra.json", MainTemplate($$"""
            {{Deployment("child", Link("""{ "relativePath": "children/child.json", "contentVersion": "1.0.0.0" }""", """ "parameters": { "name": { "value": "linked-g" } } """))}},
            {{Group("reader", "[reference('child').outputs.made.value]")}}
            """));

        var host = new EngineHost();
        var (plan, result) = await host.CreateAsync(await LoadAsync(main));

        Assert.Equal(DeploymentStatus.Succeeded, result.Status);
        Assert.Equal("child", EngineHost.Instance(plan, "/providers/Microsoft.Entra/groups/linked-g").DeploymentPath);
        Assert.Equal("made-linked-g", host.Directory.Find("groups", "displayName", "reader")?["description"]?.GetValue<string>());
    }

    [Fact]
    public async Task ParametersLink_supplies_the_values_from_a_parameter_file()
    {
        using var directory = new TestDirectory(nameof(ParametersLink_supplies_the_values_from_a_parameter_file));
        Write(directory, "child.json", ChildTemplate);
        Write(directory, "child.parameters.json", """{ "contentVersion": "1.0.0.0", "parameters": { "name": { "value": "from-file" } } }""");
        string main = Write(directory, "main.entra.json", MainTemplate(Deployment("child", Link("""{ "relativePath": "child.json" }""", """ "parametersLink": { "uri": "child.parameters.json", "contentVersion": "1.0.0.0" } """))));

        var plan = await new EngineHost().ExpandAsync(await LoadAsync(main));

        Assert.Contains(plan.Instances, i => i.PlannedId.Id == "/providers/Microsoft.Entra/groups/from-file");
    }

    [Fact]
    public async Task Https_links_are_downloaded_and_a_relativePath_inside_resolves_against_the_url()
    {
        string grandchild = Template($"\"resources\": [ {Group("grand")} ]");
        string child = Template($$"""
            "resources": [ {{Group("child-g")}}, {{Deployment("grand", Link("""{ "relativePath": "deeper/grand.json" }"""))}} ]
            """);
        var downloads = new FakeDownloads()
            .Serve("https://templates.contoso.com/t/child.json?sv=1&sig=secret", child)
            .Serve("https://templates.contoso.com/t/deeper/grand.json?sv=1&sig=secret", grandchild);
        var document = Templates.Parse("\"resources\": [ " + Deployment("child", Link("""{ "uri": "https://templates.contoso.com/t/child.json", "queryString": "sv=1&sig=secret" }""")) + " ]");

        var host = new EngineHost();
        var plan = await host.ExpandAsync(document, options: host.Options() with { LinkedTemplates = downloads.Reader });

        Assert.Equal("child/grand", EngineHost.Instance(plan, "/providers/Microsoft.Entra/groups/grand").DeploymentPath);
        Assert.Equal(2, downloads.Requested.Count);
    }

    [Fact]
    public async Task A_failed_download_names_the_url_without_its_query_string()
    {
        var document = Templates.Parse("\"resources\": [ " + Deployment("child", Link("""{ "uri": "https://templates.contoso.com/missing.json?sig=topsecret" }""")) + " ]");

        var host = new EngineHost();
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => host.ExpandAsync(document, options: host.Options() with { LinkedTemplates = new FakeDownloads().Reader }));

        var error = Assert.Single(ex.Diagnostics, d => d.Code == EngineDiagnosticCodes.LinkedTemplateUnavailable);
        Assert.Contains("https://templates.contoso.com/missing.json", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("topsecret", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://templates.contoso.com/child.json", "https only")]
    [InlineData("https://user:pw@templates.contoso.com/child.json", "user name or password")]
    public async Task Unsafe_uris_are_refused_before_anything_is_downloaded(string uri, string reason)
    {
        var document = Templates.Parse("\"resources\": [ " + Deployment("child", Link($$"""{ "uri": "{{uri}}" }""")) + " ]");
        var downloads = new FakeDownloads();

        var host = new EngineHost();
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => host.ExpandAsync(document, options: host.Options() with { LinkedTemplates = downloads.Reader }));

        Assert.Contains(ex.Diagnostics, d => d.Code == EngineDiagnosticCodes.LinkedTemplateUnavailable && d.Message.Contains(reason, StringComparison.Ordinal));
        Assert.Empty(downloads.Requested);
    }

    [Fact]
    public async Task A_template_that_links_itself_is_a_cycle()
    {
        using var directory = new TestDirectory(nameof(A_template_that_links_itself_is_a_cycle));
        Write(directory, "loop.json", Template("\"resources\": [ " + Deployment("again", Link("""{ "relativePath": "loop.json" }""")) + " ]"));
        string main = Write(directory, "main.entra.json", MainTemplate(Deployment("first", Link("""{ "relativePath": "loop.json" }"""))));

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(async () => await new EngineHost().ExpandAsync(await LoadAsync(main)));

        Assert.Contains(ex.Diagnostics, d => d.Code == EngineDiagnosticCodes.LinkedTemplateCycle);
    }

    [Fact]
    public async Task A_contentVersion_that_differs_from_the_linked_file_is_refused()
    {
        using var directory = new TestDirectory(nameof(A_contentVersion_that_differs_from_the_linked_file_is_refused));
        Write(directory, "child.json", Template($"\"resources\": [ {Group("g")} ]", contentVersion: "2.0.0.0"));
        string main = Write(directory, "main.entra.json", MainTemplate(Deployment("child", Link("""{ "relativePath": "child.json", "contentVersion": "1.0.0.0" }"""))));

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(async () => await new EngineHost().ExpandAsync(await LoadAsync(main)));

        Assert.Contains(ex.Diagnostics, d => d.Code == EngineDiagnosticCodes.LinkedTemplateContentVersionMismatch);
    }

    [Fact]
    public async Task An_error_in_a_linked_template_points_into_that_file()
    {
        using var directory = new TestDirectory(nameof(An_error_in_a_linked_template_points_into_that_file));
        string child = Write(directory, "child.json", Template($"\"resources\": [ {Group("[parameters('missing')]")} ]"));
        string main = Write(directory, "main.entra.json", MainTemplate(Deployment("child", Link("""{ "relativePath": "child.json" }"""))));

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(async () => await new EngineHost().ExpandAsync(await LoadAsync(main)));

        var error = ex.Diagnostics.First(d => d.Code == DiagnosticCodes.UndefinedParameter);
        Assert.Equal(Path.GetFullPath(child), error.Source);
        Assert.Equal("resources[0].name", error.Path?.ToString());
        Assert.NotNull(error.Location);
    }

    [Fact]
    public async Task Offline_expansion_reads_local_links_and_leaves_https_links_with_a_warning()
    {
        using var directory = new TestDirectory(nameof(Offline_expansion_reads_local_links_and_leaves_https_links_with_a_warning));
        Write(directory, "child.json", Template($"\"resources\": [ {Group("local-g")} ]"));
        string main = Write(directory, "main.entra.json", MainTemplate($$"""
            {{Deployment("local", Link("""{ "relativePath": "child.json" }"""))}},
            {{Deployment("remote", Link("""{ "uri": "https://templates.contoso.com/child.json" }"""))}}
            """));

        var expansion = OfflineTemplateExpansion.Run(await LoadAsync(main), ResourceTypeCatalog.Default);

        Assert.True(expansion.Succeeded);
        Assert.Contains(expansion.Instances, i => i.PlannedId.Id == "/providers/Microsoft.Entra/groups/local-g");
        Assert.DoesNotContain(expansion.Instances, i => i.IsDeployment);
        Assert.Contains(expansion.Warnings, d => d.Code == EngineDiagnosticCodes.LinkedTemplateNotFetched);
    }

    [Fact]
    public async Task The_nested_deployments_sample_deploys_and_returns_the_outputs_of_its_nested_templates()
    {
        var parameters = ParameterSet.FromFile(TestPaths.Sample("nested-deployments.parameters.json"));
        var document = await new TemplateLoader().LoadAsync(TestPaths.Sample("nested-deployments.entra.json"), parameters, TestContext.Current.CancellationToken);

        var host = new EngineHost();
        var (plan, result) = await host.CreateAsync(document);

        Assert.Equal(DeploymentStatus.Succeeded, result.Status);
        Assert.Equal("portal", EngineHost.Instance(plan, "/providers/Microsoft.Entra/applications/contoso-dev-portal").DeploymentPath);
        var app = host.Directory.Find("applications", "uniqueName", "contoso-dev-portal");
        Assert.NotNull(app);
        Assert.Equal("Owned by the members of contoso-dev-portal-owners.", app["notes"]?.GetValue<string>());
        Assert.Equal(app["appId"]!.GetValue<string>(), result.Outputs["portalAppId"]!["value"]!.GetValue<string>());
        Assert.Equal(2, result.Outputs["teamGroupIds"]!["value"]!.AsArray().Count);
        Assert.Equal("Deployed by nested deployment team-search.", host.Directory.Find("groups", "uniqueName", "contoso-dev-team-search")?["description"]?.GetValue<string>());
    }

    [Fact]
    public void RelativePath_must_be_relative()
    {
        var ex = Assert.Throws<ArgumentException>(() => LinkedTemplateReader.ResolveRelative("C:/elsewhere/child.json", "C:/templates/main.json", null));

        Assert.Contains("absolute", ex.Message, StringComparison.Ordinal);
    }
}
