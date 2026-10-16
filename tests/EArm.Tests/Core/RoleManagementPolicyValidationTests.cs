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
/// Offline validation of <c>Microsoft.Entra/roleManagementPolicies</c> (<see cref="RoleManagementPolicyRules"/>,
/// <see cref="DiagnosticCodes.InvalidRoleManagementPolicy"/>): the scope forms (role, group + accessId, scopeType / scopeId / roleDefinitionId), the rule ids
/// and their agreement with ruleType, target and recipientType, the per-type properties, ISO 8601 maximumDuration, enabledRules, approvers, the
/// authentication context claim value and the enums; computed values are left to plan time.
/// </summary>
public sealed class RoleManagementPolicyValidationTests
{
    private const string SchemaUri = "https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#";
    private const string Type = "Microsoft.Entra/roleManagementPolicies";
    private const string Group = "1b2c3d4e-0000-4000-8000-000000000100";
    private const string Approver = "5c0f3e2d-0000-4000-8000-000000000002";
    private const string Expiration = "{ \"id\": \"Expiration_EndUser_Assignment\", \"maximumDuration\": \"PT8H\" }";

    private static readonly TemplateLoader Loader = new();
    private static readonly TemplateValidator Validator = new();

    private static IReadOnlyList<Diagnostic> Validate(string properties)
    {
        string resource = "{ \"type\": \"" + Type + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"[guid('policy')]\", \"properties\": " + properties + " }";
        string json = "{ \"$schema\": \"" + SchemaUri + "\", \"contentVersion\": \"1.0.0.0\", \"parameters\": { \"rule\": { \"type\": \"object\", \"defaultValue\": {} }, \"duration\": { \"type\": \"string\", \"defaultValue\": \"PT1H\" } }, \"resources\": [ " + resource + " ] }";
        return Validator.Validate(Loader.Parse(json, "inline.entra.json", ParameterSet.Empty));
    }

    private static string Role(string rules)
    {
        return "{ \"role\": \"Global Administrator\", \"rules\": [ " + rules + " ] }";
    }

    private static Diagnostic SingleError(IReadOnlyList<Diagnostic> diagnostics)
    {
        return Assert.Single(diagnostics, d => d.IsError);
    }

    // ---- valid ----

    [Theory]
    [InlineData("{ \"role\": \"Global Administrator\", \"rules\": [ " + Expiration + " ] }")]
    [InlineData("{ \"role\": \"62e90394-69f5-4237-9190-012177145e10\", \"scopeType\": \"directoryrole\", \"scopeId\": \"/\", \"rules\": [ " + Expiration + " ] }")]
    [InlineData("{ \"group\": \"ops\", \"accessId\": \"Owner\", \"rules\": [ " + Expiration + " ] }")]
    [InlineData("{ \"scopeType\": \"Group\", \"scopeId\": \"" + Group + "\", \"roleDefinitionId\": \"member\", \"rules\": [ " + Expiration + " ] }")]
    [InlineData("{ \"scopeType\": \"Directory\", \"scopeId\": \"/\", \"roleDefinitionId\": \"62e90394-69f5-4237-9190-012177145e10\", \"rules\": [ " + Expiration + " ] }")]
    [InlineData("{ \"role\": \"Global Administrator\", \"rules\": [ \"[parameters('rule')]\", { \"id\": \"Expiration_Admin_Eligibility\", \"maximumDuration\": \"[parameters('duration')]\" } ] }")]
    public void ValidScopesAndComputedRules_Pass(string properties)
    {
        Assert.DoesNotContain(Validate(properties), d => d.IsError);
    }

    [Fact]
    public void EveryRuleType_InArmShape_Passes()
    {
        string rules = """
            { "id": "Expiration_Admin_Eligibility", "ruleType": "RoleManagementPolicyExpirationRule", "isExpirationRequired": false, "maximumDuration": "P365D",
              "target": { "caller": "admin", "operations": ["All"], "level": "Eligibility", "targetObjects": null, "inheritableSettings": null, "enforcedSettings": null } },
            { "id": "Enablement_EndUser_Assignment", "enabledRules": ["MultiFactorAuthentication", "justification", "Ticketing"] },
            { "id": "Approval_EndUser_Assignment", "ruleType": "roleManagementPolicyApprovalRule", "setting": { "isApprovalRequired": true, "isApprovalRequiredForExtension": false,
                "isRequestorJustificationRequired": true, "approvalMode": "SingleStage", "approvalStages": [ { "approvalStageTimeOutInDays": 1, "isApproverJustificationRequired": true,
                "escalationTimeInMinutes": 0, "isEscalationEnabled": false, "escalationApprovers": null,
                "primaryApprovers": [ { "id": "5c0f3e2d-0000-4000-8000-000000000002", "description": "Emergency", "isBackup": false, "userType": "Group" } ] } ] } },
            { "id": "AuthenticationContext_EndUser_Assignment", "isEnabled": true, "claimValue": "c42" },
            { "id": "Notification_Approver_Admin_Assignment", "notificationType": "Email", "recipientType": "approver", "notificationLevel": "Critical",
              "notificationRecipients": ["a@contoso.com"], "isDefaultRecipientsEnabled": false }
            """;

        Assert.DoesNotContain(Validate(Role(rules)), d => d.IsError);
    }

