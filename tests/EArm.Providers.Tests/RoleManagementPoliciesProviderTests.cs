using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// <c>Microsoft.Entra/roleManagementPolicies</c>: PIM policy rules in ARM's shape. Found through the policy assignment of the scope (role sugar, group +
/// accessId sugar), only declared rule ids managed (filled from the live rule, one PATCH per changed rule with its <c>@odata.type</c>), approvers mapped
/// between ARM's <c>{ id, userType }</c> and Graph's subject sets, never created (PimPolicyNotAvailable; a group onboarded by a same-run request deferred),
/// a group re-id after onboarding followed, applied before the requests it governs, never deleted (NotDeletable), no orphans.
/// </summary>
public sealed class RoleManagementPoliciesProviderTests
{
    private const string Type = RoleManagementPoliciesProvider.ResourceType;
    private const string Name = "3f6e1a2b-7c8d-4e9f-a0b1-c2d3e4f5a6b7";
    private const string GroupPolicyName = "9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d";
    private const string GlobalAdministrator = "62e90394-69f5-4237-9190-012177145e10";
    private const string ApproverGroup = "2385b0f3-5fa9-43cf-8ca4-b01dc97298cd";
    private const string ApproverUser = "2f4913c9-d15b-406a-9946-1d66a28f2690";

    private static string Policy(string properties, string name = Name)
    {
        return Templates.Resource(Type, name, properties);
    }

    private static string RolePolicy(string rules, string role = "Global Administrator")
    {
        return Policy("{ \"role\": \"" + role + "\", \"rules\": [" + rules + "] }");
    }

    private static List<FakeGraphRequest> Patches(Run run)
    {
        return run.Writes.Where(w => w.Method == "PATCH").ToList();
    }

    private static JsonObject LiveRule(ProviderHost host, string ruleId, string scopeType = "DirectoryRole", string scopeId = "/", string roleDefinitionId = GlobalAdministrator)
    {
        return host.Directory.RoleManagementPolicies.Rule(scopeType, scopeId, roleDefinitionId, ruleId);
    }

    // ---- no change / modify ----

    [Fact]
    public async Task ADeclarationMatchingTheDefaults_IsNoChange_FoundThroughThePolicyAssignment()
    {
        var host = new ProviderHost();

        var run = await host.WhatIfAsync(host.Resources(RolePolicy("""{ "id": "Expiration_EndUser_Assignment", "isExpirationRequired": true, "maximumDuration": "PT8H" }""")));

        var change = run.Change(Name);
        Assert.Equal(ChangeType.NoChange, change.Type);
        Assert.StartsWith("DirectoryRole_", change.GraphId, StringComparison.Ordinal);
        var lookup = Assert.Single(run.Requests, r => r.Path == RoleManagementPolicyLookup.AssignmentsPath);
        Assert.Contains("scopeId eq '/' and scopeType eq 'DirectoryRole' and roleDefinitionId eq '" + GlobalAdministrator + "'", lookup.UrlParameters!["$filter"], StringComparison.Ordinal);
        Assert.Equal("policy($expand=rules)", lookup.UrlParameters["$expand"]);
        Check.NoWrites(run);
    }

    [Fact]
    public async Task AnArmPastedRule_WithRuleTypeAndNullTargetMembers_AndADurationInAnotherSpelling_IsNoChange()
    {
        var host = new ProviderHost();
        string rule = """
            { "isExpirationRequired": true, "maximumDuration": "PT480M", "id": "Expiration_EndUser_Assignment", "ruleType": "RoleManagementPolicyExpirationRule",
              "target": { "caller": "EndUser", "operations": ["All"], "level": "Assignment", "targetObjects": null, "inheritableSettings": null, "enforcedSettings": null } }
            """;

        var run = await host.WhatIfAsync(host.Resources(RolePolicy(rule)));

        Assert.Equal(ChangeType.NoChange, run.Change(Name).Type);
    }

