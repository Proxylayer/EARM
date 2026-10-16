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
/// Offline validation of <c>Microsoft.Entra/privilegedAccess/groupEligibilityScheduleRequests</c>: the GUID name, the shapes the JSON Schema enforces
/// (GUIDs, object types) and the rules of <see cref="GroupEligibilityScheduleRules"/> the checker adds
/// (<see cref="DiagnosticCodes.InvalidGroupEligibilityScheduleRequest"/>): unknown properties, group / groupId (exactly one), principalId, accessId,
/// requestType and the shared schedule rules of <see cref="PimScheduleRules"/>; plus the normalisation the provider shares.
/// </summary>
public sealed class GroupEligibilityScheduleRequestValidationTests
{
    private const string SchemaUri = "https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#";
    private const string Group = "1b2c3d4e-0000-4000-8000-000000000100";
    private const string Principal = "4b9e2d1c-0000-4000-8000-000000000001";

    private static readonly TemplateLoader Loader = new();
    private static readonly TemplateValidator Validator = new();

    private static IReadOnlyList<Diagnostic> Validate(string properties, string name = "[guid('ops', 'alice', 'member')]")
    {
        string resource = "{ \"type\": \"Microsoft.Entra/privilegedAccess/groupEligibilityScheduleRequests\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", \"properties\": " + properties + " }";
        string json = "{ \"$schema\": \"" + SchemaUri + "\", \"contentVersion\": \"1.0.0.0\", \"parameters\": { \"access\": { \"type\": \"string\", \"defaultValue\": \"member\" } }, \"resources\": [ " + resource + " ] }";
        return Validator.Validate(Loader.Parse(json, "inline.entra.json", ParameterSet.Empty));
    }

    private static string Props(string extra = "", string group = "\"groupId\": \"" + Group + "\"", string access = ", \"accessId\": \"member\"")
    {
        return "{ " + group + ", \"principalId\": \"" + Principal + "\"" + access + extra + " }";
    }

    // ---- valid ----

    [Theory]
    [InlineData("")]
    [InlineData(", \"requestType\": \"AdminAssign\", \"justification\": \"j\", \"ticketInfo\": { \"ticketNumber\": \"1\", \"ticketSystem\": \"s\" }")]
    [InlineData(", \"scheduleInfo\": { \"expiration\": { \"type\": \"NoExpiration\" } }")]
    [InlineData(", \"scheduleInfo\": { \"startDateTime\": \"2026-01-01T00:00:00Z\", \"expiration\": { \"type\": \"AfterDateTime\", \"endDateTime\": \"2027-01-01T00:00:00Z\" } }")]
    [InlineData(", \"scheduleInfo\": { \"expiration\": { \"type\": \"afterDuration\", \"duration\": \"P90D\" } }")]
    public void ValidDeclarations_Pass(string extra)
    {
        Assert.DoesNotContain(Validate(Props(extra)), d => d.IsError);
    }

    [Theory]
    [InlineData("\"group\": \"pim-ops\"", ", \"accessId\": \"Owner\"")]
    [InlineData("\"groupId\": \"[parameters('access')]\"", ", \"accessId\": \"[parameters('access')]\"")]
    public void TheGroupSugar_AnyAccessIdCasing_AndComputedValues_Pass(string group, string access)
    {
        Assert.DoesNotContain(Validate(Props(group: group, access: access)), d => d.IsError);
    }

    // ---- name, schema shape ----

    [Fact]
    public void ANameThatIsNotAGuid_IsAnInvalidResourceName()
    {
        var error = Assert.Single(Validate(Props(), name: "alice-ops-member"), d => d.IsError);

        Assert.Equal(DiagnosticCodes.InvalidResourceName, error.Code);
    }

