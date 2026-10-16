using System;
using System.Collections.Generic;
using System.Linq;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Loading;
using EArm.Core.Validation;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Offline validation of the time-bound PIM active assignment types, <c>Microsoft.Entra/roleAssignmentScheduleRequests</c>
/// (<see cref="RoleAssignmentScheduleRules"/>, <see cref="DiagnosticCodes.InvalidRoleAssignmentScheduleRequest"/>) and
/// <c>Microsoft.Entra/privilegedAccess/groupAssignmentScheduleRequests</c> (<see cref="GroupAssignmentScheduleRules"/>,
/// <see cref="DiagnosticCodes.InvalidGroupAssignmentScheduleRequest"/>): the rules they share with the eligibility types, and their own rule, a
/// time-bound schedule (an absent scheduleInfo or expiration, or NoExpiration, is refused with a pointer at the permanent alternative).
/// </summary>
public sealed class PimActiveAssignmentValidationTests
{
    private const string SchemaUri = "https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#";
    private const string RoleType = "Microsoft.Entra/roleAssignmentScheduleRequests";
    private const string GroupType = "Microsoft.Entra/privilegedAccess/groupAssignmentScheduleRequests";
    private const string Group = "1b2c3d4e-0000-4000-8000-000000000100";
    private const string Principal = "4b9e2d1c-0000-4000-8000-000000000001";
    private const string TimeBound = ", \"scheduleInfo\": { \"expiration\": { \"type\": \"AfterDuration\", \"duration\": \"P30D\" } }";

    private static readonly TemplateLoader Loader = new();
    private static readonly TemplateValidator Validator = new();

    private static IReadOnlyList<Diagnostic> Validate(string type, string properties, string name = "[guid('alice', 'active')]")
    {
        string resource = "{ \"type\": \"" + type + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", \"properties\": " + properties + " }";
        string json = "{ \"$schema\": \"" + SchemaUri + "\", \"contentVersion\": \"1.0.0.0\", \"parameters\": { \"schedule\": { \"type\": \"object\", \"defaultValue\": {} }, \"kind\": { \"type\": \"string\", \"defaultValue\": \"NoExpiration\" } }, \"resources\": [ " + resource + " ] }";
        return Validator.Validate(Loader.Parse(json, "inline.entra.json", ParameterSet.Empty));
    }

    private static string RoleProps(string extra = TimeBound)
    {
        return "{ \"principalId\": \"" + Principal + "\", \"role\": \"Exchange Administrator\"" + extra + " }";
    }

    private static string GroupProps(string extra = TimeBound, string access = "member")
    {
        return "{ \"groupId\": \"" + Group + "\", \"principalId\": \"" + Principal + "\", \"accessId\": \"" + access + "\"" + extra + " }";
    }

    // ---- valid ----

    [Theory]
    [InlineData(TimeBound)]
    [InlineData(", \"requestType\": \"AdminAssign\", \"justification\": \"j\", \"ticketInfo\": { \"ticketNumber\": \"1\" }, \"scheduleInfo\": { \"startDateTime\": \"2026-01-01T00:00:00Z\", \"expiration\": { \"type\": \"afterDateTime\", \"endDateTime\": \"2026-02-01T00:00:00Z\" } }")]
    [InlineData(", \"scheduleInfo\": \"[parameters('schedule')]\"")]
    [InlineData(", \"scheduleInfo\": { \"expiration\": { \"type\": \"[parameters('kind')]\" } }")]
    public void TimeBoundOrComputedSchedules_Pass(string extra)
    {
        Assert.DoesNotContain(Validate(RoleType, RoleProps(extra)), d => d.IsError);
        Assert.DoesNotContain(Validate(GroupType, GroupProps(extra)), d => d.IsError);
    }

    // ---- the time-bound rule ----