    [Fact]
    public async Task AChangedRule_IsOnePatchOfThatRule_WithItsODataTypeFirst_AndTheUndeclaredMembersFromTheLiveRule()
    {
        var host = new ProviderHost();
        var document = host.Resources(RolePolicy("""{ "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT4H" }"""));

        var whatIf = await host.WhatIfAsync(document);
        var change = whatIf.Change(Name);
        Assert.Equal(ChangeType.Modify, change.Type);
        Assert.Equal(ChangeMode.Patch, change.Mode);
        Assert.Contains(change.Deltas.SelectMany(Flatten), d => d.HasEffect && d.Path.ToString() == "properties.rules.Expiration_EndUser_Assignment.maximumDuration");

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var patch = Assert.Single(Patches(run));
        Assert.EndsWith("/rules/Expiration_EndUser_Assignment", patch.Path, StringComparison.Ordinal);
        var body = Check.Body(patch);
        Assert.Equal("@odata.type", body.First().Key);
        Assert.Equal("#microsoft.graph.unifiedRoleManagementPolicyExpirationRule", body["@odata.type"]!.GetValue<string>());
        Assert.False(body.ContainsKey("ruleType"));
        Assert.Equal("PT4H", body["maximumDuration"]!.GetValue<string>());
        Assert.True(body["isExpirationRequired"]!.GetValue<bool>());
        Assert.Equal("EndUser", body["target"]!["caller"]!.GetValue<string>());
        Assert.Equal("PT4H", LiveRule(host, "Expiration_EndUser_Assignment")["maximumDuration"]!.GetValue<string>());

        var rerun = await host.WhatIfAsync(document);
        Assert.Equal(ChangeType.NoChange, rerun.Change(Name).Type);
    }

