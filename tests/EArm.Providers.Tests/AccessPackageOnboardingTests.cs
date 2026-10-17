using System;
using System.Linq;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using EArm.Graph;
using EArm.Providers.Tests.Infrastructure;
using Xunit;
using static EArm.Providers.Tests.AccessPackagesProviderTests;

namespace EArm.Providers.Tests;

/// <summary>
/// Eligible grants of a group a same-run PIM for Groups request onboards (docs/access-packages.md): the role scope is applied after the request and waits
/// for the catalog to offer the eligible role, so one run succeeds; the warning plus next run is only the fallback when the wait times out. And the
/// delete warning of a role scope people hold.
/// </summary>
public sealed class AccessPackageOnboardingTests
{
    private static string Eligibility(Tenant t, string principalId)
    {
        return Templates.Resource(GroupEligibilityScheduleRequestsProvider.ResourceType, Guid(4), "{ \"groupId\": \"" + t.GroupId + "\", \"principalId\": \"" + principalId + "\", \"accessId\": \"member\", \"scheduleInfo\": { \"expiration\": { \"type\": \"AfterDuration\", \"duration\": \"P30D\" } } }");
    }

    private static (Tenant Tenant, string Template) Onboarding()
    {
        var t = NewTenant();
        string alice = t.Host.Directory.AddUser("alice@contoso.com", "Alice")["id"]!.GetValue<string>();
        return (t, All(t, role: "Eligible Member") + ", " + Eligibility(t, alice));
    }

    [Fact]
    public async Task TheEligibleRoleScope_IsAppliedAfterTheRequestThatOnboardsTheGroup()
    {
        var (t, template) = Onboarding();
        var document = t.Host.Resources(template);
        var plan = await t.Host.ExpandAsync(document);
        var scopeInstance = plan.Instances.Single(i => i.Type == ResourceRoleScopesProvider.ResourceType);
        var requestInstance = plan.Instances.Single(i => i.Type == GroupEligibilityScheduleRequestsProvider.ResourceType);
        Assert.Contains(requestInstance.PlannedId, scopeInstance.Dependencies);

        var run = await t.Host.CreateAsync(document);

        Check.Succeeded(run);
        int requestBatch = run.OpOfType(GroupEligibilityScheduleRequestsProvider.ResourceType).Batch;
        int scopeBatch = run.OpOfType(ResourceRoleScopesProvider.ResourceType).Batch;
        Assert.True(requestBatch < scopeBatch, $"request batch {requestBatch}, role scope batch {scopeBatch}");
        Assert.Equal("Eligible Member", t.Host.Directory.EntitlementManagement.RoleScopes.Single()["role"]!["displayName"]!.GetValue<string>());
    }

    // The whole-properties expression is split into literal values per key (InstanceEvaluator.WithPropertiesExpressionSplit), which AccessPackageApplyOrder matches.
    [Fact]
    public async Task ARoleScopeWhosePropertiesAreOneExpression_IsStillOrderedAfterTheRequestThatOnboardsItsGroup()
    {
        var t = NewTenant();
        string alice = t.Host.Directory.AddUser("alice@contoso.com", "Alice")["id"]!.GetValue<string>();
        string scope = Templates.Resource(ResourceRoleScopesProvider.ResourceType, Guid(2), "\"[variables('grant')]\"");
        string grant = "\"grant\": { \"accessPackageId\": \"" + Guid(9) + "\", \"resourceOriginId\": \"" + t.GroupId + "\", \"role\": \"Eligible Member\" }";
        var document = t.Host.Parse(Templates.Envelope(scope + ", " + Eligibility(t, alice)).Replace("\"resources\":", "\"variables\": { " + grant + " }, \"resources\":", StringComparison.Ordinal));

        var plan = await t.Host.ExpandAsync(document);

        var scopeInstance = plan.Instances.Single(i => i.Type == ResourceRoleScopesProvider.ResourceType);
        var requestInstance = plan.Instances.Single(i => i.Type == GroupEligibilityScheduleRequestsProvider.ResourceType);
        Assert.Contains(requestInstance.PlannedId, scopeInstance.Dependencies);
    }

