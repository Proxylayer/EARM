using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EntraArm.Core;
using EntraArm.Core.Diagnostics;
using EntraArm.Core.Json;
using EntraArm.Core.Loading;
using EntraArm.Core.Secrets;
using EntraArm.Core.Validation;
using Xunit;

namespace EntraArm.Tests;

/// <summary>Binding of supplied parameter values (inline <c>-p</c> text, parameter file) to template definitions.</summary>
public sealed class ParameterBindingTests
{
    private static readonly TemplateLoader Loader = new();
    private static readonly TemplateValidator Validator = new();

    private static string TemplatePath => TestPaths.ValidFixture("parameter-types.entra.json");

    private static Task<TemplateDocument> LoadAsync(ParameterSet parameters)
    {
        return Loader.LoadAsync(TemplatePath, parameters, TestContext.Current.CancellationToken);
    }

    private static ParameterSet AllInline()
    {
        return ParameterSet.Empty
            .WithInline("count", "3")
            .WithInline("enabled", "true")
            .WithInline("settings", "{\"a\": 1}")
            .WithInline("list", "[1, 2]")
            .WithInline("secret", "hunter2")
            .WithInline("secretObj", "{\"pin\": 1234}");
    }

    [Fact]
    public async Task InlineValues_AreConvertedToTheDeclaredTypes()
    {
        var document = await LoadAsync(AllInline());

        Assert.Empty(document.LoadDiagnostics);

        var count = document.ParameterValues["count"];
        Assert.Equal(ParameterValueSource.Inline, count.Source);
        Assert.False(count.ConversionFailed);
        Assert.Equal(JsonValueKind.Number, count.Value!.GetValueKind());
        Assert.Equal(3, TemplateJson.AsInt64(count.Value));

        var enabled = document.ParameterValues["enabled"];
        Assert.Equal(JsonValueKind.True, enabled.Value!.GetValueKind());
        Assert.True(TemplateJson.AsBool(enabled.Value));

        var settings = Assert.IsType<JsonObject>(document.ParameterValues["settings"].Value);
        Assert.Equal(1, TemplateJson.AsInt64(settings["a"]));

        var list = Assert.IsType<JsonArray>(document.ParameterValues["list"].Value);
        Assert.Equal(2, list.Count);

        var diagnostics = Validator.Validate(document);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task InlineStringValue_IsTakenVerbatim()
    {
        var document = await LoadAsync(AllInline().WithInline("label", " spaced=value "));

        var label = document.ParameterValues["label"];
        Assert.Equal(ParameterValueSource.Inline, label.Source);
        Assert.Equal(" spaced=value ", TemplateJson.AsString(label.Value));
    }

    [Theory]
    [InlineData("count", "abc")]
    [InlineData("count", "1.5")]
    [InlineData("enabled", "yes")]
    [InlineData("settings", "[1]")]
    [InlineData("settings", "not json")]
    [InlineData("list", "{\"a\": 1}")]
    [InlineData("list", "1,2")]
    public async Task InlineValue_ThatCannotBeConverted_ReportsParameterValueTypeMismatch(string name, string rawText)
    {
        var document = await LoadAsync(AllInline().WithInline(name, rawText));

        var loadDiagnostic = Assert.Single(document.LoadDiagnostics);
        Assert.Equal(DiagnosticCodes.ParameterValueTypeMismatch, loadDiagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, loadDiagnostic.Severity);
        Assert.Equal(JsonPath.Of("parameters", name), loadDiagnostic.Path);
        Assert.NotNull(loadDiagnostic.Location);

        // The failed conversion is reported exactly once (by the loader, with the definition's location); the
        // semantic pass sees ConversionFailed and does not report the raw text as a second type mismatch.
        var bound = document.ParameterValues[name];
        Assert.True(bound.ConversionFailed);
        Assert.Equal(rawText, TemplateJson.AsString(bound.Value));
        Assert.All(document.ParameterValues.Values.Where(b => b.Name != name), b => Assert.False(b.ConversionFailed));

        var diagnostic = Assert.Single(Validator.Validate(document));
        Assert.Equal(DiagnosticCodes.ParameterValueTypeMismatch, diagnostic.Code);
        Assert.Equal(JsonPath.Of("parameters", name), diagnostic.Path);
        Assert.NotNull(diagnostic.Location);
    }

    [Fact]
    public async Task FileValue_OfTheWrongType_ReportsParameterValueTypeMismatchAgainstTheFile()
    {
        var file = ParameterSet.Parse(
            """{ "parameters": { "count": { "value": "3" }, "enabled": { "value": true }, "settings": { "value": {} }, "list": { "value": [] }, "secret": { "value": "s" }, "secretObj": { "value": {} } } }""",
            "values.parameters.json");

        var document = await LoadAsync(file);
        var diagnostics = Validator.Validate(document);

        Assert.Empty(document.LoadDiagnostics);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.ParameterValueTypeMismatch, diagnostic.Code);
        Assert.Equal("values.parameters.json", diagnostic.Source);
        Assert.Equal(JsonPath.Parse("parameters.count.value"), diagnostic.Path);
    }

