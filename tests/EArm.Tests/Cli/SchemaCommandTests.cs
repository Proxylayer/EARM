using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Cli.Commands;
using EArm.Core.Types;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary><c>earm schema print</c>: name resolution, the embedded text it writes and the failure for an unknown name.</summary>
[Collection(CliCollection.Name)]
public sealed class SchemaCommandTests
{
    private static string TypeResource(string fileName)
    {
        return EmbeddedSchemas.TypeSchemaPrefix + fileName;
    }

    private static string Normalize(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    // ---- name resolution ----

    [Theory]
    [InlineData("applications", "applications.json")]
    [InlineData("Microsoft.Entra/applications", "applications.json")]
    [InlineData("microsoft.entra/APPLICATIONS", "applications.json")]
    [InlineData("  applications  ", "applications.json")]
    [InlineData("conditionalAccess.policies", "conditionalAccess.policies.json")]
    [InlineData("conditionalAccess/policies", "conditionalAccess.policies.json")]
    [InlineData("CONDITIONALACCESS.POLICIES", "conditionalAccess.policies.json")]
    [InlineData("Microsoft.Entra/conditionalAccess/policies", "conditionalAccess.policies.json")]
    [InlineData("Microsoft.Entra/conditionalAccess.policies", "conditionalAccess.policies.json")]
    [InlineData("applications/passwordCredentials", "applications.passwordCredentials.json")]
    [InlineData("applications.passwordCredentials", "applications.passwordCredentials.json")]
    [InlineData("Microsoft.Entra/applications/federatedIdentityCredentials", "applications.federatedIdentityCredentials.json")]
    [InlineData("oauth2PermissionGrants", "oauth2PermissionGrants.json")]
    public void TryResolve_AcceptsEveryDocumentedSpelling(string name, string expectedFile)
    {
        bool found = SchemaCommand.TryResolve(name, out string? resource, out string? error);

        Assert.True(found, error);
        Assert.Null(error);
        Assert.Equal(TypeResource(expectedFile), resource);
    }

    [Theory]
    [InlineData("template", EmbeddedSchemas.TemplateSchemaResource)]
    [InlineData("TEMPLATE", EmbeddedSchemas.TemplateSchemaResource)]
    [InlineData("parameters", EmbeddedSchemas.ParametersSchemaResource)]
    [InlineData("Parameters", EmbeddedSchemas.ParametersSchemaResource)]
    public void TryResolve_MapsTheEnvelopeSchemas(string name, string expectedResource)
    {
        Assert.True(SchemaCommand.TryResolve(name, out string? resource, out _));
        Assert.Equal(expectedResource, resource);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("Microsoft.Entra/spaceships")]
    [InlineData("conditionalAccess.nope")]
    [InlineData("")]
    [InlineData("Other.Provider/applications")]
    public void TryResolve_RejectsUnknownNames_ListingTheKnownTypes(string name)
    {
        bool found = SchemaCommand.TryResolve(name, out string? resource, out string? error);

        Assert.False(found);
        Assert.Null(resource);
        Assert.NotNull(error);
        Assert.Contains($"Unknown schema '{name}'", error, StringComparison.Ordinal);
        foreach (var definition in ResourceTypeCatalogTypes())
        {
            Assert.Contains(definition, error, StringComparison.Ordinal);
        }
    }

    private static string[] ResourceTypeCatalogTypes()
    {
        return ResourceTypeCatalog.Default.Types.Select(t => t.Type).ToArray();
    }

    // ---- schema print ----

    [Fact]
    public async Task PrintTemplate_EqualsTheEmbeddedText_AndIsTheDefault()
    {
        var explicitForm = await CliRunner.RunAsync("schema", "print", "template");
        var defaultForm = await CliRunner.RunAsync("schema", "print");

        Assert.Equal(0, explicitForm.ExitCode);
        Assert.Equal(string.Empty, explicitForm.StandardError);
        Assert.Equal(Normalize(EmbeddedSchemas.ReadText(EmbeddedSchemas.TemplateSchemaResource).TrimEnd('\n', '\r')), Normalize(explicitForm.StandardOutput.TrimEnd('\n', '\r')));
        Assert.Equal(explicitForm.StandardOutput, defaultForm.StandardOutput);
        Assert.StartsWith(EmbeddedSchemas.ReadText(EmbeddedSchemas.TemplateSchemaResource), explicitForm.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrintParameters_EqualsTheEmbeddedText()
    {
        var result = await CliRunner.RunAsync("schema", "print", "parameters");

        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith(EmbeddedSchemas.ReadText(EmbeddedSchemas.ParametersSchemaResource), result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmbeddedEnvelopeSchemas_AreTheCheckedInFiles()
    {
        // The embedded copies are linked from /schemas at build time; a drift here means a stale build or a copied file.
        string template = await File.ReadAllTextAsync(Path.Combine(TestPaths.RepoRoot, "schemas", "2026-10-01", "deploymentTemplate.json"), TestContext.Current.CancellationToken);
        string parameters = await File.ReadAllTextAsync(Path.Combine(TestPaths.RepoRoot, "schemas", "2026-10-01", "deploymentParameters.json"), TestContext.Current.CancellationToken);

        Assert.Equal(Normalize(template), Normalize(EmbeddedSchemas.ReadText(EmbeddedSchemas.TemplateSchemaResource)));
        Assert.Equal(Normalize(parameters), Normalize(EmbeddedSchemas.ReadText(EmbeddedSchemas.ParametersSchemaResource)));
    }

    [Fact]
    public async Task PrintEveryType_WritesItsEmbeddedSchema_WithItsIdAndOverlay()
    {
        foreach (var definition in ResourceTypeCatalog.Default.Types)
        {
            var result = await CliRunner.RunAsync("schema", "print", definition.Type);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardError);
            Assert.StartsWith(EmbeddedSchemas.ReadText(definition.SchemaResourceName), result.StandardOutput, StringComparison.Ordinal);
            var schema = JsonNode.Parse(result.StandardOutput)!.AsObject();
            Assert.Equal(EmbeddedSchemas.SchemaVersion, schema["$id"]!.GetValue<string>().Split('/')[4]);
            Assert.NotNull(schema["x-earm"]);
            Assert.NotNull(schema["properties"]);
        }
    }

    [Theory]
    [InlineData("applications", "applications.json")]
    [InlineData("Microsoft.Entra/applications", "applications.json")]
    [InlineData("conditionalAccess.policies", "conditionalAccess.policies.json")]
    [InlineData("Microsoft.Entra/conditionalAccess/policies", "conditionalAccess.policies.json")]
    [InlineData("applications/passwordCredentials", "applications.passwordCredentials.json")]
    public async Task PrintType_AcceptsEveryDocumentedSpelling(string name, string expectedFile)
    {
        var result = await CliRunner.RunAsync("schema", "print", name);

        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith(EmbeddedSchemas.ReadText(TypeResource(expectedFile)), result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrintUnknownName_ExitsOne_ListingTheTypesOnStandardError()
    {
        var result = await CliRunner.RunAsync("schema", "print", "Microsoft.Entra/spaceships");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("Unknown schema 'Microsoft.Entra/spaceships'", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("template", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("parameters", result.StandardError, StringComparison.Ordinal);
        foreach (string type in ResourceTypeCatalogTypes())
        {
            Assert.Contains(type, result.StandardError, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Schema_WithoutASubcommand_PrintsHelp()
    {
        var result = await CliRunner.RunAsync("schema");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("print", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("install", result.StandardOutput, StringComparison.Ordinal);
    }
}