    [Fact]
    public async Task OnlyTheDeclaredRules_AreCompared_AndOnlyTheChangedOnesArePatched()
    {
        var host = new ProviderHost();
        var before = LiveRule(host, "Enablement_Admin_Assignment").DeepClone();
        var document = host.Resources(RolePolicy("""
            { "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT8H" },
            { "id": "Enablement_EndUser_Assignment", "enabledRules": ["multifactorauthentication", "Justification", "Ticketing"] }
            """));

        var whatIf = await host.WhatIfAsync(document);
        var rows = whatIf.Change(Name).Deltas.SelectMany(Flatten).Where(d => d.HasEffect).Select(d => d.Path.ToString()).ToList();
        Assert.All(rows.Where(p => p != "properties.rules"), p => Assert.StartsWith("properties.rules.Enablement_EndUser_Assignment", p, StringComparison.Ordinal));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var patch = Assert.Single(Patches(run));
        Assert.EndsWith("/rules/Enablement_EndUser_Assignment", patch.Path, StringComparison.Ordinal);
        Assert.Equal(["MultiFactorAuthentication", "Justification", "Ticketing"], Check.Body(patch)["enabledRules"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.True(JsonNode.DeepEquals(before, LiveRule(host, "Enablement_Admin_Assignment")), "an undeclared rule must not change");
    }

    [Fact]
    public async Task AnEmptyEnabledRulesList_ClearsTheRequirements()
    {
        var host = new ProviderHost();
        var document = host.Resources(RolePolicy("""{ "id": "Enablement_EndUser_Assignment", "enabledRules": [] }"""));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        Assert.Empty(Check.Body(Assert.Single(Patches(run)))["enabledRules"]!.AsArray());
        Assert.Empty(LiveRule(host, "Enablement_EndUser_Assignment")["enabledRules"]!.AsArray());
        Assert.Equal(ChangeType.NoChange, (await host.WhatIfAsync(document)).Change(Name).Type);
    }

    // ---- approvers ----

    [Fact]
    public async Task ArmApprovers_AreSentAsGraphSubjectSets_AndReadBackAsArmApprovers_SoTheRerunIsNoChange()
    {
        var host = new ProviderHost();
        string approval = $$"""
            { "id": "Approval_EndUser_Assignment", "setting": { "isApprovalRequired": true, "approvalMode": "singlestage", "approvalStages": [ {
                "approvalStageTimeOutInDays": 1, "isApproverJustificationRequired": true, "isEscalationEnabled": false,
                "primaryApprovers": [
                  { "id": "{{ApproverUser.ToUpperInvariant()}}", "userType": "user", "description": "Alice" },
                  { "id": "{{ApproverGroup}}", "userType": "Group", "description": "Emergency approvers", "isBackup": false } ] } ] } }
            """;
        var document = host.Resources(RolePolicy(approval));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var body = Check.Body(Assert.Single(Patches(run)));
        Assert.Equal("#microsoft.graph.unifiedRoleManagementPolicyApprovalRule", body["@odata.type"]!.GetValue<string>());
        var approvers = body["setting"]!["approvalStages"]![0]!["primaryApprovers"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.Contains(approvers, a => a["@odata.type"]!.GetValue<string>() == "#microsoft.graph.singleUser" && a["userId"]!.GetValue<string>() == ApproverUser);
        Assert.Contains(approvers, a => a["@odata.type"]!.GetValue<string>() == "#microsoft.graph.groupMembers" && a["groupId"]!.GetValue<string>() == ApproverGroup);
        Assert.All(approvers, a => Assert.False(a.ContainsKey("description") || a.ContainsKey("id") || a.ContainsKey("userType")));
        Assert.Equal("SingleStage", body["setting"]!["approvalMode"]!.GetValue<string>());

        // The live rule now echoes the approvers with a description and in Graph's shape; the declaration (another order, other casing) matches it.
        string reordered = approval.Replace("\"user\", \"description\": \"Alice\"", "\"User\"", StringComparison.Ordinal);
        var rerun = await host.WhatIfAsync(host.Resources(RolePolicy(reordered)));
        Assert.Equal(ChangeType.NoChange, rerun.Change(Name).Type);
    }

    [Fact]
    public void TheShape_MapsApproversBothWays_AndRuleTypeToODataType()
    {
        var graph = JsonNode.Parse($$"""
            { "@odata.type": "#microsoft.graph.unifiedRoleManagementPolicyApprovalRule", "id": "Approval_EndUser_Assignment",
              "target": { "@odata.type": "microsoft.graph.unifiedRoleManagementPolicyRuleTarget", "caller": "EndUser", "operations": ["all"], "level": "Assignment" },
              "setting": { "isApprovalRequired": true, "approvalStages": [ { "primaryApprovers": [
                  { "@odata.type": "#microsoft.graph.singleUser", "userId": "{{ApproverUser}}", "description": "Alice" },
                  { "@odata.type": "#microsoft.graph.groupMembers", "groupId": "{{ApproverGroup}}", "description": "Approvers" } ] } ] } }
            """)!.AsObject();

        var arm = RoleManagementPolicyShape.ToArm(graph);

        Assert.Equal("RoleManagementPolicyApprovalRule", arm["ruleType"]!.GetValue<string>());
        Assert.False(arm.ContainsKey("@odata.type"));
        Assert.Equal("All", arm["target"]!["operations"]![0]!.GetValue<string>());
        var approvers = arm["setting"]!["approvalStages"]![0]!["primaryApprovers"]!.AsArray();
        Assert.Equal("""[{"id":"2385b0f3-5fa9-43cf-8ca4-b01dc97298cd","userType":"Group"},{"id":"2f4913c9-d15b-406a-9946-1d66a28f2690","userType":"User"}]""", approvers.ToJsonString());

        var back = RoleManagementPolicyShape.ToGraph(arm);
        Assert.Equal("#microsoft.graph.unifiedRoleManagementPolicyApprovalRule", back["@odata.type"]!.GetValue<string>());
        Assert.False(back.ContainsKey("ruleType"));
        Assert.Equal("""[{"@odata.type":"#microsoft.graph.groupMembers","groupId":"2385b0f3-5fa9-43cf-8ca4-b01dc97298cd"},{"@odata.type":"#microsoft.graph.singleUser","userId":"2f4913c9-d15b-406a-9946-1d66a28f2690"}]""", back["setting"]!["approvalStages"]![0]!["primaryApprovers"]!.ToJsonString());
    }

    // ---- refusals ----

    [Fact]
    public async Task ARuleIdThePolicyDoesNotHave_IsAnError_ThatListsTheRules()
    {
        var host = new ProviderHost();
        var document = host.Resources(RolePolicy("""{ "id": "AuthenticationContext_Admin_Assignment", "isEnabled": false }"""));

        var whatIf = await host.WhatIfAsync(document);
        var change = whatIf.Change(Name);
        Assert.NotNull(change.UnsupportedReason);
        Assert.Contains("has no rule AuthenticationContext_Admin_Assignment", change.UnsupportedReason, StringComparison.Ordinal);
        Assert.Contains("Approval_EndUser_Assignment", change.UnsupportedReason, StringComparison.Ordinal);

        var run = await host.CreateAsync(document);
        Assert.False(run.Succeeded);
        Assert.Equal(EngineDiagnosticCodes.RoleManagementPolicyInvalid, run.Op(Name).ErrorCode);
        Check.NoWrites(run);
    }

    [Fact]
    public async Task ARoleWithoutAPolicy_IsPimPolicyNotAvailable_AndNothingIsCreated()
    {
        var host = new ProviderHost();
        host.Directory.RoleManagementPolicies.RolesWithoutPolicy.Add(GlobalAdministrator);
        var document = host.Resources(RolePolicy("""{ "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT4H" }"""));

        var whatIf = await host.WhatIfAsync(document);
        Assert.Contains("lists no PIM policy for directory role", whatIf.Change(Name).UnsupportedReason, StringComparison.Ordinal);

        var run = await host.CreateAsync(document);
        Assert.False(run.Succeeded);
        Assert.Equal(EngineDiagnosticCodes.PimPolicyNotAvailable, run.Op(Name).ErrorCode);
        Check.NoWrites(run);
    }

    [Fact]
    public async Task AnUnknownRole_IsRefusedInPrepare()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(RolePolicy("""{ "id": "Expiration_EndUser_Assignment" }""", role: "No Such Role")));

        Assert.False(run.Succeeded);
        Assert.Equal(EngineDiagnosticCodes.RoleManagementPolicyInvalid, run.Op(Name).ErrorCode);
    }

    // ---- groups ----

    [Fact]
    public async Task AGroupPolicy_ListedBeforeOnboarding_ByUniqueNameAndAccessId_IsPatched_OnboardsTheGroup_AndFollowsTheNewPolicyId()
    {
        // The Learn-implied tenant (opt-in on the fake): a group's policy is listed before onboarding and the first rule PATCH onboards the group under new ids.
        var host = new ProviderHost();
        host.Directory.RoleManagementPolicies.PoliciesBeforeOnboarding = true;
        string ops = host.Directory.AddGroup("pim-ops", "PIM Ops")["id"]!.GetValue<string>();
        var document = host.Resources(Policy("""
            { "group": "pim-ops", "accessId": "Member", "rules": [
                { "id": "Expiration_Admin_Eligibility", "isExpirationRequired": false },
                { "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT4H" } ] }
            """, GroupPolicyName));

        var whatIf = await host.WhatIfAsync(document);
        var change = whatIf.Change(GroupPolicyName);
        Assert.Equal(ChangeType.Modify, change.Type);
        string preOnboardingId = change.GraphId!;

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var patches = host.Directory.RoleManagementPolicies.RulePatches;
        Assert.Equal(["Expiration_Admin_Eligibility", "Expiration_EndUser_Assignment"], patches.Select(p => p.RuleId).ToArray());
        Assert.Equal(preOnboardingId, patches[0].PolicyId);
        Assert.NotEqual(preOnboardingId, patches[1].PolicyId);
        Assert.Contains(run.Requests, r => r.Method == "PATCH" && r.Path.Contains(preOnboardingId, StringComparison.Ordinal) && r.Path.EndsWith("Expiration_EndUser_Assignment", StringComparison.Ordinal));
        Assert.Contains(ops, host.Directory.RoleManagementPolicies.OnboardedGroups);
        Assert.False(LiveRule(host, "Expiration_Admin_Eligibility", "Group", ops, "member")["isExpirationRequired"]!.GetValue<bool>());
        Assert.Equal("PT4H", LiveRule(host, "Expiration_EndUser_Assignment", "Group", ops, "member")["maximumDuration"]!.GetValue<string>());
        Assert.NotEqual(preOnboardingId, run.GraphIdOf(run.Op(GroupPolicyName).Id.Id));

        Assert.Equal(ChangeType.NoChange, (await host.WhatIfAsync(document)).Change(GroupPolicyName).Type);
    }

    [Fact]
    public async Task AGroupNotYetOnboarded_WithoutASameRunRequest_IsPimPolicyNotAvailable()
    {
        var host = new ProviderHost();
        host.Directory.AddGroup("pim-ops", "PIM Ops");
        var document = host.Resources(Policy("""{ "group": "pim-ops", "accessId": "member", "rules": [ { "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT4H" } ] }""", GroupPolicyName));

        var whatIf = await host.WhatIfAsync(document);
        Assert.Contains("has not onboarded the group", whatIf.Change(GroupPolicyName).UnsupportedReason, StringComparison.Ordinal);

        var run = await host.CreateAsync(document);
        Assert.False(run.Succeeded);
        Assert.Equal(EngineDiagnosticCodes.PimPolicyNotAvailable, run.Op(GroupPolicyName).ErrorCode);
        Check.NoWrites(run);
    }

    [Fact]
    public async Task AGroupOnboardedBySameRunRequest_IsDeferredWithAWarningInTheWhatIf_TheRequestGoesAhead_AndTheSameRunAppliesThePolicyAtItsEnd()
    {
        var host = new ProviderHost();
        string ops = host.Directory.AddGroup("pim-ops", "PIM Ops")["id"]!.GetValue<string>();
        string alice = host.Directory.AddUser("alice@contoso.com", "Alice")["id"]!.GetValue<string>();
        string policy = Policy("""{ "group": "pim-ops", "accessId": "member", "rules": [ { "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT4H" } ] }""", GroupPolicyName);
        string eligibility = Templates.Resource(GroupEligibilityScheduleRequestsProvider.ResourceType, Name, "{ \"group\": \"pim-ops\", \"principalId\": \"" + alice + "\", \"accessId\": \"member\", \"scheduleInfo\": { \"expiration\": { \"type\": \"AfterDuration\", \"duration\": \"P30D\" } } }");
        var document = host.Resources(policy + ", " + eligibility);

        var whatIf = await host.WhatIfAsync(document);
        Assert.Equal(ChangeType.Ignore, whatIf.Change(GroupPolicyName).Type);
        Assert.Contains(whatIf.Plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.PimPolicyNotAvailable);

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        Check.NoDiagnostic(run, EngineDiagnosticCodes.PimPolicyNotAvailable);
        Assert.Contains(ops, host.Directory.GroupEligibility.OnboardedGroups);
        Assert.Equal("Expiration_EndUser_Assignment", Assert.Single(host.Directory.RoleManagementPolicies.RulePatches).RuleId);
        Assert.Equal("PT4H", LiveRule(host, "Expiration_EndUser_Assignment", "Group", ops, "member")["maximumDuration"]!.GetValue<string>());
        Assert.Equal(ChangeType.NoChange, (await host.WhatIfAsync(document)).Change(GroupPolicyName).Type);
    }

    // ---- order ----

    [Fact]
    public async Task APolicy_IsAppliedBeforeTheRequestsOfTheSameRoleAndGroup()
    {
        // The group's policy is listed before onboarding here, so it is written in its batch; on a tenant that lists none until the request onboards the
        // group, the policy is deferred to the end of the run instead (AGroupOnboardedBySameRunRequest_...).
        var host = new ProviderHost();
        host.Directory.RoleManagementPolicies.PoliciesBeforeOnboarding = true;
        string ops = host.Directory.AddGroup("pim-ops", "PIM Ops")["id"]!.GetValue<string>();
        string alice = host.Directory.AddUser("alice@contoso.com", "Alice")["id"]!.GetValue<string>();
        string roleEligibility = Templates.Resource(RoleEligibilityScheduleRequestsProvider.ResourceType, "0b1c2d3e-4f50-4617-8829-3a4b5c6d7e8f", "{ \"principalId\": \"" + alice + "\", \"roleDefinitionId\": \"[roleTemplate('Global Administrator')]\" }");
        string groupEligibility = Templates.Resource(GroupEligibilityScheduleRequestsProvider.ResourceType, "1c2d3e4f-5061-4728-9930-4b5c6d7e8f90", "{ \"group\": \"pim-ops\", \"principalId\": \"" + alice + "\", \"accessId\": \"member\", \"scheduleInfo\": { \"expiration\": { \"type\": \"AfterDuration\", \"duration\": \"P30D\" } } }");
        string rolePolicy = RolePolicy("""{ "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT4H" }""");
        string groupPolicy = Policy("{ \"group\": \"pim-ops\", \"accessId\": \"member\", \"rules\": [ { \"id\": \"Expiration_EndUser_Assignment\", \"maximumDuration\": \"PT2H\" } ] }", GroupPolicyName);

        // The requests are declared first: only the provider's order puts the policies ahead of them.
        var document = host.Resources(roleEligibility + ", " + groupEligibility + ", " + rolePolicy + ", " + groupPolicy);
        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var ordering = run.Plan.Graph.Edges.Where(e => e.Kind == DependencyKind.Ordering).Select(e => (e.Dependent.Id.NameLeaf, e.Dependency.Id.NameLeaf)).ToList();
        Assert.True(ordering.Contains(("0b1c2d3e-4f50-4617-8829-3a4b5c6d7e8f", Name)), string.Join("; ", run.Plan.Graph.Edges.Select(e => e.Kind + ": " + e.Dependent.Id.NameLeaf + " -> " + e.Dependency.Id.NameLeaf)));
        Assert.Contains(("1c2d3e4f-5061-4728-9930-4b5c6d7e8f90", GroupPolicyName), ordering);
        Assert.Equal(2, ordering.Count);
        var writes = run.Writes.ToList();
        int lastPatch = writes.FindLastIndex(w => w.Method == "PATCH");
        int firstPost = writes.FindIndex(w => w.Method == "POST");
        Assert.True(lastPatch >= 0 && firstPost > lastPatch, "the policies must be written before the requests:\n" + run.DescribeRequests());
    }

    [Fact]
    public async Task AnExplicitDependsOnTheOtherWay_WinsOverTheProviderOrder_WithoutACycle()
    {
        var host = new ProviderHost();
        string alice = host.Directory.AddUser("alice@contoso.com", "Alice")["id"]!.GetValue<string>();
        const string EligibilityName = "0b1c2d3e-4f50-4617-8829-3a4b5c6d7e8f";
        string eligibility = Templates.Resource(RoleEligibilityScheduleRequestsProvider.ResourceType, EligibilityName, "{ \"principalId\": \"" + alice + "\", \"role\": \"Global Administrator\" }");
        string policy = Templates.Resource(Type, Name, "{ \"role\": \"Global Administrator\", \"rules\": [ { \"id\": \"Expiration_EndUser_Assignment\" } ] }", extra: Templates.DependsOn(RoleEligibilityScheduleRequestsProvider.ResourceType, EligibilityName));

        var run = await host.WhatIfAsync(host.Resources(eligibility + ", " + policy));

        Assert.DoesNotContain(run.Plan.Graph.Edges, e => e.Kind == DependencyKind.Ordering);
        Assert.Contains(run.Plan.Graph.Edges, e => e.Kind == DependencyKind.Explicit && e.Dependent.Id.NameLeaf == Name);
        Assert.Equal(ChangeType.NoChange, run.Change(Name).Type);
    }

    // ---- delete / orphans / permissions ----

    [Fact]
    public async Task DeploymentDelete_KeepsThePolicy_AsNotDeletable()
    {
        var host = new ProviderHost();
        var document = host.Resources(RolePolicy("""{ "id": "Expiration_EndUser_Assignment" }"""));

        var result = await host.DeleteDeclaredAsync(document);

        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(DeletionStatus.Refused, outcome.Status);
        Assert.Equal(EngineDiagnosticCodes.NotDeletable, outcome.ErrorCode);
        Assert.Contains("cannot be deleted", outcome.Error, StringComparison.Ordinal);
        Assert.True(result.Succeeded);
        Assert.DoesNotContain(host.Fake.Writes, w => w.IsWrite);
    }

    [Fact]
    public async Task DeleteAsync_Throws_NotDeletable_AndCompleteModeListsNoOrphans()
    {
        var host = new ProviderHost();
        var provider = host.Provider<RoleManagementPoliciesProvider>(Type);
        var key = new ResourceKey { Type = Type, Values = new Dictionary<string, string> { ["scopeType"] = "DirectoryRole", ["scopeId"] = "/", ["roleDefinitionId"] = GlobalAdministrator } };
        var live = new ExistingResource("p", new JsonObject(), new JsonObject());

        var refused = await Assert.ThrowsAsync<ProviderException>(() => provider.DeleteAsync(key, live, host.Fake, ProviderHost.Ct));

        Assert.Equal(EngineDiagnosticCodes.NotDeletable, refused.Code);
        Assert.NotNull(provider.DescribeDeleteRefusal(key, live));
        var complete = await host.WhatIfAsync(host.Resources(RolePolicy("""{ "id": "Expiration_EndUser_Assignment" }""")), DeploymentMode.Complete);
        Assert.DoesNotContain(complete.Changes, c => c.IsOrphan && ResourceIdentifier.TypeEquals(c.ResourceType, Type));
    }

    [Fact]
    public async Task RequiredPermissions_FollowTheScope()
    {
        var host = new ProviderHost();
        var provider = host.Provider<RoleManagementPoliciesProvider>(Type);
        string valid = RolePolicy("""{ "id": "Expiration_EndUser_Assignment" }""");

        Assert.Equal(["RoleManagementPolicy.ReadWrite.Directory", "Directory.Read.All"], provider.RequiredPermissions(await InstanceFactory.WithPropertiesAsync(host, valid, "{ \"role\": \"Global Administrator\", \"rules\": [] }")));
        Assert.Equal(["RoleManagementPolicy.ReadWrite.AzureADGroup", "Directory.Read.All"], provider.RequiredPermissions(await InstanceFactory.WithPropertiesAsync(host, valid, "{ \"group\": \"ops\", \"accessId\": \"member\", \"rules\": [] }")));
        Assert.Equal(["RoleManagementPolicy.ReadWrite.Directory", "RoleManagementPolicy.ReadWrite.AzureADGroup", "Directory.Read.All"], provider.RequiredPermissions(await InstanceFactory.WithPropertiesAsync(host, valid, "{ \"scopeType\": \"[parameters('kind')]\", \"rules\": [] }")));
    }

    private static IEnumerable<PropertyDelta> Flatten(PropertyDelta delta)
    {
        yield return delta;
        foreach (var child in delta.Children ?? [])
        {
            foreach (var nested in Flatten(child))
            {
                yield return nested;
            }
        }
    }
}
