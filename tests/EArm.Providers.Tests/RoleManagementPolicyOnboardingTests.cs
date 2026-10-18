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
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// <c>Microsoft.Entra/roleManagementPolicies</c> with <c>properties.onboarding</c>: a group policy whose group PIM for Groups has not onboarded is a Create
/// row; the create requests a temporary member eligibility for the named principal, waits for Graph to list the group's policy, removes the eligibility
/// (repeating ActiveDurationTooShort) and applies the rules, all in one run. The fake behaves like a real tenant (probe PP7): no policy is listed before
/// onboarding, and the plain member lookup exposes a pre-onboarding policy id that is never a policy.
/// </summary>
public sealed class RoleManagementPolicyOnboardingTests
{
    private const string Type = RoleManagementPoliciesProvider.ResourceType;
    private const string PolicyName = "9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d";
    private const string EligibilityName = "1c2d3e4f-5061-4728-9930-4b5c6d7e8f90";
    private const string Schedules = FakeGroupScheduleRequests.EligibilitySchedulesPath;
    private const string Requests = FakeGroupScheduleRequests.EligibilityRequestsPath;

    private sealed record Tenant(ProviderHost Host, string GroupId, string PrincipalId);

    /// <summary>A tenant like a real one: no group policy before onboarding; a group pim-ops and the onboarding user.</summary>
    private static Tenant RealTenant()
    {
        var host = new ProviderHost();
        host.Directory.RoleManagementPolicies.PoliciesBeforeOnboarding = false;
        string group = host.Directory.AddGroup("pim-ops", "PIM Ops")["id"]!.GetValue<string>();
        string principal = host.Directory.AddUser("pim-onboarding@contoso.com", "PIM Onboarding")["id"]!.GetValue<string>();
        return new Tenant(host, group, principal);
    }

    private static string OptedIn(string principal, string onboarding = null!)
    {
        string value = onboarding ?? "{ \"temporaryEligiblePrincipalId\": \"" + principal + "\" }";
        return Templates.Resource(Type, PolicyName, "{ \"group\": \"pim-ops\", \"accessId\": \"member\", \"onboarding\": " + value + ", \"rules\": [ { \"id\": \"Expiration_EndUser_Assignment\", \"maximumDuration\": \"PT4H\" } ] }");
    }

    private static List<FakeGraphRequest> EligibilityPosts(Run run, string action)
    {
        return run.Writes.Where(w => w.Method == "POST" && w.Path == Requests && (string?)w.Body?["action"] == action).ToList();
    }

    private static string MemberRule(ProviderHost host, string group)
    {
        return host.Directory.RoleManagementPolicies.Rule("Group", group, "member", "Expiration_EndUser_Assignment")["maximumDuration"]!.GetValue<string>();
    }

    [Fact]
    public async Task AnOptedInGroupPolicy_OnboardsTheGroup_RemovesTheEligibility_AndAppliesThePolicy_InOneRun()
    {
        var (host, group, principal) = RealTenant();
        var document = host.Resources(OptedIn(principal));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var assign = Assert.Single(EligibilityPosts(run, "adminAssign"));
        Assert.Equal(principal, (string?)assign.Body!["principalId"]);
        Assert.Equal(group, (string?)assign.Body!["groupId"]);
        Assert.Equal("member", (string?)assign.Body!["accessId"]);
        Assert.Equal("PT1H", (string?)assign.Body!["scheduleInfo"]!["expiration"]!["duration"]);
        Assert.Equal("EARM onboarding of " + group + " for policy management", (string?)assign.Body!["justification"]);
        Assert.Single(EligibilityPosts(run, "adminRemove"));
        Assert.Empty(host.Directory.Objects(Schedules));
        Assert.Contains(group, host.Directory.GroupEligibility.OnboardedGroups);

        // Assign, then remove, then the rule PATCH on the post-onboarding policy.
        var writes = run.Writes.ToList();
        int remove = writes.FindIndex(w => (string?)w.Body?["action"] == "adminRemove");
        int patch = writes.FindIndex(w => w.Method == "PATCH");
        Assert.True(writes.FindIndex(w => (string?)w.Body?["action"] == "adminAssign") < remove && remove < patch, run.DescribeRequests());
        var patched = Assert.Single(host.Directory.RoleManagementPolicies.RulePatches);
        Assert.Equal("Expiration_EndUser_Assignment", patched.RuleId);
        Assert.Equal(RoleManagementPolicyLookup.PolicyId(LiveAssignment(host, group)!), patched.PolicyId);
        Assert.Equal("PT4H", MemberRule(host, group));
        Assert.Equal(ChangeType.Create, run.Change(PolicyName).Type);

        // The next run finds the onboarded group's policy: nothing to do, no eligibility requested.
        var rerun = await host.CreateAsync(document);
        Check.Succeeded(rerun);
        Check.NoWrites(rerun);
        Assert.Equal(ChangeType.NoChange, (await host.WhatIfAsync(document)).Change(PolicyName).Type);
    }

