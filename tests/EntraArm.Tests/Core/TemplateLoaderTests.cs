using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EntraArm.Core;
using EntraArm.Core.Diagnostics;
using EntraArm.Core.Json;
using EntraArm.Core.Loading;
using Xunit;

namespace EntraArm.Tests;

public sealed class TemplateLoaderTests
{
    private const string SchemaUri = "https://entraarm.dev/schemas/2026-10-01/deploymentTemplate.json#";
    private const string FicType = "Microsoft.Entra/applications/federatedIdentityCredentials";
    private const string PasswordType = "Microsoft.Entra/applications/passwordCredentials";

    // The exact composed names a top-level declaration must use to be equivalent to the nested form.
    private const string ComposedFicName = "[format('{0}/{1}', variables('apiName'), format('github-{0}', copyIndex('fics')))]";
    private const string ComposedPasswordName = "[format('{0}/{1}', variables('apiName'), 'ci-secret')]";
    private const string ParentDependency = "[resourceId('Microsoft.Entra/applications', variables('apiName'))]";

    private static readonly TemplateLoader Loader = new();

    private static Task<TemplateDocument> LoadSampleAsync(string fileName)
    {
        return Loader.LoadAsync(TestPaths.Sample(fileName), ParameterSet.Empty, TestContext.Current.CancellationToken);
    }

    private static IReadOnlyList<(string? Type, string? ApiVersion, string? Name)> ResourceKeys(TemplateDocument document)
    {
        return document.Resources
            .Select(r => (r.Type, r.ApiVersion, r.Name))
            .OrderBy(k => k.Type, StringComparer.Ordinal)
            .ThenBy(k => k.Name, StringComparer.Ordinal)
            .ToList();
    }

    // ---- sample equivalence ----

    [Fact]
    public async Task NestedAndFlatSamples_LoadToTheSameTypeApiVersionNameSet()
    {
        var nested = await LoadSampleAsync("orders-api.entra.json");
        var flat = await LoadSampleAsync("orders-api.flat.entra.json");

        Assert.Empty(nested.LoadDiagnostics);
        Assert.Empty(flat.LoadDiagnostics);
        Assert.Equal(15, nested.Resources.Count);
        Assert.Equal(15, flat.Resources.Count);
        Assert.Equal(ResourceKeys(flat), ResourceKeys(nested));
    }

    [Fact]
    public async Task NestedAndFlatSamples_HaveIdenticalStaticIdsWhereNamesAreLiteral()
    {
        var nested = await LoadSampleAsync("orders-api.entra.json");
        var flat = await LoadSampleAsync("orders-api.flat.entra.json");

        static List<string> StaticIds(TemplateDocument document)
        {
            return document.Resources.Where(r => r.StaticId is not null).Select(r => r.StaticId!.Id).Order(StringComparer.Ordinal).ToList();
        }

        var nestedIds = StaticIds(nested);
        Assert.Equal(nestedIds, StaticIds(flat));

        string[] expected =
        [
            "/providers/Microsoft.Entra/administrativeUnits/Orders AU",
            "/providers/Microsoft.Entra/conditionalAccess/authenticationContextClassReferences/c10",
            "/providers/Microsoft.Entra/conditionalAccess/authenticationStrengths/Orders phishing-resistant",
            "/providers/Microsoft.Entra/conditionalAccess/namedLocations/Contoso offices",
            "/providers/Microsoft.Entra/conditionalAccess/policies/CA-Orders-Admins-MFA",
            "/providers/Microsoft.Entra/roleDefinitions/Orders Support",
            "/providers/Microsoft.Entra/users/bg-orders@contoso.com",
        ];
        Assert.Equal(expected, nestedIds);

        Assert.All(nested.Resources, r => Assert.Equal(r.HasLiteralName, r.StaticId is not null));
    }