    [Fact]
    public async Task AMemberRoleScope_IsNotOrderedAfterTheRequest()
    {
        var t = NewTenant();
        string alice = t.Host.Directory.AddUser("alice@contoso.com", "Alice")["id"]!.GetValue<string>();
        var plan = await t.Host.ExpandAsync(t.Host.Resources(All(t, role: "Member") + ", " + Eligibility(t, alice)));

        var scope = plan.Instances.Single(i => i.Type == ResourceRoleScopesProvider.ResourceType);
        var request = plan.Instances.Single(i => i.Type == GroupEligibilityScheduleRequestsProvider.ResourceType);
        Assert.DoesNotContain(request.PlannedId, scope.Dependencies);
    }

    [Fact]
    public async Task AWhatIf_ShowsACreate_ThatWaitsForTheOnboarding()
    {
        var t = NewTenant();
        Check.Succeeded(await t.Host.CreateAsync(t.Host.Resources(All(t))));
        string alice = t.Host.Directory.AddUser("alice@contoso.com", "Alice")["id"]!.GetValue<string>();
        var document = t.Host.Resources(All(t) + ", " + RoleScope(t.GroupId, "Eligible Member", label: 3) + ", " + Eligibility(t, alice));

        var whatIf = await t.Host.WhatIfAsync(document);

        var row = whatIf.Change(Guid(3));
        Assert.Equal(ChangeType.Create, row.Type);
        Assert.False(row.HasError);
        Assert.Contains(row.Reasons, r => r.Contains("waits up to", StringComparison.Ordinal));
        Assert.DoesNotContain(whatIf.Plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.PimGroupNotOnboarded);
    }

    [Fact]
    public async Task TheCatalogOfferingTheRoleLate_IsWaitedFor_AndOneRunSucceeds()
    {
        var (t, template) = Onboarding();
        t.Host.Directory.EntitlementManagement.EligibleRolesHiddenReads = 2;

        var run = await t.Host.CreateAsync(t.Host.Resources(template));

        Check.Succeeded(run);
        Assert.Single(t.Host.Directory.EntitlementManagement.RoleScopes);
        Assert.True(t.Host.SettleDelays.Count >= 2, $"expected the role scope to wait for the catalog; waited {t.Host.SettleDelays.Count} time(s)");
        Assert.Equal(ChangeType.NoChange, (await t.Host.WhatIfAsync(t.Host.Resources(template))).Change(Guid(2)).Type);
    }

    [Fact]
    public async Task WhenTheWaitTimesOut_TheRoleScopeIsDeferredWithAWarning_AndTheNextRunCreatesIt()
    {
        var (t, template) = Onboarding();
        t.Host.Directory.EntitlementManagement.EligibleRolesHiddenReads = -1;

        var run = await t.Host.CreateAsync(t.Host.Resources(template));

        Check.Succeeded(run);
        Assert.Equal(OperationStatus.Ignored, run.Op(Guid(2)).Status);
        Check.Diagnostic(run, EngineDiagnosticCodes.PimGroupNotOnboarded);
        Assert.Empty(t.Host.Directory.EntitlementManagement.RoleScopes);

        t.Host.Directory.EntitlementManagement.EligibleRolesHiddenReads = 0;
        var next = await t.Host.CreateAsync(t.Host.Resources(template));
        Check.Succeeded(next);
        Assert.Single(t.Host.Directory.EntitlementManagement.RoleScopes);
    }

    // ---- delete warning ----

    [Fact]
    public async Task DeletingARoleScopePeopleHold_CarriesAWarning_WithTheCountPackageAndRole()
    {
        var t = NewTenant();
        var document = t.Host.Resources(All(t));
        Check.Succeeded(await t.Host.CreateAsync(document));
        var em = t.Host.Directory.EntitlementManagement;
        string packageId = em.Packages.Single()["id"]!.GetValue<string>();
        em.AddAssignment(packageId);
        em.AddAssignment(packageId);

        var preview = await t.Host.Deleter().FindDeclaredObjectsAsync(await t.Host.Planner().PlanAsync(document, Engine.Planning.DeploymentMode.Incremental, Engine.Planning.PlanMode.WhatIf, ProviderHost.Ct), ProviderHost.Ct);
        string? risk = preview.Single(o => o.Instance.Type == ResourceRoleScopesProvider.ResourceType).Risk;

        Assert.NotNull(risk);
        Assert.Contains("2 active assignments", risk, StringComparison.Ordinal);
        Assert.Contains("PG Development Privileged Access", risk, StringComparison.Ordinal);
        Assert.Contains("'Member' role", risk, StringComparison.Ordinal);

        var deletion = await t.Host.DeleteDeclaredAsync(document, continueOnError: true);
        Assert.Equal(risk, deletion.Outcomes.Single(o => o.Target.Instance.Type == ResourceRoleScopesProvider.ResourceType).Target.Risk);
    }

