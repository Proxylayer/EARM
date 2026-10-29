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
/// Offline validation of <c>Microsoft.Entra/azureRoleManagementPolicies</c> (<see cref="AzureRoleManagementPolicyRules"/> through <see cref="TemplateValidator"/>,
/// <see cref="DiagnosticCodes.InvalidRoleManagementPolicy"/>): the scope forms (subscription, resource group, nested resource; management groups refused),
/// one of role / roleDefinitionId, the role definition id forms normalised to the subscription-qualified id, the property set, and the rule checks shared
/// with <c>Microsoft.Entra/roleManagementPolicies</c>; computed values are left to plan time.
/// </summary>
public sealed class AzureRoleManagementPolicyValidationTests
{
    private const string SchemaUri = "https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#";
    private const string Type = AzureRoleManagementPolicyRules.ResourceType;
    private const string Subscription = "6e1f4c2a-0000-4000-8000-000000000003";
    private const string OtherSubscription = "0f0e0d0c-0000-4000-8000-000000000009";
    private const string SubscriptionScope = "/subscriptions/" + Subscription;
    private const string Rg = SubscriptionScope + "/resourceGroups/rg-app";
    private const string Reader = "acdd72a7-3385-48ef-bd42-f606fba81ae7";
    private const string QualifiedReader = SubscriptionScope + "/providers/Microsoft.Authorization/roleDefinitions/" + Reader;
    private const string Expiration = "{ \"id\": \"Expiration_EndUser_Assignment\", \"maximumDuration\": \"PT4H\" }";

    private static readonly TemplateLoader Loader = new();
    private static readonly TemplateValidator Validator = new();

    private static IReadOnlyList<Diagnostic> Validate(string properties)
    {
        string resource = "{ \"type\": \"" + Type + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"[guid('policy')]\", \"properties\": " + properties + " }";
        string json = "{ \"$schema\": \"" + SchemaUri + "\", \"contentVersion\": \"1.0.0.0\", \"parameters\": { \"rg\": { \"type\": \"string\", \"defaultValue\": \"rg-app\" } }, \"resources\": [ " + resource + " ] }";
        return Validator.Validate(Loader.Parse(json, "inline.entra.json", ParameterSet.Empty));
    }

    private static string Declaration(string scope, string role = "\"role\": \"Reader\"", string rules = Expiration)
    {
        return "{ \"scope\": \"" + scope + "\", " + role + ", \"rules\": [ " + rules + " ] }";
    }

    private static List<Diagnostic> Errors(string properties)
    {
        return [.. Validate(properties).Where(d => d.IsError)];
    }

    // ---- scope ----

    [Theory]
    [InlineData(SubscriptionScope)]
    [InlineData(Rg)]
    [InlineData(Rg + "/")]
    [InlineData("/SUBSCRIPTIONS/" + "6E1F4C2A-0000-4000-8000-000000000003" + "/resourcegroups/RG-App")]
    [InlineData(Rg + "/providers/Microsoft.Storage/storageAccounts/sa1")]
    [InlineData(Rg + "/providers/Microsoft.Storage/storageAccounts/sa1/blobServices/default/containers/c1")]
    [InlineData("[format('" + SubscriptionScope + "/resourceGroups/{0}', parameters('rg'))]")]
    public void SubscriptionResourceGroupAndResourceScopes_AreAccepted(string scope)
    {
        Assert.Empty(Errors(Declaration(scope)));
    }

    [Theory]
    [InlineData("/providers/Microsoft.Management/managementGroups/mg-platform", "is a management group scope, which is not supported yet")]
    [InlineData("/providers/Microsoft.Management/managementGroups/mg-platform/", "is a management group scope, which is not supported yet")]
    [InlineData("/subscriptions/not-a-guid", "is not an Azure scope")]
    [InlineData(SubscriptionScope + "/resourceGroups", "is not an Azure scope")]
    [InlineData(SubscriptionScope + "/providers/Microsoft.Storage/storageAccounts/sa1", "is not an Azure scope")]
    [InlineData(Rg + "/providers/Microsoft.Storage/storageAccounts", "is not an Azure scope")]
    [InlineData("subscriptions/" + Subscription, "is not an Azure scope")]
    [InlineData("/", "is not an Azure scope")]
    public void ManagementGroupAndMalformedScopes_AreRefused(string scope, string message)
    {
        var error = Assert.Single(Errors(Declaration(scope)));

        Assert.Equal(DiagnosticCodes.InvalidRoleManagementPolicy, error.Code);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.Equal("resources[0].properties.scope", error.Path!.ToString());
    }