    [Fact]
    public async Task MissingValues_ReportOneWarningPerParameterWithoutADefault()
    {
        var document = await LoadAsync(ParameterSet.Empty);
        var diagnostics = Validator.Validate(document);

        Assert.Equal(6, diagnostics.Count);
        Assert.All(diagnostics, d =>
        {
            Assert.Equal(DiagnosticCodes.MissingParameterValue, d.Code);
            Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        });

        string[] names = ["count", "enabled", "list", "secret", "secretObj", "settings"];
        Assert.Equal(names, diagnostics.Select(d => d.Path!.Last.Name).Order().ToList());
        Assert.False(document.ParameterValues["label"].IsMissing);
        Assert.Equal("orders", TemplateJson.AsString(document.ParameterValues["label"].Value));
    }

    [Fact]
    public async Task InlineValue_WinsOverTheParameterFile()
    {
        var file = ParameterSet.Parse("""{ "parameters": { "count": { "value": 1 } } }""", "values.parameters.json");
        var parameters = file.WithInline("COUNT", "2");

        var document = await LoadAsync(parameters);

        var count = document.ParameterValues["count"];
        Assert.Equal(ParameterValueSource.Inline, count.Source);
        Assert.Equal(2, TemplateJson.AsInt64(count.Value));
    }

    [Fact]
    public async Task UnknownInlineParameter_ReportsUnknownParameterWithoutAPath()
    {
        var document = await LoadAsync(AllInline().WithInline("bogus", "1"));

        var diagnostic = Assert.Single(document.LoadDiagnostics);
        Assert.Equal(DiagnosticCodes.UnknownParameter, diagnostic.Code);
        Assert.Null(diagnostic.Path);
        Assert.Null(diagnostic.Source);
        Assert.Contains("bogus", diagnostic.Message);
        Assert.Contains("-p", diagnostic.Message);
    }

    [Fact]
    public async Task UnknownFileParameter_ReportsUnknownParameterAgainstTheFile()
    {
        var file = ParameterSet.Parse("""{ "parameters": { "bogus": { "value": 1 } } }""", "values.parameters.json");

        var document = await LoadAsync(AllInline().Merge(file));

        var diagnostic = Assert.Single(document.LoadDiagnostics);
        Assert.Equal(DiagnosticCodes.UnknownParameter, diagnostic.Code);
        Assert.Equal(JsonPath.Parse("parameters.bogus"), diagnostic.Path);
        Assert.Equal("values.parameters.json", diagnostic.Source);
    }

    [Fact]
    public async Task KeyVaultReference_IsBoundAsAPendingReference()
    {
        var file = ParameterSet.Parse(
            """{ "parameters": { "secret": { "reference": { "keyVault": { "id": "contoso-kv" }, "secretName": "bg-initial", "secretVersion": "v7" } } } }""",
            "values.parameters.json");

        var document = await LoadAsync(AllInline().Merge(file));

        var secret = document.ParameterValues["secret"];
        Assert.Null(secret.Value);
        Assert.NotNull(secret.PendingReference);
        Assert.Equal("contoso-kv", secret.PendingReference.VaultName);
        Assert.Equal("bg-initial", secret.PendingReference.SecretName);
        Assert.Equal("v7", secret.PendingReference.SecretVersion);
        Assert.False(secret.IsMissing);
        Assert.True(secret.IsSecure);

        Assert.Empty(Validator.Validate(document));
    }