    [Fact]
    public async Task WhatIf_ShowsTheOnboardingAsPartOfThePolicyRow_AndWritesNothing()
    {
        var (host, group, principal) = RealTenant();

        var whatIf = await host.WhatIfAsync(host.Resources(OptedIn(principal)));

        var change = whatIf.Change(PolicyName);
        Assert.Equal(ChangeType.Create, change.Type);
        Assert.False(change.HasError);
        Assert.Contains("group not onboarded: a temporary eligibility for " + principal + " will onboard it, then be removed", change.Reasons);
        Assert.Contains(change.Reasons, r => r.Contains("applies the declared rules", StringComparison.Ordinal));
        Assert.DoesNotContain("onboarding", change.After!.Select(p => p.Key));
        Assert.Contains("group not onboarded", whatIf.WhatIfTextOutput, StringComparison.Ordinal);
        Check.NoWrites(whatIf);
        Assert.DoesNotContain(group, host.Directory.GroupEligibility.OnboardedGroups);
    }

    [Fact]
    public async Task WhatIf_WithAPrincipalCreatedInTheSameRun_SaysItIsKnownAtDeployment()
    {
        var (host, _, _) = RealTenant();
        string user = Templates.Resource("Microsoft.Entra/users", "pim-onboarder@contoso.com", "{ \"displayName\": \"PIM Onboarder\" }");
        string policy = OptedIn(string.Empty, "{ \"temporaryEligiblePrincipalId\": \"[reference(resourceId('Microsoft.Entra/users', 'pim-onboarder@contoso.com')).id]\" }");

        var whatIf = await host.WhatIfAsync(host.Resources(user + ", " + policy));

        var change = whatIf.Change(PolicyName);
        Assert.Equal(ChangeType.Create, change.Type);
        Assert.Contains(change.Reasons, r => r.StartsWith("group not onboarded: a temporary eligibility for the principal of properties.onboarding (known at deployment)", StringComparison.Ordinal));
        Check.NoWrites(whatIf);
    }

    [Fact]
    public async Task ARemovalAnsweredActiveDurationTooShort_IsRepeated_UntilItIsAccepted()
    {
        var (host, group, principal) = RealTenant();
        host.Directory.GroupEligibility.TooNewOnRemove = 2;

        var run = await host.CreateAsync(host.Resources(OptedIn(principal)));

        Check.Succeeded(run);
        Assert.Equal(3, EligibilityPosts(run, "adminRemove").Count);
        Assert.True(host.SettleDelays.Count >= 2, $"expected the removal to wait between attempts; waited {host.SettleDelays.Count} time(s)");
        Assert.Empty(host.Directory.Objects(Schedules));
        Assert.Equal("PT4H", MemberRule(host, group));
    }

    [Fact]
    public async Task ARemovalThatKeepsBeingTooNew_FailsTheResource_NamingTheLeftoverEligibility_AfterTheRulesWereApplied()
    {
        var (host, group, principal) = RealTenant();
        host.Directory.GroupEligibility.TooNewOnRemove = int.MaxValue;

        var run = await host.CreateAsync(host.Resources(OptedIn(principal)));

        Assert.False(run.Succeeded);
        var op = run.Op(PolicyName);
        Assert.Equal(EngineDiagnosticCodes.ResourceApplyFailed, op.ErrorCode);
        Assert.Contains("principal " + principal, Check.Diagnostic(run, EngineDiagnosticCodes.PimOnboardingEligibilityNotRemoved).Message, StringComparison.Ordinal);
        Assert.Contains("ActiveDurationTooShort", op.Error, StringComparison.Ordinal);
        Assert.Contains("principal " + principal + " as member of group " + group, op.Error, StringComparison.Ordinal);
        Assert.Contains("adminRemove", op.Error, StringComparison.Ordinal);

        // Bounded: about seven minutes of 30 s waits, not forever.
        int removals = EligibilityPosts(run, "adminRemove").Count;
        Assert.InRange(removals, 2, 20);
        Assert.Single(host.Directory.Objects(Schedules));
        Assert.Equal("PT4H", MemberRule(host, group));
    }

