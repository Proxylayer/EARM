using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Loading;
using EArm.Core.Validation;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Offline validation of the Azure role grants, <c>Microsoft.Entra/azureRoleEligibilityScheduleRequests</c> (<see cref="AzureRoleEligibilityScheduleRules"/>,
/// <see cref="DiagnosticCodes.InvalidRoleEligibilityScheduleRequest"/>) and <c>Microsoft.Entra/azureRoleAssignments</c> (<see cref="AzureRoleAssignmentRules"/>,
/// <see cref="DiagnosticCodes.InvalidAzureRoleAssignment"/>), through <see cref="TemplateValidator"/>: the four scope forms (<see cref="AzureScopeRules"/>), one
/// of role / roleDefinitionId, a GUID principalId, the property set, the eligibility's schedule and the assignment's principalType; computed values are left
/// to plan time.
/// </summary>
public sealed class AzureRoleGrantValidationTests
{
    private const string SchemaUri = "https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#";
    private const string Eligibility = AzureRoleEligibilityScheduleRules.ResourceType;
    private const string Assignment = AzureRoleAssignmentRules.ResourceType;
    private const string Subscription = "6e1f4c2a-0000-4000-8000-000000000003";
    private const string SubscriptionScope = "/subscriptions/" + Subscription;
    private const string Rg = SubscriptionScope + "/resourceGroups/rg-app";
    private const string Mg = "/providers/Microsoft.Management/managementGroups/mg-platform";
    private const string Contributor = "b24988ac-6180-42a0-ab88-20f7382dd24c";
    private const string Principal = "7d3c2b1a-0000-4000-8000-0000000000c1";

    private static readonly TemplateLoader Loader = new();
    private static readonly TemplateValidator Validator = new();

    private static List<Diagnostic> Errors(string type, string properties)
    {
        string resource = "{ \"type\": \"" + type + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"[guid('grant')]\", \"properties\": " + properties + " }";
        string json = "{ \"$schema\": \"" + SchemaUri + "\", \"contentVersion\": \"1.0.0.0\", \"parameters\": { \"principal\": { \"type\": \"string\", \"defaultValue\": \"" + Principal + "\" } }, \"resources\": [ " + resource + " ] }";
        return [.. Validator.Validate(Loader.Parse(json, "inline.entra.json", ParameterSet.Empty)).Where(d => d.IsError)];
    }

    private static string Declaration(string scope = SubscriptionScope, string role = "\"role\": \"Contributor\"", string principal = "\"principalId\": \"" + Principal + "\"", string extra = "")
    {
        return "{ \"scope\": \"" + scope + "\", " + role + ", " + principal + extra + " }";
    }

    private static string CodeOf(string type)
    {
        return type == Eligibility ? DiagnosticCodes.InvalidRoleEligibilityScheduleRequest : DiagnosticCodes.InvalidAzureRoleAssignment;
    }

    // ---- scope, role, principal (both types) ----

    [Theory]
    [InlineData(Eligibility, Mg)]
    [InlineData(Eligibility, SubscriptionScope)]
    [InlineData(Eligibility, Rg)]
    [InlineData(Eligibility, Rg + "/providers/Microsoft.KeyVault/vaults/kv1")]
    [InlineData(Assignment, Mg + "/")]
    [InlineData(Assignment, SubscriptionScope)]
    [InlineData(Assignment, Rg)]
    [InlineData(Assignment, "[format('/subscriptions/{0}', subscription().subscriptionId)]")]
    public void TheFourScopeForms_AreAccepted(string type, string scope)
    {
        Assert.Empty(Errors(type, Declaration(scope)));
    }

    [Theory]
    [InlineData(Eligibility, "/subscriptions/not-a-guid")]
    [InlineData(Eligibility, Mg + "/subscriptions/" + Subscription)]
    [InlineData(Assignment, "/")]
    [InlineData(Assignment, SubscriptionScope + "/providers/Microsoft.Storage/storageAccounts/sa1")]
    public void AMalformedScope_IsRefused_ListingTheForms(string type, string scope)
    {
        var error = Assert.Single(Errors(type, Declaration(scope)));

        Assert.Equal(CodeOf(type), error.Code);
        Assert.Contains("is not an Azure scope", error.Message, StringComparison.Ordinal);
        Assert.Contains("/providers/Microsoft.Management/managementGroups/{managementGroupId}", error.Message, StringComparison.Ordinal);
        Assert.Equal("resources[0].properties.scope", error.Path!.ToString());
    }

    [Theory]
    [InlineData(Eligibility)]
    [InlineData(Assignment)]
    public void AMissingScope_IsRequired(string type)
    {
        var errors = Errors(type, "{ \"role\": \"Reader\", \"principalId\": \"" + Principal + "\" }");

        Assert.Contains(errors, e => e.Code == CodeOf(type) && e.Message.Contains("properties.scope is required", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Eligibility, "\"role\": \"Contributor\", \"roleDefinitionId\": \"" + Contributor + "\"", "declare one of properties.role and properties.roleDefinitionId, not both")]
    [InlineData(Eligibility, "\"roleDefinitionId\": \"Contributor\"", "is not a role definition")]
    [InlineData(Assignment, "\"roleDefinitionId\": \"" + Contributor + "-x\"", "is not a role definition")]
    [InlineData(Assignment, "\"description\": \"no role\"", "properties.roleDefinitionId (the role definition GUID or id) or properties.role")]
    public void BothNeitherOrAnInvalidRole_AreReported(string type, string role, string message)
    {
        Assert.Contains(Errors(type, Declaration(role: role)), e => e.Code == CodeOf(type) && e.Message.Contains(message, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Eligibility, "\"roleDefinitionId\": \"" + Contributor + "\"")]
    [InlineData(Eligibility, "\"roleDefinitionId\": \"/providers/Microsoft.Authorization/roleDefinitions/" + Contributor + "\"")]
    [InlineData(Assignment, "\"roleDefinitionId\": \"" + SubscriptionScope + "/providers/Microsoft.Authorization/roleDefinitions/" + Contributor + "\"")]
    [InlineData(Assignment, "\"role\": \"Storage Blob Data Reader\"")]
    public void TheRoleForms_AreAccepted(string type, string role)
    {
        Assert.Empty(Errors(type, Declaration(role: role)));
    }

    [Theory]
    [InlineData(Eligibility)]
    [InlineData(Assignment)]
    public void AMissingPrincipalId_IsRequired_AndANonGuidIsReported(string type)
    {
        var missing = Errors(type, "{ \"scope\": \"" + SubscriptionScope + "\", \"role\": \"Reader\" }");
        var notGuid = Errors(type, Declaration(principal: "\"principalId\": \"platform-admins\""));
        var computed = Errors(type, Declaration(principal: "\"principalId\": \"[parameters('principal')]\""));

        Assert.Contains(missing, e => e.Code == CodeOf(type) && e.Message.Contains("properties.principalId is required", StringComparison.Ordinal));
        Assert.NotEmpty(notGuid);
        Assert.Empty(computed);
    }

    [Theory]
    [InlineData(Eligibility, "requestType")]
    [InlineData(Eligibility, "ticketInfo")]
    [InlineData(Eligibility, "directoryScopeId")]
    [InlineData(Assignment, "condition")]
    [InlineData(Assignment, "scheduleInfo")]
    public void AnUnknownProperty_IsReported(string type, string property)
    {
        var errors = Errors(type, Declaration(extra: ", \"" + property + "\": \"x\""));

        Assert.Contains(errors, e => e.Code == CodeOf(type) && e.Message.Contains("properties." + property + " is not a property of an Azure role", StringComparison.Ordinal));
    }

    // ---- the eligibility's schedule ----

    [Theory]
    [InlineData("{ \"expiration\": { \"type\": \"NoExpiration\" } }")]
    [InlineData("{ \"expiration\": { \"type\": \"afterDuration\", \"duration\": \"P365D\" } }")]
    [InlineData("{ \"startDateTime\": \"2026-11-01T00:00:00Z\", \"expiration\": { \"type\": \"AfterDateTime\", \"endDateTime\": \"2027-11-01T00:00:00Z\" } }")]
    public void AValidSchedule_IsAccepted(string schedule)
    {
        Assert.Empty(Errors(Eligibility, Declaration(extra: ", \"justification\": \"why\", \"scheduleInfo\": " + schedule)));
    }

    [Theory]
    [InlineData("{ \"expiration\": { \"type\": \"Forever\" } }")]
    [InlineData("{ \"expiration\": { \"type\": \"AfterDuration\" } }")]
    [InlineData("{ \"expiration\": { \"type\": \"AfterDateTime\", \"endDateTime\": \"tomorrow\" } }")]
    public void AnInvalidSchedule_IsReported(string schedule)
    {
        Assert.Contains(Errors(Eligibility, Declaration(extra: ", \"scheduleInfo\": " + schedule)), e => e.Code == DiagnosticCodes.InvalidRoleEligibilityScheduleRequest);
    }

    // ---- the assignment's principalType ----

    [Theory]
    [InlineData("User", true)]
    [InlineData("group", true)]
    [InlineData("ServicePrincipal", true)]
    [InlineData("ForeignGroup", false)]
    [InlineData("Device", false)]
    public void PrincipalType_IsUserGroupOrServicePrincipal_InAnyCasing(string principalType, bool valid)
    {
        var errors = Errors(Assignment, Declaration(extra: ", \"principalType\": \"" + principalType + "\", \"description\": \"Read access\""));

        if (valid)
        {
            Assert.Empty(errors);
        }
        else
        {
            var error = Assert.Single(errors);
            Assert.Equal(DiagnosticCodes.InvalidAzureRoleAssignment, error.Code);
            Assert.Contains("must be User, Group or ServicePrincipal", error.Message, StringComparison.Ordinal);
        }

        Assert.Equal(valid ? principalType.Equals("group", StringComparison.Ordinal) ? "Group" : principalType : null, AzureRoleAssignmentRules.NormalizePrincipalType(principalType));
    }

    [Fact]
    public void AnOpaqueValue_IsLeftToPlanTime()
    {
        var properties = JsonNode.Parse("{ \"scope\": \"opaque\", \"roleDefinitionId\": \"opaque\", \"principalId\": \"opaque\", \"principalType\": \"opaque\" }")!.AsObject();

        var findings = AzureRoleAssignmentRules.Check(properties, static n => n is JsonValue v && v.TryGetValue<string>(out var s) && s == "opaque");

        Assert.Empty(findings);
    }

    // ---- AzureScopeRules ----

    [Theory]
    [InlineData(" 7D3C2B1A-0000-4000-8000-0000000000C1 ", Principal)]
    [InlineData("not-a-guid", null)]
    [InlineData(null, null)]
    public void NormalizePrincipalId_IsTheLowerCaseGuid(string? principalId, string? expected)
    {
        Assert.Equal(expected, AzureScopeRules.NormalizePrincipalId(principalId));
    }

    [Theory]
    [InlineData(Mg, null)]
    [InlineData(Rg, Subscription)]
    public void SubscriptionOf_IsNullAtAManagementGroup(string scope, string? expected)
    {
        Assert.Equal(expected, AzureScopeRules.SubscriptionOf(scope));
    }
}