    [Fact]
    public async Task SecureValues_AreTaintedWhenBound()
    {
        var document = await LoadAsync(AllInline());

        var secret = document.ParameterValues["secret"];
        Assert.True(secret.IsSecure);
        Assert.True(SecretJson.IsSecret(secret.Value, out var secretValue));
        Assert.Equal("hunter2", secretValue.Value);
        Assert.Null(TemplateJson.AsString(secret.Value));

        var secretObj = Assert.IsType<JsonObject>(document.ParameterValues["secretObj"].Value);
        Assert.True(SecretJson.IsSecret(secretObj["pin"], out var pin));
        Assert.Equal(JsonValueKind.Number, pin.Kind);
        Assert.Equal("1234", pin.Value);
    }

    [Fact]
    public void SecureDefaultValue_IsTaintedUnlessItIsAnExpression()
    {
        const string json = """
            {
              "$schema": "https://entraarm.dev/schemas/2026-10-01/deploymentTemplate.json#",
              "contentVersion": "1.0.0.0",
              "parameters": {
                "literal":    { "type": "securestring", "defaultValue": "plain-default" },
                "computed":   { "type": "securestring", "defaultValue": "[newGuid()]" },
                "objectLit":  { "type": "secureObject", "defaultValue": { "pin": 42 } }
              },
              "resources": []
            }
            """;

        var document = Loader.Parse(json, null, ParameterSet.Empty);

        Assert.True(SecretJson.IsSecret(document.ParameterValues["literal"].Value));
        Assert.Equal("[newGuid()]", TemplateJson.AsString(document.ParameterValues["computed"].Value));
        Assert.True(SecretJson.ContainsSecret(document.ParameterValues["objectLit"].Value));
        Assert.Equal(ParameterValueSource.Default, document.ParameterValues["literal"].Source);

        var warnings = Validator.Validate(document).Where(d => d.Code == DiagnosticCodes.SecureParameterHasDefault).ToList();
        Assert.Equal(3, warnings.Count);
    }

    [Fact]
    public void AllowedValues_AreCheckedForSuppliedValues()
    {
        const string json = """
            {
              "$schema": "https://entraarm.dev/schemas/2026-10-01/deploymentTemplate.json#",
              "contentVersion": "1.0.0.0",
              "parameters": { "env": { "type": "string", "allowedValues": ["dev", "prod"] }, "size": { "type": "int", "allowedValues": [1, 2] } },
              "resources": []
            }
            """;

        var ok = Validator.Validate(Loader.Parse(json, null, ParameterSet.Empty.WithInline("env", "prod").WithInline("size", "2")));
        Assert.Empty(ok);

        var rejected = Validator.Validate(Loader.Parse(json, null, ParameterSet.Empty.WithInline("env", "staging").WithInline("size", "3")));
        Assert.Equal(2, rejected.Count);
        Assert.All(rejected, d => Assert.Equal(DiagnosticCodes.ParameterValueNotAllowed, d.Code));
    }