    [Fact]
    public void AMissingScope_IsRequired()
    {
        var errors = Errors("{ \"role\": \"Reader\", \"rules\": [ " + Expiration + " ] }");

        Assert.Contains(errors, e => e.Code == DiagnosticCodes.InvalidRoleManagementPolicy && e.Message.Contains("properties.scope is required", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(" " + Rg + "/ ", Rg)]
    [InlineData(Rg + "/", Rg)]
    [InlineData("/SUBSCRIPTIONS/6E1F4C2A-0000-4000-8000-000000000003/resourcegroups/RG-App", SubscriptionScope + "/resourceGroups/RG-App")]
    [InlineData("/Subscriptions/" + Subscription + "/", SubscriptionScope)]
    [InlineData(Rg + "/providers/Microsoft.Web/sites/app1", Rg + "/providers/Microsoft.Web/sites/app1")]
    [InlineData("/providers/Microsoft.Management/managementGroups/mg", null)]
    [InlineData("/subscriptions/x", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeScope_TrimsTheSlash_AndUsesArmsCasingForTheKeywords(string? scope, string? expected)
    {
        Assert.Equal(expected, AzureRoleManagementPolicyRules.NormalizeScope(scope));
    }

    [Theory]
    [InlineData("/providers/Microsoft.Management/managementGroups/mg-1", true)]
    [InlineData("/providers/microsoft.management/managementgroups/mg-1/", true)]
    [InlineData("/providers/Microsoft.Management/managementGroups/mg-1/subscriptions/x", false)]
    [InlineData(SubscriptionScope, false)]
    [InlineData(null, false)]
    public void IsManagementGroupScope_RecognisesOnlyAManagementGroup(string? scope, bool expected)
    {
        Assert.Equal(expected, AzureRoleManagementPolicyRules.IsManagementGroupScope(scope));
    }

    // ---- role / roleDefinitionId ----

    [Theory]
    [InlineData("\"roleDefinitionId\": \"" + Reader + "\"")]
    [InlineData("\"roleDefinitionId\": \"/providers/Microsoft.Authorization/roleDefinitions/" + Reader + "\"")]
    [InlineData("\"roleDefinitionId\": \"" + QualifiedReader + "\"")]
    [InlineData("\"roleDefinitionId\": \"" + Rg + "/providers/Microsoft.Authorization/roleDefinitions/" + Reader + "\"")]
    [InlineData("\"roleDefinitionId\": \"[concat('" + Reader + "')]\"")]
    [InlineData("\"role\": \"Storage Blob Data Reader\"")]
    [InlineData("\"role\": \"" + Reader + "\"")]
    public void TheRoleDefinitionIdForms_AndTheRoleSugar_AreAccepted(string role)
    {
        Assert.Empty(Errors(Declaration(Rg, role)));
    }

    [Theory]
    [InlineData("\"role\": \"Reader\", \"roleDefinitionId\": \"" + Reader + "\"", "declare one of properties.role and properties.roleDefinitionId, not both")]
    [InlineData("\"roleDefinitionId\": \"Reader\"", "is not a role definition")]
    [InlineData("\"roleDefinitionId\": \"" + SubscriptionScope + "/providers/Microsoft.Authorization/roleAssignments/" + Reader + "\"", "is not a role definition")]
    [InlineData("\"roleDefinitionId\": \"/subscriptions/not-a-guid/providers/Microsoft.Authorization/roleDefinitions/" + Reader + "\"", "is not a role definition")]
    [InlineData("\"roleDefinitionId\": \"" + Reader + "-extra\"", "is not a role definition")]
    public void BothNeitherOrAnInvalidRole_AreReported(string role, string message)
    {
        var errors = Errors(Declaration(Rg, role));

        Assert.Contains(errors, e => e.Code == DiagnosticCodes.InvalidRoleManagementPolicy && e.Message.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void NeitherRoleNorRoleDefinitionId_IsRequired()
    {
        var error = Assert.Single(Errors("{ \"scope\": \"" + Rg + "\", \"rules\": [ " + Expiration + " ] }"));

        Assert.Contains("properties.roleDefinitionId (the role definition GUID or id) or properties.role", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Reader, SubscriptionScope, QualifiedReader)]
    [InlineData("ACDD72A7-3385-48EF-BD42-F606FBA81AE7", Rg, QualifiedReader)]
    [InlineData("/providers/Microsoft.Authorization/roleDefinitions/" + Reader, Rg, QualifiedReader)]
    [InlineData("/subscriptions/" + OtherSubscription + "/providers/Microsoft.Authorization/roleDefinitions/" + Reader, Rg, QualifiedReader)]
    [InlineData(Rg + "/providers/Microsoft.Authorization/roleDefinitions/" + Reader, Rg + "/providers/Microsoft.Web/sites/app1", QualifiedReader)]
    [InlineData(Reader, "/SUBSCRIPTIONS/6E1F4C2A-0000-4000-8000-000000000003/resourcegroups/x/", QualifiedReader)]
    [InlineData("Reader", Rg, null)]
    [InlineData(Reader, "/providers/Microsoft.Management/managementGroups/mg", null)]
    [InlineData(null, Rg, null)]
    public void QualifiedRoleDefinitionId_IsTheIdInTheScopesSubscription(string? roleDefinitionId, string scope, string? expected)
    {
        Assert.Equal(expected, AzureRoleManagementPolicyRules.QualifiedRoleDefinitionId(scope, roleDefinitionId));
    }

    // ---- properties and the shared rule checks ----

    [Theory]
    [InlineData("displayName")]
    [InlineData("effectiveRules")]
    [InlineData("scopeType")]
    public void AnUnknownProperty_IsReported(string property)
    {
        var errors = Errors("{ \"scope\": \"" + Rg + "\", \"role\": \"Reader\", \"" + property + "\": \"x\", \"rules\": [ " + Expiration + " ] }");

        Assert.Contains(errors, e => e.Code == DiagnosticCodes.InvalidRoleManagementPolicy && e.Message.Contains("properties." + property + " is not a property of an Azure role management policy", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{ \"id\": \"Expiration_Everyone_Assignment\" }", "is not a PIM policy rule id")]
    [InlineData("{ \"id\": \"Expiration_EndUser_Assignment\", \"maximumDuration\": \"4 hours\" }", "is not a positive ISO 8601 duration")]
    [InlineData("{ \"id\": \"Enablement_EndUser_Assignment\", \"enabledRules\": [\"Mfa\"] }", "must be one of MultiFactorAuthentication, Justification, Ticketing")]
    [InlineData(Expiration + ", " + Expiration, "declared twice")]
    public void TheSharedRuleChecks_Apply(string rules, string message)
    {
        var error = Assert.Single(Errors(Declaration(Rg, rules: rules)));

        Assert.Equal(DiagnosticCodes.InvalidRoleManagementPolicy, error.Code);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.StartsWith("resources[0].properties.rules", error.Path!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyOrMissingRulesList_IsReported()
    {
        Assert.Contains(Errors(Declaration(Rg, rules: string.Empty)), e => e.Message.Contains("must declare at least one rule", StringComparison.Ordinal));
        Assert.Contains(Errors("{ \"scope\": \"" + Rg + "\", \"role\": \"Reader\" }"), e => e.Message.Contains("properties.rules is required", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSample_HasNoFinding()
    {
        var findings = AzureRoleManagementPolicyRules.Check(
            JsonNode.Parse(Declaration(Rg, rules: Expiration + ", { \"id\": \"Enablement_EndUser_Assignment\", \"enabledRules\": [\"Justification\"] }, { \"id\": \"Expiration_Admin_Assignment\", \"isExpirationRequired\": true, \"maximumDuration\": \"P90D\" }"))!.AsObject(),
            static _ => false);

        Assert.Empty(findings);
    }

    [Fact]
    public void AnOpaqueScopeOrRole_IsLeftToPlanTime()
    {
        var properties = JsonNode.Parse("{ \"scope\": \"opaque\", \"roleDefinitionId\": \"opaque\", \"rules\": [ " + Expiration + " ] }")!.AsObject();

        var findings = AzureRoleManagementPolicyRules.Check(properties, static n => n is JsonValue v && v.TryGetValue<string>(out var s) && s == "opaque");

        Assert.Empty(findings);
    }
}