    [Fact]
    public async Task ARoleScopeNobodyHolds_HasNoDeleteWarning_AndUnreadableAssignmentsSaySo()
    {
        var t = NewTenant();
        var document = t.Host.Resources(All(t));
        Check.Succeeded(await t.Host.CreateAsync(document));

        var plan = await t.Host.Planner().PlanAsync(document, Engine.Planning.DeploymentMode.Incremental, Engine.Planning.PlanMode.WhatIf, ProviderHost.Ct);
        Assert.Null((await t.Host.Deleter().FindDeclaredObjectsAsync(plan, ProviderHost.Ct)).Single(o => o.Instance.Type == ResourceRoleScopesProvider.ResourceType).Risk);

        t.Host.Directory.EntitlementManagement.AssignmentsForbidden = true;
        plan = await t.Host.Planner().PlanAsync(document, Engine.Planning.DeploymentMode.Incremental, Engine.Planning.PlanMode.WhatIf, ProviderHost.Ct);
        Assert.Contains("could not be read", (await t.Host.Deleter().FindDeclaredObjectsAsync(plan, ProviderHost.Ct)).Single(o => o.Instance.Type == ResourceRoleScopesProvider.ResourceType).Risk, StringComparison.Ordinal);
    }

    // ---- licence ----

    [Fact]
    public async Task NoGovernanceSku_OnAnEligibleRole_IsGovernanceLicenseRequired_WithWhatToDo()
    {
        var (t, template) = Onboarding();
        t.Host.Fake.Failures.Add("POST", "/resourceRoleScopes", request => new GraphException(
            System.Net.HttpStatusCode.BadRequest, "NoGovernanceSku", "The tenant does not have a valid governance SKU.", request.Hint));

        var run = await t.Host.CreateAsync(t.Host.Resources(template), continueOnError: true);

        var op = run.OpOfType(ResourceRoleScopesProvider.ResourceType);
        Assert.Equal(EngineDiagnosticCodes.GovernanceLicenseRequired, op.ErrorCode);
        Assert.Contains("Microsoft Entra ID Governance licence", op.Error, StringComparison.Ordinal);
        Assert.Contains("'Eligible Member'", op.Error, StringComparison.Ordinal);
        Assert.Empty(t.Host.Directory.EntitlementManagement.RoleScopes);
    }

    [Theory]
    [InlineData("NoGovernanceSku", "anything", true)]
    [InlineData("nogovernancesku", "anything", true)]
    [InlineData("BadRequest", "Error code: NoGovernanceSku", true)]
    [InlineData("BadRequest", "Some other problem", false)]
    public void IsNoGovernanceSku_MatchesTheCode_OrTheMessageOfAGeneric400(string code, string message, bool expected)
    {
        Assert.Equal(expected, ResourceRoleScopesProvider.IsNoGovernanceSku(new GraphException(System.Net.HttpStatusCode.BadRequest, code, message)));
    }

    [Fact]
    public async Task AWhatIf_DoesNotReadThePackagesAssignments_OnlyAPlannedDeleteDoes()
    {
        var t = NewTenant();
        var document = t.Host.Resources(All(t));
        Check.Succeeded(await t.Host.CreateAsync(document));
        t.Host.Directory.EntitlementManagement.AddAssignment(t.Host.Directory.EntitlementManagement.Packages.Single()["id"]!.GetValue<string>());

        var whatIf = await t.Host.WhatIfAsync(document);
        Assert.DoesNotContain(whatIf.Requests, r => r.Path.EndsWith("/assignments", StringComparison.Ordinal));

        // The synchronous listing has only the key reads: it says the assignments were not counted.
        Assert.Contains("were not counted", TemplateDeleter.FindDeclaredObjects(whatIf.Plan).Single(o => o.Instance.Type == ResourceRoleScopesProvider.ResourceType).Risk, StringComparison.Ordinal);

        int before = t.Host.Fake.Requests.Count;
        var listed = await t.Host.Deleter().FindDeclaredObjectsAsync(whatIf.Plan, ProviderHost.Ct);
        Assert.Contains("1 active assignment of", listed.Single(o => o.Instance.Type == ResourceRoleScopesProvider.ResourceType).Risk, StringComparison.Ordinal);
        Assert.Single(t.Host.RequestsSince(before), r => r.Path.EndsWith("/assignments", StringComparison.Ordinal));
    }
}