    [Fact]
    public async Task NestedChildren_GetFullTypesAndComposedNames()
    {
        var nested = await LoadSampleAsync("orders-api.entra.json");

        var fic = Assert.Single(nested.Resources, r => r.Type == FicType);
        Assert.Equal(ComposedFicName, fic.Name);
        Assert.Equal("federatedIdentityCredentials", fic.DeclaredType);
        Assert.Equal("[format('github-{0}', copyIndex('fics'))]", fic.DeclaredName);
        Assert.True(fic.IsNested);
        Assert.Equal(2, fic.NestingDepth);
        Assert.Equal("Microsoft.Entra/applications", fic.NestedParentType);
        Assert.Equal(JsonPath.Parse("resources[0]"), fic.NestedParentPath);
        Assert.Equal(JsonPath.Parse("resources[0].resources[0]"), fic.Path);
        Assert.Null(fic.StaticId);
        Assert.NotNull(fic.Copy);
        Assert.Equal("fics", TemplateJson.AsString(fic.Copy.Name));
        Assert.Equal(2, TemplateJson.AsInt64(fic.Copy.Count));

        var password = Assert.Single(nested.Resources, r => r.Type == PasswordType);
        Assert.Equal(ComposedPasswordName, password.Name);
        Assert.Equal("ci-secret", password.DeclaredName);
        Assert.Equal(JsonPath.Parse("resources[0].resources[1]"), password.Path);
        Assert.Null(password.StaticId);
    }

    [Fact]
    public async Task FlatSample_SpellsTheSameComposedNamesAtTopLevel()
    {
        var flat = await LoadSampleAsync("orders-api.flat.entra.json");

        var fic = Assert.Single(flat.Resources, r => r.Type == FicType);
        Assert.Equal(ComposedFicName, fic.Name);
        Assert.Equal(ComposedFicName, fic.DeclaredName);
        Assert.Equal(FicType, fic.DeclaredType);
        Assert.False(fic.IsNested);
        Assert.Equal(1, fic.NestingDepth);
        Assert.Null(fic.NestedParentType);
        Assert.Null(fic.NestedParentPath);

        var password = Assert.Single(flat.Resources, r => r.Type == PasswordType);
        Assert.Equal(ComposedPasswordName, password.Name);
    }

    [Fact]
    public async Task InferredDependsOn_IsPopulatedOnlyForNestedChildren()
    {
        var nested = await LoadSampleAsync("orders-api.entra.json");
        var flat = await LoadSampleAsync("orders-api.flat.entra.json");

        foreach (var resource in nested.Resources)
        {
            if (resource.IsNested)
            {
                Assert.Equal([ParentDependency], resource.InferredDependsOn);
                Assert.Empty(resource.DependsOn);
            }
            else
            {
                Assert.Empty(resource.InferredDependsOn);
            }
        }

        Assert.Equal(2, nested.Resources.Count(r => r.IsNested));

        Assert.All(flat.Resources, r => Assert.Empty(r.InferredDependsOn));
        foreach (var child in flat.Resources.Where(r => r.Type is FicType or PasswordType))
        {
            Assert.Equal([ParentDependency], child.DependsOn);
        }
    }

    [Fact]
    public async Task ExplicitDependsOn_IsKeptVerbatim()
    {
        var nested = await LoadSampleAsync("orders-api.entra.json");

        var policy = Assert.Single(nested.Resources, r => r.Type == "Microsoft.Entra/conditionalAccess/policies");
        Assert.Equal(3, policy.DependsOn.Count);
        Assert.Contains("[resourceId('Microsoft.Entra/conditionalAccess/authenticationContextClassReferences', 'c10')]", policy.DependsOn);
        Assert.NotNull(policy.Condition);
        Assert.Equal("[equals(parameters('env'), 'prod')]", TemplateJson.AsString(policy.Condition));

        var roleAssignment = Assert.Single(nested.Resources, r => r.Type == "Microsoft.Entra/roleAssignments");
        Assert.StartsWith("Standalone form", roleAssignment.Comments);
    }