    // ---- scope ----

    [Theory]
    [InlineData("{ \"role\": \"Global Administrator\", \"group\": \"ops\", \"rules\": [ " + Expiration + " ] }", "not both")]
    [InlineData("{ \"group\": \"ops\", \"rules\": [ " + Expiration + " ] }", "accessId")]
    [InlineData("{ \"group\": \"ops\", \"accessId\": \"admin\", \"rules\": [ " + Expiration + " ] }", "must be member or owner")]
    [InlineData("{ \"role\": \"Global Administrator\", \"scopeId\": \"/x\", \"rules\": [ " + Expiration + " ] }", "must be '/'")]
    [InlineData("{ \"role\": \"Global Administrator\", \"scopeType\": \"Group\", \"rules\": [ " + Expiration + " ] }", "contradicts")]
    [InlineData("{ \"scopeType\": \"Tenant\", \"scopeId\": \"/\", \"roleDefinitionId\": \"62e90394-69f5-4237-9190-012177145e10\", \"rules\": [ " + Expiration + " ] }", "DirectoryRole")]
    [InlineData("{ \"scopeType\": \"DirectoryRole\", \"roleDefinitionId\": \"62e90394-69f5-4237-9190-012177145e10\", \"rules\": [ " + Expiration + " ] }", "scopeId is required")]
    [InlineData("{ \"role\": \"Global Administrator\" }", "rules is required")]
    [InlineData("{ \"role\": \"Global Administrator\", \"effectiveRules\": [], \"rules\": [ " + Expiration + " ] }", "not a property of a role management policy")]
    public void ScopeProblems_AreReported(string properties, string message)
    {
        var errors = Validate(properties).Where(d => d.IsError).ToList();
        Assert.Contains(errors, d => d.Code == DiagnosticCodes.InvalidRoleManagementPolicy && d.Message.Contains(message, System.StringComparison.Ordinal));
    }

    // ---- rules ----