    [Fact]
    public async Task ARefusedRemoval_FailsTheResource_NamingTheLeftoverEligibility()
    {
        var (host, group, principal) = RealTenant();
        host.Directory.GroupEligibility.RefuseRemove = true;

        var run = await host.CreateAsync(host.Resources(OptedIn(principal)));

        Assert.False(run.Succeeded);
        Check.Diagnostic(run, EngineDiagnosticCodes.PimOnboardingEligibilityNotRemoved);
        Assert.Contains("principal " + principal + " as member of group " + group, run.Op(PolicyName).Error, StringComparison.Ordinal);
        Assert.Single(EligibilityPosts(run, "adminRemove"));
        Assert.Single(host.Directory.Objects(Schedules));
    }

    [Fact]
    public async Task ARefusedEligibility_IsPimGroupOnboardingFailed_WithGraphsAnswer_AndNothingIsRemovedOrPatched()
    {
        var (host, _, _) = RealTenant();
        string missing = "0f0e0d0c-0b0a-4908-8706-050403020100";

        var run = await host.CreateAsync(host.Resources(OptedIn(missing)));

        Assert.False(run.Succeeded);
        var op = run.Op(PolicyName);
        Assert.Contains(EngineDiagnosticCodes.PimGroupOnboardingFailed, op.ErrorCode + " " + op.Error, StringComparison.Ordinal);
        Assert.Contains("PrincipalNotFound", op.Error, StringComparison.Ordinal);
        Assert.Contains("allows to be made eligible", op.Error, StringComparison.Ordinal);
        Assert.Empty(EligibilityPosts(run, "adminRemove"));
        Assert.Empty(host.Directory.RoleManagementPolicies.RulePatches);
    }

