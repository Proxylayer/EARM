using System.Collections.Generic;
using System.Linq;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Loading;
using EArm.Core.Validation;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Offline check of Conditional Access policy declarations: Graph refuses (400, 1012) a conditions.applications that declares includeApplications
/// together with includeUserActions or includeAuthenticationContextClassReferences.
/// </summary>
public sealed class ConditionalAccessPolicyCheckerTests
{
    private const string SchemaUri = "https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#";

    private static readonly TemplateLoader Loader = new();
    private static readonly TemplateValidator Validator = new();

    private static Diagnostic[] Conflicts(string applications)
    {
        string policy = "{ \"type\": \"Microsoft.Entra/conditionalAccess/policies\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"CA-1\", \"properties\": { "
            + "\"conditions\": { \"users\": { \"includeUsers\": [\"All\"] }, \"applications\": " + applications + " }, \"grantControls\": { \"builtInControls\": [\"mfa\"] } } }";
        string json = "{ \"$schema\": \"" + SchemaUri + "\", \"contentVersion\": \"1.0.0.0\", \"parameters\": { \"ctx\": { \"type\": \"string\", \"defaultValue\": \"c1\" }, \"apps\": { \"type\": \"array\", \"defaultValue\": [] } }, \"resources\": [ " + policy + " ] }";
        IReadOnlyList<Diagnostic> diagnostics = Validator.Validate(Loader.Parse(json, "inline.entra.json", ParameterSet.Empty));
        return diagnostics.Where(d => d.Code == DiagnosticCodes.ConflictingApplicationIncludes).ToArray();
    }

    [Theory]
    [InlineData("{ \"includeApplications\": [\"All\"], \"includeAuthenticationContextClassReferences\": [\"c10\"] }", "includeAuthenticationContextClassReferences")]
    [InlineData("{ \"includeApplications\": [\"All\"], \"includeAuthenticationContextClassReferences\": [\"[parameters('ctx')]\"] }", "includeAuthenticationContextClassReferences")]
    [InlineData("{ \"includeApplications\": [\"All\"], \"includeUserActions\": [\"urn:user:registersecurityinfo\"] }", "includeUserActions")]
    [InlineData("{ \"IncludeApplications\": [\"11111111-2222-3333-4444-555555555555\"], \"includeUserActions\": [\"urn:user:registerdevice\"] }", "includeUserActions")]
    public void IncludeApplications_WithUserActionsOrContexts_IsAnError(string applications, string member)
    {
        var error = Assert.Single(Conflicts(applications));

        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("1012", error.Message, System.StringComparison.Ordinal);
        Assert.EndsWith(member, error.Path!.ToString(), System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ \"includeApplications\": [\"All\"] }")]
    [InlineData("{ \"includeAuthenticationContextClassReferences\": [\"c10\"] }")]
    [InlineData("{ \"includeUserActions\": [\"urn:user:registersecurityinfo\"] }")]
    [InlineData("{ \"includeApplications\": [], \"includeAuthenticationContextClassReferences\": [\"c10\"] }")]
    [InlineData("{ \"includeApplications\": [\"All\"], \"includeUserActions\": [] }")]
    [InlineData("{ \"includeApplications\": [\" \"], \"includeUserActions\": [\"urn:user:registerdevice\"] }")]
    [InlineData("{ \"includeApplications\": \"[parameters('apps')]\", \"includeUserActions\": [\"urn:user:registerdevice\"] }")]
    public void OneKindOfInclude_OrEmptyOrComputedArrays_Pass(string applications)
    {
        Assert.Empty(Conflicts(applications));
    }
}