    [Fact]
    public async Task Envelope_IsReadIntoTheDocument()
    {
        var document = await LoadSampleAsync("orders-api.entra.json");

        Assert.Equal(SchemaUri, document.Schema);
        Assert.Equal("1.0.0.0", document.ContentVersion);
        Assert.Equal(TestPaths.Sample("orders-api.entra.json"), document.SourcePath);
        Assert.Equal(["$schema", "contentVersion", "parameters", "variables", "functions", "resources", "outputs"], document.TopLevelKeys);

        Assert.Equal(3, document.Parameters.Count);
        Assert.True(document.Parameters.ContainsKey("INITIALPASSWORD"), "parameter lookup is case-insensitive");
        Assert.True(document.Parameters["initialPassword"].IsSecure);
        Assert.True(document.Parameters["officeCidrs"].HasDefaultValue);
        Assert.Equal(TemplateParameterType.Array, document.Parameters["officeCidrs"].ParsedType);

        Assert.Equal(5, document.Variables.Count);
        Assert.True(document.Variables.ContainsKey("APINAME"), "variable lookup is case-insensitive");

        var ns = Assert.Single(document.Functions);
        Assert.Equal("c", ns.Namespace);
        var slug = Assert.Single(ns.Members).Value;
        Assert.Equal("slug", slug.Name);
        Assert.Equal("string", slug.OutputType);
        Assert.Equal(new UserFunctionParameter("s", "string"), Assert.Single(slug.Parameters));

        Assert.Equal(2, document.Outputs.Count);
        Assert.True(document.Outputs["ciSecret"].IsSecure);

        Assert.Equal(3, document.ParameterValues.Count);
        Assert.True(document.ParameterValues["env"].IsMissing);
        Assert.False(document.ParameterValues["officeCidrs"].IsMissing);
        Assert.Equal(ParameterValueSource.Default, document.ParameterValues["officeCidrs"].Source);
    }

    // ---- nesting ----