    [Fact]
    public async Task APreOnboardingPolicyId_FromTheMemberLookup_IsNeverPatched()
    {
        var (host, group, principal) = RealTenant();
        string filter = RoleManagementPolicyLookup.Filter(new PolicyScope("Group", group, "member"));
        var plain = new List<JsonNode>();
        await foreach (var item in host.Fake.GetAllPagesAsync(new GraphRequest(RoleManagementPolicyLookup.AssignmentsPath).WithFilter(filter), ProviderHost.Ct))
        {
            plain.Add(item);
        }

        string preOnboardingId = RoleManagementPolicyLookup.PolicyId((JsonObject)Assert.Single(plain))!;
        Assert.Equal(FakeRoleManagementPolicies.PreOnboardingPolicyId(group, "member"), preOnboardingId);

        var run = await host.CreateAsync(host.Resources(OptedIn(principal)));

        Check.Succeeded(run);
        Assert.DoesNotContain(run.Requests, r => r.Path.Contains(preOnboardingId, StringComparison.OrdinalIgnoreCase));
        var patched = Assert.Single(host.Directory.RoleManagementPolicies.RulePatches);
        Assert.NotEqual(preOnboardingId, patched.PolicyId, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(preOnboardingId, run.GraphIdOf(run.Op(PolicyName).Id.Id), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WithoutTheOptIn_TheGroupIsNotOnboarded_AndThePolicyIsPimPolicyNotAvailable()
    {
        var (host, group, _) = RealTenant();
        var document = host.Resources(Templates.Resource(Type, PolicyName, "{ \"group\": \"pim-ops\", \"accessId\": \"member\", \"rules\": [ { \"id\": \"Expiration_EndUser_Assignment\", \"maximumDuration\": \"PT4H\" } ] }"));

        var whatIf = await host.WhatIfAsync(document);
        Assert.Contains("properties.onboarding", whatIf.Change(PolicyName).UnsupportedReason, StringComparison.Ordinal);

        var run = await host.CreateAsync(document);
        Assert.Equal(EngineDiagnosticCodes.PimPolicyNotAvailable, run.Op(PolicyName).ErrorCode);
        Check.NoWrites(run);
        Assert.DoesNotContain(group, host.Directory.GroupEligibility.OnboardedGroups);
    }

    [Fact]
    public async Task WithASameRunRequest_TheOptInIsNotUsed_AndThePolicyIsDeferredToTheEndOfTheRun()
    {
        var (host, group, principal) = RealTenant();
        string alice = host.Directory.AddUser("alice@contoso.com", "Alice")["id"]!.GetValue<string>();
        var document = host.Resources(OptedIn(principal) + ", " + AliceEligibility(alice));

        Assert.Equal(ChangeType.Ignore, (await host.WhatIfAsync(document)).Change(PolicyName).Type);
        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        Check.NoDiagnostic(run, EngineDiagnosticCodes.PimPolicyNotAvailable);
        var assign = Assert.Single(EligibilityPosts(run, "adminAssign"));
        Assert.Equal(alice, (string?)assign.Body!["principalId"]);
        Assert.Empty(EligibilityPosts(run, "adminRemove"));
        Assert.Contains(group, host.Directory.GroupEligibility.OnboardedGroups);
        Assert.Equal("PT4H", MemberRule(host, group));
    }

    [Fact]
    public async Task AGroupPolicyDeferredForASameRunRequest_IsAppliedAtTheEndOfTheSameRun_ToThePostOnboardingPolicy()
    {
        // A tenant like a real one (no group policy listed before onboarding) and no opt-in: the request onboards the group, and one run applies the policy.
        var (host, group, _) = RealTenant();
        string alice = host.Directory.AddUser("alice@contoso.com", "Alice")["id"]!.GetValue<string>();
        string policy = Templates.Resource(Type, PolicyName, "{ \"group\": \"pim-ops\", \"accessId\": \"member\", \"rules\": [ { \"id\": \"Expiration_EndUser_Assignment\", \"maximumDuration\": \"PT4H\" } ] }");
        var document = host.Resources(policy + ", " + AliceEligibility(alice));

        var whatIf = await host.WhatIfAsync(document);
        var deferred = whatIf.Change(PolicyName);
        Assert.Equal(ChangeType.Ignore, deferred.Type);
        Assert.True(deferred.RetryAtEndOfRun);
        Assert.Contains(deferred.Reasons, r => r.Contains("applied at the end of this run", StringComparison.Ordinal));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        Check.NoDiagnostic(run, EngineDiagnosticCodes.PimPolicyNotAvailable);
        Assert.Equal(OperationStatus.Succeeded, run.Op(PolicyName).Status);
        Assert.Equal(ChangeType.Modify, run.Change(PolicyName).Type);

        // The request first (it onboards the group), then the rule PATCH on the policy the onboarding created; no temporary eligibility.
        var writes = run.Writes.ToList();
        int assign = writes.FindIndex(w => (string?)w.Body?["action"] == "adminAssign");
        int patch = writes.FindIndex(w => w.Method == "PATCH");
        Assert.True(assign >= 0 && patch > assign, run.DescribeRequests());
        Assert.Empty(EligibilityPosts(run, "adminRemove"));
        var patched = Assert.Single(host.Directory.RoleManagementPolicies.RulePatches);
        Assert.Equal(RoleManagementPolicyLookup.PolicyId(LiveAssignment(host, group)!), patched.PolicyId);
        Assert.Equal("PT4H", MemberRule(host, group));

        // Converged in one run: the next one has nothing to do.
        var rerun = await host.CreateAsync(document);
        Check.Succeeded(rerun);
        Check.NoWrites(rerun);
        Assert.Equal(ChangeType.NoChange, (await host.WhatIfAsync(document)).Change(PolicyName).Type);
    }

    private static string AliceEligibility(string alice)
    {
        return Templates.Resource(GroupEligibilityScheduleRequestsProvider.ResourceType, EligibilityName, "{ \"group\": \"pim-ops\", \"principalId\": \"" + alice + "\", \"accessId\": \"member\", \"scheduleInfo\": { \"expiration\": { \"type\": \"AfterDuration\", \"duration\": \"P30D\" } } }");
    }

    [Fact]
    public async Task AnOnboardedGroup_IgnoresTheOptIn_AndIsAPlainModify()
    {
        var (host, group, principal) = RealTenant();
        var document = host.Resources(OptedIn(principal));
        Check.Succeeded(await host.CreateAsync(document));
        host.Directory.RoleManagementPolicies.Rule("Group", group, "member", "Expiration_EndUser_Assignment")["maximumDuration"] = "PT8H";

        var whatIf = await host.WhatIfAsync(document);
        Assert.Equal(ChangeType.Modify, whatIf.Change(PolicyName).Type);
        Assert.DoesNotContain(whatIf.Change(PolicyName).Reasons, r => r.Contains("not onboarded", StringComparison.Ordinal));

        var run = await host.CreateAsync(document);
        Check.Succeeded(run);
        Assert.Empty(EligibilityPosts(run, "adminAssign"));
        Assert.Equal("PT4H", MemberRule(host, group));
    }

    [Theory]
    [InlineData("{ \"role\": \"Global Administrator\", \"onboarding\": { \"temporaryEligiblePrincipalId\": \"0f0e0d0c-0b0a-4908-8706-050403020100\" }, \"rules\": [ { \"id\": \"Expiration_EndUser_Assignment\" } ] }", "applies only to a group policy")]
    [InlineData("{ \"group\": \"pim-ops\", \"accessId\": \"member\", \"onboarding\": { \"temporaryEligiblePrincipalId\": \"pim-onboarding@contoso.com\" }, \"rules\": [ { \"id\": \"Expiration_EndUser_Assignment\" } ] }", "onboarding.temporaryEligiblePrincipalId")]
    [InlineData("{ \"group\": \"pim-ops\", \"accessId\": \"member\", \"onboarding\": { }, \"rules\": [ { \"id\": \"Expiration_EndUser_Assignment\" } ] }", "temporaryEligiblePrincipalId is required")]
    [InlineData("{ \"group\": \"pim-ops\", \"accessId\": \"member\", \"onboarding\": { \"temporaryEligiblePrincipalId\": \"0f0e0d0c-0b0a-4908-8706-050403020100\", \"duration\": \"PT1H\" }, \"rules\": [ { \"id\": \"Expiration_EndUser_Assignment\" } ] }", "is not a property of onboarding")]
    public async Task AMalformedOptIn_IsRefusedOffline_BeforeAnythingIsWritten(string properties, string expected)
    {
        var (host, _, _) = RealTenant();

        var refused = await Assert.ThrowsAsync<ValidationFailedException>(() => host.CreateAsync(host.Resources(Templates.Resource(Type, PolicyName, properties))));

        Assert.Contains(expected, string.Join(" | ", refused.Errors.Select(e => e.ToString())), StringComparison.Ordinal);
        Assert.DoesNotContain(host.Fake.Requests, r => r.IsWrite);
    }

    [Fact]
    public async Task RequiredPermissions_AddTheEligibilityScope_OnlyWithTheOptIn()
    {
        var host = new ProviderHost();
        var provider = host.Provider<RoleManagementPoliciesProvider>(Type);
        string valid = Templates.Resource(Type, PolicyName, "{ \"group\": \"ops\", \"accessId\": \"member\", \"rules\": [ { \"id\": \"Expiration_EndUser_Assignment\" } ] }");

        Assert.Equal(
            ["RoleManagementPolicy.ReadWrite.AzureADGroup", "PrivilegedEligibilitySchedule.ReadWrite.AzureADGroup", "Directory.Read.All"],
            provider.RequiredPermissions(await InstanceFactory.WithPropertiesAsync(host, valid, "{ \"group\": \"ops\", \"accessId\": \"member\", \"onboarding\": { \"temporaryEligiblePrincipalId\": \"0f0e0d0c-0b0a-4908-8706-050403020100\" }, \"rules\": [] }")));
        Assert.Equal(
            ["RoleManagementPolicy.ReadWrite.AzureADGroup", "Directory.Read.All"],
            provider.RequiredPermissions(await InstanceFactory.WithPropertiesAsync(host, valid, "{ \"group\": \"ops\", \"accessId\": \"member\", \"rules\": [] }")));
    }

    private static JsonObject? LiveAssignment(ProviderHost host, string group)
    {
        var policy = host.Directory.RoleManagementPolicies.PolicyOf("Group", group, "member");
        return policy is null ? null : new JsonObject { ["policyId"] = policy["id"]?.DeepClone() };
    }
}
