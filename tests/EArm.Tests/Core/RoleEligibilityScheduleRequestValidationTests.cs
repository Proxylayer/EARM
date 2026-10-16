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
/// Offline validation of <c>Microsoft.Entra/roleEligibilityScheduleRequests</c>: the GUID name (the type's <c>namePattern</c>), the shapes the JSON Schema
/// enforces (GUIDs, the scope pattern, object types) and the rules of <see cref="RoleEligibilityScheduleRules"/> the checker adds
/// (<see cref="DiagnosticCodes.InvalidRoleEligibilityScheduleRequest"/>): ARM-only and unknown properties, the exclusive pairs, requestType, and the
/// schedule (expiration combinations, ISO 8601 values, end after start); plus the pure helpers the provider shares.
/// </summary>
public sealed class RoleEligibilityScheduleRequestValidationTests
{
    private const string SchemaUri = "https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#";
    private const string Principal = "4b9e2d1c-0000-4000-8000-000000000001";
    private const string GlobalReader = "f2ef992c-3afb-46b9-b7cf-a126ee74c451";

    private static readonly TemplateLoader Loader = new();
    private static readonly TemplateValidator Validator = new();

    private static IReadOnlyList<Diagnostic> Validate(string properties, string name = "[guid('alice', 'reader')]")
    {
        string resource = "{ \"type\": \"Microsoft.Entra/roleEligibilityScheduleRequests\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", \"properties\": " + properties + " }";
        string json = "{ \"$schema\": \"" + SchemaUri + "\", \"contentVersion\": \"1.0.0.0\", \"parameters\": { \"end\": { \"type\": \"string\", \"defaultValue\": \"2020-01-01T00:00:00Z\" } }, \"resources\": [ " + resource + " ] }";
        return Validator.Validate(Loader.Parse(json, "inline.entra.json", ParameterSet.Empty));
    }

    private static string Props(string extra = "", string role = "\"roleDefinitionId\": \"" + GlobalReader + "\"")
    {
        return "{ \"principalId\": \"" + Principal + "\", " + role + extra + " }";
    }

    // ---- valid ----

    [Theory]
    [InlineData("")]
    [InlineData(", \"requestType\": \"AdminAssign\", \"justification\": \"j\", \"ticketInfo\": { \"ticketNumber\": \"1\", \"ticketSystem\": \"s\" }")]
    [InlineData(", \"requestType\": \"adminassign\", \"scheduleInfo\": { \"expiration\": { \"type\": \"NoExpiration\" } }")]
    [InlineData(", \"scheduleInfo\": { \"startDateTime\": \"2026-01-01T00:00:00Z\", \"expiration\": { \"type\": \"AfterDateTime\", \"endDateTime\": \"2027-01-01T00:00:00Z\" } }")]
    [InlineData(", \"scheduleInfo\": { \"expiration\": { \"type\": \"afterDuration\", \"duration\": \"P365D\" } }")]
    [InlineData(", \"scheduleInfo\": { \"expiration\": { \"type\": \"AfterDuration\", \"duration\": \"PT8H\" } }")]
    [InlineData(", \"directoryScopeId\": \"/administrativeUnits/1b2c3d4e-0000-4000-8000-000000000001\"")]
    [InlineData(", \"administrativeUnit\": \"Orders AU\"")]
    [InlineData(", \"appScopeId\": \"/\"")]
    [InlineData(", \"scheduleInfo\": { \"startDateTime\": \"2026-01-01T00:00:00Z\", \"expiration\": { \"type\": \"AfterDateTime\", \"endDateTime\": \"[parameters('end')]\" } }")]
    public void ValidOrComputedDeclarations_Pass(string extra)
    {
        Assert.DoesNotContain(Validate(Props(extra)), d => d.IsError);
    }

    [Fact]
    public void TheRoleSugar_Passes()
    {
        Assert.DoesNotContain(Validate(Props(role: "\"role\": \"Global Reader\"")), d => d.IsError);
    }

    [Fact]
    public void TheSample_ValidatesWithoutDiagnostics()
    {
        var parameters = ParameterSet.FromFile(TestPaths.Sample("pim-eligibility.parameters.json"));
        var document = Loader.Parse(System.IO.File.ReadAllText(TestPaths.Sample("pim-eligibility.entra.json")), "pim-eligibility.entra.json", parameters);

        Assert.Empty(Validator.Validate(document));
    }

    // ---- name, schema shape ----

