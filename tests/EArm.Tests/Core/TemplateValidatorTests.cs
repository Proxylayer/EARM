using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Loading;
using EArm.Core.Validation;
using Xunit;

namespace EArm.Tests;

/// <summary>Offline validation through the Core API (loader + validator), independent of the CLI.</summary>
public sealed class TemplateValidatorTests
{
    private const string SchemaUri = "https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#";

    private static readonly TemplateLoader Loader = new();
    private static readonly TemplateValidator Validator = new();

    /// <summary>Wraps <paramref name="body"/> (top-level members) in a well-formed envelope.</summary>
    private static string Envelope(string body)
    {
        return "{ \"$schema\": \"" + SchemaUri + "\", \"contentVersion\": \"1.0.0.0\", " + body + " }";
    }

    private static IReadOnlyList<Diagnostic> ValidateText(string json, ParameterSet? parameters = null)
    {
        return Validator.Validate(Loader.Parse(json, "inline.entra.json", parameters ?? ParameterSet.Empty));
    }

    private static async Task<IReadOnlyList<Diagnostic>> ValidateFileAsync(string path, ParameterSet? parameters = null)
    {
        var document = await Loader.LoadAsync(path, parameters ?? ParameterSet.Empty, TestContext.Current.CancellationToken);
        return Validator.Validate(document);
    }

    // ---- samples ----

    [Theory]
    [InlineData("orders-api.entra.json")]
    [InlineData("orders-api.flat.entra.json")]
    public async Task Sample_HasNoErrorsAndExactlyTwoMissingParameterWarnings(string fileName)
    {
        var diagnostics = await ValidateFileAsync(TestPaths.Sample(fileName));

        Assert.Equal(2, diagnostics.Count);
        Assert.All(diagnostics, d =>
        {
            Assert.Equal(DiagnosticCodes.MissingParameterValue, d.Code);
            Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
            Assert.Equal(TestPaths.Sample(fileName), d.Source);
            Assert.NotNull(d.Location);
        });
        Assert.Equal(["parameters.env", "parameters.initialPassword"], diagnostics.Select(d => d.Path!.ToString()).Order().ToList());
    }

    [Fact]
    public async Task Sample_WithItsParameterFile_HasNoDiagnostics()
    {
        var parameters = ParameterSet.FromFile(TestPaths.Sample("orders-api.parameters.json"));

        var diagnostics = await ValidateFileAsync(TestPaths.Sample("orders-api.entra.json"), parameters);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Sample_WithAValueOutsideAllowedValues_ReportsParameterValueNotAllowed()
    {
        var diagnostics = await ValidateFileAsync(TestPaths.Sample("orders-api.entra.json"), ParameterSet.Empty.WithInline("env", "staging"));

        var error = Assert.Single(diagnostics, d => d.IsError);
        Assert.Equal(DiagnosticCodes.ParameterValueNotAllowed, error.Code);
        Assert.Null(error.Path);
        Assert.Contains("env", error.Message);
    }

    // ---- de-duplication of schema findings ----

    [Fact]
    public async Task ApiVersionV1_0_ReportsExactlyOneUnknownApiVersion()
    {
        var diagnostics = await ValidateFileAsync(TestPaths.InvalidFixture("unknown-api-version-v1-0.entra.json"));

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.UnknownApiVersion, diagnostic.Code);
        Assert.Equal(JsonPath.Parse("resources[0].apiVersion"), diagnostic.Path);
        Assert.Contains("2026-10-01-preview", diagnostic.Message);
        Assert.Contains("2026-10-01-preview.beta", diagnostic.Message);
    }