    [Theory]
    [InlineData("{ \"id\": \"Expiration_Everyone_Assignment\" }", "is not a PIM policy rule id")]
    [InlineData("{ \"id\": \"Expiration_EndUser_Assignment\", \"ruleType\": \"RoleManagementPolicyApprovalRule\" }", "does not match the rule id")]
    [InlineData("{ \"id\": \"Expiration_EndUser_Assignment\", \"ruleType\": \"ExpirationRule\" }", "must be one of RoleManagementPolicyExpirationRule")]
    [InlineData("{ \"id\": \"Expiration_EndUser_Assignment\", \"enabledRules\": [] }", "is not a property of a RoleManagementPolicyExpirationRule")]
    [InlineData("{ \"id\": \"Expiration_EndUser_Assignment\", \"maximumDuration\": \"8h\" }", "is not a positive ISO 8601 duration")]
    [InlineData("{ \"id\": \"Enablement_EndUser_Assignment\", \"enabledRules\": [\"Mfa\"] }", "must be one of MultiFactorAuthentication, Justification, Ticketing")]
    [InlineData("{ \"id\": \"Enablement_EndUser_Assignment\", \"enabledRules\": [\"Justification\", \"justification\"] }", "lists Justification twice")]
    [InlineData("{ \"id\": \"Approval_EndUser_Assignment\", \"setting\": { \"isApprovalRequired\": true } }", "no approvalStages are declared")]
    [InlineData("{ \"id\": \"Approval_EndUser_Assignment\", \"setting\": { \"isApprovalRequired\": true, \"approvalStages\": [ { \"primaryApprovers\": [] } ] } }", "has no primaryApprovers")]
    [InlineData("{ \"id\": \"Approval_EndUser_Assignment\", \"setting\": { \"approvalStages\": [ { \"primaryApprovers\": [ { \"id\": \"" + Approver + "\", \"userType\": \"Role\" } ] } ] } }", "must be one of User, Group")]
    [InlineData("{ \"id\": \"Approval_EndUser_Assignment\", \"setting\": { \"approvalStages\": [ { \"isEscalationEnabled\": true, \"primaryApprovers\": [ { \"id\": \"" + Approver + "\", \"userType\": \"User\" } ] } ] } }", "no escalationApprovers")]
    [InlineData("{ \"id\": \"Approval_EndUser_Assignment\", \"setting\": { \"approvalStages\": [ { \"primaryApprovers\": [ { \"id\": \"" + Approver + "\", \"userType\": \"Group\", \"isBackup\": true } ] } ] } }", "Graph does not return isBackup")]
    [InlineData("{ \"id\": \"Approval_EndUser_Assignment\", \"setting\": { \"approvalMode\": \"Quorum\" } }", "must be one of SingleStage")]
    [InlineData("{ \"id\": \"Approval_EndUser_Assignment\", \"setting\": { \"approvalStages\": [ {}, {}, {} ] } }", "one or two stages")]
    [InlineData("{ \"id\": \"AuthenticationContext_EndUser_Assignment\", \"isEnabled\": true }", "no claimValue is declared")]
    [InlineData("{ \"id\": \"AuthenticationContext_EndUser_Assignment\", \"isEnabled\": true, \"claimValue\": \"c0\" }", "is not an authentication context id")]
    [InlineData("{ \"id\": \"Notification_Admin_EndUser_Assignment\", \"recipientType\": \"Requestor\" }", "does not match the rule id")]
    [InlineData("{ \"id\": \"Notification_Admin_EndUser_Assignment\", \"notificationLevel\": \"Loud\" }", "must be one of None, Critical, All")]
    [InlineData("{ \"id\": \"Expiration_EndUser_Assignment\", \"target\": { \"caller\": \"Admin\" } }", "does not match the rule id")]
    [InlineData("{ \"id\": \"Expiration_EndUser_Assignment\", \"target\": { \"level\": \"Eligibility\" } }", "does not match the rule id")]
    [InlineData("{ \"id\": \"Expiration_EndUser_Assignment\", \"target\": { \"operations\": [\"Delete\"] } }", "must be one of All, Activate")]
    public void RuleProblems_AreReported(string rule, string message)
    {
        var error = SingleError(Validate(Role(rule)));

        Assert.Equal(DiagnosticCodes.InvalidRoleManagementPolicy, error.Code);
        Assert.Contains(message, error.Message, System.StringComparison.Ordinal);
        Assert.StartsWith("resources[0].properties.rules[0]", error.Path!.ToString(), System.StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingRuleIdOrApproverUserType_IsTheSchemasFinding_AndTheProviderStillRefusesIt()
    {
        var missingId = SingleError(Validate(Role("{ \"maximumDuration\": \"PT8H\" }")));
        Assert.Equal(DiagnosticCodes.SchemaViolation, missingId.Code);

        var findings = RoleManagementPolicyRules.Check(JsonNode.Parse(Role("{ \"maximumDuration\": \"PT8H\" }"))!.AsObject(), static _ => false);
        Assert.Contains(findings, f => f.SchemaCovered && f.Message.Contains("id is required", System.StringComparison.Ordinal));
    }

    [Fact]
    public void ADuplicateRuleId_IsReported()
    {
        var error = SingleError(Validate(Role(Expiration + ", " + Expiration)));

        Assert.Contains("declared twice", error.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeRules_SetsRuleTypeFromTheId_AndTheCanonicalCasing()
    {
        var rules = JsonNode.Parse("""
            [ { "id": "Approval_EndUser_Assignment", "target": { "caller": "enduser", "operations": ["all"], "level": "assignment" },
                "setting": { "approvalMode": "serial", "approvalStages": [ { "primaryApprovers": [ { "id": "5C0F3E2D-0000-4000-8000-000000000002", "userType": "group" } ] } ] } },
              { "id": "Enablement_EndUser_Assignment", "enabledRules": ["ticketing"] } ]
            """)!.AsArray();

        RoleManagementPolicyRules.NormalizeRules(rules, static _ => false);

        Assert.Equal("RoleManagementPolicyApprovalRule", rules[0]!["ruleType"]!.GetValue<string>());
        Assert.Equal("""{"caller":"EndUser","operations":["All"],"level":"Assignment"}""", rules[0]!["target"]!.ToJsonString());
        Assert.Equal("Serial", rules[0]!["setting"]!["approvalMode"]!.GetValue<string>());
        Assert.Equal("""[{"id":"5c0f3e2d-0000-4000-8000-000000000002","userType":"Group"}]""", rules[0]!["setting"]!["approvalStages"]![0]!["primaryApprovers"]!.ToJsonString());
        Assert.Equal("Ticketing", rules[1]!["enabledRules"]![0]!.GetValue<string>());
        Assert.Equal("RoleManagementPolicyEnablementRule", rules[1]!["ruleType"]!.GetValue<string>());
    }

    [Fact]
    public void TheRuleTypes_MapToTheGraphODataTypes()
    {
        Assert.Equal(
            ["#microsoft.graph.unifiedRoleManagementPolicyExpirationRule", "#microsoft.graph.unifiedRoleManagementPolicyEnablementRule", "#microsoft.graph.unifiedRoleManagementPolicyApprovalRule", "#microsoft.graph.unifiedRoleManagementPolicyAuthenticationContextRule", "#microsoft.graph.unifiedRoleManagementPolicyNotificationRule"],
            RoleManagementPolicyRules.RuleTypes.Select(t => t.ODataType).ToArray());
        Assert.Same(RoleManagementPolicyRules.RuleTypes[2], RoleManagementPolicyRules.FindODataType("microsoft.graph.unifiedRoleManagementPolicyApprovalRule"));
        Assert.Same(RoleManagementPolicyRules.RuleTypes[4], RoleManagementPolicyRules.RuleTypeOfId("Notification_Requestor_EndUser_Assignment"));
        Assert.Null(RoleManagementPolicyRules.RuleTypeOfId("Notification_EndUser_Assignment"));
    }
}