    [Fact]
    public async Task NestingDeeperThanTwoLevels_ReportsNestingTooDeepAndIgnoresDeeperResources()
    {
        var document = await Loader.LoadAsync(TestPaths.InvalidFixture("nesting-depth-3.entra.json"), ParameterSet.Empty, TestContext.Current.CancellationToken);

        var diagnostic = Assert.Single(document.LoadDiagnostics);
        Assert.Equal(DiagnosticCodes.NestingTooDeep, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(JsonPath.Parse("resources[0].resources[0].resources"), diagnostic.Path);
        Assert.NotNull(diagnostic.Location);
        Assert.Equal(document.SourcePath, diagnostic.Source);

        Assert.Equal(2, document.Resources.Count);
        Assert.DoesNotContain(document.Resources, r => r.DeclaredName == "too-deep");
        Assert.Equal("orders-api/github-main", document.Resources[1].Name);
        Assert.Equal("/providers/Microsoft.Entra/applications/orders-api/federatedIdentityCredentials/github-main", document.Resources[1].StaticId?.Id);
    }

    [Fact]
    public void NestedChild_WithLiteralNames_GetsLiteralParentChildNameAndStaticId()
    {
        const string json = """
            {
              "$schema": "https://entraarm.dev/schemas/2026-10-01/deploymentTemplate.json#",
              "contentVersion": "1.0.0.0",
              "resources": [
                {
                  "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "name": "orders-api",
                  "resources": [
                    { "type": "federatedIdentityCredentials", "apiVersion": "2026-10-01-preview", "name": "github-main" },
                    { "type": "Microsoft.Entra/applications/passwordCredentials", "apiVersion": "2026-10-01-preview", "name": "ci-secret" }
                  ]
                }
              ]
            }
            """;

        var document = Loader.Parse(json, null, ParameterSet.Empty);

        Assert.Empty(document.LoadDiagnostics);
        Assert.Equal(3, document.Resources.Count);

        var fic = document.Resources[1];
        Assert.Equal(FicType, fic.Type);
        Assert.Equal("orders-api/github-main", fic.Name);
        Assert.Equal(["[resourceId('Microsoft.Entra/applications', 'orders-api')]"], fic.InferredDependsOn);
        Assert.Equal(ResourceIdentifier.Create(FicType, "orders-api/github-main"), fic.StaticId);

        // A full type spelled inside a nested declaration is accepted when it sits under the parent's type.
        var password = document.Resources[2];
        Assert.Equal(PasswordType, password.Type);
        Assert.Equal("orders-api/ci-secret", password.Name);
        Assert.Equal(PasswordType, password.DeclaredType);
    }

    [Fact]
    public void EmptyNestedResourcesArray_IsNotNesting()
    {
        const string json = """
            {
              "$schema": "https://entraarm.dev/schemas/2026-10-01/deploymentTemplate.json#",
              "contentVersion": "1.0.0.0",
              "resources": [
                { "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "name": "a",
                  "resources": [ { "type": "federatedIdentityCredentials", "apiVersion": "2026-10-01-preview", "name": "f", "resources": [] } ] }
              ]
            }
            """;

        var document = Loader.Parse(json, null, ParameterSet.Empty);

        Assert.Empty(document.LoadDiagnostics);
        Assert.Equal(2, document.Resources.Count);
    }

    // ---- lexical tolerance ----

    [Fact]
    public void Parse_AcceptsCommentsTrailingCommasAndByteOrderMark()
    {
        const string json = """
            // line comment before the root
            {
              "$schema": "https://entraarm.dev/schemas/2026-10-01/deploymentTemplate.json#", /* block comment */
              "contentVersion": "1.0.0.0",
              "resources": [
                { "type": "Microsoft.Entra/groups", "apiVersion": "2026-10-01-preview", "name": "g1", "properties": { "displayName": "G1", }, },
              ],
            }
            """;
        byte[] bytes = [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(json)];

        var document = Loader.Parse(bytes, "inline.entra.json", ParameterSet.Empty);

        Assert.Empty(document.LoadDiagnostics);
        Assert.Equal("1.0.0.0", document.ContentVersion);
        var resource = Assert.Single(document.Resources);
        Assert.Equal("g1", resource.Name);
        Assert.Equal("/providers/Microsoft.Entra/groups/g1", resource.StaticId?.Id);
        Assert.Equal("G1", TemplateJson.AsString(resource.Properties?["displayName"]));

        // The BOM is not counted as a column; the comment line is still line 1.
        Assert.Equal(new SourceLocation(6, 5), document.LocationOf(resource.Path));
    }

    [Fact]
    public void Parse_AcceptsTheEscapedBracketLiteralAsAName()
    {
        const string json = """
            {
              "$schema": "https://entraarm.dev/schemas/2026-10-01/deploymentTemplate.json#",
              "contentVersion": "1.0.0.0",
              "resources": [ { "type": "Microsoft.Entra/groups", "apiVersion": "2026-10-01-preview", "name": "[[not-an-expression]" } ]
            }
            """;

        var document = Loader.Parse(json, null, ParameterSet.Empty);

        var resource = Assert.Single(document.Resources);
        Assert.True(resource.HasLiteralName);
        Assert.Equal("[[not-an-expression]", resource.Name);
        Assert.Equal("/providers/Microsoft.Entra/groups/[not-an-expression]", resource.StaticId?.Id);
    }

    // ---- locations ----

    [Fact]
    public async Task LocationOf_ResourcePaths_PointAtTheResourceObjectsInTheFile()
    {
        string path = TestPaths.Sample("orders-api.entra.json");
        var document = await Loader.LoadAsync(path, ParameterSet.Empty, TestContext.Current.CancellationToken);
        string[] lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(15, document.Resources.Count);
        foreach (var resource in document.Resources)
        {
            var location = document.LocationOf(resource.Path);
            Assert.True(location.HasValue, $"no location for {resource.Path}");
            Assert.Equal(location, resource.Location);
            Assert.Equal('{', lines[location!.Value.Line - 1][location.Value.Column - 1]);

            var nameLocation = document.LocationOf(resource.Path.Append("name"));
            Assert.True(nameLocation.HasValue, $"no location for {resource.Path}.name");
            Assert.StartsWith("\"name\"", lines[nameLocation!.Value.Line - 1][(nameLocation.Value.Column - 1)..]);
        }

        Assert.Equal(new SourceLocation(20, 5), document.LocationOf(JsonPath.Parse("resources[0]")));
        Assert.Equal(new SourceLocation(1, 1), document.LocationOf(JsonPath.Root));
    }

    [Fact]
    public void LocationOf_FallsBackToTheNearestAncestorAndNullForNull()
    {
        const string json = """
            {
              "$schema": "https://entraarm.dev/schemas/2026-10-01/deploymentTemplate.json#",
              "contentVersion": "1.0.0.0",
              "resources": [ { "type": "Microsoft.Entra/groups", "apiVersion": "2026-10-01-preview", "name": "g1" } ]
            }
            """;

        var document = Loader.Parse(json, null, ParameterSet.Empty);

        Assert.Equal(new SourceLocation(4, 18), document.LocationOf(JsonPath.Parse("resources[0]")));
        Assert.Equal(new SourceLocation(4, 18), document.LocationOf(JsonPath.Parse("resources[0].properties.displayName")));
        Assert.Null(document.LocationOf(null));
    }

    // ---- duplicate definitions ----

    [Fact]
    public void DuplicateParameterNamesIgnoringCase_ReportDuplicateDefinitionAndKeepTheFirst()
    {
        const string json = """
            {
              "$schema": "https://entraarm.dev/schemas/2026-10-01/deploymentTemplate.json#",
              "contentVersion": "1.0.0.0",
              "parameters": { "env": { "type": "string" }, "Env": { "type": "int" } },
              "resources": []
            }
            """;

        var document = Loader.Parse(json, null, ParameterSet.Empty);

        var diagnostic = Assert.Single(document.LoadDiagnostics);
        Assert.Equal(DiagnosticCodes.DuplicateDefinition, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(JsonPath.Parse("parameters.Env"), diagnostic.Path);
        Assert.Equal(new SourceLocation(4, 48), diagnostic.Location);

        var definition = Assert.Single(document.Parameters).Value;
        Assert.Equal("env", definition.Name);
        Assert.Equal("string", definition.Type);
    }

    [Theory]
    [InlineData("\"variables\": { \"a\": 1, \"A\": 2 }", "variables.A")]
    [InlineData("\"outputs\": { \"o\": { \"type\": \"string\", \"value\": 1 }, \"O\": { \"type\": \"string\", \"value\": 2 } }", "outputs.O")]
    public void DuplicateVariableOrOutputNamesIgnoringCase_ReportDuplicateDefinition(string section, string expectedPath)
    {
        string json = "{ \"$schema\": \"" + SchemaUri + "\", \"contentVersion\": \"1.0.0.0\", \"resources\": [], " + section + " }";

        var document = Loader.Parse(json, null, ParameterSet.Empty);

        var diagnostic = Assert.Single(document.LoadDiagnostics);
        Assert.Equal(DiagnosticCodes.DuplicateDefinition, diagnostic.Code);
        Assert.Equal(expectedPath, diagnostic.Path?.ToString());
    }

    // ---- fatal failures ----

    [Fact]
    public async Task LoadAsync_MissingFile_ThrowsFileNotFound()
    {
        string path = TestPaths.Sample("does-not-exist.entra.json");

        var exception = await Assert.ThrowsAsync<TemplateLoadException>(
            () => Loader.LoadAsync(path, ParameterSet.Empty, TestContext.Current.CancellationToken));

        Assert.Equal(DiagnosticCodes.FileNotFound, exception.Code);
        Assert.Equal(path, exception.Source);
        Assert.Contains(path, exception.Message);

        var diagnostic = exception.ToDiagnostic();
        Assert.Equal(DiagnosticCodes.FileNotFound, diagnostic.Code);
        Assert.True(diagnostic.IsError);
        Assert.Equal(path, diagnostic.Source);
    }

    [Fact]
    public void Parse_InvalidJson_ThrowsInvalidJsonWithALocation()
    {
        const string json = "{\n  \"contentVersion\": \"1.0.0.0\",\n  \"resources\": [ { \"type\": \"x\" ]\n}";

        var exception = Assert.Throws<TemplateLoadException>(() => Loader.Parse(json, "broken.entra.json", ParameterSet.Empty));

        Assert.Equal(DiagnosticCodes.InvalidJson, exception.Code);
        Assert.Equal("broken.entra.json", exception.Source);
        Assert.NotNull(exception.Location);
        Assert.Equal(3, exception.Location.Value.Line);
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(exception.InnerException);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    public void Parse_NonObjectRoot_ThrowsTemplateRootNotObject(string json)
    {
        var exception = Assert.Throws<TemplateLoadException>(() => Loader.Parse(json, null, ParameterSet.Empty));

        Assert.Equal(DiagnosticCodes.TemplateRootNotObject, exception.Code);
        Assert.Equal(JsonPath.Root, exception.Path);
    }

    [Fact]
    public void Parse_NullRoot_ThrowsTemplateRootNotObject()
    {
        var exception = Assert.Throws<TemplateLoadException>(() => Loader.Parse("null", null, ParameterSet.Empty));

        Assert.Equal(DiagnosticCodes.TemplateRootNotObject, exception.Code);
    }
}
