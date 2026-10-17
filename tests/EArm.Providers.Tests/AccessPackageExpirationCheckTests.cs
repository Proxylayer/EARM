using System;
using System.Linq;
using System.Threading.Tasks;
using EArm.Core.Diagnostics;
using EArm.Engine;
using EArm.Engine.Planning;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// <see cref="AccessPackageExpirationCheck"/>: an assignment policy whose assignments outlive the PIM for Groups eligibility maximum the template sets for
/// a group the package grants eligibly is a warning (AccessPackageExpirationExceedsEligibility) before any Graph call, in what-if and create alike.
/// </summary>
public sealed class AccessPackageExpirationCheckTests
{
    private const string Catalog = "Catalog";
    private const string Package = "Package";

    private static string Guid(int n)
    {
        return $"00000000-0000-4000-8000-{n:D12}";
    }

    private static string Template(string expiration, string role = "Eligible Member", string maximum = "P180D", bool required = true, string accessId = "member", string groupPolicyTarget = "[reference('ops').id]", string? roleScopeGroup = null)
    {
        string group = Templates.Resource("Microsoft.Entra/groups", "ops", "{ \"displayName\": \"Ops\", \"mailEnabled\": false, \"mailNickname\": \"ops\", \"securityEnabled\": true }");
        string catalog = Templates.Resource(AccessPackageCatalogsProvider.ResourceType, Catalog, "{ \"description\": \"c\" }");
        string catalogResource = Templates.Resource(CatalogResourcesProvider.ResourceType, Guid(1), "{ \"catalogId\": \"[reference('" + Catalog + "').id]\", \"originId\": \"[reference('ops').id]\" }");
        string package = Templates.Resource(AccessPackagesProvider.ResourceType, Package, "{ \"catalogId\": \"[reference('" + Catalog + "').id]\", \"description\": \"p\" }");
        string scope = Templates.Resource(ResourceRoleScopesProvider.ResourceType, Guid(2), "{ \"accessPackageId\": \"[reference('" + Package + "').id]\", \"resourceOriginId\": \"" + (roleScopeGroup ?? "[reference('ops').id]") + "\", \"role\": \"" + role + "\" }");
        string policy = Templates.Resource(AssignmentPoliciesProvider.ResourceType, Package + "/Requests", "{ \"description\": \"d\", \"allowedTargetScope\": \"allMemberUsers\", \"expiration\": " + expiration + " }");
        string rule = "{ \"id\": \"Expiration_Admin_Eligibility\", \"isExpirationRequired\": " + (required ? "true" : "false") + ", \"maximumDuration\": \"" + maximum + "\" }";
        string groupPolicy = Templates.Resource(RoleManagementPoliciesProvider.ResourceType, Guid(3), "{ \"group\": \"" + groupPolicyTarget + "\", \"accessId\": \"" + accessId + "\", \"rules\": [ " + rule + " ] }");
        return string.Join(", ", group, catalog, catalogResource, package, scope, policy, groupPolicy);
    }

    private static async Task<Diagnostic[]> WarningsAsync(string resources, string? parameters = null, params (string Name, string Value)[] values)
    {
        var host = new ProviderHost();
        var plan = await host.ExpandAsync(host.Resources(resources, parameters: parameters, values: values));
        return plan.Diagnostics.Where(d => d.Code == EngineDiagnosticCodes.AccessPackageExpirationExceedsEligibility).ToArray();
    }

    [Fact]
    public async Task APolicyLongerThanTheGroupsEligibilityMaximum_IsAWarning_AtThePolicysExpiration()
    {
        var warnings = await WarningsAsync(Template("{ \"type\": \"afterDuration\", \"duration\": \"P365D\" }"));

        var warning = Assert.Single(warnings);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.EndsWith("properties.expiration", warning.Path!.ToString(), StringComparison.Ordinal);
        Assert.Contains("P365D (365 days)", warning.Message, StringComparison.Ordinal);
        Assert.Contains("P180D (180 days)", warning.Message, StringComparison.Ordinal);
        Assert.Contains("'Eligible Member'", warning.Message, StringComparison.Ordinal);
        Assert.Contains("Expiration_Admin_Eligibility", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANeverExpiringPolicy_ExceedsARequiredMaximum()
    {
        var warning = Assert.Single(await WarningsAsync(Template("{ \"type\": \"noExpiration\" }")));

        Assert.Contains("forever (noExpiration)", warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ \"type\": \"afterDuration\", \"duration\": \"P180D\" }", "Eligible Member", "P180D", true, "member")]
    [InlineData("{ \"type\": \"afterDuration\", \"duration\": \"P90D\" }", "Eligible Member", "P180D", true, "member")]
    [InlineData("{ \"type\": \"afterDuration\", \"duration\": \"P365D\" }", "Member", "P180D", true, "member")]
    [InlineData("{ \"type\": \"afterDuration\", \"duration\": \"P365D\" }", "Eligible Member", "P180D", false, "member")]
    [InlineData("{ \"type\": \"afterDuration\", \"duration\": \"P365D\" }", "Eligible Member", "P180D", true, "owner")]
    [InlineData("{ \"type\": \"afterDateTime\", \"endDateTime\": \"2099-01-01T00:00:00Z\" }", "Eligible Member", "P180D", true, "member")]
    public async Task NoWarning_WhenThePolicyFits_TheRoleIsActive_TheMaximumIsOptional_TheAccessDiffers_OrTheEndIsADate(string expiration, string role, string maximum, bool required, string accessId)
    {
        Assert.Empty(await WarningsAsync(Template(expiration, role, maximum, required, accessId)));
    }

    [Fact]
    public async Task EligibleOwner_IsComparedWithTheOwnerPolicy()
    {
        Assert.Single(await WarningsAsync(Template("{ \"type\": \"afterDuration\", \"duration\": \"P365D\" }", role: "Eligible Owner", accessId: "owner")));
    }

    [Fact]
    public async Task ParameterValues_AreEvaluated()
    {
        string template = Template("{ \"type\": \"afterDuration\", \"duration\": \"[parameters('duration')]\" }", maximum: "P30D");
        string parameters = "\"duration\": { \"type\": \"string\" }";

        Assert.Single(await WarningsAsync(template, parameters, ("duration", "P60D")));
        Assert.Empty(await WarningsAsync(template, parameters, ("duration", "P30D")));
    }

    [Fact]
    public async Task AGroupNamedByTheSameLiteralObjectId_IsMatched_ADifferentGroupIsNot()
    {
        const string id = "11111111-2222-3333-4444-555555555555";
        Assert.Single(await WarningsAsync(Template("{ \"type\": \"afterDuration\", \"duration\": \"P365D\" }", groupPolicyTarget: id, roleScopeGroup: id)));
        Assert.Empty(await WarningsAsync(Template("{ \"type\": \"afterDuration\", \"duration\": \"P365D\" }", groupPolicyTarget: "99999999-2222-3333-4444-555555555555", roleScopeGroup: id)));
    }

    [Fact]
    public async Task TheWarning_DoesNotStopTheWhatIf()
    {
        var host = new ProviderHost();
        var run = await host.WhatIfAsync(host.Resources(Template("{ \"type\": \"afterDuration\", \"duration\": \"P365D\" }")));

        Assert.Contains(run.Plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.AccessPackageExpirationExceedsEligibility && !d.IsError);
        Assert.NotEmpty(run.Plan.Changes);
        Assert.Equal(PlanMode.WhatIf, run.Plan.PlanMode);
    }
}