    [Fact]
    public void ANameThatIsNotAGuid_IsAnInvalidResourceName()
    {
        var error = Assert.Single(Validate(Props(), name: "alice-reader"), d => d.IsError);

        Assert.Equal(DiagnosticCodes.InvalidResourceName, error.Code);
    }

    [Theory]
    [InlineData("{ \"principalId\": \"alice\", \"roleDefinitionId\": \"" + GlobalReader + "\" }", "properties.principalId")]
    [InlineData("{ \"principalId\": \"" + Principal + "\", \"roleDefinitionId\": \"Global Reader\" }", "properties.roleDefinitionId")]
    [InlineData("{ \"principalId\": \"" + Principal + "\", \"roleDefinitionId\": \"" + GlobalReader + "\", \"directoryScopeId\": \"tenant\" }", "properties.directoryScopeId")]
    [InlineData("{ \"principalId\": \"" + Principal + "\", \"roleDefinitionId\": \"" + GlobalReader + "\", \"scheduleInfo\": [] }", "properties.scheduleInfo")]
    [InlineData("{ \"principalId\": \"" + Principal + "\", \"roleDefinitionId\": \"" + GlobalReader + "\", \"ticketInfo\": \"CHG-1\" }", "properties.ticketInfo")]
    public void ShapeErrors_AreSchemaViolations_ReportedOnce(string properties, string pathSuffix)
    {
        var errors = Validate(properties).Where(d => d.IsError).ToList();

        Assert.NotEmpty(errors);
        Assert.All(errors, d => Assert.Equal(DiagnosticCodes.SchemaViolation, d.Code));
        Assert.Contains(errors, d => d.Path!.ToString().EndsWith(pathSuffix, StringComparison.Ordinal));
    }

    // ---- rules ----