    [Theory]
    [InlineData("{ \"groupId\": \"ops\", \"principalId\": \"" + Principal + "\", \"accessId\": \"member\" }", "properties.groupId")]
    [InlineData("{ \"groupId\": \"" + Group + "\", \"principalId\": \"alice\", \"accessId\": \"member\" }", "properties.principalId")]
    [InlineData("{ \"groupId\": \"" + Group + "\", \"principalId\": \"" + Principal + "\", \"accessId\": \"member\", \"scheduleInfo\": [] }", "properties.scheduleInfo")]
    public void ShapeErrors_AreSchemaViolations_ReportedOnce(string properties, string pathSuffix)
    {
        var errors = Validate(properties).Where(d => d.IsError).ToList();

        Assert.NotEmpty(errors);
        Assert.All(errors, d => Assert.Equal(DiagnosticCodes.SchemaViolation, d.Code));
        Assert.Contains(errors, d => d.Path!.ToString().EndsWith(pathSuffix, StringComparison.Ordinal));
    }

    // ---- rules ----

    [Theory]
    [InlineData(", \"roleDefinitionId\": \"x\"", "properties.roleDefinitionId", "is not a property of a group eligibility schedule request")]
    [InlineData(", \"group\": \"pim-ops\"", "properties.group", "not both")]
    [InlineData(", \"requestType\": \"AdminRemove\"", "properties.requestType", "must be AdminAssign")]
    [InlineData(", \"scheduleInfo\": { \"expiration\": { \"type\": \"AfterDateTime\" } }", "properties.scheduleInfo.expiration.endDateTime", "is required with type AfterDateTime")]
    [InlineData(", \"scheduleInfo\": { \"expiration\": { \"type\": \"AfterDuration\", \"duration\": \"90 days\" } }", "properties.scheduleInfo.expiration.duration", "positive ISO 8601 duration")]
    [InlineData(", \"scheduleInfo\": { \"startDateTime\": \"2027-01-01T00:00:00Z\", \"expiration\": { \"type\": \"AfterDateTime\", \"endDateTime\": \"2026-01-01T00:00:00Z\" } }", "properties.scheduleInfo.expiration.endDateTime", "must be after")]
    public void Rules_AreInvalidGroupEligibilityScheduleRequestErrors(string extra, string pathSuffix, string message)
    {
        var error = Assert.Single(Validate(Props(extra)), d => d.IsError);

        Assert.Equal(DiagnosticCodes.InvalidGroupEligibilityScheduleRequest, error.Code);
        Assert.EndsWith(pathSuffix, error.Path!.ToString(), StringComparison.Ordinal);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Location);
    }

    [Theory]
    [InlineData("", "accessId is required")]
    [InlineData(", \"accessId\": \"admin\"", "must be member or owner")]
    public void AMissingOrUnknownAccessId_IsReported(string access, string message)
    {
        var error = Assert.Single(Validate(Props(access: access)), d => d.IsError);

        Assert.Equal(DiagnosticCodes.InvalidGroupEligibilityScheduleRequest, error.Code);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingGroupOrPrincipal_IsReported()
    {
        var missingGroup = Assert.Single(Validate("{ \"principalId\": \"" + Principal + "\", \"accessId\": \"member\" }"), d => d.IsError);
        var missingPrincipal = Assert.Single(Validate("{ \"groupId\": \"" + Group + "\", \"accessId\": \"member\" }"), d => d.IsError);

        Assert.Contains("the group is required", missingGroup.Message, StringComparison.Ordinal);
        Assert.Contains("principalId is required", missingPrincipal.Message, StringComparison.Ordinal);
    }

    // ---- the shared rules ----

    [Theory]
    [InlineData("Member", "member")]
    [InlineData(" OWNER ", "owner")]
    [InlineData("admin", null)]
    public void NormalizeAccessId_GivesGraphsLowercase(string declared, string? expected)
    {
        Assert.Equal(expected, GroupEligibilityScheduleRules.NormalizeAccessId(declared));
    }

    [Fact]
    public void Normalize_DropsRequestType_AndNormalisesTheAccessIdAndExpirationType()
    {
        var properties = JsonNode.Parse("{ \"accessId\": \"Owner\", \"requestType\": \"AdminAssign\", \"scheduleInfo\": { \"expiration\": { \"type\": \"NoExpiration\" } } }")!.AsObject();

        GroupEligibilityScheduleRules.Normalize(properties, TemplateJson.IsExpression);

        Assert.False(properties.ContainsKey("requestType"));
        Assert.Equal("owner", properties["accessId"]!.GetValue<string>());
        Assert.Equal("noExpiration", properties["scheduleInfo"]!["expiration"]!["type"]!.GetValue<string>());
    }
}