    [Theory]
    [InlineData("", "properties.scheduleInfo", "scheduleInfo is required")]
    [InlineData(", \"scheduleInfo\": { \"startDateTime\": \"2026-01-01T00:00:00Z\" }", "properties.scheduleInfo.expiration", "expiration is required")]
    [InlineData(", \"scheduleInfo\": { \"expiration\": { \"type\": \"noexpiration\" } }", "properties.scheduleInfo.expiration.type", "NoExpiration is not allowed")]
    public void APermanentSchedule_IsRefused_PointingAtRoleAssignments(string extra, string pathSuffix, string message)
    {
        var error = Assert.Single(Validate(RoleType, RoleProps(extra)), d => d.IsError);

        Assert.Equal(DiagnosticCodes.InvalidRoleAssignmentScheduleRequest, error.Code);
        Assert.EndsWith(pathSuffix, error.Path!.ToString(), StringComparison.Ordinal);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.Contains("time-bound", error.Message, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Entra/roleAssignments", error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Location);
    }

    [Theory]
    [InlineData("")]
    [InlineData(", \"scheduleInfo\": { \"expiration\": { \"type\": \"NoExpiration\" } }")]
    public void APermanentGroupSchedule_IsRefused_PointingAtTheGroupsMembersAndOwners(string extra)
    {
        var error = Assert.Single(Validate(GroupType, GroupProps(extra)), d => d.IsError);

        Assert.Equal(DiagnosticCodes.InvalidGroupAssignmentScheduleRequest, error.Code);
        Assert.Contains("time-bound", error.Message, StringComparison.Ordinal);
        Assert.Contains("members / owners", error.Message, StringComparison.Ordinal);
    }

    // ---- shared rules, reported under the type's own code ----

    [Theory]
    [InlineData(", \"condition\": \"x\"" + TimeBound, "an ARM property that a PIM active assignment of a directory role does not support")]
    [InlineData(", \"linkedRoleEligibilityScheduleId\": \"x\"" + TimeBound, "an ARM property")]
    [InlineData(", \"accessId\": \"member\"" + TimeBound, "is not a property of a role assignment schedule request")]
    [InlineData(", \"requestType\": \"SelfActivate\"" + TimeBound, "must be AdminAssign")]
    [InlineData(", \"administrativeUnit\": \"EMEA\", \"directoryScopeId\": \"/\"" + TimeBound, "at most one of")]
    [InlineData(", \"scheduleInfo\": { \"expiration\": { \"type\": \"AfterDateTime\" } }", "is required with type AfterDateTime")]
    public void SharedRoleRules_AreInvalidRoleAssignmentScheduleRequestErrors(string extra, string message)
    {
        var error = Assert.Single(Validate(RoleType, RoleProps(extra)), d => d.IsError);

        Assert.Equal(DiagnosticCodes.InvalidRoleAssignmentScheduleRequest, error.Code);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("admin", "must be member or owner")]
    public void SharedGroupRules_AreInvalidGroupAssignmentScheduleRequestErrors(string access, string message)
    {
        var error = Assert.Single(Validate(GroupType, GroupProps(access: access)), d => d.IsError);

        Assert.Equal(DiagnosticCodes.InvalidGroupAssignmentScheduleRequest, error.Code);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownGroupProperty_NamesTheAssignmentRequest()
    {
        var error = Assert.Single(Validate(GroupType, GroupProps(", \"role\": \"x\"" + TimeBound)), d => d.IsError);

        Assert.Contains("is not a property of a group assignment schedule request", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANameThatIsNotAGuid_IsAnInvalidResourceName()
    {
        Assert.Equal(DiagnosticCodes.InvalidResourceName, Assert.Single(Validate(RoleType, RoleProps(), name: "alice-exchange"), d => d.IsError).Code);
        Assert.Equal(DiagnosticCodes.InvalidResourceName, Assert.Single(Validate(GroupType, GroupProps(), name: "alice-ops"), d => d.IsError).Code);
    }

    [Fact]
    public void ShapeErrors_AreSchemaViolations()
    {
        var errors = Validate(RoleType, "{ \"principalId\": \"alice\", \"role\": \"Exchange Administrator\"" + TimeBound + " }").Where(d => d.IsError).ToList();

        Assert.NotEmpty(errors);
        Assert.All(errors, d => Assert.Equal(DiagnosticCodes.SchemaViolation, d.Code));
    }

    // ---- the rule itself ----

    [Fact]
    public void CheckTimeBound_SkipsOpaqueValues_AndLeavesShapeErrorsToTheSharedRules()
    {
        var findings = new List<PimRequestFinding>();
        var opaque = System.Text.Json.Nodes.JsonNode.Parse("{ \"scheduleInfo\": { \"expiration\": \"[x]\" } }")!.AsObject();
        var notAnObject = System.Text.Json.Nodes.JsonNode.Parse("{ \"scheduleInfo\": [] }")!.AsObject();

        PimScheduleRules.CheckTimeBound(opaque, EArm.Core.Json.TemplateJson.IsExpression, findings, "X");
        PimScheduleRules.CheckTimeBound(notAnObject, EArm.Core.Json.TemplateJson.IsExpression, findings, "X");

        Assert.Empty(findings);
    }
}