    [Fact]
    public async Task RetiredApiVersion_ReportsExactlyOneUnknownApiVersionWithTheRenameHint()
    {
        var diagnostics = await ValidateFileAsync(TestPaths.InvalidFixture("unknown-api-version-retired.entra.json"));

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.UnknownApiVersion, diagnostic.Code);
        Assert.Equal(JsonPath.Parse("resources[0].apiVersion"), diagnostic.Path);
        Assert.Equal(
            "apiVersion '2026-10-01' was renamed '2026-10-01-preview' (the format is in preview); use '2026-10-01-preview' (Graph v1.0) or '2026-10-01-preview.beta' (Graph beta). Valid: 2026-10-01-preview, 2026-10-01-preview.beta.",
            diagnostic.Message);
    }

    [Fact]
    public async Task UnknownResourceKey_ReportsASingleSchemaViolationAtTheKey()
    {
        var diagnostics = await ValidateFileAsync(TestPaths.InvalidFixture("unknown-resource-key.entra.json"));

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.SchemaViolation, diagnostic.Code);
        Assert.Equal(JsonPath.Parse("resources[0].location"), diagnostic.Path);
        Assert.Contains("additionalProperties", diagnostic.Message);
        Assert.NotNull(diagnostic.Location);
    }

    [Fact]
    public async Task NestingTooDeep_IsReportedOnceEvenThoughTheSchemaRejectsItToo()
    {
        var diagnostics = await ValidateFileAsync(TestPaths.InvalidFixture("nesting-depth-3.entra.json"));

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.NestingTooDeep, diagnostic.Code);
    }

    [Fact]
    public void SchemaViolation_IsReportedForAWrongPropertyType()
    {
        var diagnostics = ValidateText(Envelope(
            """
            "resources": [ { "type": "Microsoft.Entra/groups", "apiVersion": "2026-10-01-preview", "name": "g", "properties": { "displayName": "G", "mailEnabled": "nope" } } ]
            """));

        Assert.All(diagnostics, d => Assert.Equal(DiagnosticCodes.SchemaViolation, d.Code));
        var diagnostic = Assert.Single(diagnostics, d => d.Path == JsonPath.Parse("resources[0].properties.mailEnabled"));
        Assert.Contains("boolean", diagnostic.Message);
        Assert.NotNull(diagnostic.Location);
    }

    [Fact]
    public void SchemaViolation_InsideProperties_DoesNotAlsoReportTheExpressionBranchAtTheParent()
    {
        // properties is anyOf [typed object schema, expression string]. When the typed branch fails at a deeper
        // location (mailEnabled), the expression branch's failure at the anyOf's own location ("properties should
        // be a string") is not actionable: the author wrote an object, not an expression.
        var diagnostics = ValidateText(Envelope(
            """
            "resources": [ { "type": "Microsoft.Entra/groups", "apiVersion": "2026-10-01-preview", "name": "g", "properties": { "displayName": "G", "mailEnabled": "nope" } } ]
            """));

        Assert.DoesNotContain(diagnostics, d => d.Path == JsonPath.Parse("resources[0].properties"));
    }

    [Fact]
    public void SchemaViolation_PropertiesNotAnObject_ReportsTheTypedBranchMessageOnce()
    {
        // properties is anyOf [typed object schema, expression string]; both branches fail for an integer. The typed
        // branch's message ("should be object") is the one reported, once.
        var diagnostics = ValidateText(Envelope(
            """
            "resources": [ { "type": "Microsoft.Entra/groups", "apiVersion": "2026-10-01-preview", "name": "g", "properties": 5 } ]
            """));

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.SchemaViolation, diagnostic.Code);
        Assert.Equal(JsonPath.Parse("resources[0].properties"), diagnostic.Path);
        Assert.Contains("object", diagnostic.Message);
        Assert.DoesNotContain("should be \"string\"", diagnostic.Message);
    }

    [Fact]
    public void SchemaViolation_IsNotReportedWhenAnotherAnyOfBranchPasses()
    {
        // Typed branches fail (an expression is not a boolean / array) but the expression branch passes.
        var diagnostics = ValidateText(Envelope(
            """
            "resources": [ { "type": "Microsoft.Entra/groups", "apiVersion": "2026-10-01-preview", "name": "g",
                             "properties": { "displayName": "G", "mailEnabled": "[false()]", "groupTypes": "[createArray()]", "securityEnabled": true } } ]
            """));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Expressions_SatisfyTypedPropertySchemas()
    {
        var diagnostics = ValidateText(Envelope(
            """
            "resources": [ { "type": "Microsoft.Entra/groups", "apiVersion": "2026-10-01-preview", "name": "g",
                             "condition": "[true()]", "copy": { "name": "c", "count": "[length(createArray(1))]" },
                             "properties": { "displayName": "[concat('a', 'b')]", "mailEnabled": "[false()]", "securityEnabled": true } } ]
            """));

        Assert.Empty(diagnostics);
    }

    // ---- envelope ----

    [Theory]
    [InlineData("\"resources\": [], \"extra\": 1", DiagnosticCodes.UnknownTopLevelProperty, "extra", DiagnosticSeverity.Warning)]
    [InlineData("\"resources\": [], \"parameters\": { \"pw\": { \"type\": \"securestring\", \"defaultValue\": \"x\" } }", DiagnosticCodes.SecureParameterHasDefault, "parameters.pw.defaultValue", DiagnosticSeverity.Warning)]
    [InlineData("\"resources\": [], \"parameters\": { \"n\": { \"type\": \"string\", \"minValue\": 1 } }", DiagnosticCodes.InvalidParameterConstraint, "parameters.n.minValue", DiagnosticSeverity.Error)]
    [InlineData("\"resources\": [], \"parameters\": { \"n\": { \"type\": \"int\", \"minValue\": 5, \"maxValue\": 1 } }", DiagnosticCodes.InvalidParameterConstraint, "parameters.n.minValue", DiagnosticSeverity.Error)]
    [InlineData("\"resources\": [], \"parameters\": { \"n\": { \"type\": \"string\", \"minLength\": -1 } }", DiagnosticCodes.InvalidParameterConstraint, "parameters.n.minLength", DiagnosticSeverity.Error)]
    [InlineData("\"resources\": [], \"parameters\": { \"n\": { \"type\": \"int\", \"defaultValue\": \"x\" } }", DiagnosticCodes.DefaultValueTypeMismatch, "parameters.n.defaultValue", DiagnosticSeverity.Error)]
    [InlineData("\"resources\": [], \"parameters\": { \"n\": { \"type\": \"string\", \"allowedValues\": [\"a\"], \"defaultValue\": \"b\" } }", DiagnosticCodes.DefaultValueNotAllowed, "parameters.n.defaultValue", DiagnosticSeverity.Error)]
    [InlineData("\"resources\": [], \"parameters\": { \"n\": { \"type\": \"int\", \"allowedValues\": [1, \"two\"] } }", DiagnosticCodes.AllowedValueTypeMismatch, "parameters.n.allowedValues[1]", DiagnosticSeverity.Error)]
    [InlineData("\"resources\": [], \"parameters\": { \"n\": { \"type\": \"blob\" } }", DiagnosticCodes.InvalidParameterType, "parameters.n.type", DiagnosticSeverity.Error)]
    [InlineData("\"resources\": [], \"outputs\": { \"o\": { \"value\": 1 } }", DiagnosticCodes.MissingRequiredProperty, "outputs.o.type", DiagnosticSeverity.Error)]
    [InlineData("\"resources\": [], \"outputs\": { \"o\": { \"type\": \"blob\", \"value\": 1 } }", DiagnosticCodes.InvalidOutputType, "outputs.o.type", DiagnosticSeverity.Error)]
    [InlineData("\"resources\": [], \"outputs\": { \"o\": { \"type\": \"string\" } }", DiagnosticCodes.MissingRequiredProperty, "outputs.o.value", DiagnosticSeverity.Error)]
    [InlineData("\"resources\": [], \"functions\": [ { \"namespace\": \"c\", \"members\": { \"f\": { \"output\": { \"type\": \"blob\", \"value\": 1 } } } } ]", DiagnosticCodes.InvalidOutputType, "functions[0].members.f.output.type", DiagnosticSeverity.Error)]
    [InlineData("\"resources\": [], \"functions\": [ { \"namespace\": \"c\", \"members\": { \"f\": { \"parameters\": [ { \"name\": \"p\", \"type\": \"blob\" } ], \"output\": { \"type\": \"string\", \"value\": 1 } } } } ]", DiagnosticCodes.InvalidParameterType, "functions[0].members.f.parameters[0].type", DiagnosticSeverity.Error)]
    [InlineData("\"resources\": [ { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"g\", \"copy\": { \"name\": \"c\", \"count\": -1 } } ]", DiagnosticCodes.InvalidCopy, "resources[0].copy.count", DiagnosticSeverity.Error)]
    [InlineData("\"resources\": [ { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"g\", \"copy\": { \"name\": \"c\", \"count\": 1, \"mode\": \"sideways\" } } ]", DiagnosticCodes.InvalidCopy, "resources[0].copy.mode", DiagnosticSeverity.Error)]
    [InlineData("\"resources\": [ { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"g\", \"copy\": { \"name\": \"c\", \"count\": 1, \"batchSize\": 0 } } ]", DiagnosticCodes.InvalidCopy, "resources[0].copy.batchSize", DiagnosticSeverity.Error)]
    [InlineData("\"resources\": [ { \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"no-at-sign\" } ]", DiagnosticCodes.InvalidResourceName, "resources[0].name", DiagnosticSeverity.Error)]
    [InlineData("\"resources\": [ { \"type\": \"Microsoft.Entra/servicePrincipals\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"not-a-guid\" } ]", DiagnosticCodes.InvalidResourceName, "resources[0].name", DiagnosticSeverity.Error)]
    [InlineData("\"resources\": [ { \"type\": \"Microsoft.Entra/applications/federatedIdentityCredentials\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"app/1bad\" } ]", DiagnosticCodes.InvalidResourceName, "resources[0].name", DiagnosticSeverity.Error)]
    [InlineData("\"resources\": [ { \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"a/\" } ]", DiagnosticCodes.InvalidResourceName, "resources[0].name", DiagnosticSeverity.Error)]
    [InlineData("\"resources\": [ { \"type\": \"Microsoft.Entra/administrativeUnits\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"au\", \"properties\": { \"membershipType\": \"Dynamic\", \"members\": [] } } ]", DiagnosticCodes.DynamicMembershipConflict, "resources[0].properties.members", DiagnosticSeverity.Error)]
    [InlineData("\"resources\": [ { \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"u@contoso.com\", \"properties\": { \"userPrincipalName\": \"other@contoso.com\" } } ]", DiagnosticCodes.NameMappedPropertyConflict, "resources[0].properties.userPrincipalName", DiagnosticSeverity.Error)]
    [InlineData("\"resources\": [ { \"apiVersion\": \"2026-10-01-preview\", \"name\": \"x\" } ]", DiagnosticCodes.MissingResourceType, "resources[0].type", DiagnosticSeverity.Error)]
    [InlineData("\"resources\": [ { \"type\": \"Microsoft.Entra/groups/widgets\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"g/w\" } ]", DiagnosticCodes.UnknownResourceType, "resources[0].type", DiagnosticSeverity.Error)]
    [InlineData("\"resources\": [ { \"type\": \"Microsoft.Entra/groups/federatedIdentityCredentials\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"g/f\" } ]", DiagnosticCodes.ParentChildTypeMismatch, "resources[0].type", DiagnosticSeverity.Error)]
    public void SemanticChecks_ReportTheExpectedCodeAtTheExpectedPath(string body, string expectedCode, string expectedPath, DiagnosticSeverity expectedSeverity)
    {
        var diagnostics = ValidateText(Envelope(body));

        var diagnostic = Assert.Single(diagnostics, d => d.Code == expectedCode);
        Assert.Equal(expectedPath, diagnostic.Path?.ToString());
        Assert.Equal(expectedSeverity, diagnostic.Severity);
        Assert.Equal("inline.entra.json", diagnostic.Source);
        Assert.NotNull(diagnostic.Location);
    }

    [Fact]
    public void MissingSchemaAndContentVersion_AreReportedSeparately()
    {
        var diagnostics = ValidateText("{ \"resources\": [] }");

        // "$schema" is not a simple identifier, so its ARM-style rendering is ['$schema'].
        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.UnrecognizedSchema && d.Path == JsonPath.Of("$schema"));
        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.UnrecognizedSchema && d.Path?.ToString() == "['$schema']");
        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.MissingRequiredProperty && d.Path?.ToString() == "contentVersion");
        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.SchemaViolation);
    }

    [Fact]
    public void MissingResources_IsReportedAsMissingRequiredProperty()
    {
        var diagnostics = ValidateText("{ \"$schema\": \"" + SchemaUri + "\", \"contentVersion\": \"1.0.0.0\" }");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.MissingRequiredProperty, diagnostic.Code);
        Assert.Equal("resources", diagnostic.Path?.ToString());
    }

    [Theory]
    [InlineData("1.0.0.0", true)]
    [InlineData("12.3.4.5", true)]
    [InlineData("1.0", false)]
    [InlineData("1.0.0.0-beta", false)]
    [InlineData("[variables('v')]", false)]
    public void ContentVersion_MustBeFourDotSeparatedNumbers(string contentVersion, bool valid)
    {
        var diagnostics = ValidateText("{ \"$schema\": \"" + SchemaUri + "\", \"contentVersion\": \"" + contentVersion + "\", \"resources\": [] }");

        if (valid)
        {
            Assert.Empty(diagnostics);
        }
        else
        {
            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal(DiagnosticCodes.InvalidContentVersion, diagnostic.Code);
        }
    }

    [Theory]
    [InlineData("https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#", true)]
    [InlineData("https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json", true)]
    [InlineData("HTTPS://SCHEMA.EARM.PROXYLAYER.NET/schemas/2026-10-01/deploymentTemplate.json#", true)]
    [InlineData("https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#", false)]
    public void Schema_MustBeTheEArmTemplateSchema(string schema, bool valid)
    {
        var diagnostics = ValidateText("{ \"$schema\": \"" + schema + "\", \"contentVersion\": \"1.0.0.0\", \"resources\": [] }");

        if (valid)
        {
            Assert.Empty(diagnostics);
        }
        else
        {
            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal(DiagnosticCodes.UnrecognizedSchema, diagnostic.Code);
        }
    }

    // ---- resources ----

    [Fact]
    public void PreviewApiVersion_IsAcceptedForEveryType()
    {
        var diagnostics = ValidateText(Envelope(
            """
            "resources": [
              { "type": "Microsoft.Entra/groups", "apiVersion": "2026-10-01-preview.beta", "name": "g", "properties": { "displayName": "G" } },
              { "type": "Microsoft.Entra/conditionalAccess/policies", "apiVersion": "2026-10-01-preview.beta", "name": "p", "properties": { "state": "disabled" } }
            ]
            """));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void DuplicateResourceId_ComparesTypesCaseInsensitivelyAndNamesCaseSensitively()
    {
        var duplicate = ValidateText(Envelope(
            """
            "resources": [
              { "type": "Microsoft.Entra/groups", "apiVersion": "2026-10-01-preview", "name": "g" },
              { "type": "microsoft.entra/GROUPS", "apiVersion": "2026-10-01-preview", "name": "g" }
            ]
            """));
        var error = Assert.Single(duplicate, d => d.Code == DiagnosticCodes.DuplicateResourceId);
        Assert.Equal("resources[1].name", error.Path?.ToString());

        var distinct = ValidateText(Envelope(
            """
            "resources": [
              { "type": "Microsoft.Entra/groups", "apiVersion": "2026-10-01-preview", "name": "g" },
              { "type": "Microsoft.Entra/groups", "apiVersion": "2026-10-01-preview", "name": "G" }
            ]
            """));
        Assert.DoesNotContain(distinct, d => d.Code == DiagnosticCodes.DuplicateResourceId);
    }

    [Fact]
    public void NameMappedProperty_EqualToTheName_IsNotAConflict()
    {
        var diagnostics = ValidateText(Envelope(
            """
            "resources": [ { "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "name": "orders-api", "properties": { "uniqueName": "orders-api", "displayName": "Orders" } } ]
            """));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void DirectoryRoles_OnARoleAssignableGroup_IsAccepted()
    {
        var diagnostics = ValidateText(Envelope(
            """
            "resources": [ { "type": "Microsoft.Entra/groups", "apiVersion": "2026-10-01-preview", "name": "g",
                             "properties": { "displayName": "G", "isAssignableToRole": true, "directoryRoles": [ { "role": "Global Reader", "directoryScopeId": "/" } ] } } ]
            """));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ExpressionNames_AreNotShapeCheckedOffline()
    {
        var diagnostics = ValidateText(Envelope(
            """
            "resources": [
              { "type": "Microsoft.Entra/servicePrincipals", "apiVersion": "2026-10-01-preview", "name": "[reference('app').appId]" },
              { "type": "Microsoft.Entra/users", "apiVersion": "2026-10-01-preview", "name": "[parameters('upn')]" }
            ],
            "parameters": { "upn": { "type": "string", "defaultValue": "u@contoso.com" } }
            """));

        Assert.Empty(diagnostics);
    }

    // ---- ordering and composition ----

    [Fact]
    public void Diagnostics_AreSortedByPositionInTheTemplate()
    {
        const string json = """
            {
              "$schema": "https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#",
              "contentVersion": "1.0.0.0",
              "outputs": { "o": { "type": "blob", "value": 1 } },
              "resources": [
                { "type": "Microsoft.Entra/groups", "apiVersion": "2020-01-01", "name": "g" },
                { "type": "Microsoft.Entra/users", "apiVersion": "2026-10-01-preview", "name": "no-at" }
              ]
            }
            """;

        var diagnostics = ValidateText(json);

        Assert.Equal(
            [DiagnosticCodes.InvalidOutputType, DiagnosticCodes.UnknownApiVersion, DiagnosticCodes.InvalidResourceName],
            diagnostics.Select(d => d.Code).ToList());
        Assert.Equal([4, 6, 7], diagnostics.Select(d => d.Location!.Value.Line).ToList());
    }

    [Fact]
    public void Validate_IncludesTheLoaderDiagnostics()
    {
        var document = Loader.Parse(Envelope("\"resources\": [], \"variables\": { \"a\": 1, \"A\": 2 }"), "inline.entra.json", ParameterSet.Empty);

        var diagnostics = Validator.Validate(document);

        Assert.Single(document.LoadDiagnostics);
        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.DuplicateDefinition);
    }

    [Fact]
    public void ParameterFileSchema_IsCheckedWhenAFileIsSupplied()
    {
        var parameters = ParameterSet.Parse("""{ "parameters": { "env": { "value": "dev", "unexpected": true } } }""", "values.parameters.json");

        var diagnostics = ValidateText(Envelope("\"resources\": [], \"parameters\": { \"env\": { \"type\": \"string\" } }"), parameters);

        Assert.All(diagnostics, d =>
        {
            Assert.Equal(DiagnosticCodes.SchemaViolation, d.Code);
            Assert.Equal("values.parameters.json", d.Source);
            Assert.Null(d.Location);
        });
        var diagnostic = Assert.Single(diagnostics, d => d.Path == JsonPath.Parse("parameters.env.unexpected"));
        Assert.Contains("additionalProperties", diagnostic.Message);
    }

    [Fact]
    public void ParameterFileSchema_DoesNotReportTheNonMatchingOneOfBranch()
    {
        // A parameter entry is oneOf [{ value }, { reference }]. When the value branch fails only because of an
        // extra key, the reference branch's complaint that 'value' is not allowed is noise.
        var parameters = ParameterSet.Parse("""{ "parameters": { "env": { "value": "dev", "unexpected": true } } }""", "values.parameters.json");

        var diagnostics = ValidateText(Envelope("\"resources\": [], \"parameters\": { \"env\": { \"type\": \"string\" } }"), parameters);

        Assert.DoesNotContain(diagnostics, d => d.Path == JsonPath.Parse("parameters.env.value"));
    }

    [Fact]
    public void Diagnostic_ToString_RendersSeverityCodePathLocationAndMessage()
    {
        var diagnostic = Diagnostic.Error("SomeCode", "Something happened.", JsonPath.Parse("resources[0].name"), new SourceLocation(12, 7), "t.entra.json");

        Assert.Equal("error SomeCode [resources[0].name] (12:7): Something happened.", diagnostic.ToString());
        Assert.Equal("warning W: m", Diagnostic.Warning("W", "m").ToString());
        Assert.Equal("info I: m", Diagnostic.Info("I", "m", JsonPath.Root).ToString());
    }
}