    [Theory]
    [InlineData(", \"condition\": \"@Resource[x] == 'y'\"", "properties.condition", "is an ARM property")]
    [InlineData(", \"conditionVersion\": \"2.0\"", "properties.conditionVersion", "is an ARM property")]
    [InlineData(", \"targetRoleEligibilityScheduleId\": \"x\"", "properties.targetRoleEligibilityScheduleId", "is an ARM property")]
    [InlineData(", \"targetRoleEligibilityScheduleInstanceId\": \"x\"", "properties.targetRoleEligibilityScheduleInstanceId", "is an ARM property")]
    [InlineData(", \"description\": \"x\"", "properties.description", "is not a property of a role eligibility schedule request")]
    [InlineData(", \"requestType\": \"AdminRemove\"", "properties.requestType", "must be AdminAssign")]
    [InlineData(", \"role\": \"Global Reader\"", "properties.role", "not both")]
    [InlineData(", \"directoryScopeId\": \"/\", \"appScopeId\": \"/\"", "properties.appScopeId", "at most one")]
    [InlineData(", \"administrativeUnit\": \"Orders AU\", \"directoryScopeId\": \"/\"", "properties.administrativeUnit", "at most one")]
    [InlineData(", \"scheduleInfo\": { \"expiration\": { \"type\": \"Permanent\" } }", "properties.scheduleInfo.expiration.type", "must be one of NoExpiration, AfterDateTime, AfterDuration")]
    [InlineData(", \"scheduleInfo\": { \"expiration\": { } }", "properties.scheduleInfo.expiration.type", "is required")]
    [InlineData(", \"scheduleInfo\": { \"expiration\": { \"type\": \"AfterDateTime\" } }", "properties.scheduleInfo.expiration.endDateTime", "is required with type AfterDateTime")]
    [InlineData(", \"scheduleInfo\": { \"expiration\": { \"type\": \"AfterDuration\" } }", "properties.scheduleInfo.expiration.duration", "is required with type AfterDuration")]
    [InlineData(", \"scheduleInfo\": { \"expiration\": { \"type\": \"NoExpiration\", \"duration\": \"P1D\" } }", "properties.scheduleInfo.expiration.duration", "is not allowed with type NoExpiration")]
    [InlineData(", \"scheduleInfo\": { \"expiration\": { \"type\": \"NoExpiration\", \"endDateTime\": \"2027-01-01T00:00:00Z\" } }", "properties.scheduleInfo.expiration.endDateTime", "is not allowed with type NoExpiration")]
    [InlineData(", \"scheduleInfo\": { \"expiration\": { \"type\": \"AfterDuration\", \"duration\": \"P1D\", \"endDateTime\": \"2027-01-01T00:00:00Z\" } }", "properties.scheduleInfo.expiration.endDateTime", "is not allowed with type AfterDuration")]
    [InlineData(", \"scheduleInfo\": { \"expiration\": { \"type\": \"AfterDateTime\", \"endDateTime\": \"2027-01-01T00:00:00Z\", \"duration\": \"P1D\" } }", "properties.scheduleInfo.expiration.duration", "is not allowed with type AfterDateTime")]
    [InlineData(", \"scheduleInfo\": { \"expiration\": { \"type\": \"AfterDuration\", \"duration\": \"365 days\" } }", "properties.scheduleInfo.expiration.duration", "positive ISO 8601 duration")]
    [InlineData(", \"scheduleInfo\": { \"expiration\": { \"type\": \"AfterDuration\", \"duration\": \"PT0S\" } }", "properties.scheduleInfo.expiration.duration", "positive ISO 8601 duration")]
    [InlineData(", \"scheduleInfo\": { \"expiration\": { \"type\": \"AfterDateTime\", \"endDateTime\": \"next year\" } }", "properties.scheduleInfo.expiration.endDateTime", "ISO 8601 date-time")]
    [InlineData(", \"scheduleInfo\": { \"startDateTime\": \"tomorrow\" }", "properties.scheduleInfo.startDateTime", "ISO 8601 date-time")]
    [InlineData(", \"scheduleInfo\": { \"startDateTime\": \"2027-01-01T00:00:00Z\", \"expiration\": { \"type\": \"AfterDateTime\", \"endDateTime\": \"2026-01-01T00:00:00Z\" } }", "properties.scheduleInfo.expiration.endDateTime", "must be after")]
    public void Rules_AreInvalidRoleEligibilityScheduleRequestErrors(string extra, string pathSuffix, string message)
    {
        var error = Assert.Single(Validate(Props(extra)), d => d.IsError);

        Assert.Equal(DiagnosticCodes.InvalidRoleEligibilityScheduleRequest, error.Code);
        Assert.EndsWith(pathSuffix, error.Path!.ToString(), StringComparison.Ordinal);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Location);
    }

    [Fact]
    public void AMissingPrincipalOrRole_IsReported()
    {
        var missingPrincipal = Assert.Single(Validate("{ \"roleDefinitionId\": \"" + GlobalReader + "\" }"), d => d.IsError);
        var missingRole = Assert.Single(Validate("{ \"principalId\": \"" + Principal + "\" }"), d => d.IsError);

        Assert.Contains("principalId is required", missingPrincipal.Message, StringComparison.Ordinal);
        Assert.Contains("the role is required", missingRole.Message, StringComparison.Ordinal);
    }

    // ---- the shared rules ----

    [Theory]
    [InlineData("NoExpiration", "noExpiration")]
    [InlineData("AFTERDATETIME", "afterDateTime")]
    [InlineData(" afterDuration ", "afterDuration")]
    [InlineData("Permanent", null)]
    public void NormalizeExpirationType_GivesGraphsCamelCase(string declared, string? expected)
    {
        Assert.Equal(expected, PimScheduleRules.NormalizeExpirationType(declared));
    }

    [Theory]
    [InlineData("P365D", true)]
    [InlineData("PT8H", true)]
    [InlineData("P1DT12H", true)]
    [InlineData("-P1D", false)]
    [InlineData("PT0S", false)]
    [InlineData("8 hours", false)]
    public void TryParseDuration_AcceptsPositiveIso8601Durations(string text, bool valid)
    {
        Assert.Equal(valid, PimScheduleRules.TryParseDuration(text, out _));
    }

    [Fact]
    public void Normalize_DropsRequestType_AndCamelCasesTheExpirationType()
    {
        var properties = JsonNode.Parse("{ \"requestType\": \"AdminAssign\", \"scheduleInfo\": { \"expiration\": { \"type\": \"AfterDuration\", \"duration\": \"P1D\" } } }")!.AsObject();

        PimScheduleRules.Normalize(properties, TemplateJson.IsExpression);

        Assert.False(properties.ContainsKey("requestType"));
        Assert.Equal("afterDuration", properties["scheduleInfo"]!["expiration"]!["type"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("condition", true)]
    [InlineData("ConditionVersion", true)]
    [InlineData("targetRoleEligibilityScheduleId", true)]
    [InlineData("linkedRoleEligibilityScheduleId", true)]
    [InlineData("principalId", false)]
    public void ArmOnlyProperties_AreRecognised(string name, bool armOnly)
    {
        Assert.Equal(armOnly, RoleEligibilityScheduleRules.IsArmOnlyProperty(name));
    }
}
