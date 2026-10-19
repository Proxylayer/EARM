using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Graph;
using EArm.Providers.Tests.Infrastructure;
using Xunit;
using static EArm.Providers.Tests.AccessPackagesProviderTests;

namespace EArm.Providers.Tests;

/// <summary>
/// Eligible grants of a group a same-run PIM for Groups request onboards (docs/access-packages.md): the role scope is applied after the request and waits
/// for the catalog to offer the eligible role, so one run succeeds; the warning plus next run is only the fallback when the wait times out. A group PIM
/// policy that opts in to onboarding (<c>properties.onboarding</c>) is such an onboarding source too. And the delete warning of a role scope people hold.
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

    // ---- a group PIM policy that opts in to onboarding ----

    private const string PimPolicyName = "9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d";

    private const string GroupName = "pg-dev-privileged-access";

    private const string GroupReference = "[reference(resourceId('Microsoft.Entra/groups', '" + GroupName + "')).id]";

    /// <summary>The group of the tenant declared in the template (the existing group, so it is no create).</summary>
    private static string DeclaredGroup()
    {
        return Templates.Resource("Microsoft.Entra/groups", GroupName, "{ \"displayName\": \"PG Development Privileged Access\", \"mailEnabled\": false, \"mailNickname\": \"" + GroupName + "\", \"securityEnabled\": true }");
    }

    /// <summary>A group PIM policy naming its group by <paramref name="scope"/> (<c>"group": …</c> or <c>"scopeId": …</c>), opting in to onboarding with <paramref name="onboarding"/> (omitted when null).</summary>
    private static string PimPolicy(string scope, string? onboarding, string? extra = null)
    {
        string optIn = onboarding is null ? string.Empty : ", \"onboarding\": " + onboarding;
        return Templates.Resource(RoleManagementPoliciesProvider.ResourceType, PimPolicyName, "{ " + scope + ", \"accessId\": \"member\"" + optIn + ", \"rules\": [ { \"id\": \"Expiration_EndUser_Assignment\", \"maximumDuration\": \"PT4H\" } ] }", extra: extra);
    }

    private static string OptIn(string principalId)
    {
        return "{ \"temporaryEligiblePrincipalId\": \"" + principalId + "\" }";
    }

    private static string Onboarder(Tenant t)
    {
        return t.Host.Directory.AddUser("pim-onboarding@contoso.com", "PIM Onboarding")["id"]!.GetValue<string>();
    }

    /// <summary>True when the plan holds an Ordering edge that applies <paramref name="later"/> after <paramref name="earlier"/>.</summary>
    private static bool Ordered(DeploymentPlan plan, string later, string earlier)
    {
        return plan.Graph.Edges.Any(e => e.Kind == DependencyKind.Ordering && e.Dependent.Id.NameLeaf == later && e.Dependency.Id.NameLeaf == earlier);
    }

    private static string Edges(DeploymentPlan plan)
    {
        return string.Join("; ", plan.Graph.Edges.Select(e => e.Kind + ": " + e.Dependent.Id.NameLeaf + " -> " + e.Dependency.Id.NameLeaf));
    }

    [Theory]
    [InlineData("\"group\": \"" + GroupReference + "\"", "Eligible Member")]
    [InlineData("\"group\": \"" + GroupReference + "\"", "Eligible Owner")]
    [InlineData("\"scopeId\": \"" + GroupReference + "\"", "Eligible Member")]
    [InlineData("\"scopeId\": \"" + GroupReference + "\"", "Eligible Owner")]
    [InlineData("\"group\": \"" + GroupName + "\"", "Eligible Member")]
    [InlineData("\"group\": \"PG-DEV-Privileged-Access\"", "Eligible Owner")]
    public async Task AnEligibleRoleScopeOfADeclaredGroup_IsAppliedAfterTheGroupPolicyThatOnboardsIt(string scope, string role)
    {
        var t = NewTenant();
        string onboarder = Onboarder(t);
        var document = t.Host.Resources(string.Join(", ", All(t), DeclaredGroup(), RoleScope(GroupReference, role, label: 3), PimPolicy(scope, OptIn(onboarder))));

        var plan = await t.Host.ExpandAsync(document);

        Assert.True(Ordered(plan, Guid(3), PimPolicyName), Edges(plan));
        Assert.False(Ordered(plan, Guid(2), PimPolicyName), "the Member role scope needs no onboarding: " + Edges(plan));
    }

    [Theory]
    [InlineData("scopeId", true)]
    [InlineData("scopeId", false)]
    [InlineData("group", true)]
    [InlineData("group", false)]
    public async Task ALiteralGroupIdOnThePolicy_MatchesTheSameLiteralResourceOriginId_CaseInsensitively(string key, bool upperOnPolicy)
    {
        var t = NewTenant();
        string onboarder = Onboarder(t);
        string policyId = upperOnPolicy ? t.GroupId.ToUpperInvariant() : t.GroupId;
        string scopeId = upperOnPolicy ? t.GroupId : t.GroupId.ToUpperInvariant();
        var document = t.Host.Resources(string.Join(", ", All(t), RoleScope(scopeId, "Eligible Member", label: 3), PimPolicy("\"" + key + "\": \"" + policyId + "\"", OptIn(onboarder))));

        var plan = await t.Host.ExpandAsync(document);

        Assert.True(Ordered(plan, Guid(3), PimPolicyName), Edges(plan));
    }

    [Fact]
    public async Task AComputedRole_IsStillAppliedAfterTheGroupPolicyThatOnboardsItsGroup()
    {
        var t = NewTenant();
        string onboarder = Onboarder(t);
        var document = t.Host.Resources(
            string.Join(", ", All(t), RoleScope(t.GroupId, "[parameters('role')]", label: 3), PimPolicy("\"scopeId\": \"" + t.GroupId + "\"", OptIn(onboarder))),
            parameters: "\"role\": { \"type\": \"string\" }",
            values: ("role", "Eligible Member"));

        var plan = await t.Host.ExpandAsync(document);

        Assert.True(Ordered(plan, Guid(3), PimPolicyName), Edges(plan));
    }

    public static TheoryData<string, string> PoliciesThatOnboardNothing => new()
    {
        { "no onboarding", "{ \"scopeId\": \"GROUP\", \"accessId\": \"member\", \"rules\": [ { \"id\": \"Expiration_EndUser_Assignment\" } ] }" },
        { "a null onboarding", "{ \"scopeId\": \"GROUP\", \"accessId\": \"member\", \"onboarding\": null, \"rules\": [ { \"id\": \"Expiration_EndUser_Assignment\" } ] }" },
        { "another group", "{ \"group\": \"pg-dev-approvers\", \"accessId\": \"member\", \"onboarding\": ONBOARDING, \"rules\": [ { \"id\": \"Expiration_EndUser_Assignment\" } ] }" },
    };

    [Theory]
    [MemberData(nameof(PoliciesThatOnboardNothing))]
    public async Task AGroupPolicyThatDoesNotOnboardTheGroup_OrdersNothing_AndTheEligibleRoleIsPimGroupNotOnboarded(string because, string policyProperties)
    {
        var t = NewTenant();
        Check.Succeeded(await t.Host.CreateAsync(t.Host.Resources(All(t))));
        string onboarder = Onboarder(t);
        string properties = policyProperties.Replace("GROUP", t.GroupId, StringComparison.Ordinal).Replace("ONBOARDING", OptIn(onboarder), StringComparison.Ordinal);
        var document = t.Host.Resources(string.Join(", ", All(t), RoleScope(t.GroupId, "Eligible Member", label: 3), Templates.Resource(RoleManagementPoliciesProvider.ResourceType, PimPolicyName, properties)));

        var whatIf = await t.Host.WhatIfAsync(document);

        Assert.False(Ordered(whatIf.Plan, Guid(3), PimPolicyName), because + ": " + Edges(whatIf.Plan));
        Assert.Equal(EngineDiagnosticCodes.PimGroupNotOnboarded, whatIf.Change(Guid(3)).UnsupportedCode);
    }

    [Fact]
    public async Task AnIgnoredGroupPolicy_OrdersNothing_AndTheEligibleRoleIsPimGroupNotOnboarded()
    {
        var t = NewTenant();
        Check.Succeeded(await t.Host.CreateAsync(t.Host.Resources(All(t))));
        string onboarder = Onboarder(t);
        var document = t.Host.Resources(string.Join(", ", All(t), RoleScope(t.GroupId, "Eligible Member", label: 3), PimPolicy("\"scopeId\": \"" + t.GroupId + "\"", OptIn(onboarder), extra: ", \"condition\": false")));

        var whatIf = await t.Host.WhatIfAsync(document);

        Assert.False(Ordered(whatIf.Plan, Guid(3), PimPolicyName), Edges(whatIf.Plan));
        Assert.Equal(EngineDiagnosticCodes.PimGroupNotOnboarded, whatIf.Change(Guid(3)).UnsupportedCode);
    }

    [Theory]
    [InlineData("Member")]
    [InlineData("Owner")]
    public async Task AMemberOrOwnerRoleScope_IsNotOrderedAfterTheGroupPolicy(string role)
    {
        var t = NewTenant();
        string onboarder = Onboarder(t);
        var document = t.Host.Resources(string.Join(", ", All(t), RoleScope(t.GroupId, role, label: 3), PimPolicy("\"scopeId\": \"" + t.GroupId + "\"", OptIn(onboarder))));

        var plan = await t.Host.ExpandAsync(document);

        Assert.False(Ordered(plan, Guid(3), PimPolicyName), Edges(plan));
        Assert.DoesNotContain(plan.Graph.Edges, e => e.Kind == DependencyKind.Ordering && e.Dependency.Id.NameLeaf == PimPolicyName);
    }

    // The offline validator refuses onboarding on a directory role policy; the order helper is asked directly with the opt-in added to the declaration.
    [Fact]
    public async Task ADirectoryRolePolicy_WithAnOnboardingOptIn_OnboardsNoGroup()
    {
        var t = NewTenant();
        string onboarder = Onboarder(t);
        string rolePolicy = Templates.Resource(RoleManagementPoliciesProvider.ResourceType, PimPolicyName, "{ \"role\": \"Global Administrator\", \"rules\": [ { \"id\": \"Expiration_EndUser_Assignment\" } ] }");
        var plan = await t.Host.ExpandAsync(t.Host.Resources(string.Join(", ", All(t), RoleScope(t.GroupId, "Eligible Member", label: 3), rolePolicy)));
        var policy = plan.Instances.Single(i => i.Id.NameLeaf == PimPolicyName);
        var roleScope = plan.Instances.Single(i => i.Id.NameLeaf == Guid(3));
        policy.Declaration.Properties!.AsObject()["onboarding"] = JsonNode.Parse(OptIn(onboarder));
        policy.Declaration.Properties!.AsObject()["scopeId"] = t.GroupId;

        Assert.False(RoleManagementPolicyApplyOrder.OnboardsGroup(policy, new HashSet<ResourceInstance>(), t.GroupId, plan.Instances));
        Assert.Empty(AccessPackageApplyOrder.OnboardingPolicies(roleScope, plan.Instances));
        Assert.Empty(AccessPackageApplyOrder.EligibleRoleScopesOnboardedBy(policy, plan.Instances));
        Assert.False(AccessPackageApplyOrder.GroupOnboardedThisRun(roleScope, plan.Instances));
    }

    [Fact]
    public async Task AWhatIf_WithAnOnboardingGroupPolicy_ShowsACreate_ThatWaitsForTheOnboarding()
    {
        var t = NewTenant();
        Check.Succeeded(await t.Host.CreateAsync(t.Host.Resources(All(t))));
        string onboarder = Onboarder(t);
        var document = t.Host.Resources(string.Join(", ", All(t), RoleScope(t.GroupId, "Eligible Member", label: 3), PimPolicy("\"scopeId\": \"" + t.GroupId + "\"", OptIn(onboarder))));

        var whatIf = await t.Host.WhatIfAsync(document);

        var row = whatIf.Change(Guid(3));
        Assert.Equal(ChangeType.Create, row.Type);
        Assert.False(row.HasError);
        Assert.Contains(row.Reasons, r => r.Contains("an onboarding PIM policy", StringComparison.Ordinal));
        Assert.Contains(row.Reasons, r => r.Contains("waits up to 90 s", StringComparison.Ordinal));
        Assert.DoesNotContain(whatIf.Plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.PimGroupNotOnboarded);
        Assert.Equal(ChangeType.Create, whatIf.Change(PimPolicyName).Type);
        Assert.True(Ordered(whatIf.Plan, Guid(3), PimPolicyName), Edges(whatIf.Plan));
        Check.NoWrites(whatIf);
    }

    private static (Tenant Tenant, string Template) PolicyOnboarding()
    {
        var t = NewTenant();
        string onboarder = Onboarder(t);
        return (t, string.Join(", ", All(t, role: "Eligible Member"), PimPolicy("\"scopeId\": \"" + t.GroupId + "\"", OptIn(onboarder))));
    }

    [Fact]
    public async Task TheEligibleRoleScope_IsCreatedAfterThePolicyOnboardsTheGroup_InOneRun()
    {
        var (t, template) = PolicyOnboarding();

        var run = await t.Host.CreateAsync(t.Host.Resources(template));

        Check.Succeeded(run);
        Assert.Contains(t.GroupId, t.Host.Directory.GroupEligibility.OnboardedGroups);
        Assert.Equal("Eligible Member", t.Host.Directory.EntitlementManagement.RoleScopes.Single()["role"]!["displayName"]!.GetValue<string>());
        Assert.True(run.Op(PimPolicyName).Batch < run.Op(Guid(2)).Batch, $"policy batch {run.Op(PimPolicyName).Batch}, role scope batch {run.Op(Guid(2)).Batch}");
        var writes = run.Writes.ToList();
        int onboarding = writes.FindIndex(w => (string?)w.Body?["action"] == "adminAssign");
        int roleScope = writes.FindIndex(w => w.Path.EndsWith("/resourceRoleScopes", StringComparison.Ordinal));
        Assert.True(onboarding >= 0 && roleScope > onboarding, run.DescribeRequests());
    }

    [Fact]
    public async Task AfterThePolicyOnboarding_TheRoleScopeWaitsForTheCatalogToOfferTheRole()
    {
        var (t, template) = PolicyOnboarding();
        t.Host.Directory.EntitlementManagement.EligibleRolesHiddenReads = 3;

        var run = await t.Host.CreateAsync(t.Host.Resources(template));

        Check.Succeeded(run);
        Assert.Single(t.Host.Directory.EntitlementManagement.RoleScopes);
        Assert.Equal(ChangeType.NoChange, (await t.Host.WhatIfAsync(t.Host.Resources(template))).Change(Guid(2)).Type);
    }

    [Fact]
    public void TheRoleScopeWait_IsEvery5Seconds_UpTo90Seconds()
    {
        var host = new ProviderHost();
        var settle = host.Provider<ResourceRoleScopesProvider>(ResourceRoleScopesProvider.ResourceType).Settle;

        Assert.Equal(TimeSpan.FromSeconds(5), settle.Interval);
        Assert.Equal(TimeSpan.FromSeconds(90), settle.Window);
        Assert.Same(host.SettleDelays, settle.Delay);
    }

    [Fact]
    public async Task WhenTheRoleNeverAppearsAfterThePolicyOnboarding_TheRoleScopeIsDeferredWithAWarning()
    {
        var (t, template) = PolicyOnboarding();
        t.Host.Directory.EntitlementManagement.EligibleRolesHiddenReads = -1;

        var run = await t.Host.CreateAsync(t.Host.Resources(template));

        Check.Succeeded(run);
        Assert.Equal(OperationStatus.Ignored, run.Op(Guid(2)).Status);
        Assert.Equal(ChangeType.Ignore, run.Change(Guid(2)).Type);
        string warning = Check.Diagnostic(run, EngineDiagnosticCodes.PimGroupNotOnboarded).Message;
        Assert.Contains("90 s after the schedule request or PIM policy of this deployment that onboards it", warning, StringComparison.Ordinal);
        Assert.Empty(t.Host.Directory.EntitlementManagement.RoleScopes);

        // One read, then one every 5 s for 90 s, all after the policy onboarded the group.
        var requests = run.Requests.ToList();
        int onboarded = requests.FindIndex(r => r.IsWrite && (string?)r.Body?["action"] == "adminAssign");
        Assert.Equal(1 + (90 / 5), requests.Skip(onboarded).Count(r => r.Method == "GET" && r.Path.EndsWith("/resourceRoles", StringComparison.Ordinal)));
        Assert.True(t.Host.SettleDelays.Count >= 90 / 5, $"waited {t.Host.SettleDelays.Count} time(s)");
    }

    [Fact]
    public async Task ARequestAndAnOnboardingPolicyForTheSameGroup_OrderTheRoleScopeAfterBoth_WithoutACycle_AndThePolicyStillWaitsForTheEndOfTheRun()
    {
        var t = NewTenant();
        string onboarder = Onboarder(t);
        string alice = t.Host.Directory.AddUser("alice@contoso.com", "Alice")["id"]!.GetValue<string>();
        string requestName = Guid(4);
        var document = t.Host.Resources(string.Join(", ", All(t, role: "Eligible Member"), Eligibility(t, alice), PimPolicy("\"scopeId\": \"" + t.GroupId + "\"", OptIn(onboarder))));

        var plan = await t.Host.ExpandAsync(document);

        Assert.True(Ordered(plan, Guid(2), PimPolicyName), Edges(plan));
        Assert.True(Ordered(plan, Guid(2), requestName), Edges(plan));
        Assert.True(Ordered(plan, requestName, PimPolicyName), "the policy still precedes the request it governs: " + Edges(plan));
        Assert.False(Ordered(plan, PimPolicyName, requestName), Edges(plan));
        Assert.False(Ordered(plan, PimPolicyName, Guid(2)), Edges(plan));

        var whatIf = await t.Host.WhatIfAsync(document);
        Assert.Equal(ChangeType.Ignore, whatIf.Change(PimPolicyName).Type);
        Assert.True(whatIf.Change(PimPolicyName).RetryAtEndOfRun);
        Assert.Equal(ChangeType.Create, whatIf.Change(Guid(2)).Type);
        Assert.False(whatIf.Change(Guid(2)).HasError);

        var run = await t.Host.CreateAsync(document);

        Check.Succeeded(run);
        Check.NoDiagnostic(run, EngineDiagnosticCodes.PimGroupNotOnboarded);
        Assert.Equal(OperationStatus.Succeeded, run.Op(PimPolicyName).Status);
        var assign = Assert.Single(run.Writes, w => (string?)w.Body?["action"] == "adminAssign");
        Assert.Equal(alice, (string?)assign.Body!["principalId"]);
        Assert.DoesNotContain(run.Writes, w => (string?)w.Body?["action"] == "adminRemove");
        Assert.Single(t.Host.Directory.EntitlementManagement.RoleScopes);
    }

    [Fact]
    public async Task TheRequestStillOrdersOnlyTheRoleScope_AndThePolicyOrdersTheRequestAndTheRoleScope()
    {
        var t = NewTenant();
        string onboarder = Onboarder(t);
        string alice = t.Host.Directory.AddUser("alice@contoso.com", "Alice")["id"]!.GetValue<string>();
        var plan = await t.Host.ExpandAsync(t.Host.Resources(string.Join(", ", All(t, role: "Eligible Member"), Eligibility(t, alice), PimPolicy("\"scopeId\": \"" + t.GroupId + "\"", OptIn(onboarder)))));
        var request = plan.Instances.Single(i => i.Id.NameLeaf == Guid(4));
        var policy = plan.Instances.Single(i => i.Id.NameLeaf == PimPolicyName);
        var roleScope = plan.Instances.Single(i => i.Id.NameLeaf == Guid(2));

        var afterRequest = t.Host.Provider<GroupEligibilityScheduleRequestsProvider>(GroupEligibilityScheduleRequestsProvider.ResourceType).AppliesBefore(request, plan.Instances).ToList();
        var afterPolicy = t.Host.Provider<RoleManagementPoliciesProvider>(RoleManagementPoliciesProvider.ResourceType).AppliesBefore(policy, plan.Instances).ToList();

        Assert.Same(roleScope, Assert.Single(afterRequest));
        Assert.Equal(2, afterPolicy.Count);
        Assert.Same(request, afterPolicy[0]);
        Assert.Same(roleScope, afterPolicy[1]);
        Assert.Equal([request, policy], AccessPackageApplyOrder.OnboardingSources(roleScope, plan.Instances));
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