    [Fact]
    public void ParameterDefinition_ExposesConstraintsAndMetadata()
    {
        const string json = """
            {
              "$schema": "https://entraarm.dev/schemas/2026-10-01/deploymentTemplate.json#",
              "contentVersion": "1.0.0.0",
              "parameters": {
                "n": { "type": "int", "minValue": 1, "maxValue": 10, "defaultValue": 5, "metadata": { "description": "A number" } },
                "s": { "type": "string", "minLength": 1, "maxLength": 3, "allowedValues": ["a", "b"] }
              },
              "resources": []
            }
            """;

        var document = Loader.Parse(json, null, ParameterSet.Empty);

        var n = document.Parameters["n"];
        Assert.Equal(TemplateParameterType.Int, n.ParsedType);
        Assert.Equal(1, TemplateJson.AsInt64(n.MinValue));
        Assert.Equal(10, TemplateJson.AsInt64(n.MaxValue));
        Assert.True(n.HasDefaultValue);
        Assert.Equal("A number", TemplateJson.AsString(n.Metadata?["description"]));
        Assert.Equal(JsonPath.Parse("parameters.n"), n.Path);
        Assert.NotNull(n.Location);

        var s = document.Parameters["s"];
        Assert.Equal(1, TemplateJson.AsInt64(s.MinLength));
        Assert.Equal(3, TemplateJson.AsInt64(s.MaxLength));
        Assert.Equal(2, s.AllowedValues?.Count);
        Assert.False(s.HasDefaultValue);
        Assert.False(s.IsSecure);

        Assert.DoesNotContain(Validator.Validate(document), d => d.IsError);
    }

    [Theory]
    [InlineData("string", "\"x\"", true)]
    [InlineData("string", "1", false)]
    [InlineData("securestring", "\"x\"", true)]
    [InlineData("int", "1", true)]
    [InlineData("int", "1.5", false)]
    [InlineData("int", "\"1\"", false)]
    [InlineData("bool", "true", true)]
    [InlineData("bool", "\"true\"", false)]
    [InlineData("object", "{}", true)]
    [InlineData("object", "[]", false)]
    [InlineData("secureObject", "{}", true)]
    [InlineData("array", "[]", true)]
    [InlineData("array", "{}", false)]
    [InlineData("int", "\"[parameters('x')]\"", true)]
    public void TemplateParameterTypes_Matches_ChecksLiteralKinds(string typeName, string valueJson, bool expected)
    {
        Assert.True(TemplateParameterTypes.TryParse(typeName, out var type));

        Assert.Equal(expected, TemplateParameterTypes.Matches(JsonNode.Parse(valueJson), type));
    }

    [Fact]
    public void TemplateParameterTypes_Matches_RejectsNullAndHonoursSecretKinds()
    {
        Assert.False(TemplateParameterTypes.Matches(null, TemplateParameterType.String));
        Assert.True(TemplateParameterTypes.Matches(SecretJson.CreateString("x"), TemplateParameterType.SecureString));
        Assert.True(TemplateParameterTypes.Matches(SecretJson.Create(new SecretValue("1", JsonValueKind.Number)), TemplateParameterType.Int));
        Assert.False(TemplateParameterTypes.Matches(SecretJson.CreateString("x"), TemplateParameterType.Int));
    }

    [Theory]
    [InlineData("string", TemplateParameterType.String)]
    [InlineData("SecureString", TemplateParameterType.SecureString)]
    [InlineData(" secureobject ", TemplateParameterType.SecureObject)]
    [InlineData("INT", TemplateParameterType.Int)]
    public void TemplateParameterTypes_TryParse_IsCaseInsensitive(string text, TemplateParameterType expected)
    {
        Assert.True(TemplateParameterTypes.TryParse(text, out var type));
        Assert.Equal(expected, type);
        Assert.Contains(TemplateParameterTypes.CanonicalName(type), TemplateParameterTypes.CanonicalNames);
    }

    [Theory]
    [InlineData("")]
    [InlineData("integer")]
    [InlineData(null)]
    public void TemplateParameterTypes_TryParse_RejectsUnknownNames(string? text)
    {
        Assert.False(TemplateParameterTypes.TryParse(text, out _));
    }

    [Fact]
    public void BoundParameters_ExposeDefinitionAndSecurity()
    {
        var document = Loader.Parse(
            """{ "$schema": "https://entraarm.dev/schemas/2026-10-01/deploymentTemplate.json#", "contentVersion": "1.0.0.0", "parameters": { "pw": { "type": "securestring" } }, "resources": [] }""",
            null,
            ParameterSet.Empty.WithInline("pw", "x"));

        var bound = document.ParameterValues["pw"];
        IReadOnlyDictionary<string, BoundParameter> values = document.ParameterValues;
        Assert.Same(document.Parameters["pw"], bound.Definition);
        Assert.True(bound.IsSecure);
        Assert.Single(values);
    }
}
