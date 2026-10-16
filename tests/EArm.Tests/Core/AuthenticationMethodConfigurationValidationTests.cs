using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Loading;
using EArm.Core.Validation;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Offline validation of <c>Microsoft.Entra/authenticationMethodConfigurations</c>: the method-name enum (the type's case-insensitive <c>namePattern</c>), the
/// shape rules the JSON Schema enforces (state, AAGUID format, Temporary Access Pass bounds, the certificate mode enum) and the cross-field rules of
/// <see cref="AuthenticationMethodConfigurationRules"/> the checker adds (<see cref="DiagnosticCodes.InvalidAuthenticationMethodConfiguration"/>); plus the
/// pure rules the provider shares (normalisation, the method table, the legacy FIDO2 properties).
/// </summary>
public sealed class AuthenticationMethodConfigurationValidationTests
{
    private const string SchemaUri = "https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#";

    private static readonly TemplateLoader Loader = new();
    private static readonly TemplateValidator Validator = new();

    private static IReadOnlyList<Diagnostic> Validate(string name, string properties)
    {
        string resource = "{ \"type\": \"Microsoft.Entra/authenticationMethodConfigurations\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", \"properties\": " + properties + " }";
        string json = "{ \"$schema\": \"" + SchemaUri + "\", \"contentVersion\": \"1.0.0.0\", \"parameters\": { \"guids\": { \"type\": \"array\", \"defaultValue\": [] }, \"minutes\": { \"type\": \"int\", \"defaultValue\": 5 } }, \"resources\": [ " + resource + " ] }";
        return Validator.Validate(Loader.Parse(json, "inline.entra.json", ParameterSet.Empty));
    }

    private static bool AlwaysJudged(JsonNode? node)
    {
        return false;
    }

    // ---- names ----

    [Theory]
    [InlineData("fido2")]
    [InlineData("FIDO2")]
    [InlineData("temporaryAccessPass")]
    [InlineData("x509Certificate")]
    [InlineData("microsoftAuthenticator")]
    [InlineData("Sms")]
    [InlineData("voice")]
    [InlineData("email")]
    [InlineData("softwareOath")]
    public void EverySupportedMethod_InAnyCasing_IsAValidName(string name)
    {
        Assert.DoesNotContain(Validate(name, "{ }"), d => d.IsError);
    }

    [Theory]
    [InlineData("hardwareOath")]
    [InlineData("fido")]
    [InlineData("fido2x")]
    public void AnUnsupportedMethod_IsAnInvalidResourceName(string name)
    {
        var error = Assert.Single(Validate(name, "{ }"), d => d.IsError);

        Assert.Equal(DiagnosticCodes.InvalidResourceName, error.Code);
    }

    // ---- schema shape ----

    [Theory]
    [InlineData("sms", "{ \"state\": \"on\" }", "properties.state")]
    [InlineData("fido2", "{ \"keyRestrictions\": { \"aaGuids\": [\"not-a-guid\"] } }", "properties.keyRestrictions.aaGuids[0]")]
    [InlineData("fido2", "{ \"keyRestrictions\": { \"enforcementType\": \"deny\" } }", "properties.keyRestrictions.enforcementType")]
    [InlineData("temporaryAccessPass", "{ \"defaultLength\": 7 }", "properties.defaultLength")]
    [InlineData("temporaryAccessPass", "{ \"maximumLifetimeInMinutes\": 50000 }", "properties.maximumLifetimeInMinutes")]
    [InlineData("x509Certificate", "{ \"authenticationModeConfiguration\": { \"x509CertificateAuthenticationDefaultMode\": \"strong\" } }", "properties.authenticationModeConfiguration.x509CertificateAuthenticationDefaultMode")]
    public void ShapeErrors_AreSchemaViolations_ReportedOnce(string name, string properties, string pathSuffix)
    {
        var errors = Validate(name, properties).Where(d => d.IsError).ToList();

        Assert.All(errors, d => Assert.Equal(DiagnosticCodes.SchemaViolation, d.Code));
        Assert.Contains(errors, d => d.Path!.ToString().EndsWith(pathSuffix, StringComparison.Ordinal));
    }

    // ---- cross-field rules ----

    [Theory]
    [InlineData("sms", "{ \"@odata.type\": \"#microsoft.graph.voiceAuthenticationMethodConfiguration\" }", "properties['@odata.type']", "must be '#microsoft.graph.smsAuthenticationMethodConfiguration'")]
    [InlineData("fido2", "{ \"keyRestrictions\": { \"isEnforced\": true, \"enforcementType\": \"allow\", \"aaGuids\": [] } }", "properties.keyRestrictions.aaGuids", "enforces an allow-list")]
    [InlineData("fido2", "{ \"keyRestrictions\": { \"isEnforced\": true, \"enforcementType\": \"allow\" } }", "properties.keyRestrictions.aaGuids", "with no aaGuids")]
    [InlineData("fido2", "{ \"passkeyProfiles\": [ { \"id\": \"p1\", \"keyRestrictions\": { \"isEnforced\": true, \"enforcementType\": \"allow\", \"aaGuids\": [] } } ] }", "properties.passkeyProfiles[0].keyRestrictions.aaGuids", "enforces an allow-list")]
    [InlineData("fido2", "{ \"passkeyProfiles\": [ { \"id\": \"p1\" }, { \"id\": \"P1\" } ] }", "properties.passkeyProfiles[1].id", "is listed more than once")]
    [InlineData("temporaryAccessPass", "{ \"minimumLifetimeInMinutes\": 120, \"defaultLifetimeInMinutes\": 60 }", "properties.minimumLifetimeInMinutes", "minimum <= default <= maximum")]
    [InlineData("temporaryAccessPass", "{ \"defaultLifetimeInMinutes\": 600, \"maximumLifetimeInMinutes\": 480 }", "properties.defaultLifetimeInMinutes", "must not be greater than properties.maximumLifetimeInMinutes")]
    [InlineData("sms", "{ \"includeTargets\": [ { \"id\": \"all_users\" }, { \"id\": \"ALL_USERS\" } ] }", "properties.includeTargets[1].id", "is listed more than once")]
    public void CrossFieldRules_AreInvalidAuthenticationMethodConfigurationErrors(string name, string properties, string pathSuffix, string message)
    {
        var error = Assert.Single(Validate(name, properties), d => d.IsError);

        Assert.Equal(DiagnosticCodes.InvalidAuthenticationMethodConfiguration, error.Code);
        Assert.EndsWith(pathSuffix, error.Path!.ToString(), StringComparison.Ordinal);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Location);
    }

    [Theory]
    [InlineData("fido2", "{ \"keyRestrictions\": { \"isEnforced\": true, \"enforcementType\": \"allow\", \"aaGuids\": \"[parameters('guids')]\" } }")]
    [InlineData("fido2", "{ \"keyRestrictions\": { \"isEnforced\": false, \"enforcementType\": \"allow\", \"aaGuids\": [] } }")]
    [InlineData("fido2", "{ \"keyRestrictions\": { \"isEnforced\": true, \"enforcementType\": \"block\", \"aaGuids\": [] } }")]
    [InlineData("temporaryAccessPass", "{ \"minimumLifetimeInMinutes\": \"[parameters('minutes')]\", \"defaultLifetimeInMinutes\": 60 }")]
    [InlineData("temporaryAccessPass", "{ \"minimumLifetimeInMinutes\": 60, \"defaultLifetimeInMinutes\": 60, \"maximumLifetimeInMinutes\": 60, \"defaultLength\": 48 }")]
    [InlineData("x509Certificate", "{ \"authenticationModeConfiguration\": { \"x509CertificateAuthenticationDefaultMode\": \"x509CertificateMultiFactor\", \"rules\": [] } }")]
    [InlineData("email", "{ \"@odata.type\": \"#microsoft.graph.emailAuthenticationMethodConfiguration\", \"state\": \"disabled\" }")]
    public void ValidOrComputedValues_Pass(string name, string properties)
    {
        Assert.DoesNotContain(Validate(name, properties), d => d.IsError);
    }

    [Fact]
    public void TheSample_ValidatesWithoutDiagnostics()
    {
        var parameters = ParameterSet.FromFile(TestPaths.Sample("authentication-methods.parameters.json"));
        var document = Loader.Parse(System.IO.File.ReadAllText(TestPaths.Sample("authentication-methods.entra.json")), "authentication-methods.entra.json", parameters);

        Assert.Empty(Validator.Validate(document));
    }

    // ---- the shared rules ----

    [Fact]
    public void TheMethodTable_DerivesGraphIdsAndODataTypes_AndOnlyThreeMethodsCanBeReset()
    {
        Assert.Equal(8, AuthenticationMethodConfigurationRules.Methods.Count);
        Assert.All(AuthenticationMethodConfigurationRules.Methods, m => Assert.Equal("#microsoft.graph." + m.Name + "AuthenticationMethodConfiguration", m.ODataType));
        Assert.All(AuthenticationMethodConfigurationRules.Methods, m => Assert.Equal(m.Name, m.GraphId, ignoreCase: true));
        Assert.Equal(["fido2", "temporaryAccessPass", "x509Certificate"], AuthenticationMethodConfigurationRules.Methods.Where(m => m.CanReset).Select(m => m.Name).ToArray());
        Assert.Same(AuthenticationMethodConfigurationRules.Find("fido2"), AuthenticationMethodConfigurationRules.Find("FIDO2"));
        Assert.Null(AuthenticationMethodConfigurationRules.Find("hardwareOath"));
        Assert.Contains("one of fido2, temporaryAccessPass", AuthenticationMethodConfigurationRules.NameProblem("x"), StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_LowerCasesAaGuidsAndEnums_AndDropsTheNameDerivedProperties()
    {
        var fido2 = AuthenticationMethodConfigurationRules.Find("fido2")!;
        var properties = JsonNode.Parse("{ \"id\": \"Fido2\", \"@odata.type\": \"#microsoft.graph.fido2AuthenticationMethodConfiguration\", \"State\": \"Enabled\", "
            + "\"keyRestrictions\": { \"enforcementType\": \"Allow\", \"aaGuids\": [\"CB69481E-8FF7-4039-93EC-0A2729A154A8\"] }, "
            + "\"passkeyProfiles\": [ { \"id\": \"p\", \"keyRestrictions\": { \"aaGuids\": [\"EE882879-721C-4913-9775-3DFCCE97072A\"] } } ] }")!.AsObject();

        AuthenticationMethodConfigurationRules.Normalize(fido2, properties, AlwaysJudged);

        Assert.False(properties.ContainsKey("id"));
        Assert.False(properties.ContainsKey("@odata.type"));
        Assert.Equal("enabled", properties["State"]!.GetValue<string>());
        Assert.Equal("allow", properties["keyRestrictions"]!["enforcementType"]!.GetValue<string>());
        Assert.Equal("cb69481e-8ff7-4039-93ec-0a2729a154a8", properties["keyRestrictions"]!["aaGuids"]![0]!.GetValue<string>());
        Assert.Equal("ee882879-721c-4913-9775-3dfcce97072a", properties["passkeyProfiles"]![0]!["keyRestrictions"]!["aaGuids"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Normalize_LeavesOpaqueValuesAlone()
    {
        var fido2 = AuthenticationMethodConfigurationRules.Find("fido2")!;
        var properties = JsonNode.Parse("{ \"state\": \"[parameters('s')]\", \"keyRestrictions\": { \"aaGuids\": [\"[parameters('g')]\", \"CB69481E-8FF7-4039-93EC-0A2729A154A8\"] } }")!.AsObject();

        AuthenticationMethodConfigurationRules.Normalize(fido2, properties, TemplateJson.IsExpression);

        Assert.Equal("[parameters('s')]", properties["state"]!.GetValue<string>());
        Assert.Equal(["[parameters('g')]", "cb69481e-8ff7-4039-93ec-0a2729a154a8"], properties["keyRestrictions"]!["aaGuids"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
    }

    [Fact]
    public void DeprecatedProperties_AreTheLegacyFido2Ones_AndOnlyForFido2()
    {
        var fido2 = AuthenticationMethodConfigurationRules.Find("fido2")!;
        var sms = AuthenticationMethodConfigurationRules.Find("sms")!;
        var legacy = JsonNode.Parse("{ \"isAttestationEnforced\": true, \"keyRestrictions\": { }, \"passkeyProfiles\": [] }")!.AsObject();

        Assert.Equal(["isAttestationEnforced", "keyRestrictions"], AuthenticationMethodConfigurationRules.DeprecatedProperties(fido2, legacy));
        Assert.Empty(AuthenticationMethodConfigurationRules.DeprecatedProperties(fido2, JsonNode.Parse("{ \"keyRestrictions\": null }")!.AsObject()));
        Assert.Empty(AuthenticationMethodConfigurationRules.DeprecatedProperties(sms, legacy));
    }
}
